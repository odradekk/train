using System.Collections.Immutable;
using Train.Core.Contracts;
using Train.Core.Trading;
using Train.Core.World;

namespace Train.Core.Combat;

internal static class CombatResolution
{
    private sealed record Shot(CombatUnitId Target, AttackSource Source, HitTarget Hit, long Damage);

    internal static WorldState Resolve(WorldState world, CoreContentProjection content, CombatId id, ulong step,
        ImmutableArray<CoreEvent>.Builder events)
    {
        var combat = world.Combats.Single(c => c.Id == id);
        var shots = new List<Shot>();
        foreach (var controller in combat.Controllers.Where(state => !state.Retreating))
        {
            var scope = Scope(world, controller.Controller);
            foreach (var unit in CombatQueries.Units(world, controller.Controller)
                         .Where(unit => CombatQueries.Armed(world, content, unit)))
            {
                var target = ChooseTarget(world, content, combat, controller, scope);
                if (target is not { } selected) continue;
                foreach (var weapon in Weapons(world, content, unit))
                {
                    var interval = WorldResources.IntervalSteps(weapon.Interval)
                        ?? throw new InvalidOperationException("武器间隔超出可表示范围。");
                    if ((step - controller.JoinedAtStep) % interval != 0) continue;
                    if (Roll(world, content, combat, unit, weapon.Part, weapon.Damage, selected, step) is { } shot)
                        shots.Add(shot);
                }
            }
        }
        if (shots.Count == 0) return world;

        var structureDamage = new Dictionary<InstanceId, long>();
        var partDamage = new Dictionary<(InstanceId Instance, PartId Part), long>();
        foreach (var shot in shots)
        {
            if (shot.Hit is HitTarget.Structure structure)
            {
                var instance = structure.Instance.Instance;
                structureDamage[instance] = checked(structureDamage.GetValueOrDefault(instance) + shot.Damage);
            }
            else if (shot.Hit is HitTarget.Part part)
            {
                var key = (part.Instance.Instance, part.HitPart);
                partDamage[key] = checked(partDamage.GetValueOrDefault(key) + shot.Damage);
            }
        }

        var destroyedParts = new List<DestroyedPart>();
        var destroyedUnits = new List<DestroyedUnit>();
        var instances = world.Instances;
        foreach (var instanceId in structureDamage.Keys.Concat(partDamage.Keys.Select(key => key.Instance)).Distinct())
        {
            var instance = instances.Single(i => i.Id == instanceId);
            var design = world.Designs.Single(d => d.Design == instance.Design);
            var parts = instance.Parts;
            var overflow = 0L;
            foreach (var (key, damage) in partDamage.Where(pair => pair.Key.Instance == instanceId)
                         .OrderBy(pair => pair.Key.Part.Value))
            {
                var specification = design.Parts.Single(part => part.Id == key.Part);
                var index = parts.Select((part, position) => (part, position))
                    .Single(item => item.part.Id == key.Part).position;
                var current = parts[index].Current;
                var remaining = Math.Max(0, Math.Min(current, specification.MaxDurability));
                var applied = Math.Min(damage, remaining);
                overflow = checked(overflow + damage - applied);
                var next = remaining - applied;
                parts = parts.SetItem(index, new PartDurability(key.Part, next));
                if (remaining > 0 && next == 0)
                    destroyedParts.Add(new DestroyedPart(Reference(instance, design, combat.Location), key.Part));
            }
            var structure = Math.Max(0, Math.Min(instance.StructureDurability, design.MaxStructureDurability));
            var incoming = checked(structureDamage.GetValueOrDefault(instanceId) + overflow);
            var nextStructure = Math.Max(0, structure - incoming);
            if (structure > 0 && nextStructure == 0)
                destroyedUnits.Add(new DestroyedUnit.Instance(Reference(instance, design, combat.Location)));
            instances = instances.Select(item => item.Id == instanceId ? item with
            {
                StructureDurability = nextStructure, Parts = parts
            } : item).ToImmutableArray();
        }
        world = world with { Instances = instances };
        events.Add(new CoreEvent.CombatHits(id, combat.Location,
            shots.Select(shot => new CombatHit(shot.Source, shot.Hit, shot.Damage)).ToImmutableArray()));
        if (destroyedParts.Count > 0 || destroyedUnits.Count > 0)
            events.Add(new CoreEvent.CombatDestruction(id, combat.Location,
                destroyedParts.OrderBy(part => CombatQueries.UnitKey(new CombatUnitId.Instance(part.Instance.Instance)))
                    .ThenBy(part => part.Part.Value).ToImmutableArray(),
                destroyedUnits.OrderBy(unit => unit switch
                {
                    DestroyedUnit.Instance instance => CombatQueries.UnitKey(new CombatUnitId.Instance(instance.Value.Instance)),
                    _ => (int.MaxValue, ulong.MaxValue)
                }).ToImmutableArray()));
        return Remove(world, content, id, destroyedParts, destroyedUnits, step, events);
    }

    private static IEnumerable<(PartId Part, long Damage, double Interval)> Weapons(WorldState world,
        CoreContentProjection content, CombatUnitId unit)
    {
        if (unit is not CombatUnitId.Instance { Id: var id }) yield break;
        var instance = world.Instances.Single(i => i.Id == id);
        var design = world.Designs.Single(d => d.Design == instance.Design);
        foreach (var part in design.Parts.OrderBy(part => part.Id.Value))
        {
            if (InstanceCapabilities.DefinitionMissing(world, content, design.Design, part)
                || Math.Min(instance.Parts.Single(p => p.Id == part.Id).Current, part.MaxDurability) <= 0)
                continue;
            var weapon = part.Capabilities.OfType<PartCapability.Weapon>().SingleOrDefault();
            if (weapon is not null) yield return (part.Id, weapon.Damage, weapon.IntervalSeconds);
        }
    }

    private static CombatUnitId? ChooseTarget(WorldState world, CoreContentProjection content, CombatState combat,
        CombatControllerState controller, TargetScope scope)
    {
        var candidates = combat.Controllers.Where(state => state.Controller != controller.Controller)
            .SelectMany(state => CombatQueries.Units(world, state.Controller))
            .Where(unit => CombatQueries.Hostile(world, CombatQueries.Units(world, controller.Controller).First(), unit)
                && (scope == TargetScope.All || CombatQueries.Armed(world, content, unit))
                && CombatQueries.Hittable(world, content, unit)).ToArray();
        if (controller.FocusTarget is { } focus && candidates.Contains(focus)) return focus;
        if (candidates.Length == 0) return null;
        var priority = Priority(world, controller.Controller);
        if (priority == TargetPriority.ArmedFirst)
        {
            var armed = candidates.Where(unit => CombatQueries.Armed(world, content, unit)).ToArray();
            if (armed.Length > 0) candidates = armed;
        }
        return priority switch
        {
            TargetPriority.LowestDurability => candidates.OrderBy(unit => CombatQueries.Durability(world, unit))
                .ThenBy(CombatQueries.UnitKey).First(),
            TargetPriority.HighestDurability => candidates.OrderByDescending(unit => CombatQueries.Durability(world, unit))
                .ThenBy(CombatQueries.UnitKey).First(),
            _ => candidates.OrderBy(CombatQueries.UnitKey).First()
        };
    }

    private static Shot? Roll(WorldState world, CoreContentProjection content, CombatState combat,
        CombatUnitId attacker, PartId weapon, long damage, CombatUnitId target, ulong step)
    {
        if (target is not CombatUnitId.Instance { Id: var targetId }) return null;
        var instance = world.Instances.Single(i => i.Id == targetId);
        var design = world.Designs.Single(d => d.Design == instance.Design);
        var buckets = new List<(long Weight, PartId? Part)>();
        var total = design.StructureHitWeight;
        buckets.Add((total, null));
        foreach (var part in design.Parts.OrderBy(part => part.Id.Value))
        {
            if (InstanceCapabilities.DefinitionMissing(world, content, design.Design, part) || part.HitWeight <= 0) continue;
            total = checked(total + part.HitWeight);
            buckets.Add((part.HitWeight, part.Id));
        }
        if (total <= 0) return null;
        var kind = attacker is CombatUnitId.Instance { Id: InstanceId.Vehicle } ? 0UL
            : attacker is CombatUnitId.Instance { Id: InstanceId.Building } ? 1UL
            : throw new InvalidOperationException("射击者种类无效。");
        var identity = attacker switch
        {
            CombatUnitId.Instance { Id: InstanceId.Vehicle vehicle } => vehicle.Id.Value,
            CombatUnitId.Instance { Id: InstanceId.Building building } => building.Id.Value,
            _ => throw new InvalidOperationException("射击者身份无效。")
        };
        var sample = KeyedRandom.Sample(new RandomKey(world.Source.WorldSeed, "combat.hit.v1",
            [combat.Id.Value, step, kind, identity, (ulong)weapon.Value]));
        var choice = WorldRandom.Integer(sample, 0, total - 1);
        PartId? hitPart = null;
        foreach (var bucket in buckets)
        {
            if (choice < bucket.Weight)
            {
                hitPart = bucket.Part;
                break;
            }
            choice -= bucket.Weight;
        }
        var reference = Reference(instance, design, combat.Location);
        var hit = hitPart is { } partId
            ? (HitTarget)new HitTarget.Part(reference, partId)
            : new HitTarget.Structure(reference);
        var source = new AttackSource.Weapon(Reference(world.Instances.Single(i => i.Id == AttackerInstance(attacker)),
            world.Designs.Single(d => d.Design == world.Instances.Single(i => i.Id == AttackerInstance(attacker)).Design),
            combat.Location), weapon);
        return new Shot(target, source, hit, damage);
    }

    private static InstanceId AttackerInstance(CombatUnitId unit) => unit switch
    {
        CombatUnitId.Instance instance => instance.Id,
        _ => throw new InvalidOperationException("射击者不是实例。")
    };

    private static InstancePresentationRef Reference(InstanceState instance, DesignProjection design,
        WorldLocation location) => new(instance.Id, design.Design, location);

    private static TargetScope Scope(WorldState world, CombatController controller) => controller switch
    {
        CombatController.Train train => world.Trains.Single(t => t.Id == train.Id).Engagement.Scope,
        CombatController.Settlement settlement => world.Settlements.Single(s => s.Id == settlement.Id).Engagement.Scope,
        _ => throw new InvalidOperationException("战斗控制方种类无效。")
    };

    private static TargetPriority Priority(WorldState world, CombatController controller) => controller switch
    {
        CombatController.Train train => world.Trains.Single(t => t.Id == train.Id).Engagement.Priority,
        CombatController.Settlement settlement => world.Settlements.Single(s => s.Id == settlement.Id).Engagement.Priority,
        _ => throw new InvalidOperationException("战斗控制方种类无效。")
    };

    private static WorldState Remove(WorldState world, CoreContentProjection content, CombatId id,
        List<DestroyedPart> parts, List<DestroyedUnit> units, ulong step, ImmutableArray<CoreEvent>.Builder events)
    {
        if (parts.Count == 0 && units.Count == 0) return world;
        var instanceIds = units.OfType<DestroyedUnit.Instance>().Select(unit => unit.Value.Instance).ToHashSet();
        var combat = world.Combats.Single(c => c.Id == id);

        foreach (var part in parts)
            AddLoss(ref world, id, Owner(world, part.Instance.Instance), [], [part], []);
        foreach (var unit in units)
            AddLoss(ref world, id, UnitOwner(world, unit), [unit], [], []);
        foreach (var train in world.Trains.Where(train => train.Vehicles.Any(vehicle =>
                         instanceIds.Contains(new InstanceId.Vehicle(vehicle)))
                     && train.Vehicles.All(vehicle => instanceIds.Contains(new InstanceId.Vehicle(vehicle)))))
            AddLoss(ref world, id, CombatQueries.OwnerOfUnit(world,
                    new CombatUnitId.Instance(new InstanceId.Vehicle(train.Vehicles[0]))), [],
                [], world.Inventories.Single(i => i.Id == train.Inventory).Goods);

        var buildingNodes = world.Settlements.Where(settlement => settlement.Plots.Any(plot =>
                plot.Building is { } building && instanceIds.Contains(new InstanceId.Building(building))))
            .Select(settlement => settlement.Node).ToHashSet();
        world = RemoveVehicles(world, content, instanceIds, step, events);
        world = RemoveBuildings(world, instanceIds);
        world = RemoveParked(world, instanceIds);
        world = world with { Instances = world.Instances.Where(instance => !instanceIds.Contains(instance.Id)).ToImmutableArray() };
        world = UpdatePlatforms(world, buildingNodes);
        world = ClearControllers(world);
        return world;
    }

    private static WorldState RemoveVehicles(WorldState world, CoreContentProjection content,
        HashSet<InstanceId> instanceIds, ulong step, ImmutableArray<CoreEvent>.Builder events)
    {
        var vehicles = instanceIds.OfType<InstanceId.Vehicle>().Select(vehicle => vehicle.Id).ToHashSet();
        foreach (var train in world.Trains.Where(train => train.Vehicles.Any(vehicles.Contains)).ToArray())
        {
            var survivors = train.Vehicles.Where(vehicle => !vehicles.Contains(vehicle)).ToImmutableArray();
            if (survivors.IsEmpty)
            {
                events.Add(new CoreEvent.TrainCompositionChanged(train.Id, []));
                var ended = RunReports.End(ref world, content, train, new(step, 0), RunEndReason.AllVehiclesDestroyed, events);
                world = world with
                {
                    Trains = world.Trains.Where(item => item.Id != ended.Id).ToImmutableArray(),
                    Inventories = world.Inventories.Where(item => item.Id != train.Inventory).ToImmutableArray()
                };
            }
            else
            {
                events.Add(new CoreEvent.TrainCompositionChanged(train.Id, survivors));
                world = world with
                {
                    Trains = world.Trains.Select(item => item.Id == train.Id ? item with { Vehicles = survivors } : item)
                        .ToImmutableArray()
                };
            }
        }
        return world;
    }

    private static WorldState RemoveBuildings(WorldState world, HashSet<InstanceId> instanceIds)
    {
        foreach (var building in instanceIds.OfType<InstanceId.Building>().ToArray())
        {
            var settlement = world.Settlements.FirstOrDefault(s => s.Plots.Any(plot => plot.Building == building.Id));
            if (settlement is null) continue;
            var removed = settlement.ProductionFacilities.Where(facility => facility.Facility.Building == building.Id).ToArray();
            var inventory = world.Inventories.Single(i => i.Id == settlement.Inventory);
            var goods = inventory.Goods;
            foreach (var facility in removed)
                goods = Merge(goods, facility.OutputBuffer);
            world = world with
            {
                Settlements = world.Settlements.Select(s => s.Id != settlement.Id ? s : s with
                {
                    Plots = s.Plots.Select(plot => plot.Id == settlement.Plots.Single(p => p.Building == building.Id).Id
                        ? plot with { Building = null } : plot).ToImmutableArray(),
                    ProductionFacilities = removed.Length == 0 ? s.ProductionFacilities
                        : s.ProductionFacilities.Where(facility => facility.Facility.Building != building.Id).ToImmutableArray()
                }).ToImmutableArray(),
                Inventories = world.Inventories.Select(i => i.Id != inventory.Id ? i : inventory with { Goods = goods })
                    .ToImmutableArray()
            };
        }
        return world;
    }

    private static WorldState RemoveParked(WorldState world, HashSet<InstanceId> instanceIds)
    {
        var vehicles = instanceIds.OfType<InstanceId.Vehicle>().Select(vehicle => vehicle.Id).ToHashSet();
        if (vehicles.Count == 0) return world;
        return world with
        {
            Settlements = world.Settlements.Select(settlement => settlement.ParkedVehicles.Any(vehicles.Contains)
                ? settlement with { ParkedVehicles = settlement.ParkedVehicles.Where(vehicle => !vehicles.Contains(vehicle))
                    .ToImmutableArray() } : settlement).ToImmutableArray()
        };
    }

    private static WorldState UpdatePlatforms(WorldState world, HashSet<NodeId> nodes)
    {
        if (nodes.Count == 0) return world;
        var instances = world.Instances.ToDictionary(instance => instance.Id);
        var designs = world.Designs.ToDictionary(design => design.Design);
        return world with
        {
            Nodes = world.Nodes.Select(node => nodes.Contains(node.Id)
                ? node with { Platforms = WorldState.CountPlatforms(world.Settlements.Single(s => s.Node == node.Id),
                    instances, designs) }
                : node).ToImmutableArray()
        };
    }

    private static WorldState ClearControllers(WorldState world)
    {
        var combats = world.Combats.Select(combat =>
        {
            var controllers = combat.Controllers.Where(controller => Alive(world, controller.Controller)).ToArray();
            var units = controllers.SelectMany(controller => CombatQueries.Units(world, controller.Controller)).ToHashSet();
            return combat with
            {
                Controllers = controllers.Select(controller => controller with
                {
                    FocusTarget = controller.FocusTarget is { } focus && units.Contains(focus) ? focus : null
                }).OrderBy(controller => CombatQueries.ControllerKey(controller.Controller)).ToImmutableArray()
            };
        }).ToImmutableArray();
        return world with { Combats = combats };
    }

    private static bool Alive(WorldState world, CombatController controller) => controller switch
    {
        CombatController.Train train => world.Trains.Any(item => item.Id == train.Id && !item.Vehicles.IsEmpty),
        CombatController.Settlement settlement => CombatQueries.Units(world, controller).Any(),
        _ => false
    };

    private static void AddLoss(ref WorldState world, CombatId id, CombatOwner owner,
        ImmutableArray<DestroyedUnit> units, ImmutableArray<DestroyedPart> parts, ImmutableArray<GoodsQuantity> cargo)
    {
        var combat = world.Combats.Single(c => c.Id == id);
        var sides = combat.Sides;
        var index = sides.Select((side, position) => (side, position))
            .FirstOrDefault(item => item.side.Owner == owner).position;
        var side = index < sides.Length && sides[index].Owner == owner
            ? sides[index] : new CombatSideSummary(owner, [], [], [], new(0));
        var mergedUnits = side.UnitsLost.AddRange(units)
            .OrderBy(unit => unit is DestroyedUnit.Instance instance
                ? CombatQueries.UnitKey(new CombatUnitId.Instance(instance.Value.Instance))
                : (int.MaxValue, ulong.MaxValue)).ToImmutableArray();
        var mergedParts = side.PartsLost.AddRange(parts)
            .OrderBy(part => CombatQueries.UnitKey(new CombatUnitId.Instance(part.Instance.Instance)))
            .ThenBy(part => part.Part.Value).ToImmutableArray();
        var mergedCargo = Merge(side.CargoLost, cargo);
        side = side with { UnitsLost = mergedUnits, PartsLost = mergedParts, CargoLost = mergedCargo };
        sides = index < sides.Length && sides[index].Owner == owner
            ? sides.SetItem(index, side)
            : sides.Add(side).OrderBy(item => OwnerKey(item.Owner)).ToImmutableArray();
        world = world with
        {
            Combats = world.Combats.Select(c => c.Id == id ? c with { Sides = sides } : c).ToImmutableArray()
        };
    }

    private static CombatOwner UnitOwner(WorldState world, DestroyedUnit unit) => unit switch
    {
        DestroyedUnit.Instance instance => Owner(world, instance.Value.Instance),
        DestroyedUnit.Creature => new CombatOwner.Creatures(),
        _ => throw new InvalidOperationException("被毁单位种类无效。")
    };

    private static CombatOwner Owner(WorldState world, InstanceId instance)
        => CombatQueries.OwnerOfUnit(world, new CombatUnitId.Instance(instance));

    private static ImmutableArray<GoodsQuantity> Merge(ImmutableArray<GoodsQuantity> current,
        ImmutableArray<GoodsQuantity> added)
    {
        if (added.IsEmpty) return current;
        var quantities = current.ToDictionary(good => good.Good, good => good.Quantity);
        foreach (var good in added)
            quantities[good.Good] = checked(quantities.GetValueOrDefault(good.Good) + good.Quantity);
        return quantities.OrderBy(pair => WorldReferences.GoodKey(pair.Key))
            .Select(pair => new GoodsQuantity(pair.Key, pair.Value)).ToImmutableArray();
    }

    private static (int Kind, ulong Id) OwnerKey(CombatOwner owner) => owner switch
    {
        CombatOwner.Player => (0, 0),
        CombatOwner.Settlement settlement => (1, settlement.Id.Value),
        CombatOwner.Creatures => (2, 0),
        _ => throw new InvalidOperationException("战斗方种类无效。")
    };
}
