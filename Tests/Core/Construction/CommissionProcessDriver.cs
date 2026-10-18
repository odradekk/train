using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.Tests.World;
using static Train.Core.Tests.Construction.ConstructionFixture;

namespace Train.Core.Tests.Construction;

internal static class CommissionProcessDriver
{
    internal static CoreContentProjection Original => CommissionFixture.Content(rate: 30);
    internal static CoreContentProjection Updated => Changed(Original) with
    {
        ConstructionRules = [Original.ConstructionRules[0] with { ConstructionWorkPerItem = 100 }],
        MarketRules = [Original.MarketRules[0] with { CommissionMoneyPerWork = new(9) }]
    };

    internal static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var (mode, output, directory) = (args[0], args[1], args[2]);
            var factory = CoreFactory.Create();
            var samples = new List<ConstructionProcessSample>();
            var events = new List<string>();
            if (mode == "replay")
            {
                var disk = JsonSerializer.Deserialize<CommissionDiskRecording>(await File.ReadAllTextAsync(Path.Combine(directory, "recording.json")), Json)!;
                Assert.IsTrue(disk.Commands.Any(c => c.Decision is CommandDecision.Rejected));
                using var replay = WorldFixture.Success(factory.PrepareReplay(new(2, WorldFixture.Options.Runtime, disk.InitialState,
                    disk.Commands.Select(c => new RecordedCommand(c.Source, c.Payload, new(c.Number, c.Before, c.After, c.Decision),
                        c.InputBefore, c.InputAfter)).ToImmutableArray(), disk.Checkpoints.ToImmutableArray(), disk.EndPosition, null),
                    WorldFixture.Options.Runtime, [Original, Updated]));
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
                using var session = mode == "resume" ? Restore() : CommissionFixture.Session(Original);
                if (mode != "resume")
                {
                    Start(session, CommissionFixture.Commission(Original));
                    Start(session, CommissionFixture.Commission(Original));
                    var rejected = Submit(session, new CoreCommand.CommissionVehicleWork(CommissionFixture.BuildRequest(Original), new(0)));
                    Assert.IsInstanceOfType<CommandDecision.Rejected>(rejected.Receipt.Decision);
                    Step(session);
                    Accept(session, new CoreCommand.PublishContent(Updated));
                }
                samples.Add(Sample(WorldFixture.Success(session.Capture(CapturePurpose.Save))));
                if (mode == "save") await File.WriteAllTextAsync(Path.Combine(directory, "state.json"), samples[0].Payload);
                else
                {
                    for (var step = 0; step < 8; step++)
                    {
                        events.AddRange(Step(session).Events.Select(Event));
                        samples.Add(Sample(WorldFixture.Success(session.Capture(CapturePurpose.Save))));
                    }
                    if (mode == "complete")
                    {
                        var recording = session.ReadRecording();
                        var disk = new CommissionDiskRecording(recording.Starts[0].State.Encode(), recording.Commands.Select(c =>
                            new CommissionDiskCommand(c.Source, c.CanonicalPayload, c.Result.Number, c.Result.Before, c.Result.After,
                                c.Result.Decision, c.InputBefore, c.InputAfter)).ToArray(), recording.Checkpoints.ToArray(),
                            recording.EndPosition, samples.Select(s => s.Position).ToArray());
                        await File.WriteAllTextAsync(Path.Combine(directory, "recording.json"), JsonSerializer.Serialize(disk, Json));
                    }
                }
            }
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new ConstructionProcessResult(Environment.ProcessId, samples.ToArray(), events.ToArray())));
            return 0;

            ICoreSession Restore()
            {
                var state = WorldFixture.Success(factory.DecodeState(File.ReadAllText(Path.Combine(directory, "state.json"))));
                using var candidate = WorldFixture.Success(factory.PrepareRestore(state, Updated, WorldFixture.Options));
                return candidate.Activate();
            }
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
    }

    private static ConstructionProcessSample Sample(ICoreCapture capture)
    {
        var payload = capture.Encode();
        WorldFixture.Success(CoreFactory.Create().DecodeState(payload));
        return new(capture.Position, payload, capture.Checkpoint.CanonicalText, capture.Checkpoint.Sha256);
    }
    private static readonly JsonSerializerOptions Json = CreateJson();
    private static JsonSerializerOptions CreateJson()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info =>
        {
            if (info.Type == typeof(WorldObjectId))
            {
                info.PolymorphismOptions = new() { TypeDiscriminatorPropertyName = "$kind" };
                info.PolymorphismOptions.DerivedTypes.Add(new(typeof(WorldObjectId.Job), "job"));
            }
            if (info.Type == typeof(CommandDecision))
            {
                info.PolymorphismOptions = new() { TypeDiscriminatorPropertyName = "$kind" };
                info.PolymorphismOptions.DerivedTypes.Add(new(typeof(CommandDecision.Accepted), "accepted"));
                info.PolymorphismOptions.DerivedTypes.Add(new(typeof(CommandDecision.Rejected), "rejected"));
            }
        });
        return new() { TypeInfoResolver = resolver };
    }
    private static string Event(EventEnvelope envelope) => JsonSerializer.Serialize(new
    { envelope.Position, envelope.Ordinal, Kind = envelope.Event.GetType().Name, Value = JsonSerializer.Serialize(envelope.Event, envelope.Event.GetType()) });
}

internal sealed record CommissionDiskCommand(CommandSource Source, string Payload, ulong Number,
    SimulationPosition Before, SimulationPosition After, CommandDecision Decision, CoreInputId InputBefore, CoreInputId InputAfter);
internal sealed record CommissionDiskRecording(string InitialState, CommissionDiskCommand[] Commands,
    CoreCheckpoint[] Checkpoints, SimulationPosition EndPosition, SimulationPosition[] Positions);
