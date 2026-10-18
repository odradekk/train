using System.Collections.Immutable;
using Train.Core.Contracts;
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

        foreach (var existing in world.Combats.ToArray())
        {
            var combat = existing;
            if (combat.Location is not WorldLocation.Node location) continue;
            var participants = combat.Controllers.SelectMany(controller => CombatQueries.Units(world, controller.Controller))
                .ToArray();
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
                participants = combat.Controllers.SelectMany(state => CombatQueries.Units(world, state.Controller)).ToArray();
            }
        }

        foreach (var node in world.Nodes.Where(node => world.Combats.All(combat =>
                     combat.Location is not WorldLocation.Node location || location.Id != node.Id)))
        {
            var groups = Groups(world, node);
            if (!HasAttack(world, content, groups)) continue;
            if (world.Next.Combat == ulong.MaxValue)
                throw new InvalidOperationException("战斗身份已耗尽。");
            var controllers = groups.Select(group => new CombatControllerState(group.Controller, step, false, 0, null))
                .OrderBy(state => CombatQueries.ControllerKey(state.Controller)).ToImmutableArray();
            var combat = new CombatState(new(world.Next.Combat), new WorldLocation.Node(node.Id), step, controllers,
                Sides(world, controllers));
            world = world with { Next = world.Next with { Combat = world.Next.Combat + 1 } };
            world = world with { Combats = world.Combats.Add(combat) };
            events.Add(new CoreEvent.CombatStarted(combat.Id, combat.Location,
                groups.SelectMany(group => group.Units).OrderBy(CombatQueries.UnitKey).ToImmutableArray()));
            foreach (var group in groups)
                world = Apply(world, content, combat.Id, group, step, events);
        }

        foreach (var combat in world.Combats.ToArray())
            world = CombatResolution.Resolve(world, content, combat.Id, step, events);

        foreach (var combat in world.Combats.ToArray())
        {
            if (HasAttacker(world, content, combat)) continue;
            events.Add(new CoreEvent.CombatEnded(combat.Id, combat.Location,
                new CombatSummary(combat.StartedAtStep, step, combat.Sides)));
            foreach (var state in combat.Controllers)
            {
                if (state.Controller is not CombatController.Train controller) continue;
                var train = world.Trains.Single(t => t.Id == controller.Id);
                if (state.Retreating)
                {
                    train = TrainOperations.ChangeActivity(train with { SpeedMetersPerSecond = 0,
                        RetreatDestination = null }, TrainActivity.Retreating, events);
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
            world = world with { Combats = world.Combats.Where(c => c.Id != combat.Id).ToImmutableArray() };
        }

        return (world, events.ToImmutable());
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

    private static bool HasAttack(WorldState world, CoreContentProjection content, ImmutableArray<Group> groups)
    {
        foreach (var attacker in groups.Where(group => !group.Retreat))
            foreach (var target in groups.Where(target => target.Controller != attacker.Controller))
                foreach (var unit in attacker.Units.Where(unit => CombatQueries.Armed(world, content, unit)))
                    foreach (var other in target.Units.Where(other => CombatQueries.Hostile(world, unit, other)
                                 && CombatQueries.Hittable(world, content, other)
                                 && (attacker.Scope == TargetScope.All || CombatQueries.Armed(world, content, other))))
                        return true;
        return false;
    }

    private static bool HasAttacker(WorldState world, CoreContentProjection content, CombatState combat)
    {
        foreach (var state in combat.Controllers.Where(state => !state.Retreating))
        {
            var scope = state.Controller switch
            {
                CombatController.Train train => world.Trains.Single(t => t.Id == train.Id).Engagement.Scope,
                CombatController.Settlement settlement => world.Settlements.Single(s => s.Id == settlement.Id).Engagement.Scope,
                _ => throw new InvalidOperationException("战斗控制方种类无效。")
            };
            foreach (var unit in CombatQueries.Units(world, state.Controller)
                         .Where(unit => CombatQueries.Armed(world, content, unit)))
                foreach (var target in combat.Controllers.Where(target => target.Controller != state.Controller))
                    foreach (var other in CombatQueries.Units(world, target.Controller)
                                 .Where(other => CombatQueries.Hostile(world, unit, other)
                                     && CombatQueries.Hittable(world, content, other)
                                     && (scope == TargetScope.All || CombatQueries.Armed(world, content, other))))
                        return true;
        }
        return false;
    }

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
