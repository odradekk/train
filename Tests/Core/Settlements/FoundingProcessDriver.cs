using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.Tests.Sessions;
using Train.Core.Tests.World;
using static Train.Core.Tests.Settlements.FoundingFixture;

namespace Train.Core.Tests.Settlements;

internal static class FoundingProcessDriver
{
    private static readonly JsonSerializerOptions RecordingJson = CreateRecordingJson();

    internal static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var mode = args[0];
            var directory = args[2];
            var factory = CoreFactory.Create();
            var samples = new List<FoundingProcessSample>();
            var events = new List<string>();
            if (mode == "replay")
            {
                var disk = JsonSerializer.Deserialize<CoreDiskRecording>(await File.ReadAllTextAsync(Path.Combine(directory, "recording.json")), RecordingJson)!;
                using var replay = WorldFixture.Success(factory.PrepareReplay(new(2, disk.Runtime, disk.InitialState,
                    disk.Commands.Select(c => new RecordedCommand(c.Source, c.Payload,
                        new(c.Number, c.Before, c.After, c.Decision), c.InputBefore, c.InputAfter)).ToImmutableArray(),
                    disk.Checkpoints.ToImmutableArray(), disk.EndPosition, null), WorldFixture.Options.Runtime, [Content]));
                foreach (var position in disk.Positions)
                {
                    var result = replay.Advance(uint.MaxValue, position);
                    Assert.AreEqual(ReplayStop.RequestedPosition, result.Stop);
                    Assert.IsNull(result.Difference);
                    Assert.IsNull(result.Fault);
                    if (samples.Count > 0) events.AddRange(result.Events.Select(Event));
                    samples.Add(Sample(replay.CaptureBoundary()));
                }
                var end = replay.Advance(1);
                Assert.AreEqual(ReplayStop.EndOfRecording, end.Stop);
                Assert.IsNull(end.Difference);
                Assert.IsNull(end.Fault);
            }
            else
            {
                using var session = mode == "resume" ? Restore() : Session(stock: 5);
                if (mode != "resume") { Start(session); Step(session); }
                samples.Add(Sample(WorldFixture.Success(session.Capture(CapturePurpose.Save))));
                if (mode == "save")
                    await File.WriteAllTextAsync(Path.Combine(directory, "state.json"), samples[0].Payload);
                else
                {
                    for (var i = 0; i < 3; i++)
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
                        await File.WriteAllTextAsync(Path.Combine(directory, "recording.json"), JsonSerializer.Serialize(disk, RecordingJson));
                    }
                }
            }
            await File.WriteAllTextAsync(args[1], JsonSerializer.Serialize(new FoundingProcessResult(Environment.ProcessId, samples.ToArray(), events.ToArray())));
            return 0;

            ICoreSession Restore()
            {
                var state = WorldFixture.Success(factory.DecodeState(File.ReadAllText(Path.Combine(directory, "state.json"))));
                using var candidate = WorldFixture.Success(factory.PrepareRestore(state, Content, WorldFixture.Options));
                return candidate.Activate();
            }
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
    }

    private static FoundingProcessSample Sample(ICoreCapture capture)
        => new(capture.Position, capture.Encode(), capture.Checkpoint.CanonicalText, capture.Checkpoint.Sha256);
    private static string Event(EventEnvelope envelope) => JsonSerializer.Serialize(new
    { envelope.Position, envelope.Ordinal, Kind = envelope.Event.GetType().Name, Value = JsonSerializer.Serialize(envelope.Event, envelope.Event.GetType()) });
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
}

internal sealed record FoundingProcessSample(SimulationPosition Position, string Payload, string CanonicalText, string Sha256);
internal sealed record FoundingProcessResult(int ProcessId, FoundingProcessSample[] Samples, string[] Events);
