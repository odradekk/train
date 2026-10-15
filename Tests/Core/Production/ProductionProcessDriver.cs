using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.Sessions;
using Train.Core.Tests.Sessions;
using Train.Core.Tests.World;
using static Train.Core.Tests.Production.ProductionFixture;

namespace Train.Core.Tests.Production;

internal static class ProductionProcessDriver
{
    internal static CoreContentProjection Original => Content(rate: 30, capacity: 10);
    internal static CoreContentProjection Updated => Changed(Original) with
    {
        Processes = [Original.Processes[0] with
        {
            Revision = new(new string('A', 64)),
            Details = new ProcessDetails.Production(new("fixture:production"), [new(Input, 3)], [new(Product, 7)])
        }]
    };
    internal static CoreContentProjection Expanded => Changed(Content(rate: 30, capacity: 20), 'E') with
    { Processes = Updated.Processes };

    internal static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var mode = args[0];
            var output = args[1];
            var directory = args[2];
            var runtime = CoreProcessDriver.Identity();
            if (mode == "replay") return await Replay(output, directory, runtime);
            var factory = CoreFactory.Create();
            using var candidate = mode is "resume" or "resume-buffer"
                ? WorldFixture.Success(factory.PrepareRestore(WorldFixture.Success(factory.DecodeState(
                    await File.ReadAllTextAsync(Path.Combine(directory, mode == "resume" ? "state.json" : "buffer.json")))),
                    Updated, WorldFixture.Options with { Runtime = runtime }))
                : WorldFixture.Success(factory.PrepareNew(WorldFixture.Input, Original, WorldFixture.Options with { Runtime = runtime }));
            using var session = candidate.Activate();
            var samples = new List<ProductionProcessSample>();
            var events = new List<string>();
            void Advance()
            {
                events.AddRange(Step(session).Events.Select(Event));
                Sample();
            }
            void PublishContent(CoreContentProjection content)
            {
                var result = (SubmissionResult.Completed)session.Submit(new CoreCommand.PublishContent(content), CommandSource.AutomationTest);
                Assert.IsInstanceOfType<CommandDecision.Accepted>(result.Receipt.Decision);
                events.AddRange(result.Events.Select(Event));
            }
            void Sample() => samples.Add(SampleOf(WorldFixture.Success(session.Capture(CapturePurpose.Save)), session.Read()));
            if (mode is not ("resume" or "resume-buffer"))
            {
                Step(session);
                PublishContent(Updated);
                Step(session);
            }
            events.Clear();
            Sample();
            if (mode == "save")
                await File.WriteAllTextAsync(Path.Combine(directory, "state.json"), samples[0].Payload, new UTF8Encoding(false));
            else
            {
                if (mode != "resume-buffer")
                {
                    for (var i = 0; i < 3; i++) Advance();
                    if (mode == "resume")
                        await File.WriteAllTextAsync(Path.Combine(directory, "buffer.json"), samples[^1].Payload, new UTF8Encoding(false));
                }
                PublishContent(Expanded);
                for (var i = 0; i < 6; i++) Advance();
                if (mode == "complete")
                {
                    var recording = session.ReadRecording();
                    var disk = new CoreDiskRecording(runtime, recording.Starts[0].State.Encode(),
                        recording.Commands.Select(c => new CoreDiskCommand(c.Source, c.CanonicalPayload, c.Result.Number,
                            c.Result.Before, c.Result.After, (CommandDecision.Accepted)c.Result.Decision, c.InputBefore, c.InputAfter)).ToArray(),
                        recording.Checkpoints.ToArray(), recording.EndPosition, samples.Select(s => s.Position).ToArray());
                    await File.WriteAllTextAsync(Path.Combine(directory, "recording.json"), JsonSerializer.Serialize(disk), new UTF8Encoding(false));
                }
            }
            await Write(output, samples, events);
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
    }

    private static async Task<int> Replay(string output, string directory, RuntimeIdentity runtime)
    {
        var disk = JsonSerializer.Deserialize<CoreDiskRecording>(await File.ReadAllTextAsync(Path.Combine(directory, "recording.json")))!;
        var input = new CoreReplayInput(2, disk.Runtime, disk.InitialState,
            disk.Commands.Select(c => new RecordedCommand(c.Source, c.Payload,
                new(c.Number, c.Before, c.After, c.Decision), c.InputBefore, c.InputAfter)).ToImmutableArray(),
            disk.Checkpoints.ToImmutableArray(), disk.EndPosition, null);
        using var replay = WorldFixture.Success(CoreFactory.Create().PrepareReplay(input, runtime, [Original, Updated, Expanded]));
        var samples = new List<ProductionProcessSample>();
        var events = new List<string>();
        foreach (var position in disk.Positions)
        {
            var result = replay.Advance(uint.MaxValue, position);
            Assert.AreEqual(ReplayStop.RequestedPosition, result.Stop);
            Assert.IsNull(result.Difference);
            Assert.IsNull(result.Fault);
            if (samples.Count > 0) events.AddRange(result.Events.Select(Event));
            samples.Add(SampleOf(replay.CaptureBoundary(), replay.Read()));
        }
        var end = replay.Advance(1);
        Assert.AreEqual(ReplayStop.EndOfRecording, end.Stop);
        Assert.IsNull(end.Difference);
        Assert.IsNull(end.Fault);
        await Write(output, samples, events);
        return 0;
    }

    private static ProductionProcessSample SampleOf(ICoreCapture capture, ICoreReadView view)
    {
        var facility = Read(view);
        var runtime = ((CoreCapture)capture).World.Settlements.Single().ProductionFacilities.Single();
        var inventory = ((Lookup<InventoryView>.Found)view.FindInventory(LocalInventory)).Value;
        return new(capture.Position, capture.Encode(), capture.Checkpoint.CanonicalText, capture.Checkpoint.Sha256,
            JsonSerializer.Serialize(new { Facility = facility, runtime.LastActualWorkPerSecond, runtime.LastLimits,
                runtime.LastPowerDemand, runtime.LastActualSupply, inventory.Used, inventory.Reserved, inventory.Available,
                Stacks = inventory.Stacks.Select(s => new { Good = JsonSerializer.Serialize(s.Good, s.Good.GetType()), s.Quantity, s.DefinitionMissing }) }));
    }

    private static string Event(EventEnvelope envelope) => JsonSerializer.Serialize(new
    { envelope.Position, envelope.Ordinal, Kind = envelope.Event.GetType().Name, Value = JsonSerializer.Serialize(envelope.Event, envelope.Event.GetType()) });

    private static Task Write(string output, List<ProductionProcessSample> samples, List<string> events)
        => File.WriteAllTextAsync(output, JsonSerializer.Serialize(new ProductionProcessResult(Environment.ProcessId,
            samples.ToArray(), events.ToArray())), new UTF8Encoding(false));
}

internal sealed record ProductionProcessSample(SimulationPosition Position, string Payload, string CanonicalText, string Sha256, string Query);
internal sealed record ProductionProcessResult(int ProcessId, ProductionProcessSample[] Samples, string[] Events);
