using System.Collections.Immutable;
using System.Text.Json;
using Train.Core.Contracts;
using Train.Core.Sessions;
using Train.Core.Tests.Sessions;
using Train.Core.Tests.World;
using Train.Core.World;
using static Train.Core.Tests.Railway.TrainDispatchFixture;

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
                var started = (SubmissionResult.Completed)session.Submit(new CoreCommand.ExpandRail(new(1), Player(session)), CommandSource.AutomationTest);
                if (started.Receipt.Decision is not CommandDecision.Accepted) throw new InvalidOperationException(started.Receipt.Decision.ToString());
            }
            using (session)
            {
                var samples = new List<RailwayProcessSample> { Sample(session) };
                var events = new List<string>();
                if (mode == "save")
                    await File.WriteAllTextAsync(args[2], samples[0].Payload);
                else
                {
                    var steps = ((CoreSession)session).State.World.RailwayJobs.Single().FrozenDurationTicks;
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

    private static CoreState Prepared()
    {
        var basis = State(1, 0, 1);
        var rule = basis.Content.RailwayRules[0] with { RailUnitsPerMeter = 1, BuildSecondsPerMeter = 1, RepairRailUnitsPerBreak = 1, RepairSecondsPerBreak = 1 };
        var world = Claim(basis.World);
        var player = world.Settlements.Single(settlement => settlement.Relation == SettlementRelation.PlayerOwned);
        return basis with
        {
            Content = basis.Content with { RailwayRules = [rule] },
            World = Stock(world, player.Id)
        };
    }

    private static CoreContentProjection Content() => Prepared().Content;

    private static WorldState Claim(WorldState world)
    {
        var settlement = world.Settlements.Single(item => item.Node == world.Nodes[0].Id) with { Relation = SettlementRelation.PlayerOwned };
        return world with
        {
            Settlements = world.Settlements.Select(item => item.Id == settlement.Id ? settlement : item).ToImmutableArray(),
            Nodes = world.Nodes.Select(node => node.Id != settlement.Node ? node : node with
                { Occupant = new NodeOccupant.Settlement(settlement.Id, SettlementRelation.PlayerOwned) }).ToImmutableArray(),
            Instances = world.Instances.Select(instance => instance.Id is InstanceId.Building building
                && settlement.Plots.Any(plot => plot.Building == building.Id)
                ? instance with { Ownership = SettlementRelation.PlayerOwned } : instance).ToImmutableArray()
        };
    }

    private static WorldState Stock(WorldState world, SettlementId settlement)
    {
        var owner = world.Settlements.Single(item => item.Id == settlement);
        var good = new GoodId.Voxel(WorldFixture.Material);
        return world with
        {
            Inventories = world.Inventories.Select(inventory => inventory.Id != owner.Inventory ? inventory : inventory with
            {
                Goods = inventory.Goods.Where(stack => stack.Good != good).Append(new GoodsQuantity(good, 5))
                    .OrderBy(stack => WorldReferences.GoodKey(stack.Good), StringComparer.Ordinal).ToImmutableArray()
            }).ToImmutableArray()
        };
    }

    private static SettlementId Player(ICoreSession session)
        => ((CoreSession)session).State.World.Settlements.Single(settlement => settlement.Relation == SettlementRelation.PlayerOwned).Id;

    private static RailwayProcessSample Sample(ICoreSession session)
    {
        var capture = WorldFixture.Success(session.Capture(CapturePurpose.Save));
        return new(capture.Position, capture.Encode(), capture.Checkpoint.CanonicalText, capture.Checkpoint.Sha256);
    }
}

internal sealed record RailwayProcessSample(SimulationPosition Position, string Payload, string CanonicalText, string Sha256);
internal sealed record RailwayProcessResult(int ProcessId, RuntimeIdentity Runtime, RailwayProcessSample[] Samples, string[] Events);
