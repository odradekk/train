using System.Text.Json;
using Train.Core.Contracts;
using Train.Core.Sessions;
using Train.Core.Tests.Sessions;
using Train.Core.Tests.World;

namespace Train.Core.Tests.Railway;

internal static class RailwayProcessDriver
{
    internal static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var mode = args[0];
            var factory = CoreFactory.Create();
            ICoreSession session;
            if (mode == "resume")
            {
                var state = WorldFixture.Success(factory.DecodeState(await File.ReadAllTextAsync(args[2])));
                using var candidate = WorldFixture.Success(factory.PrepareRestore(state, Content(), new(CoreProcessDriver.Identity(), false, 1)));
                session = candidate.Activate();
            }
            else
            {
                session = new CoreSession(Prepared());
                var player = Player(session);
                Accept(session, new CoreCommand.SetAutomaticRailRepair(new(1), player));
                Accept(session, new CoreCommand.ExpandRail(new(1), player));
                Accept(session, new CoreCommand.DebugAddRailBreak(new(1), 1, 0.5));
                for (var step = 0; step < PrefixSteps; step++)
                {
                    var tick = session.Advance(1);
                    if (tick.Fault is not null) throw new InvalidOperationException(tick.Fault.Code);
                }
            }
            using (session)
            {
                var samples = new List<RailwayProcessSample> { Sample(session) };
                var events = new List<string>();
                if (mode == "save")
                    await File.WriteAllTextAsync(args[2], samples[0].Payload);
                else
                {
                    var add = ((CoreSession)session).State.World.RailwayJobs.Single(job => job.Kind == RailwayWorkKind.AddTrack);
                    var steps = add.FrozenDurationTicks - add.ElapsedTicks;
                    for (ulong step = 0; step < steps; step++)
                    {
                        var result = session.Advance(1);
                        if (result.Fault is not null) throw new InvalidOperationException(result.Fault.Code);
                        events.AddRange(result.Events.Select(item => item.Event.GetType().Name));
                        samples.Add(Sample(session));
                    }
                }
                await File.WriteAllTextAsync(args[1], JsonSerializer.Serialize(new RailwayProcessResult(
                    Environment.ProcessId, CoreProcessDriver.Identity(), samples.ToArray(), events.ToArray())));
            }
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    internal const int PrefixSteps = 10;

    private static CoreState Prepared() => RailwayWorkFixture.Prepared(1, 1, 1, 1, 2, 1, 1);

    private static CoreContentProjection Content() => Prepared().Content;

    private static SettlementId Player(ICoreSession session)
        => RailwayWorkFixture.Player(((CoreSession)session).State).Id;

    private static void Accept(ICoreSession session, CoreCommand command)
    {
        var result = (SubmissionResult.Completed)session.Submit(command, CommandSource.AutomationTest);
        if (result.Receipt.Decision is not CommandDecision.Accepted)
            throw new InvalidOperationException(result.Receipt.Decision.ToString());
    }

    private static RailwayProcessSample Sample(ICoreSession session)
    {
        var capture = WorldFixture.Success(session.Capture(CapturePurpose.Save));
        return new(capture.Position, capture.Encode(), capture.Checkpoint.CanonicalText, capture.Checkpoint.Sha256);
    }
}

internal sealed record RailwayProcessSample(SimulationPosition Position, string Payload, string CanonicalText, string Sha256);
internal sealed record RailwayProcessResult(int ProcessId, RuntimeIdentity Runtime, RailwayProcessSample[] Samples, string[] Events);
