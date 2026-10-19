using System.Collections.Immutable;
using Train.Core.Combat;
using Train.Core.Construction;
using Train.Core.Contracts;
using Train.Core.Railway;
using Train.Core.Sessions;
using Train.Core.World;

namespace Train.Core.Settlements;

internal static class SettlementWar
{
    internal static void Validate(CoreCommand command)
    {
        switch (command)
        {
            case CoreCommand.DeclareWar war:
                Identity(war.Settlement.Value, nameof(command));
                return;
            case CoreCommand.OccupySettlement occupy:
                Identity(occupy.Settlement.Value, nameof(command));
                Identity(occupy.PresentTrain.Value, nameof(command));
                return;
            default:
                throw new InvalidOperationException("战争命令种类无效。");
        }
    }

    internal static CommandEffect Execute(WorldState world, CoreContentProjection content,
        CoreCommand command, SimulationPosition boundary) => command switch
    {
        CoreCommand.DeclareWar war => Declare(world, content, war),
        CoreCommand.OccupySettlement occupy => Occupy(world, content, occupy, boundary),
        _ => throw new InvalidOperationException("战争命令种类无效。")
    };

    private static CommandEffect Declare(WorldState world, CoreContentProjection content, CoreCommand.DeclareWar command)
    {
        var settlement = world.Settlements.FirstOrDefault(item => item.Id == command.Settlement);
        var subject = new WorldObjectId.Settlement(command.Settlement);
        if (settlement is null) return Reject("object_missing", "Settlement", subject);
        if (settlement.Relation != SettlementRelation.NonHostile)
            return Reject("invalid_state", "Settlement.Relation", subject);
        if (world.ConstructionJobs.Any(job => job.Settlement == settlement.Id && job.Funding == JobFunding.NpcCommission))
            return Reject("invalid_state", "Settlement.CommissionJobs", subject);
        if (HasPlayerVehicleAt(world, settlement.Node))
            return Reject("invalid_state", "Settlement.Node", subject);
        var changed = ChangeRelation(world, settlement.Id, SettlementRelation.Hostile);
        return new CommandEffect.Accepted(content,
            [new CoreEvent.SettlementRelationChanged(settlement.Id, settlement.Relation, SettlementRelation.Hostile)],
            changed);
    }

    private static CommandEffect Occupy(WorldState world, CoreContentProjection content,
        CoreCommand.OccupySettlement command, SimulationPosition boundary)
    {
        var settlement = world.Settlements.FirstOrDefault(item => item.Id == command.Settlement);
        var subject = new WorldObjectId.Settlement(command.Settlement);
        if (settlement is null) return Reject("object_missing", "Settlement", subject);
        if (settlement.Relation != SettlementRelation.Hostile)
            return Reject("invalid_state", "Settlement.Relation", subject);
        var train = world.Trains.FirstOrDefault(item => item.Id == command.PresentTrain);
        if (train is null) return Reject("object_missing", "Train", new WorldObjectId.Train(command.PresentTrain));
        if (train.Position is not TrainPosition.Node node || node.Id != settlement.Node)
            return Reject("invalid_state", "Train.Position", new WorldObjectId.Train(train.Id));
        if (world.Combats.Any(combat => combat.Location is WorldLocation.Node location && location.Id == settlement.Node))
            return Reject("invalid_state", "Settlement.Node", subject);
        if (!settlement.Surrendered && HasIntactWeapon(world, content, settlement))
            return Reject("conditions_not_met", "Settlement", subject);
        var changed = ChangeRelation(world, settlement.Id, SettlementRelation.PlayerOwned);
        changed = MergeLeases(changed, settlement.Id);
        var adapted = TrainOperations.AdaptNodes(changed, content, boundary);
        var events = ImmutableArray.CreateBuilder<CoreEvent>();
        events.Add(new CoreEvent.SettlementRelationChanged(settlement.Id, settlement.Relation,
            SettlementRelation.PlayerOwned));
        events.AddRange(adapted.Events);
        return new CommandEffect.Accepted(content, events.ToImmutable(), adapted.World);
    }

    private static bool HasPlayerVehicleAt(WorldState world, NodeId node)
        => world.Trains.Any(train => train.Position is TrainPosition.Node position && position.Id == node)
            || world.Settlements.Any(settlement => settlement.Node == node
                && settlement.ParkedVehicles.Any(vehicle => world.Instances
                    .Single(instance => instance.Id == new InstanceId.Vehicle(vehicle)).Ownership
                    == SettlementRelation.PlayerOwned));

    private static bool HasIntactWeapon(WorldState world, CoreContentProjection content, SettlementState settlement)
    {
        foreach (var plot in settlement.Plots.Where(plot => plot.Building is not null))
        {
            var unit = new CombatUnitId.Instance(new InstanceId.Building(plot.Building!.Value));
            if (CombatQueries.Armed(world, content, unit)) return true;
        }
        foreach (var vehicle in settlement.ParkedVehicles)
        {
            var instance = world.Instances.Single(item => item.Id == new InstanceId.Vehicle(vehicle));
            if (instance.Ownership == SettlementRelation.PlayerOwned) continue;
            if (CombatQueries.Armed(world, content, new CombatUnitId.Instance(instance.Id))) return true;
        }
        return false;
    }

    private static WorldState MergeLeases(WorldState world, SettlementId settlementId)
    {
        var leases = world.Leases.Where(lease => lease.Settlement == settlementId).ToArray();
        if (leases.Length == 0) return world;
        var settlement = world.Settlements.Single(item => item.Id == settlementId);
        var inventory = world.Inventories.Single(item => item.Id == settlement.Inventory);
        var goods = inventory.Goods;
        foreach (var lease in leases)
            goods = CombatResolution.Merge(goods,
                world.Inventories.Single(item => item.Id == lease.Inventory).Goods);
        var leaseInventories = leases.Select(lease => lease.Inventory).ToHashSet();
        return world with
        {
            Inventories = world.Inventories.Select(item => item.Id == inventory.Id
                    ? item with { Goods = goods } : item)
                .Where(item => !leaseInventories.Contains(item.Id)).ToImmutableArray(),
            Leases = world.Leases.Where(lease => lease.Settlement != settlementId).ToImmutableArray()
        };
    }

    private static WorldState ChangeRelation(WorldState world, SettlementId id, SettlementRelation after)
    {
        var settlement = world.Settlements.Single(item => item.Id == id);
        var owned = world.Instances.Where(instance => instance.Ownership == settlement.Relation
            && Belongs(instance, settlement)).Select(instance => instance.Id).ToHashSet();
        return world with
        {
            Settlements = world.Settlements.Select(item => item.Id == id
                ? item with { Relation = after, Surrendered = false } : item).ToImmutableArray(),
            Nodes = world.Nodes.Select(node => node.Occupant is NodeOccupant.Settlement occupant
                && occupant.Id == id
                ? node with { Occupant = new NodeOccupant.Settlement(id, after) } : node).ToImmutableArray(),
            Instances = world.Instances.Select(instance => owned.Contains(instance.Id)
                ? instance with { Ownership = after } : instance).ToImmutableArray()
        };
    }

    private static bool Belongs(InstanceState instance, SettlementState settlement) => instance.Id switch
    {
        InstanceId.Building building => settlement.Plots.Any(plot => plot.Building == building.Id),
        InstanceId.Vehicle vehicle => settlement.ParkedVehicles.Contains(vehicle.Id),
        _ => false
    };

    private static CommandEffect.Rejected Reject(string code, string field, WorldObjectId subject)
        => new([new CoreProblem(code, field, subject, [])]);

    private static void Identity(ulong value, string parameter)
    {
        if (value == 0) throw new ArgumentException("世界身份必须非零。", parameter);
    }
}
