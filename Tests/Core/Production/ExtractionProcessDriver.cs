using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.Sessions;
using Train.Core.Tests.Sessions;
using Train.Core.Tests.World;

namespace Train.Core.Tests.Production;

internal static class ExtractionProcessDriver
{
    private static readonly JsonSerializerOptions ObservationJson = new()
    {
        Converters =
        {
            new AbstractValueConverter<GoodId>(), new AbstractValueConverter<ProcessRef>(),
            new AbstractValueConverter<NodeOccupant>(), new AbstractValueConverter<ResourceRenewal>(),
            new AbstractValueConverter<TrainPosition>(), new AbstractValueConverter<InventoryOwner>(),
            new AbstractValueConverter<WorldObjectId>(), new AbstractValueConverter<InstanceId>()
        }
    };

    private static CoreContentProjection Content(bool expanded = false)
    {
        var content = ExtractionFixture.Content(work: 2, capacity: expanded ? 100 : 1);
        var template = content.SettlementTemplates[0];
        var ore = new GoodId.Voxel(WorldFixture.Ore);
        return content with
        {
            Identity = new(new string(expanded ? 'E' : 'D', 64)),
            Goods = [..content.Goods, content.Goods[0] with { Good = ore }],
            Processes = [..content.Processes, new(new ProcessDetails.Extraction(new("fixture:forest"), new(ore, 2)),
                WorldFixture.Revision, 4, 0, 0, FacilityTag.Extraction, [WorldFixture.Plain])],
            SettlementTemplates = [template with
            {
                Plots = [..template.Plots, new(3, PlotSize.Small, WorldFixture.FactoryBuilding)],
                ProductionPlans = [..template.ProductionPlans,
                    new(3, CapabilityKind.Extraction, new ProcessRef.Production(new("fixture:forest")), true, 1)]
            }]
        };
    }

    private static WorldGenerationInput Input => WorldFixture.MultiInput with
    {
        Start = WorldFixture.Input.Start,
        Configuration = WorldFixture.MultiInput.Configuration with
        {
            TerrainBands = WorldFixture.Input.Configuration.TerrainBands,
            SettlementRatio = .1, LairRatio = 0, LairWeights = [],
            NpcInitialization = WorldFixture.Input.Configuration.NpcInitialization,
            StartConstraints = WorldFixture.Input.Configuration.StartConstraints,
            Resources = [new(WorldFixture.Material, [WorldFixture.Plain], 1, 2, 2, false, 0, .1, 1),
                new(WorldFixture.Ore, [WorldFixture.Plain], 1, 2, 2, true, 15, 1000, 0)]
        }
    };

    internal static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var mode = args[0];
            var output = args[1];
            var directory = args[2];
            var runtime = CoreProcessDriver.Identity();
            var runs = new List<ExtractionProcessRun>();
            if (mode == "replay")
            {
                var disk = JsonSerializer.Deserialize<CoreDiskRecording>(await File.ReadAllTextAsync(Path.Combine(directory, "recording.json")))!;
                var input = new CoreReplayInput(2, disk.Runtime, disk.InitialState,
                    disk.Commands.Select(c => new RecordedCommand(c.Source, c.Payload,
                        new(c.Number, c.Before, c.After, c.Decision), c.InputBefore, c.InputAfter)).ToImmutableArray(),
                    disk.Checkpoints.ToImmutableArray(), disk.EndPosition, null);
                using var replay = WorldFixture.Success(CoreFactory.Create().PrepareReplay(input, runtime, [Content(), Content(true)]));
                var samples = new List<ExtractionProcessSample>();
                var events = new List<string>();
                foreach (var position in disk.Positions)
                {
                    var result = replay.Advance(uint.MaxValue, position);
                    Assert.AreEqual(ReplayStop.RequestedPosition, result.Stop);
                    Assert.IsNull(result.Difference);
                    Assert.IsNull(result.Fault);
                    events.AddRange(result.Events.Select(Event));
                    samples.Add(Sample(replay.CaptureBoundary(), replay.Read()));
                }
                Assert.AreEqual(ReplayStop.EndOfRecording, replay.Advance(1).Stop);
                runs.Add(new(0, samples.ToArray(), events.ToArray()));
            }
            else foreach (var savedAt in mode == "resume" ? new[] { 2, 4 } : new[] { 0 })
            {
                var factory = CoreFactory.Create();
                using var candidate = savedAt == 0
                    ? WorldFixture.Success(factory.PrepareNew(Input, Content(), WorldFixture.Options with { Runtime = runtime }))
                    : WorldFixture.Success(factory.PrepareRestore(WorldFixture.Success(factory.DecodeState(
                        await File.ReadAllTextAsync(Path.Combine(directory, $"state-{savedAt}.json")))), Content(),
                        WorldFixture.Options with { Runtime = runtime }));
                using var session = candidate.Activate();
                var samples = new List<ExtractionProcessSample>();
                var events = new List<string>();
                if (savedAt > 0) samples.Add(Sample(WorldFixture.Success(session.Capture(CapturePurpose.Save)), session.Read()));
                for (var step = savedAt + 1; step <= 15; step++)
                {
                    if (step == 8)
                    {
                        var published = ProductionFixture.Publish(session, Content(true));
                        events.AddRange(published.Events.Select(Event));
                    }
                    events.AddRange(ProductionFixture.Step(session).Events.Select(Event));
                    var capture = WorldFixture.Success(session.Capture(CapturePurpose.Save));
                    samples.Add(Sample(capture, session.Read()));
                    if (savedAt == 0 && step is 2 or 4)
                        await File.WriteAllTextAsync(Path.Combine(directory, $"state-{step}.json"), capture.Encode(), new UTF8Encoding(false));
                }
                if (savedAt == 0)
                {
                    var recording = session.ReadRecording();
                    var disk = new CoreDiskRecording(runtime, recording.Starts[0].State.Encode(),
                        recording.Commands.Select(c => new CoreDiskCommand(c.Source, c.CanonicalPayload, c.Result.Number,
                            c.Result.Before, c.Result.After, (CommandDecision.Accepted)c.Result.Decision, c.InputBefore, c.InputAfter)).ToArray(),
                        samples.Select(s => new CoreCheckpoint(s.Position, s.CanonicalText, s.Sha256)).ToArray(), recording.EndPosition,
                        samples.Select(s => s.Position).ToArray());
                    await File.WriteAllTextAsync(Path.Combine(directory, "recording.json"), JsonSerializer.Serialize(disk), new UTF8Encoding(false));
                }
                runs.Add(new(savedAt, samples.ToArray(), events.ToArray()));
            }
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new ExtractionProcessResult(Environment.ProcessId, runs.ToArray())), new UTF8Encoding(false));
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
    }

    private static ExtractionProcessSample Sample(ICoreCapture capture, ICoreReadView view)
        => new(capture.Position, capture.Encode(), capture.Checkpoint.CanonicalText, capture.Checkpoint.Sha256,
            JsonSerializer.Serialize(new { view.World,
                Finite = ((Lookup<FacilityView>.Found)view.FindFacility(ExtractionFixture.Facility)).Value,
                Renewable = ((Lookup<FacilityView>.Found)view.FindFacility(new(new(3), CapabilityKind.Extraction))).Value,
                Inventory = ((Lookup<InventoryView>.Found)view.FindInventory(ProductionFixture.LocalInventory)).Value,
                ((CoreCapture)capture).World.ResourceRecovery, ((CoreCapture)capture).World.ResourceRegenerations }, ObservationJson));

    private static string Event(EventEnvelope envelope) => JsonSerializer.Serialize(new
    { envelope.Position, envelope.Ordinal, Kind = envelope.Event.GetType().Name,
        Value = JsonSerializer.Serialize(envelope.Event, envelope.Event.GetType(), ObservationJson) }, ObservationJson);

    private sealed class AbstractValueConverter<T> : JsonConverter<T> where T : class
    {
        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => throw new NotSupportedException();

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteString("kind", value.GetType().Name);
            writer.WritePropertyName("value");
            JsonSerializer.Serialize(writer, value, value.GetType(), options);
            writer.WriteEndObject();
        }
    }
}

internal sealed record ExtractionProcessSample(SimulationPosition Position, string Payload, string CanonicalText, string Sha256, string Query);
internal sealed record ExtractionProcessRun(int SavedAt, ExtractionProcessSample[] Samples, string[] Events);
internal sealed record ExtractionProcessResult(int ProcessId, ExtractionProcessRun[] Runs);
