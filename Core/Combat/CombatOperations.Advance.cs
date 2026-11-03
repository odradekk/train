using System.Collections.Immutable;
using Train.Core.Contracts;
using Train.Core.Creatures;
using Train.Core.Railway;
using Train.Core.World;

namespace Train.Core.Combat;

internal static partial class CombatOperations
{
    internal static (WorldState World, ImmutableArray<CoreEvent> Events) Advance(WorldState world,
        CoreContentProjection content, ulong step, WorldState before)
    {
        var events = ImmutableArray.CreateBuilder<CoreEvent>();
        world = RemoveDeparted(world);
        world = AdvanceRetreats(world, content, step);
        world = SettlementMorale.Recover(world, content);
        var moraleDamage = ImmutableArray.CreateBuilder<(CombatId Combat, ImmutableArray<SettlementMoraleDamage> Damage)>();

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
            if (!world.CreatureGroups.Any(group => group.Location is WorldLocation.Node at && at.Id == nodeId)
                && (node.Occupant is not NodeOccupant.Settlement settlement
                    || !CombatQueries.Hostile(world, new CombatOwner.Player(),
                        CombatQueries.Owner(world, new CombatController.Settlement(settlement.Id)))))
                continue;
            var groups = Groups(world, node);
            var creatures = CreaturesAt(world, node.Id);
            if (!HasAttacker(world, content, groups, creatures.Select(creature => creature.Id))) continue;
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

        world = EdgeCombat.Advance(world, before, content, step, events);

        foreach (var combat in world.Combats.ToArray())
        {
            var resolution = CombatResolution.Resolve(world, content, combat.Id, step, events);
            world = resolution.World;
            if (!resolution.Damage.IsEmpty) moraleDamage.Add((combat.Id, resolution.Damage));
        }

        world = ApplyRetreatThresholds(world, content, step, events);
        foreach (var damage in moraleDamage)
            world = SettlementMorale.Apply(world, before, content, damage.Combat, damage.Damage, events);

        foreach (var combat in world.Combats.ToArray())
        {
            if (HasAttacker(world, content, CombatGroups(world, combat), combat.Creatures.Select(creature => creature.Group)))
                continue;
            var sides = ClearanceReward(world, content, combat, out var clearedLair, out var clearedNode, out var reward);
            events.Add(new CoreEvent.CombatEnded(combat.Id, combat.Location,
                new CombatSummary(combat.StartedAtStep, step, sides)));
            if (clearedLair is not null)
            {
                world = world with { Money = new(checked(world.Money.Value + reward.Value)) };
                events.Add(new CoreEvent.LairCleared(clearedLair.Id, clearedNode, reward));
            }
            foreach (var state in combat.Controllers)
            {
                if (state.Controller is not CombatController.Train controller) continue;
                var train = world.Trains.Single(t => t.Id == controller.Id);
                if (state.Retreating && train.Position is TrainPosition.Node)
                {
                    var destination = ChooseDestination(world, content, train, combat.Location);
                    train = TrainOperations.ChangeActivity(train with { SpeedMetersPerSecond = 0,
                        RetreatDestination = destination }, TrainActivity.Retreating, events);
                }
                else if (train.Activity == TrainActivity.Combat && train.Position is TrainPosition.Node)
                {
                    train = TrainOperations.ResolveNode(ref world, content, train, events,
                        new TrainDispatch(world, content), step, new(step, 0));
                }
                else if (train.Activity == TrainActivity.Combat)
                    train = TrainOperations.ChangeActivity(train, TrainActivity.Running, events);
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
                if (group.Home is null || state.Retreating || group.Objective is CreatureObjective.Railway)
                {
                    activity = group.Home is null ? CreatureActivity.Wandering : CreatureActivity.Retreating;
                    objective = null;
                    started = step;
                }
                else if (group.Objective is CreatureObjective.Settlement)
                    activity = CreatureActivity.RaidingSettlement;
                else if (CreatureQueries.AtOwnLair(world, group))
                {
                    world = CreatureOperations.BecomeGarrison(world, group, step);
                    continue;
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
            if (clearedLair is not null)
                world = RemoveLair(world, clearedLair, step);
            world = world with { Combats = world.Combats.Where(c => c.Id != combat.Id).ToImmutableArray() };
        }
        world = RetryRetreatDestinations(world, content);

        return (world, events.ToImmutable());
    }
}
