using System.Collections.Immutable;
using Train.Core.Contracts;
using Train.Core.Creatures;
using Train.Core.Railway;
using Train.Core.World;

namespace Train.Core.Combat;

internal static class CombatOperations
{
    private sealed record Group(CombatController Controller, ImmutableArray<CombatUnitId> Units,
        TargetScope Scope, bool Retreat);

    internal static (WorldState World, ImmutableArray<CoreEvent> Events) Advance(WorldState world,
        CoreContentProjection content, ulong step)
    {
        var events = ImmutableArray.CreateBuilder<CoreEvent>();
        world = RemoveDeparted(world);
        world = AdvanceRetreats(world, content);

        foreach (var existing in world.Combats.ToArray())
        {
            var combat = existing;
            if (combat.Location is not WorldLocation.Node location) continue;
            var participants = ParticipantUnits(world, combat);
            foreach (var train in world.Trains.Where(t => t.Position is TrainPosition.Node node && node.Id == location.Id))
            {
                if (combat.Controllers.Any(controller => controller.Controller is CombatController.Train id && id.Id == train.Id)
                    || train.Activity == TrainActivity.Retreating) continue;
                var units = CombatQueries.Units(world, new CombatController.Train(train.Id));
                if (!units.Any(unit => participants.Any(other => CombatQueries.Hostile(world, unit, other)))) continue;
                var controller = new CombatController.Train(train.Id);
                combat = combat with
                {
                    Controllers = combat.Controllers.Add(new(controller, step, false, 0, null))
                        .OrderBy(state => CombatQueries.ControllerKey(state.Controller)).ToImmutableArray(),
                    Sides = EnsureSide(world, combat.Sides, CombatQueries.Owner(world, controller))
                };
                foreach (var unit in units) events.Add(new CoreEvent.CombatJoined(combat.Id, unit));
                world = Replace(world, combat);
                var group = new Group(controller, units, train.Engagement.Scope,
                    train.Engagement.Encounter == EncounterAction.Retreat);
                world = Apply(world, content, combat.Id, group, step, events);
                participants = ParticipantUnits(world, combat);
            }
            foreach (var creature in CreaturesAt(world, location.Id))
            {
                var unit = new CombatUnitId.CreatureGroup(creature.Id);
                if (!participants.Any(other => CombatQueries.Hostile(world, unit, other))) continue;
                combat = combat with
                {
                    Creatures = combat.Creatures.Add(new CreatureCombatantState(creature.Id, step, creature.Creatures.Length, false, 0))
                        .OrderBy(state => state.Group.Value).ToImmutableArray(),
                    Sides = EnsureSide(world, combat.Sides, new CombatOwner.Creatures())
                };
                events.Add(new CoreEvent.CombatJoined(combat.Id, unit));
                world = Replace(world, combat);
                world = CreatureQueries.Replace(world, creature with { Activity = CreatureActivity.Combat });
                participants = ParticipantUnits(world, combat);
            }
        }

        foreach (var nodeId in ContestedNodes(world))
        {
            if (world.Combats.Any(combat => combat.Location is WorldLocation.Node location && location.Id == nodeId))
                continue;
            var node = world.Nodes.Single(item => item.Id == nodeId);
            var groups = Groups(world, node);
            var creatures = CreaturesAt(world, node.Id);
            if (!HasAttacker(world, content, groups, CreaturesOf(creatures))) continue;
            if (world.Next.Combat == ulong.MaxValue)
                throw new InvalidOperationException("战斗身份已耗尽。");
            var controllers = groups.Select(group => new CombatControllerState(group.Controller, step, false, 0, null))
                .OrderBy(state => CombatQueries.ControllerKey(state.Controller)).ToImmutableArray();
            var combatants = creatures.Select(creature => new CreatureCombatantState(creature.Id, step,
                creature.Creatures.Length, false, 0)).OrderBy(creature => creature.Group.Value).ToImmutableArray();
            var sides = Sides(world, controllers);
            if (!combatants.IsEmpty) sides = EnsureSide(world, sides, new CombatOwner.Creatures());
            var combat = new CombatState(new(world.Next.Combat), new WorldLocation.Node(node.Id), step, controllers, sides)
            {
                Creatures = combatants
            };
            world = world with { Next = world.Next with { Combat = world.Next.Combat + 1 } };
            world = world with { Combats = world.Combats.Add(combat) };
            events.Add(new CoreEvent.CombatStarted(combat.Id, combat.Location,
                groups.SelectMany(group => group.Units)
                    .Concat(creatures.Select(creature => (CombatUnitId)new CombatUnitId.CreatureGroup(creature.Id)))
                    .OrderBy(CombatQueries.UnitKey).ToImmutableArray()));
            foreach (var group in groups)
                world = Apply(world, content, combat.Id, group, step, events);
            foreach (var creature in creatures)
                world = CreatureQueries.Replace(world, creature with { Activity = CreatureActivity.Combat });
        }

        foreach (var combat in world.Combats.ToArray())
            world = CombatResolution.Resolve(world, content, combat.Id, step, events);

        world = ApplyRetreatThresholds(world, content, step, events);

        foreach (var combat in world.Combats.ToArray())
        {
            if (HasAttacker(world, content, CombatGroups(world, combat), combat.Creatures)) continue;
            events.Add(new CoreEvent.CombatEnded(combat.Id, combat.Location,
                new CombatSummary(combat.StartedAtStep, step, combat.Sides)));
            foreach (var state in combat.Controllers)
            {
                if (state.Controller is not CombatController.Train controller) continue;
                var train = world.Trains.Single(t => t.Id == controller.Id);
                if (state.Retreating)
                {
                    var destination = ChooseDestination(world, content, train, combat.Location);
                    train = TrainOperations.ChangeActivity(train with { SpeedMetersPerSecond = 0,
                        RetreatDestination = destination }, TrainActivity.Retreating, events);
                }
                else if (train.Activity == TrainActivity.Combat)
                {
                    train = TrainOperations.ResolveNode(ref world, content, train, events,
                        new TrainDispatch(world, content), step, new(step, 0));
                }
                world = world with
                {
                    Trains = world.Trains.Select(t => t.Id == train.Id ? train : t).ToImmutableArray()
                };
            }
            foreach (var state in combat.Creatures)
            {
                var group = CreatureQueries.FindGroup(world, state.Group);
                if (group is null) continue;
                var objective = group.Objective;
                var activity = group.Activity;
                var started = group.ActivityStartedAtStep;
                if (state.Retreating || group.Objective is CreatureObjective.Railway)
                {
                    activity = CreatureActivity.Retreating;
                    objective = null;
                    started = step;
                }
                else if (group.Objective is CreatureObjective.Settlement)
                    activity = CreatureActivity.RaidingSettlement;
                else if (CreatureQueries.AtOwnLair(world, group))
                {
                    activity = CreatureActivity.Guarding;
                    objective = null;
                    started = step;
                }
                else
                {
                    activity = CreatureActivity.Wandering;
                    objective = null;
                }
                world = CreatureQueries.Replace(world, group with
                {
                    Activity = activity, Objective = objective, ActivityStartedAtStep = started
                });
            }
            world = world with { Combats = world.Combats.Where(c => c.Id != combat.Id).ToImmutableArray() };
        }
        world = RetryRetreatDestinations(world, content);

        return (world, events.ToImmutable());
    }

    private static WorldState ApplyRetreatThresholds(WorldState world, CoreContentProjection content, ulong step,
        ImmutableArray<CoreEvent>.Builder events)
    {
        foreach (var combat in world.Combats.ToArray())
        {
            foreach (var state in combat.Controllers)
            {
                if (state.Retreating || state.Controller is not CombatController.Train controller) continue;
                var train = world.Trains.Single(t => t.Id == controller.Id);
                var ratio = train.Engagement.RetreatStructureRatio;
                if (ratio <= 0) continue;
                var units = CombatQueries.Units(world, state.Controller);
                var current = 0L;
                var maximum = 0L;
                foreach (var unit in units)
                {
                    if (unit is not CombatUnitId.Instance { Id: var id }) continue;
                    var instance = world.Instances.Single(item => item.Id == id);
                    var limit = world.Designs.Single(design => design.Design == instance.Design).MaxStructureDurability;
                    current += Math.Max(0, Math.Min(instance.StructureDurability, limit));
                    maximum += limit;
                }
                if (maximum == 0 || current >= ratio * maximum) continue;
                world = Apply(world, content, combat.Id,
                    new Group(state.Controller, units, train.Engagement.Scope, true), step, events);
            }
            var latest = world.Combats.Single(item => item.Id == combat.Id);
            foreach (var state in latest.Creatures)
            {
                if (state.Retreating) continue;
                var group = CreatureQueries.FindGroup(world, state.Group);
                if (group is null || CreatureQueries.AtOwnLair(world, group)) continue;
                var loss = state.CountAtJoin <= 0 ? 0
                    : (state.CountAtJoin - group.Creatures.Length) / (double)state.CountAtJoin;
                var ratio = CreatureQueries.MinRetreatRatio(content, group);
                var duration = CreatureDurationElapsed(content, group, step);
                if ((ratio is { } threshold && loss > threshold) || duration)
                    world = RetreatCreature(world, content, combat.Id, group, step, events);
            }
        }
        return world;
    }

    private static bool CreatureDurationElapsed(CoreContentProjection content, CreatureGroupState group, ulong step)
    {
        var counts = group.Objective is null || group.Objective is CreatureObjective.Settlement target
            && group.Location is WorldLocation.Node node && node.Id == target.Node;
        if (!counts || CreatureQueries.MinRaidDuration(content, group) is not { } seconds) return false;
        var interval = WorldResources.IntervalSteps(seconds)
            ?? throw new InvalidOperationException("袭击时长超出可表示范围。");
        return step >= group.ActivityStartedAtStep && step - group.ActivityStartedAtStep >= interval;
    }

    private static WorldState RetreatCreature(WorldState world, CoreContentProjection content, CombatId id,
        CreatureGroupState group, ulong step, ImmutableArray<CoreEvent>.Builder events)
    {
        var combat = world.Combats.Single(item => item.Id == id);
        var index = -1;
        for (var position = 0; position < combat.Creatures.Length; position++)
            if (combat.Creatures[position].Group == group.Id) { index = position; break; }
        if (index < 0 || combat.Creatures[index].Retreating) return world;
        var remaining = WorldResources.IntervalSteps(CombatRules.Rule(world, content).RetreatSeconds)
            ?? throw new InvalidOperationException("战斗规则脱离时长超出可表示范围。");
        combat = combat with
        {
            Creatures = combat.Creatures.SetItem(index, combat.Creatures[index] with
            {
                Retreating = true, RemainingDisengagementSteps = remaining
            })
        };
        events.Add(new CoreEvent.CombatRetreatStarted(id, new CombatUnitId.CreatureGroup(group.Id), remaining));
        world = Replace(world, combat);
        return CreatureQueries.Replace(world, group with
        {
            Activity = CreatureActivity.Retreating, Objective = null, ActivityStartedAtStep = step
        });
    }

    private static WorldState RemoveDeparted(WorldState world)
    {
        var combats = world.Combats.Select(combat =>
        {
            var controllers = combat.Controllers.Where(state => state.Controller switch
            {
                CombatController.Train train => world.Trains.Any(item => item.Id == train.Id
                    && CombatQueries.SameLocation(combat.Location, WorldState.Location(item.Position))),
                CombatController.Settlement => true,
                _ => false
            }).ToArray();
            var creatures = combat.Creatures.Where(state =>
                CreatureQueries.FindGroup(world, state.Group) is { } group
                && CombatQueries.SameLocation(combat.Location, group.Location)).ToArray();
            if (controllers.Length == combat.Controllers.Length && creatures.Length == combat.Creatures.Length)
                return combat;
            var units = controllers.SelectMany(state => CombatQueries.Units(world, state.Controller))
                .Concat(creatures.Select(state => (CombatUnitId)new CombatUnitId.CreatureGroup(state.Group)))
                .ToHashSet();
            return combat with
            {
                Controllers = controllers.Select(state => state with
                {
                    FocusTarget = state.FocusTarget is { } focus && units.Contains(focus) ? focus : null
                }).OrderBy(state => CombatQueries.ControllerKey(state.Controller)).ToImmutableArray(),
                Creatures = creatures.OrderBy(state => state.Group.Value).ToImmutableArray()
            };
        }).ToImmutableArray();
        return world with { Combats = combats };
    }

    private static WorldState AdvanceRetreats(WorldState world, CoreContentProjection content)
    {
        var trains = world.Trains.ToDictionary(train => train.Id);
        var combats = world.Combats.Select(combat =>
        {
            var controllers = combat.Controllers.Select(state =>
            {
                if (!state.Retreating || state.Controller is not CombatController.Train train
                    || !trains.TryGetValue(train.Id, out var current)) return state;
                var remaining = state.RemainingDisengagementSteps == 0 ? 0 : state.RemainingDisengagementSteps - 1;
                if (remaining == 0 && current.RetreatDestination is null)
                {
                    var destination = ChooseDestination(world, content, current, combat.Location);
                    trains[train.Id] = current with { RetreatDestination = destination };
                }
                return state with { RemainingDisengagementSteps = remaining };
            }).ToImmutableArray();
            var creatures = combat.Creatures.Select(state => state.Retreating
                ? state with
                {
                    RemainingDisengagementSteps = state.RemainingDisengagementSteps == 0
                        ? 0 : state.RemainingDisengagementSteps - 1
                }
                : state).ToImmutableArray();
            return combat with { Controllers = controllers, Creatures = creatures };
        }).ToImmutableArray();
        return world with { Combats = combats, Trains = world.Trains.Select(train => trains[train.Id]).ToImmutableArray() };
    }

    private static WorldState RetryRetreatDestinations(WorldState world, CoreContentProjection content)
    {
        var trains = world.Trains.Select(train => train.Activity == TrainActivity.Retreating
            && train.RetreatDestination is null && !CombatQueries.TrainInCombat(world, train.Id)
            ? train with { RetreatDestination = ChooseDestination(world, content, train,
                WorldState.Location(train.Position)) }
            : train).ToImmutableArray();
        return world with { Trains = trains };
    }

    internal static NodeId? ChooseDestination(WorldState world, CoreContentProjection content, TrainState train,
        WorldLocation origin)
    {
        if (origin is not WorldLocation.Node node) return null;
        var speed = TrainOperations.Capabilities(world, content, train).MaximumSpeedMetersPerSecond;
        if (speed <= 0) return null;
        NodeId? best = null;
        double bestTime = double.PositiveInfinity;
        foreach (var settlement in world.Settlements.Where(settlement =>
                     settlement.Node != node.Id && settlement.Relation is SettlementRelation.PlayerOwned
                         or SettlementRelation.NonHostile))
        {
            var route = TrainRouting.FindRoute(world.Nodes, world.Edges, node.Id, settlement.Node, speed);
            if (route is not { } found) continue;
            var time = found.DistanceMeters / speed;
            if (time < bestTime || time == bestTime && (best is null || settlement.Node.Value < best.Value.Value))
            {
                best = settlement.Node;
                bestTime = time;
            }
        }
        return best;
    }

    private static CombatUnitId[] ParticipantUnits(WorldState world, CombatState combat) =>
        combat.Controllers.SelectMany(controller => CombatQueries.Units(world, controller.Controller))
            .Concat(combat.Creatures.Select(creature => (CombatUnitId)new CombatUnitId.CreatureGroup(creature.Group)))
            .ToArray();

    private static CreatureGroupState[] CreaturesAt(WorldState world, NodeId node) =>
        world.CreatureGroups.Where(group => group.Location is WorldLocation.Node location && location.Id == node
                && group.Activity != CreatureActivity.Retreating && !CreatureQueries.InCombat(world, group.Id))
            .OrderBy(group => group.Id.Value).ToArray();

    private static CreatureCombatantState[] CreaturesOf(CreatureGroupState[] groups) =>
        groups.Select(group => new CreatureCombatantState(group.Id, 0, group.Creatures.Length, false, 0)).ToArray();

    private static NodeId[] ContestedNodes(WorldState world)
    {
        var seen = new HashSet<NodeId>();
        var nodes = new List<NodeId>();
        foreach (var position in world.Trains.Select(train => train.Position).OfType<TrainPosition.Node>())
            if (seen.Add(position.Id)) nodes.Add(position.Id);
        foreach (var group in world.CreatureGroups)
            if (group.Location is WorldLocation.Node node && seen.Add(node.Id)) nodes.Add(node.Id);
        return nodes.ToArray();
    }

    private static ImmutableArray<Group> Groups(WorldState world, NodeMapItem node)
    {
        var groups = ImmutableArray.CreateBuilder<Group>();
        foreach (var train in world.Trains.Where(t => t.Position is TrainPosition.Node position && position.Id == node.Id
                     && t.Activity != TrainActivity.Retreating))
        {
            var units = CombatQueries.Units(world, new CombatController.Train(train.Id));
            if (!units.IsEmpty) groups.Add(new(new CombatController.Train(train.Id), units,
                train.Engagement.Scope, train.Engagement.Encounter == EncounterAction.Retreat));
        }
        if (node.Occupant is NodeOccupant.Settlement settlement)
        {
            var controller = new CombatController.Settlement(settlement.Id);
            var units = CombatQueries.Units(world, controller);
            if (!units.IsEmpty)
            {
                var state = world.Settlements.Single(s => s.Id == settlement.Id);
                groups.Add(new(controller, units, state.Engagement.Scope, false));
            }
        }
        return groups.OrderBy(group => CombatQueries.ControllerKey(group.Controller)).ToImmutableArray();
    }

    private static ImmutableArray<Group> CombatGroups(WorldState world, CombatState combat)
        => combat.Controllers.Select(state => new Group(state.Controller, CombatQueries.Units(world, state.Controller),
            Scope(world, state.Controller), state.Retreating)).ToImmutableArray();

    private static bool HasAttacker(WorldState world, CoreContentProjection content, ImmutableArray<Group> groups,
        IReadOnlyList<CreatureCombatantState> creatures)
    {
        foreach (var attacker in groups.Where(group => !group.Retreat))
            foreach (var unit in attacker.Units.Where(unit => CombatQueries.Armed(world, content, unit)))
            {
                foreach (var target in groups.Where(target => target.Controller != attacker.Controller))
                    foreach (var other in target.Units.Where(other => CombatQueries.Hostile(world, unit, other)
                                 && CombatQueries.Hittable(world, content, other)
                                 && (attacker.Scope == TargetScope.All || CombatQueries.Armed(world, content, other))))
                        return true;
                foreach (var creature in creatures)
                {
                    var other = new CombatUnitId.CreatureGroup(creature.Group);
                    if (CombatQueries.Hostile(world, unit, other) && CombatQueries.Hittable(world, content, other)
                        && (attacker.Scope == TargetScope.All || CombatQueries.Armed(world, content, other)))
                        return true;
                }
            }
        foreach (var creature in creatures.Where(creature => !creature.Retreating))
        {
            var attacker = new CombatUnitId.CreatureGroup(creature.Group);
            if (!CombatQueries.Armed(world, content, attacker)) continue;
            foreach (var target in groups)
                foreach (var other in target.Units.Where(other => CombatQueries.Hostile(world, attacker, other)
                             && CombatQueries.Hittable(world, content, other)))
                    return true;
        }
        return false;
    }

    private static TargetScope Scope(WorldState world, CombatController controller) => controller switch
    {
        CombatController.Train train => world.Trains.Single(t => t.Id == train.Id).Engagement.Scope,
        CombatController.Settlement settlement => world.Settlements.Single(s => s.Id == settlement.Id).Engagement.Scope,
        _ => throw new InvalidOperationException("战斗控制方种类无效。")
    };

    private static WorldState Apply(WorldState world, CoreContentProjection content, CombatId id, Group group,
        ulong step, ImmutableArray<CoreEvent>.Builder events)
    {
        var combat = world.Combats.Single(c => c.Id == id);
        var index = -1;
        for (var position = 0; position < combat.Controllers.Length; position++)
            if (combat.Controllers[position].Controller == group.Controller) { index = position; break; }
        if (index < 0) return world;
        var controller = combat.Controllers[index];
        if (group.Retreat && !controller.Retreating)
        {
            var seconds = CombatRules.Rule(world, content).RetreatSeconds;
            var remaining = WorldResources.IntervalSteps(seconds)
                ?? throw new InvalidOperationException("战斗规则脱离时长超出可表示范围。");
            combat = combat with
            {
                Controllers = combat.Controllers.SetItem(index, controller with
                {
                    Retreating = true, RemainingDisengagementSteps = remaining
                })
            };
            foreach (var unit in group.Units)
                events.Add(new CoreEvent.CombatRetreatStarted(id, unit, remaining));
            world = Replace(world, combat);
            if (group.Controller is CombatController.Train retreatTrain)
            {
                var current = world.Trains.Single(t => t.Id == retreatTrain.Id);
                current = TrainOperations.ChangeActivity(current with { SpeedMetersPerSecond = 0 },
                    TrainActivity.Retreating, events);
                world = world with { Trains = world.Trains.Select(t => t.Id == retreatTrain.Id ? current : t).ToImmutableArray() };
            }
            return world;
        }
        if (group.Controller is not CombatController.Train trainController) return world;
        var train = world.Trains.Single(t => t.Id == trainController.Id);
        if (train.Activity is TrainActivity.Running or TrainActivity.Servicing or TrainActivity.Waiting
            or TrainActivity.NoRoute)
        {
            train = TrainOperations.ChangeActivity(train with { SpeedMetersPerSecond = 0 },
                TrainActivity.Combat, events);
            world = world with { Trains = world.Trains.Select(t => t.Id == train.Id ? train : t).ToImmutableArray() };
        }
        return world;
    }

    private static WorldState Replace(WorldState world, CombatState combat)
        => world with
        {
            Combats = world.Combats.Select(c => c.Id == combat.Id ? combat : c).ToImmutableArray()
        };

    private static ImmutableArray<CombatSideSummary> Sides(WorldState world,
        ImmutableArray<CombatControllerState> controllers)
        => controllers.Select(state => CombatQueries.Owner(world, state.Controller)).Distinct()
            .OrderBy(OwnerKey).Select(owner => new CombatSideSummary(owner, [], [], [], new(0)))
            .ToImmutableArray();

    private static ImmutableArray<CombatSideSummary> EnsureSide(WorldState world,
        ImmutableArray<CombatSideSummary> sides, CombatOwner owner)
        => sides.Any(side => side.Owner == owner) ? sides
            : sides.Add(new(owner, [], [], [], new(0))).OrderBy(side => OwnerKey(side.Owner)).ToImmutableArray();

    private static (int Kind, ulong Id) OwnerKey(CombatOwner owner) => owner switch
    {
        CombatOwner.Player => (0, 0),
        CombatOwner.Settlement settlement => (1, settlement.Id.Value),
        CombatOwner.Creatures => (2, 0),
        _ => throw new InvalidOperationException("战斗方种类无效。")
    };
}
