using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.Sessions;
using Train.Core.Tests.World;
using static Train.Core.Tests.Construction.ConstructionFixture;

namespace Train.Core.Tests.Construction;

internal static class VehicleWorkProcessDriver
{
    private static readonly JsonSerializerOptions RecordingJson = CreateRecordingJson();
    private static CoreContentProjection ContentInput => Content() with
    {
        ConstructionRules = [Content().ConstructionRules[0] with { RefitWorkPerChangedItem = 1, DeconstructionWorkPerItem = 1 }],
        Designs = Content().Designs.Select(design => design.Design != WorldFixture.Vehicle ? design : design with
            { StructuralGoods = [new(Material, 1)] }).ToImmutableArray()
    };

    internal static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var mode = args[0];
            var output = args[1];
            var directory = args[2];
            var samples = new List<VehicleWorkProcessSample>();
            var events = new List<string>();
            var factory = CoreFactory.Create();
            if (mode == "replay")
            {
                var disk = JsonSerializer.Deserialize<VehicleWorkDiskRecording>(await File.ReadAllTextAsync(Path.Combine(directory, "recording.json")), RecordingJson)!;
                var prepared = factory.PrepareReplay(new(2, WorldFixture.Options.Runtime, disk.InitialState,
                    disk.Commands, disk.Checkpoints, disk.EndPosition, null), WorldFixture.Options.Runtime, [ContentInput]);
                using var replay = WorldFixture.Success(prepared);
                foreach (var position in disk.Positions)
                {
                    var result = replay.Advance(uint.MaxValue, position);
                    Assert.AreEqual(ReplayStop.RequestedPosition, result.Stop);
                    Assert.IsNull(result.Difference);
                    Assert.IsNull(result.Fault);
                    if (samples.Count > 0) events.AddRange(result.Events.Select(Event));
                    AddSample(replay.CaptureBoundary(), replay.Read());
                }
                Assert.AreEqual(ReplayStop.EndOfRecording, replay.Advance(1).Stop);
            }
            else
            {
                var resumed = mode.StartsWith("resume-", StringComparison.Ordinal);
                var paused = mode.EndsWith("paused", StringComparison.Ordinal);
                using var session = resumed ? Restore(paused ? "paused" : "refit") : Initial();
                if (!resumed)
                {
                    Accept(session, new CoreCommand.DisbandTrain(new(1)));
                    Send(session, new CoreCommand.ApplyInstanceDesign(Vehicle, Immediate()));
                    Send(session, Refit());
                    var rejected = Submit(session, new CoreCommand.ApplyInstanceDesign(Vehicle, Immediate()));
                    Assert.AreEqual("instance_locked", ((CommandDecision.Rejected)rejected.Receipt.Decision).Problems.Single().Code);
                    Advance(session);
                    events.Clear();
                }
                AddSample(WorldFixture.Success(session.Capture(CapturePurpose.Save)), session.Read());
                if (mode == "save-refit") await Save(session, "refit");
                else
                {
                    if (!resumed || !paused)
                    {
                        Advance(session);
                        AddSample(WorldFixture.Success(session.Capture(CapturePurpose.Save)), session.Read());
                        Send(session, new CoreCommand.StartOwnedJob(new JobRequest.Dismantle(Facility, Vehicle, 0)));
                        Advance(session);
                        Send(session, new CoreCommand.SetDeconstructionPaused(new(2), true));
                        AddSample(WorldFixture.Success(session.Capture(CapturePurpose.Save)), session.Read());
                    }
                    if (mode == "save-paused") await Save(session, "paused");
                    else
                    {
                        Advance(session, 2);
                        AddSample(WorldFixture.Success(session.Capture(CapturePurpose.Save)), session.Read());
                        Send(session, new CoreCommand.SetDeconstructionPaused(new(2), false));
                        Advance(session);
                        AddSample(WorldFixture.Success(session.Capture(CapturePurpose.Save)), session.Read());
                        Assert.IsInstanceOfType<Lookup<InstanceView>.Missing>(session.Read().FindInstance(Vehicle));
                        Assert.AreEqual(12L, Stock(session, Local));
                        Assert.AreEqual(0L, ((Lookup<InventoryView>.Found)session.Read().FindInventory(Local)).Value.Reserved);
                        if (mode == "complete")
                        {
                            var recording = session.ReadRecording();
                            var disk = new VehicleWorkDiskRecording(recording.Starts[0].State.Encode(), recording.Commands,
                                recording.Checkpoints, recording.EndPosition, samples.Select(sample => sample.Position).ToArray());
                            await File.WriteAllTextAsync(Path.Combine(directory, "recording.json"), JsonSerializer.Serialize(disk, RecordingJson), new UTF8Encoding(false));
                        }
                    }
                }
            }
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new VehicleWorkProcessResult(Environment.ProcessId, samples.ToArray())), new UTF8Encoding(false));
            return 0;

            void AddSample(ICoreCapture capture, ICoreReadView view)
            {
                var inventory = ((Lookup<InventoryView>.Found)view.FindInventory(Local)).Value;
                var instance = view.FindInstance(Vehicle) is Lookup<InstanceView>.Found found ? found.Value : null;
                var job = view.FindJob(new(1)) is Lookup<JobView>.Found first ? ((JobView.Construction)first.Value).Value
                    : view.FindJob(new(2)) is Lookup<JobView>.Found second ? ((JobView.Construction)second.Value).Value : null;
                var query = JsonSerializer.Serialize(new
                {
                    Design = instance?.Design.Value, Lock = instance?.Lock, Structure = instance?.Structure,
                    Job = job?.Id.Value, State = job?.State, ProgressWork = job?.ProgressWork, FrozenTotalWork = job?.FrozenTotalWork,
                    inventory.Used, inventory.Reserved, inventory.Available,
                    Reservations = inventory.Reservations.Select(reservation => new
                        { reservation.Job, reservation.Capacity, Returns = reservation.ExpectedReturns.Select(goods => goods.Quantity).ToArray() }).ToArray()
                });
                samples.Add(new(capture.Position, capture.Encode(), capture.Checkpoint.CanonicalText, capture.Checkpoint.Sha256,
                    query, JsonSerializer.Serialize(events)));
                events.Clear();
            }
            void Send(ICoreSession session, CoreCommand command) => events.AddRange(Accept(session, command).Events.Select(Event));
            void Advance(ICoreSession session, uint count = 1) => events.AddRange(Step(session, count).Events.Select(Event));
            Task Save(ICoreSession session, string boundary) => File.WriteAllTextAsync(Path.Combine(directory, boundary + ".state.json"),
                WorldFixture.Success(session.Capture(CapturePurpose.Save)).Encode(), new UTF8Encoding(false));
            ICoreSession Restore(string boundary)
            {
                var decoded = WorldFixture.Success(factory.DecodeState(File.ReadAllText(Path.Combine(directory, boundary + ".state.json"))));
                using var candidate = WorldFixture.Success(factory.PrepareRestore(decoded, ContentInput, WorldFixture.Options));
                return candidate.Activate();
            }
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
    }

    private static CoreSession Initial()
    {
        using var initial = Session(ContentInput);
        return new(initial.State with { World = initial.State.World with
        {
            Inventories = initial.State.World.Inventories.Select(inventory => inventory.Id != Cargo ? inventory : inventory with { Goods = [] }).ToImmutableArray()
        } });
    }
    private static DesignChangeProjection Immediate()
    {
        var input = Design(ContentInput);
        return new(input.Design.Design, input.Design.Composition,
            input with { Design = input.Design with { Design = new(new string('9', 64)) } },
            input.Design.Parts.Select(part => new PartCorrespondence(part.Id, part.Id)).ToImmutableArray(), [], [], 0);
    }
    private static CoreCommand Refit()
    {
        var immediate = Immediate().Target;
        var change = new DesignChangeProjection(immediate.Design.Design, immediate.Design.Composition,
            immediate with { Design = immediate.Design with { Design = new(new string('8', 64)), Composition = new(new string('7', 64)) } },
            immediate.Design.Parts.Select(part => new PartCorrespondence(part.Id, part.Id)).ToImmutableArray(),
            [new(Material, 1)], [new(Material, 1, [], [new(Material, 1)], [])], 2);
        return new CoreCommand.StartOwnedJob(new JobRequest.Refit(Facility, Vehicle, change, [Local], 0));
    }
    private static JsonSerializerOptions CreateRecordingJson()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info =>
        {
            (Type Type, string Name)[] variants = info.Type == typeof(CommandDecision)
                ? [(typeof(CommandDecision.Accepted), "accepted"), (typeof(CommandDecision.Rejected), "rejected")]
                : info.Type == typeof(WorldObjectId)
                    ? [(typeof(WorldObjectId.Job), "job"), (typeof(WorldObjectId.Instance), "instance")]
                    : info.Type == typeof(InstanceId) ? [(typeof(InstanceId.Vehicle), "vehicle")] : [];
            if (variants.Length == 0) return;
            info.PolymorphismOptions = new() { TypeDiscriminatorPropertyName = "$kind" };
            foreach (var variant in variants) info.PolymorphismOptions.DerivedTypes.Add(new(variant.Type, variant.Name));
        });
        return new() { TypeInfoResolver = resolver };
    }
    private static string Event(EventEnvelope envelope) => JsonSerializer.Serialize(new
        { envelope.Position, envelope.Ordinal, Kind = envelope.Event.GetType().Name, Value = JsonSerializer.Serialize(envelope.Event, envelope.Event.GetType()) });
}

internal sealed record VehicleWorkDiskRecording(string InitialState, ImmutableArray<RecordedCommand> Commands,
    ImmutableArray<CoreCheckpoint> Checkpoints, SimulationPosition EndPosition, SimulationPosition[] Positions);
internal sealed record VehicleWorkProcessSample(SimulationPosition Position, string Payload, string CanonicalText, string Sha256,
    string Queries, string Events);
internal sealed record VehicleWorkProcessResult(int ProcessId, VehicleWorkProcessSample[] Samples);
