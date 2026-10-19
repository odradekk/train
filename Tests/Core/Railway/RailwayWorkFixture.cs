using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.Sessions;
using Train.Core.Tests.World;
using Train.Core.World;
using static Train.Core.Tests.Railway.TrainDispatchFixture;

namespace Train.Core.Tests.Railway;

internal static class RailwayWorkFixture
{
    internal static CoreState Prepared(int tracks, double length, double units, double buildSeconds,
        long repairUnits, double repairSeconds, long stock = 20)
    {
        var basis = State(1, tracks, length);
        var rule = basis.Content.RailwayRules[0] with
        {
            RailUnitsPerMeter = units, BuildSecondsPerMeter = buildSeconds,
            RepairRailUnitsPerBreak = repairUnits, RepairSecondsPerBreak = repairSeconds
        };
        var world = Claim(basis.World);
        var player = world.Settlements.Single(settlement => settlement.Relation == SettlementRelation.PlayerOwned);
        return basis with
        {
            Content = basis.Content with { RailwayRules = [rule] },
            World = stock > 0 ? Stock(world, player.Id, stock) : world
        };
    }

    internal static WorldState Claim(WorldState world)
    {
        var settlement = world.Settlements.Single(item => item.Node == world.Nodes[0].Id);
        if (settlement.Relation == SettlementRelation.PlayerOwned) return world;
        var claimed = settlement with { Relation = SettlementRelation.PlayerOwned };
        return world with
        {
            Settlements = world.Settlements.Select(item => item.Id == claimed.Id ? claimed : item).ToImmutableArray(),
            Nodes = world.Nodes.Select(node => node.Id != claimed.Node ? node : node with
            {
                Occupant = new NodeOccupant.Settlement(claimed.Id, SettlementRelation.PlayerOwned)
            }).ToImmutableArray(),
            Instances = world.Instances.Select(instance => instance.Id is InstanceId.Building building
                && claimed.Plots.Any(plot => plot.Building == building.Id)
                ? instance with { Ownership = SettlementRelation.PlayerOwned } : instance).ToImmutableArray()
        };
    }

    internal static SettlementState Player(CoreState state)
        => state.World.Settlements.Single(settlement => settlement.Relation == SettlementRelation.PlayerOwned);

    internal static WorldState Stock(WorldState world, SettlementId settlement, long quantity)
    {
        var owner = world.Settlements.Single(item => item.Id == settlement);
        var good = new GoodId.Voxel(WorldFixture.Material);
        return world with
        {
            Inventories = world.Inventories.Select(inventory => inventory.Id != owner.Inventory ? inventory : inventory with
            {
                Goods = inventory.Goods.Where(stack => stack.Good != good).Append(new GoodsQuantity(good, quantity))
                    .OrderBy(stack => WorldReferences.GoodKey(stack.Good), StringComparer.Ordinal).ToImmutableArray()
            }).ToImmutableArray()
        };
    }

    internal static AdvanceResult Advance(ICoreSession session, ulong steps)
    {
        AdvanceResult? last = null;
        for (ulong step = 0; step < steps; step++) last = Step(session);
        return last ?? throw new AssertFailedException("没有推进。");
    }
}
