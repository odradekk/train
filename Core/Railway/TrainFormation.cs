using System.Collections.Immutable;
using Train.Core.Combat;
using Train.Core.Construction;
using Train.Core.Contracts;
using Train.Core.Inventory;
using Train.Core.Sessions;
using Train.Core.World;

namespace Train.Core.Railway;

// Decision note: 2026-10-31-object-centric-management-screens
internal static class TrainFormation
{
    internal static void Validate(ImmutableArray<VehicleId> vehicles, TrainId? train)
    {
        if (vehicles.IsDefault || vehicles.Any(v => v.Value == 0) || train is { Value: 0 })
            throw new ArgumentException("车辆数组必须初始化，车辆和列车编号必须非零。");
    }

    internal static CoreResult<TrainFormationPreview> Preview(WorldState world, CoreContentProjection content,
        ImmutableArray<VehicleId> vehicles, TrainId? id)
    {
        TrainState? train = null;
        SettlementState? settlement = null;
        if (id is { } existing && CheckTrain(world, existing, out train, out settlement) is { } unavailable)
            return new CoreResult<TrainFormationPreview>.Failure([unavailable]);
        if (CheckVehicles(world, vehicles, train, ref settlement) is { } problem)
            return new CoreResult<TrainFormationPreview>.Failure([problem]);
        var inventory = train is null ? null : world.Inventories.Single(i => i.Id == train.Inventory);
        var capabilities = TrainCapabilities.Calculate(world, content, vehicles, inventory?.Goods ?? []);
        var capacity = InventoryStorage.VehicleCapacity(world, content, vehicles);
        var blocker = train is null ? IdentityProblem(world) : CapacityProblem(train, inventory!, capacity);
        return new CoreResult<TrainFormationPreview>.Success(new(capabilities.LengthMeters, capabilities.MassTons,
            capabilities.TractionKilonewtons, capacity, blocker is null ? [] : [blocker],
            capabilities.DepartureProblem(id, TrainOperations.Rule(world, content).MinimumAcceleration)));
    }

    private static CoreProblem? CheckTrain(WorldState world, TrainId id,
        out TrainState? train, out SettlementState? settlement)
    {
        train = world.Trains.FirstOrDefault(t => t.Id == id);
        settlement = null;
        var subject = new WorldObjectId.Train(id);
        if (train is null) return new("object_missing", "Train", subject, []);
        if (train.Activity == TrainActivity.Retreating || CombatQueries.TrainInCombat(world, id))
            return new("invalid_state", "Train.Activity", subject, []);
        if (train.Position is not TrainPosition.Node node || train.SpeedMetersPerSecond != 0)
            return new("train_not_stationary_at_node", "Train.Position", subject, []);
        if (train.Activity != TrainActivity.Stopped) return new("invalid_state", "Train.Activity", subject, []);
        settlement = world.Settlements.FirstOrDefault(s => s.Node == node.Id);
        if (settlement is null) return new("settlement_unavailable", "Train.Position", subject, []);
        return settlement.Relation == SettlementRelation.Hostile ? new("access_denied", "Train.Position", subject, []) : null;
    }

    internal static CommandEffect Replace(WorldState world, CoreContentProjection content, CoreCommand.ReplaceTrainFormation command)
    {
        if (CheckTrain(world, command.Train, out var found, out var settlement) is { } unavailable)
            return new CommandEffect.Rejected([unavailable]);
        var train = found!;
        var vehicles = command.OrderedVehicles;
        if (CheckVehicles(world, vehicles, train, ref settlement) is { } problem)
            return new CommandEffect.Rejected([problem]);
        var inventory = world.Inventories.Single(i => i.Id == train.Inventory);
        if (CapacityProblem(train, inventory, InventoryStorage.VehicleCapacity(world, content, vehicles)) is { } capacity)
            return new CommandEffect.Rejected([capacity]);
        var site = settlement!;
        return new CommandEffect.Accepted(content, [new CoreEvent.TrainCompositionChanged(train.Id, vehicles)], world with
        {
            Trains = world.Trains.Select(t => t.Id == train.Id ? t with { Vehicles = vehicles } : t).ToImmutableArray(),
            Instances = world.Instances.Select(i => i.Id is InstanceId.Vehicle v && (train.Vehicles.Contains(v.Id) || vehicles.Contains(v.Id))
                ? i with { Details = ((InstanceDetails.Vehicle)i.Details) with { Train = vehicles.Contains(v.Id) ? train.Id : null } } : i).ToImmutableArray(),
            Settlements = world.Settlements.Select(s => s.Id == site.Id ? s with
            { ParkedVehicles = s.ParkedVehicles.Except(vehicles).Concat(train.Vehicles.Except(vehicles)).OrderBy(v => v.Value).ToImmutableArray() } : s).ToImmutableArray()
        });
    }

    internal static CoreProblem? CheckVehicles(WorldState world, ImmutableArray<VehicleId> vehicles,
        TrainState? train, ref SettlementState? settlement)
    {
        if (vehicles.IsEmpty) return new("composition_empty", "OrderedVehicles", null, []);
        if (vehicles.Distinct().Count() != vehicles.Length) return new("vehicle_duplicate", "OrderedVehicles", null, []);
        foreach (var id in vehicles)
        {
            var subject = new WorldObjectId.Instance(new InstanceId.Vehicle(id));
            var instance = world.Instances.FirstOrDefault(i => i.Id == new InstanceId.Vehicle(id));
            if (instance is null) return new("object_missing", "OrderedVehicles", subject, []);
            if (CombatQueries.IsInCombat(world, instance.Id)) return new("invalid_state", "OrderedVehicles", subject, []);
            if (instance.Ownership != SettlementRelation.PlayerOwned) return new("not_owned", "OrderedVehicles", subject, []);
            if (ConstructionJobs.Lock(world, instance.Id) == InstanceLock.UnderDeconstruction)
                return new("instance_locked", "OrderedVehicles", subject, []);
            if (instance.Details is not InstanceDetails.Vehicle vehicle || vehicle.Train is { } owner && owner != train?.Id)
                return new("vehicle_in_train", "OrderedVehicles", subject, []);
            if (vehicle.Train is not null) continue;
            var parked = world.Settlements.Single(s => s.ParkedVehicles.Contains(id));
            if (settlement is not null && settlement.Id != parked.Id)
                return new("vehicles_not_colocated", "OrderedVehicles", subject, []);
            settlement = parked;
        }
        return settlement!.Relation == SettlementRelation.Hostile
            ? new("access_denied", "OrderedVehicles", new WorldObjectId.Settlement(settlement.Id), []) : null;
    }

    internal static CoreProblem? IdentityProblem(WorldState world)
        => world.Next.Train == ulong.MaxValue || world.Next.Inventory == ulong.MaxValue
            ? new("identity_exhausted", "World.Next", null, []) : null;

    private static CoreProblem? CapacityProblem(TrainState train, InventoryState inventory, long capacity)
        => capacity < checked(InventoryStorage.Used(inventory) + InventoryStorage.Reserved(inventory))
            ? new("insufficient_capacity", "Train.Inventory.Capacity", new WorldObjectId.Train(train.Id), []) : null;
}
