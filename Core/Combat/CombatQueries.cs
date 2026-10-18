using System.Collections.Immutable;
using Train.Core.Contracts;
using Train.Core.World;

namespace Train.Core.Combat;

internal static class CombatQueries
{
    internal static CombatView Read(WorldState world, CombatState combat) => new(combat.Id, combat.Location,
        combat.StartedAtStep, Participants(world, combat));

    internal static ImmutableArray<CombatParticipantView> Participants(WorldState world, CombatState combat)
        => combat.Controllers.SelectMany(controller => Units(world, controller.Controller).Select(unit =>
                new CombatParticipantView(unit, controller.Retreating, controller.RemainingDisengagementSteps,
                    controller.FocusTarget)))
            .OrderBy(participant => UnitKey(participant.Unit)).ToImmutableArray();

    internal static ImmutableArray<CombatUnitId> Units(WorldState world, CombatController controller)
        => controller switch
        {
            CombatController.Train train => world.Trains.Single(t => t.Id == train.Id).Vehicles
                .Select(vehicle => (CombatUnitId)new CombatUnitId.Instance(new InstanceId.Vehicle(vehicle)))
                .OrderBy(UnitKey).ToImmutableArray(),
            CombatController.Settlement settlement => SettlementUnits(world, settlement.Id),
            _ => throw new InvalidOperationException("战斗控制方种类无效。")
        };

    private static ImmutableArray<CombatUnitId> SettlementUnits(WorldState world, SettlementId id)
    {
        var settlement = world.Settlements.Single(s => s.Id == id);
        return settlement.Plots.Where(plot => plot.Building is not null)
            .Select(plot => (CombatUnitId)new CombatUnitId.Instance(new InstanceId.Building(plot.Building!.Value)))
            .Concat(settlement.ParkedVehicles.Select(vehicle =>
                (CombatUnitId)new CombatUnitId.Instance(new InstanceId.Vehicle(vehicle))))
            .OrderBy(UnitKey).ToImmutableArray();
    }

    internal static bool IsInCombat(WorldState world, InstanceId id)
        => ControllerOf(world, id) is { } controller
            && world.Combats.Any(combat => combat.Controllers.Any(state => state.Controller == controller)
                && SameLocation(combat.Location, Location(world, controller)));

    internal static CombatController? ControllerOf(WorldState world, InstanceId id)
    {
        var instance = world.Instances.SingleOrDefault(i => i.Id == id);
        if (instance is null) return null;
        return instance.Details switch
        {
            InstanceDetails.Vehicle { Train: { } train } => new CombatController.Train(train),
            InstanceDetails.Vehicle { Train: null } => new CombatController.Settlement(
                world.Settlements.Single(s => s.ParkedVehicles.Contains(((InstanceId.Vehicle)id).Id)).Id),
            InstanceDetails.Building building => new CombatController.Settlement(
                world.Settlements.Single(s => s.Plots.Any(plot => plot.Id == building.Plot)).Id),
            _ => throw new InvalidOperationException("实例所属不明。")
        };
    }

    internal static WorldLocation Location(WorldState world, CombatController controller) => controller switch
    {
        CombatController.Train train => WorldState.Location(world.Trains.Single(t => t.Id == train.Id).Position),
        CombatController.Settlement settlement => new WorldLocation.Node(
            world.Settlements.Single(s => s.Id == settlement.Id).Node),
        _ => throw new InvalidOperationException("战斗控制方种类无效。")
    };

    internal static CombatOwner Owner(WorldState world, CombatController controller) => controller switch
    {
        CombatController.Train => new CombatOwner.Player(),
        CombatController.Settlement settlement => world.Settlements.Single(s => s.Id == settlement.Id).Relation
            == SettlementRelation.PlayerOwned ? new CombatOwner.Player() : new CombatOwner.Settlement(settlement.Id),
        _ => throw new InvalidOperationException("战斗控制方种类无效。")
    };

    internal static CombatState? Find(WorldState world, WorldLocation location)
        => world.Combats.FirstOrDefault(combat => SameLocation(combat.Location, location));

    internal static (CombatState Combat, int Index)? Controller(WorldState world, CombatController controller)
    {
        foreach (var combat in world.Combats)
            for (var index = 0; index < combat.Controllers.Length; index++)
                if (combat.Controllers[index].Controller == controller) return (combat, index);
        return null;
    }

    internal static bool IsParticipant(WorldState world, CombatState combat, CombatUnitId unit)
        => combat.Controllers.Any(controller => Units(world, controller.Controller).Contains(unit));

    internal static CombatOwner OwnerOfUnit(WorldState world, CombatUnitId unit) => unit switch
    {
        CombatUnitId.Instance instance => ControllerOf(world, instance.Id) is { } controller
            ? Owner(world, controller) : throw new InvalidOperationException("参战实例所属不明。"),
        CombatUnitId.CreatureGroup => new CombatOwner.Creatures(),
        _ => throw new InvalidOperationException("战斗单位种类无效。")
    };

    internal static bool Hostile(WorldState world, CombatUnitId left, CombatUnitId right)
    {
        var leftOwner = OwnerOfUnit(world, left);
        var rightOwner = OwnerOfUnit(world, right);
        if (leftOwner is CombatOwner.Creatures || rightOwner is CombatOwner.Creatures)
            return leftOwner is not CombatOwner.Creatures || rightOwner is not CombatOwner.Creatures;
        return (leftOwner, rightOwner) switch
        {
            (CombatOwner.Player, CombatOwner.Settlement settlement) => world.Settlements
                .Single(s => s.Id == settlement.Id).Relation == SettlementRelation.Hostile,
            (CombatOwner.Settlement settlement, CombatOwner.Player) => world.Settlements
                .Single(s => s.Id == settlement.Id).Relation == SettlementRelation.Hostile,
            _ => false
        };
    }

    internal static bool Armed(WorldState world, CoreContentProjection content, CombatUnitId unit)
        => unit is CombatUnitId.Instance { Id: var id }
            && CombatRules.Armed(world, content, world.Instances.Single(i => i.Id == id));

    internal static CombatUnitId? Target(WorldObjectId target) => target switch
    {
        WorldObjectId.Instance instance => new CombatUnitId.Instance(instance.Id),
        WorldObjectId.CreatureGroup group => new CombatUnitId.CreatureGroup(group.Id),
        _ => null
    };

    internal static bool Exists(WorldState world, CombatUnitId unit) => unit switch
    {
        CombatUnitId.Instance instance => world.Instances.Any(i => i.Id == instance.Id),
        CombatUnitId.CreatureGroup => false,
        _ => false
    };

    internal static bool SameLocation(WorldLocation left, WorldLocation right) => (left, right) switch
    {
        (WorldLocation.Node a, WorldLocation.Node b) => a.Id == b.Id,
        (WorldLocation.Edge a, WorldLocation.Edge b) => a.Id == b.Id && a.DistanceMeters.Equals(b.DistanceMeters),
        _ => false
    };

    internal static (int Kind, ulong Id) UnitKey(CombatUnitId unit) => unit switch
    {
        CombatUnitId.Instance { Id: InstanceId.Building building } => (0, building.Id.Value),
        CombatUnitId.Instance { Id: InstanceId.Vehicle vehicle } => (1, vehicle.Id.Value),
        CombatUnitId.CreatureGroup group => (2, group.Id.Value),
        _ => throw new InvalidOperationException("战斗单位种类无效。")
    };

    internal static (int Kind, ulong Id) ControllerKey(CombatController controller) => controller switch
    {
        CombatController.Train train => (0, train.Id.Value),
        CombatController.Settlement settlement => (1, settlement.Id.Value),
        _ => throw new InvalidOperationException("战斗控制方种类无效。")
    };
}
