using System.Text.Json;
using Train.Core.Contracts;
using Train.Core.Tests.World;
using static Train.Core.Tests.Settlements.PopulationFixture;

namespace Train.Core.Tests.Settlements;

internal static class PopulationProcessDriver
{
    internal static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var mode = args[0];
            var output = args[1];
            var statePath = args[2];
            var content = Content(Rule() with { StatisticsPeriodTicks = 4, BaseGrowthPerSecond = 1 }, Stock(100, 100));
            using var session = mode == "resume" ? Restore(await File.ReadAllTextAsync(statePath), content) : Start(content);
            if (mode != "resume") Advance(session, 5);
            var prefix = Capture(session);
            if (mode == "save") await File.WriteAllTextAsync(statePath, prefix.Encode());
            var samples = new List<string> { prefix.Encode() };
            var checkpoints = new List<CoreCheckpoint> { prefix.Checkpoint };
            var events = new List<string>();
            if (mode != "save")
                for (var i = 0; i < 9; i++)
                {
                    var advanced = session.Advance(1);
                    if (advanced.Fault is not null) throw new InvalidOperationException(advanced.Diagnostic?.Message);
                    events.AddRange(advanced.Events.Select(e => JsonSerializer.Serialize(new
                    { e.Position, e.Ordinal, Kind = e.Event.GetType().Name, Value = JsonSerializer.Serialize(e.Event, e.Event.GetType()) })));
                    var capture = Capture(session);
                    samples.Add(capture.Encode());
                    checkpoints.Add(capture.Checkpoint);
                }
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new PopulationProcessResult(Environment.ProcessId,
                samples.ToArray(), checkpoints.ToArray(), events.ToArray())));
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static ICoreSession Restore(string payload, CoreContentProjection content)
    {
        var decoded = WorldFixture.Success(CoreFactory.Create().DecodeState(payload));
        using var candidate = WorldFixture.Success(CoreFactory.Create().PrepareRestore(decoded, content, WorldFixture.Options));
        return candidate.Activate();
    }
}

internal sealed record PopulationProcessResult(int ProcessId, string[] Samples, CoreCheckpoint[] Checkpoints, string[] Events);
