using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.Sessions;
using Train.Core.Tests.Sessions;
using Train.Core.Tests.World;
using static Train.Core.Tests.Construction.ConstructionFixture;

namespace Train.Core.Tests.Construction;

internal static class ConstructionProcessDriver
{
    private static readonly JsonSerializerOptions RecordingJson = CreateRecordingJson();
    internal static CoreContentProjection Original => Content(rate: 30);
    internal static CoreContentProjection Updated => Changed(Original) with
    { ConstructionRules = [Original.ConstructionRules[0] with { ConstructionWorkPerItem = 100, PowerPerWork = 100, LaborPerWork = 100 }] };
    internal static readonly CoreContentProjection BuildingOriginal = WithBuilding(Content());
    private static readonly GoodId AssemblyGood = new GoodId.Assembly(new(new string('1', 64)));

    internal static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var mode = args[0];
            var output = args[1];
            var directory = args[2];
            var building = args.Length > 3 && args[3] == "building";
            var failing = args.Length > 3 && args[3] == "failure";
            var original = building ? BuildingOriginal : Original;
            var factory = CoreFactory.Create();
            var samples = new List<ConstructionProcessSample>();
            var events = new List<string>();
            if (mode == "replay")
            {
                SimulationPosition[] positions;
                CoreReplayInput replayInput;
                if (building)
                {
                    var disk = JsonSerializer.Deserialize<VehicleWorkDiskRecording>(await File.ReadAllTextAsync(Path.Combine(directory, "recording.json")), RecordingJson)!;
                    Assert.IsInstanceOfType<CommandDecision.Rejected>(disk.Commands[0].Result.Decision);
                    Assert.IsTrue(disk.Commands.Any(c => c.CanonicalPayload.Contains("\"kind\":\"build_equipment\"", StringComparison.Ordinal)));
                    Assert.IsTrue(disk.Commands.Any(c => c.CanonicalPayload.Contains("\"kind\":\"dismantle_building\"", StringComparison.Ordinal)));
                    Assert.IsTrue(disk.Commands.Any(c => c.CanonicalPayload.Contains("\"kind\":\"construction.set_deconstruction_paused\"", StringComparison.Ordinal)));
                    positions = disk.Positions;
                    replayInput = new(2, WorldFixture.Options.Runtime, disk.InitialState, disk.Commands, disk.Checkpoints, disk.EndPosition, null);
                }
                else
                {
                    var disk = JsonSerializer.Deserialize<CoreDiskRecording>(await File.ReadAllTextAsync(Path.Combine(directory, "recording.json")), RecordingJson)!;
                    Assert.IsInstanceOfType<WorldObjectId.Job>(disk.Commands[0].Decision.Created.Single());
                    positions = disk.Positions;
                    replayInput = new(2, disk.Runtime, disk.InitialState,
                        disk.Commands.Select(c => new RecordedCommand(c.Source, c.Payload, new(c.Number, c.Before, c.After, c.Decision),
                            c.InputBefore, c.InputAfter)).ToImmutableArray(), disk.Checkpoints.ToImmutableArray(), disk.EndPosition, null);
                }
                var prepared = factory.PrepareReplay(replayInput, WorldFixture.Options.Runtime, building ? [original] : [original, Updated]);
                Assert.IsInstanceOfType<CoreResult<ICoreReplay>.Success>(prepared,
                    prepared is CoreResult<ICoreReplay>.Failure failure
                        ? string.Join("; ", failure.Problems.Select(p => $"{p.Code}: {p.Field}")) : null);
                using var replay = ((CoreResult<ICoreReplay>.Success)prepared).Value;
                foreach (var position in positions)
                {
                    var result = replay.Advance(uint.MaxValue, position);
                    Assert.AreEqual(ReplayStop.RequestedPosition, result.Stop);
                    Assert.IsNull(result.Difference);
                    Assert.IsNull(result.Fault);
                    if (samples.Count > 0) events.AddRange(result.Events.Select(Event));
                    samples.Add(Sample(replay.CaptureBoundary()));
                }
                Assert.AreEqual(ReplayStop.EndOfRecording, replay.Advance(1).Stop);
            }
            else
            {
                using var session = mode == "resume" ? Restore(building || failing ? original : Updated) : Session(original);
                if (mode != "resume")
                {
                    if (building) BeginBuilding(session, original);
                    else if (failing)
                    {
                        Start(session, Build(original, Local));
                        Step(session);
                    }
                    else
                    {
                        Start(session, Build(original, Local));
                        Step(session);
                        Accept(session, new CoreCommand.PublishContent(Updated));
                    }
                }
                samples.Add(Sample(WorldFixture.Success(session.Capture(CapturePurpose.Save))));
                if (mode == "save")
                    await File.WriteAllTextAsync(Path.Combine(directory, "state.json"), samples[0].Payload, new UTF8Encoding(false));
                else if (failing)
                {
                    // 保存边界之后发布去掉施工能力的内容：进行中的任务失败并释放设施，恢复的进程必须得到同样的失败。
                    events.AddRange(Accept(session, new CoreCommand.PublishContent(Disqualified(original))).Events.Select(Event));
                    for (var step = 0; step < 2; step++)
                    {
                        events.AddRange(Step(session).Events.Select(Event));
                        samples.Add(Sample(WorldFixture.Success(session.Capture(CapturePurpose.Save))));
                    }
                    Assert.IsInstanceOfType<Lookup<JobView>.Missing>(session.Read().FindJob(new(1)));
                }
                else if (building)
                {
                    FinishBuilding(session, original);
                    if (mode == "complete") await WriteBuildingRecording(session, samples);
                }
                else
                {
                    for (var step = 0; step < 3; step++)
                    {
                        events.AddRange(Step(session).Events.Select(Event));
                        samples.Add(Sample(WorldFixture.Success(session.Capture(CapturePurpose.Save))));
                    }
                    if (mode == "complete")
                    {
                        var recording = session.ReadRecording();
                        var disk = new CoreDiskRecording(WorldFixture.Options.Runtime, recording.Starts[0].State.Encode(),
                            recording.Commands.Select(c => new CoreDiskCommand(c.Source, c.CanonicalPayload, c.Result.Number,
                                c.Result.Before, c.Result.After, (CommandDecision.Accepted)c.Result.Decision, c.InputBefore, c.InputAfter)).ToArray(),
                            recording.Checkpoints.ToArray(), recording.EndPosition, samples.Select(s => s.Position).ToArray());
                        await File.WriteAllTextAsync(Path.Combine(directory, "recording.json"), JsonSerializer.Serialize(disk, RecordingJson), new UTF8Encoding(false));
                    }
                }
            }
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new ConstructionProcessResult(Environment.ProcessId,
                samples.ToArray(), events.ToArray())), new UTF8Encoding(false));
            return 0;

            ICoreSession Restore(CoreContentProjection content)
            {
                var decoded = WorldFixture.Success(factory.DecodeState(File.ReadAllText(Path.Combine(directory, "state.json"))));
                using var candidate = WorldFixture.Success(factory.PrepareRestore(decoded, content, WorldFixture.Options));
                return candidate.Activate();
            }
            void FinishBuilding(ICoreSession session, CoreContentProjection content)
            {
                events.AddRange(Step(session).Events.Select(Event));
                samples.Add(Sample(WorldFixture.Success(session.Capture(CapturePurpose.Save))));
                var plot = ((CoreSession)session).State.World.Settlements.Single().Plots.Single(p => p.Building is null).Id;
                events.AddRange(Accept(session, new CoreCommand.StartOwnedJob(new JobRequest.BuildBuilding(
                    Facility, plot, BuildingDesign(content), [Local], 0))).Events.Select(Event));
                events.AddRange(Step(session, 2).Events.Select(Event));
                samples.Add(Sample(WorldFixture.Success(session.Capture(CapturePurpose.Save))));
                var built = ((CoreSession)session).State.World.Instances.Single(i => i.Id is InstanceId.Building id && id.Id != Facility.Building);
                var started = Accept(session, new CoreCommand.StartOwnedJob(new JobRequest.Dismantle(Facility, built.Id, 0)));
                events.AddRange(started.Events.Select(Event));
                var dismantle = started.Events.Select(e => e.Event).OfType<CoreEvent.ConstructionStarted>().Single().Job;
                events.AddRange(Accept(session, new CoreCommand.SetDeconstructionPaused(dismantle.Id, true)).Events.Select(Event));
                events.AddRange(Step(session).Events.Select(Event));
                Assert.AreEqual(0d, Job(session.Read(), dismantle.Id).ProgressWork);
                Assert.AreEqual(ConstructionState.Paused, Job(session.Read(), dismantle.Id).State);
                samples.Add(Sample(WorldFixture.Success(session.Capture(CapturePurpose.Save))));
                events.AddRange(Accept(session, new CoreCommand.SetDeconstructionPaused(dismantle.Id, false)).Events.Select(Event));
                events.AddRange(Step(session, 2).Events.Select(Event));
                samples.Add(Sample(WorldFixture.Success(session.Capture(CapturePurpose.Save))));
                Assert.AreEqual(9L, Stock(session, Local));
                Assert.AreEqual(1L, AssemblyCount(session));
                Assert.AreEqual(0L, ((Lookup<InventoryView>.Found)session.Read().FindInventory(Local)).Value.Reserved);
            }
            Task WriteBuildingRecording(ICoreSession session, List<ConstructionProcessSample> taken)
            {
                var recording = session.ReadRecording();
                var disk = new VehicleWorkDiskRecording(recording.Starts[0].State.Encode(), recording.Commands,
                    recording.Checkpoints, recording.EndPosition, taken.Select(s => s.Position).ToArray());
                return File.WriteAllTextAsync(Path.Combine(directory, "recording.json"), JsonSerializer.Serialize(disk, RecordingJson), new UTF8Encoding(false));
            }
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
    }

    private static CoreContentProjection Disqualified(CoreContentProjection content) => Changed(content) with
    {
        Designs = content.Designs.Select(d => d.Design != WorldFixture.Station ? d : d with
        {
            Parts = d.Parts.Select(p => p with { Capabilities = p.Capabilities.Where(c => c is not PartCapability.Construction)
                .ToImmutableArray() }).ToImmutableArray()
        }).ToImmutableArray()
    };

    private static CoreContentProjection WithBuilding(CoreContentProjection content)
    {
        var template = content.SettlementTemplates[0];
        var number = template.Plots.Max(p => p.Number) + 1;
        var basis = content.Designs.First(d => d.Kind is DesignKind.Vehicle);
        var design = basis with
        {
            Design = new(new string('2', 64)),
            Kind = new DesignKind.Building(PlotSize.Small, BuildingKind.Production, []),
            Parts = [basis.Parts[0] with { Capabilities = [] }],
            BillOfMaterials = [new(Material, 2)],
            BillOfMaterialsItemCount = 2
        };
        return content with
        {
            Designs = content.Designs.Add(design),
            SettlementTemplates = [template with { Plots = template.Plots.Add(new(number, PlotSize.Small, null)) }]
        };
    }

    private static void BeginBuilding(ICoreSession session, CoreContentProjection content)
    {
        var rejected = Submit(session, new CoreCommand.StartOwnedJob(new JobRequest.BuildBuilding(
            Facility, new(999), BuildingDesign(content), [Local], 0)));
        Assert.AreEqual("object_missing", ((CommandDecision.Rejected)rejected.Receipt.Decision).Problems.Single().Code);
        Start(session, new CoreCommand.StartOwnedJob(new JobRequest.BuildEquipment(
            Facility, EquipmentDesign(content), [Local], 0)));
        Step(session);
        Assert.AreEqual(1d, Job(session.Read(), new(1)).ProgressWork, 1e-12);
        Assert.AreEqual(8L, Stock(session, Local));
        Assert.AreEqual(0L, AssemblyCount(session));
    }

    private static DesignInput BuildingDesign(CoreContentProjection content)
        => new(content.Identity, content.Designs.Single(d => d.Design == new DesignHash(new string('2', 64))), []);

    private static DesignInput EquipmentDesign(CoreContentProjection content) => new(content.Identity,
        new(new(new string('3', 64)), new(new string('1', 64)), new DesignKind.LargeEquipment(AssemblyGood), [],
            0, 0, 0, new(0), [], [new(Material, 2)], 2, [], true, []),
        [new(AssemblyGood, WorldFixture.Revision, new(1), 1, false, null)]);

    private static long AssemblyCount(ICoreSession session)
        => ((Lookup<InventoryView>.Found)session.Read().FindInventory(Local)).Value.Stacks
            .Where(s => s.Good == AssemblyGood).Sum(s => s.Quantity);

    private static ConstructionProcessSample Sample(ICoreCapture capture)
    {
        var payload = capture.Encode();
        var decoded = CoreFactory.Create().DecodeState(payload);
        Assert.IsInstanceOfType<CoreResult<ICoreCapture>.Success>(decoded,
            decoded is CoreResult<ICoreCapture>.Failure failure
                ? string.Join("; ", failure.Problems.Select(p => $"{p.Code}: {p.Field} at {capture.Position}")) : null);
        return new(capture.Position, payload, capture.Checkpoint.CanonicalText, capture.Checkpoint.Sha256);
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
                    : info.Type == typeof(InstanceId)
                        ? [(typeof(InstanceId.Vehicle), "vehicle"), (typeof(InstanceId.Building), "building")] : [];
            if (variants.Length == 0) return;
            info.PolymorphismOptions = new() { TypeDiscriminatorPropertyName = "$kind" };
            foreach (var variant in variants) info.PolymorphismOptions.DerivedTypes.Add(new(variant.Type, variant.Name));
        });
        return new() { TypeInfoResolver = resolver };
    }
    private static string Event(EventEnvelope envelope) => JsonSerializer.Serialize(new
    { envelope.Position, envelope.Ordinal, Kind = envelope.Event.GetType().Name, Value = JsonSerializer.Serialize(envelope.Event, envelope.Event.GetType()) });
}

internal sealed record ConstructionProcessSample(SimulationPosition Position, string Payload, string CanonicalText, string Sha256);
internal sealed record ConstructionProcessResult(int ProcessId, ConstructionProcessSample[] Samples, string[] Events);
