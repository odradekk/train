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
    internal static readonly CoreContentProjection BuildingOriginal = WithBuilding(Content(rate: 30));
    internal static readonly CoreContentProjection BuildingUpdated = Changed(BuildingOriginal) with
    { ConstructionRules = [BuildingOriginal.ConstructionRules[0] with { ConstructionWorkPerItem = 100, PowerPerWork = 100, LaborPerWork = 100 }] };

    internal static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var mode = args[0];
            var output = args[1];
            var directory = args[2];
            var building = args.Length > 3 && args[3] == "building";
            var original = building ? BuildingOriginal : Original;
            var updated = building ? BuildingUpdated : Updated;
            var factory = CoreFactory.Create();
            var samples = new List<ConstructionProcessSample>();
            var events = new List<string>();
            if (mode == "replay")
            {
                var disk = JsonSerializer.Deserialize<CoreDiskRecording>(await File.ReadAllTextAsync(Path.Combine(directory, "recording.json")), RecordingJson)!;
                Assert.IsInstanceOfType<WorldObjectId.Job>(disk.Commands[0].Decision.Created.Single());
                var prepared = factory.PrepareReplay(new(2, disk.Runtime, disk.InitialState,
                    disk.Commands.Select(c => new RecordedCommand(c.Source, c.Payload, new(c.Number, c.Before, c.After, c.Decision),
                        c.InputBefore, c.InputAfter)).ToImmutableArray(), disk.Checkpoints.ToImmutableArray(), disk.EndPosition, null),
                    WorldFixture.Options.Runtime, [original, updated]);
                Assert.IsInstanceOfType<CoreResult<ICoreReplay>.Success>(prepared,
                    prepared is CoreResult<ICoreReplay>.Failure failure
                        ? string.Join("; ", failure.Problems.Select(p => $"{p.Code}: {p.Field}")) : null);
                using var replay = ((CoreResult<ICoreReplay>.Success)prepared).Value;
                foreach (var position in disk.Positions)
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
                using var session = mode == "resume" ? Restore() : Session(original);
                if (mode != "resume")
                {
                    if (building) StartBuilding(session, original);
                    else Start(session, Build(original, Local));
                    Step(session);
                    Accept(session, new CoreCommand.PublishContent(updated));
                }
                samples.Add(Sample(WorldFixture.Success(session.Capture(CapturePurpose.Save))));
                if (mode == "save")
                    await File.WriteAllTextAsync(Path.Combine(directory, "state.json"), samples[0].Payload, new UTF8Encoding(false));
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

            ICoreSession Restore()
            {
                var decoded = WorldFixture.Success(factory.DecodeState(File.ReadAllText(Path.Combine(directory, "state.json"))));
                using var candidate = WorldFixture.Success(factory.PrepareRestore(decoded, updated, WorldFixture.Options));
                return candidate.Activate();
            }
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
    }

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

    private static void StartBuilding(ICoreSession session, CoreContentProjection content)
    {
        var plot = ((CoreSession)session).State.World.Settlements.Single().Plots.Single(p => p.Building is null).Id;
        var design = content.Designs.Single(d => d.Design == new DesignHash(new string('2', 64)));
        Start(session, new CoreCommand.StartOwnedJob(new JobRequest.BuildBuilding(
            Facility, plot, new(content.Identity, design, []), [Local], 0)));
    }

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
            if (info.Type != typeof(WorldObjectId)) return;
            info.PolymorphismOptions = new() { TypeDiscriminatorPropertyName = "$kind" };
            info.PolymorphismOptions.DerivedTypes.Add(new(typeof(WorldObjectId.Job), "job"));
        });
        return new() { TypeInfoResolver = resolver };
    }
    private static string Event(EventEnvelope envelope) => JsonSerializer.Serialize(new
    { envelope.Position, envelope.Ordinal, Kind = envelope.Event.GetType().Name, Value = JsonSerializer.Serialize(envelope.Event, envelope.Event.GetType()) });
}

internal sealed record ConstructionProcessSample(SimulationPosition Position, string Payload, string CanonicalText, string Sha256);
internal sealed record ConstructionProcessResult(int ProcessId, ConstructionProcessSample[] Samples, string[] Events);
