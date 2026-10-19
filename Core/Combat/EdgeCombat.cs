using System.Collections.Immutable;
using Train.Core.Contracts;
using Train.Core.Creatures;
using Train.Core.Railway;
using Train.Core.World;

namespace Train.Core.Combat;

internal static class EdgeCombat
{
    internal static WorldState Advance(WorldState world, WorldState before, CoreContentProjection content, ulong step,
        ImmutableArray<CoreEvent>.Builder events)
    {
        foreach (var id in world.CreatureGroups.Select(group => group.Id).OrderBy(group => group.Value).ToArray())
        {
            var group = CreatureQueries.FindGroup(world, id);
            if (group is null || group.Activity == CreatureActivity.Retreating || CreatureQueries.InCombat(world, id)
                || group.Location is not WorldLocation.Edge end) continue;
            var edge = world.Edges.Single(item => item.Id == end.Id);
            var creatureStart = CreatureStart(before, group, edge);
            TrainState? opener = null;
            foreach (var train in world.Trains.OrderBy(train => train.Id.Value))
            {
                if (!FreeTrain(world, train, edge.Id)) continue;
                var trainStart = TrainStart(before, train, edge);
                var trainEnd = ((TrainPosition.Edge)train.Position).DistanceMeters;
                if (!Crosses(creatureStart, end.DistanceMeters, trainStart, trainEnd)) continue;
                if (!CanFight(world, content, train, group)) continue;
                opener = train;
                break;
            }
            if (opener is null) continue;
            world = Open(world, content, step, events, group, end.DistanceMeters, opener);
        }

        foreach (var existing in world.Combats.Where(combat => combat.Location is WorldLocation.Edge).ToArray())
        {
            var combat = existing;
            var location = (WorldLocation.Edge)combat.Location;
            var edge = world.Edges.Single(item => item.Id == location.Id);
            foreach (var id in world.CreatureGroups.Select(group => group.Id).OrderBy(group => group.Value).ToArray())
            {
                var group = CreatureQueries.FindGroup(world, id);
                if (group is null || group.Activity == CreatureActivity.Retreating || CreatureQueries.InCombat(world, id)
                    || group.Location is not WorldLocation.Edge at || at.Id != edge.Id) continue;
                if (!Crosses(CreatureStart(before, group, edge), at.DistanceMeters, location.DistanceMeters, location.DistanceMeters))
                    continue;
                if (!combat.Controllers.Any(state => state.Controller is CombatController.Train train
                        && world.Trains.Any(item => item.Id == train.Id && CanFight(world, content, item, group))))
                    continue;
                world = JoinCreature(world, combat.Id, group, location.DistanceMeters, step, events);
                combat = world.Combats.Single(item => item.Id == combat.Id);
            }
            foreach (var train in world.Trains.OrderBy(train => train.Id.Value).ToArray())
            {
                if (!FreeTrain(world, train, edge.Id)) continue;
                var end = ((TrainPosition.Edge)train.Position).DistanceMeters;
                var reached = Crosses(TrainStart(before, train, edge), end, location.DistanceMeters, location.DistanceMeters);
                var behind = combat.Controllers.Any(state => state.Controller is CombatController.Train controller
                    && world.Trains.Any(item => item.Id == controller.Id && Behind(train, item)));
                if (!reached && !behind) continue;
                var group = world.CreatureGroups.FirstOrDefault(item =>
                    combat.Creatures.Any(creature => creature.Group == item.Id) && CanFight(world, content, train, item));
                if (group is null) continue;
                world = JoinTrain(world, content, combat.Id, train, step, events);
                combat = world.Combats.Single(item => item.Id == combat.Id);
            }
        }
        return world;
    }

    internal static TrainState[] TurnAround(WorldState world, TrainState train)
    {
        if (train.Position is not TrainPosition.Edge lead) return [train];
        var turned = new List<TrainState> { Reverse(train) };
        foreach (var other in world.Trains)
        {
            if (other.Id == train.Id || other.Position is not TrainPosition.Edge follower) continue;
            if (follower.Id != lead.Id || follower.Track != lead.Track || follower.Direction != lead.Direction)
                continue;
            if (!(lead.Direction == TravelDirection.Forward
                    ? follower.DistanceMeters < lead.DistanceMeters
                    : follower.DistanceMeters > lead.DistanceMeters)) continue;
            if (OtherCombat(world, train.Id, other.Id)) continue;
            turned.Add(Reverse(other));
        }
        return turned.ToArray();
    }

    private static WorldState Open(WorldState world, CoreContentProjection content, ulong step,
        ImmutableArray<CoreEvent>.Builder events, CreatureGroupState group, double distance, TrainState train)
    {
        if (world.Next.Combat == ulong.MaxValue)
            throw new InvalidOperationException("战斗身份已耗尽。");
        var location = new WorldLocation.Edge(((WorldLocation.Edge)group.Location).Id, distance);
        var controller = new CombatController.Train(train.Id);
        var units = CombatQueries.Units(world, controller);
        var retreating = train.Engagement.Encounter == EncounterAction.Retreat;
        var remaining = 0UL;
        if (retreating)
        {
            var seconds = CombatRules.Rule(world, content).RetreatSeconds;
            remaining = WorldResources.IntervalSteps(seconds)
                ?? throw new InvalidOperationException("战斗规则脱离时长超出可表示范围。");
        }
        var combat = new CombatState(new(world.Next.Combat), location, step,
            [new CombatControllerState(controller, step, retreating, remaining, null)],
            [Side(world, controller), new CombatSideSummary(new CombatOwner.Creatures(), [], [], [], new(0))])
        {
            Creatures = [new CreatureCombatantState(group.Id, step, group.Creatures.Length, false, 0)]
        };
        world = world with
        {
            Next = world.Next with { Combat = world.Next.Combat + 1 },
            Combats = world.Combats.Add(combat)
        };
        events.Add(new CoreEvent.CombatStarted(combat.Id, location,
            units.Add(new CombatUnitId.CreatureGroup(group.Id)).OrderBy(CombatQueries.UnitKey).ToImmutableArray()));
        if (retreating)
            foreach (var unit in units)
                events.Add(new CoreEvent.CombatRetreatStarted(combat.Id, unit, remaining));
        if (retreating)
            train = TrainOperations.ChangeActivity(train, TrainActivity.Retreating, events);
        else if (train.Activity is TrainActivity.Running or TrainActivity.Servicing or TrainActivity.Waiting
            or TrainActivity.NoRoute)
            train = TrainOperations.ChangeActivity(train, TrainActivity.Combat, events);
        world = world with { Trains = world.Trains.Select(item => item.Id == train.Id ? train : item).ToImmutableArray() };
        return Snap(world, group, distance);
    }

    private static WorldState JoinCreature(WorldState world, CombatId combatId, CreatureGroupState group, double distance,
        ulong step, ImmutableArray<CoreEvent>.Builder events)
    {
        var combat = world.Combats.Single(item => item.Id == combatId);
        if (combat.Creatures.Any(creature => creature.Group == group.Id))
            return Snap(world, group, distance);
        combat = combat with
        {
            Creatures = combat.Creatures.Add(new CreatureCombatantState(group.Id, step, group.Creatures.Length, false, 0))
                .OrderBy(creature => creature.Group.Value).ToImmutableArray(),
            Sides = EnsureCreatures(combat.Sides)
        };
        events.Add(new CoreEvent.CombatJoined(combat.Id, new CombatUnitId.CreatureGroup(group.Id)));
        world = world with { Combats = world.Combats.Select(item => item.Id == combat.Id ? combat : item).ToImmutableArray() };
        return Snap(world, group, distance);
    }

    private static WorldState Snap(WorldState world, CreatureGroupState group, double distance)
    {
        var edge = ((WorldLocation.Edge)group.Location).Id;
        return CreatureQueries.Replace(world, group with
        {
            Location = new WorldLocation.Edge(edge, distance),
            Activity = CreatureActivity.Combat
        });
    }

    private static WorldState JoinTrain(WorldState world, CoreContentProjection content, CombatId combatId, TrainState train,
        ulong step, ImmutableArray<CoreEvent>.Builder events)
    {
        var combat = world.Combats.Single(item => item.Id == combatId);
        var controller = new CombatController.Train(train.Id);
        if (combat.Controllers.Any(state => state.Controller == controller)) return world;
        var owner = CombatQueries.Owner(world, controller);
        combat = combat with
        {
            Controllers = combat.Controllers.Add(new CombatControllerState(controller, step, false, 0, null))
                .OrderBy(state => CombatQueries.ControllerKey(state.Controller)).ToImmutableArray(),
            Sides = combat.Sides.Any(side => side.Owner == owner) ? combat.Sides
                : combat.Sides.Add(Side(world, controller)).OrderBy(side => OwnerKey(side.Owner)).ToImmutableArray()
        };
        var units = CombatQueries.Units(world, controller);
        foreach (var unit in units) events.Add(new CoreEvent.CombatJoined(combat.Id, unit));
        if (train.Engagement.Encounter == EncounterAction.Retreat)
        {
            var seconds = CombatRules.Rule(world, content).RetreatSeconds;
            var remaining = WorldResources.IntervalSteps(seconds)
                ?? throw new InvalidOperationException("战斗规则脱离时长超出可表示范围。");
            var index = combat.Controllers.ToList().FindIndex(state => state.Controller == controller);
            combat = combat with
            {
                Controllers = combat.Controllers.SetItem(index, combat.Controllers[index] with
                {
                    Retreating = true, RemainingDisengagementSteps = remaining
                })
            };
            foreach (var unit in units)
                events.Add(new CoreEvent.CombatRetreatStarted(combat.Id, unit, remaining));
            train = TrainOperations.ChangeActivity(train, TrainActivity.Retreating, events);
        }
        else if (train.Activity is TrainActivity.Running or TrainActivity.Servicing or TrainActivity.Waiting
            or TrainActivity.NoRoute)
            train = TrainOperations.ChangeActivity(train, TrainActivity.Combat, events);
        world = world with
        {
            Combats = world.Combats.Select(item => item.Id == combat.Id ? combat : item).ToImmutableArray(),
            Trains = world.Trains.Select(item => item.Id == train.Id ? train : item).ToImmutableArray()
        };
        return world;
    }

    private static bool FreeTrain(WorldState world, TrainState train, EdgeId edge)
        => train.Activity != TrainActivity.Retreating
            && train.Position is TrainPosition.Edge position && position.Id == edge
            && !CombatQueries.TrainInCombat(world, train.Id);

    private static bool Behind(TrainState follower, TrainState lead)
    {
        if (follower.Position is not TrainPosition.Edge back || lead.Position is not TrainPosition.Edge front)
            return false;
        if (back.Id != front.Id || back.Track != front.Track || back.Direction != front.Direction) return false;
        return front.Direction == TravelDirection.Forward
            ? back.DistanceMeters < front.DistanceMeters
            : back.DistanceMeters > front.DistanceMeters;
    }

    private static bool OtherCombat(WorldState world, TrainId lead, TrainId follower)
    {
        CombatId? leadCombat = null;
        CombatId? followerCombat = null;
        foreach (var combat in world.Combats)
        {
            if (combat.Controllers.Any(state => state.Controller is CombatController.Train train && train.Id == lead))
                leadCombat = combat.Id;
            if (combat.Controllers.Any(state => state.Controller is CombatController.Train train && train.Id == follower))
                followerCombat = combat.Id;
        }
        return followerCombat is not null && followerCombat != leadCombat;
    }

    private static bool CanFight(WorldState world, CoreContentProjection content, TrainState train, CreatureGroupState group)
    {
        var units = CombatQueries.Units(world, new CombatController.Train(train.Id));
        var creature = new CombatUnitId.CreatureGroup(group.Id);
        var trainHits = train.Activity != TrainActivity.Retreating && units.Any(unit =>
            CombatQueries.Armed(world, content, unit) && CombatQueries.Hostile(world, unit, creature)
            && CombatQueries.Hittable(world, content, creature)
            && (train.Engagement.Scope == TargetScope.All || CombatQueries.Armed(world, content, creature)));
        var creatureHits = group.Activity != CreatureActivity.Retreating && CombatQueries.Armed(world, content, creature)
            && units.Any(unit => CombatQueries.Hostile(world, creature, unit) && CombatQueries.Hittable(world, content, unit));
        return trainHits || creatureHits;
    }

    private static double CreatureStart(WorldState before, CreatureGroupState end, EdgeMapItem edge)
    {
        var previous = CreatureQueries.FindGroup(before, end.Id);
        if (previous?.Location is WorldLocation.Edge at && at.Id == edge.Id) return at.DistanceMeters;
        if (previous?.Location is WorldLocation.Node node)
        {
            if (node.Id == edge.Start) return 0;
            if (node.Id == edge.End) return edge.LengthMeters;
        }
        return end.Direction == TravelDirection.Reverse ? edge.LengthMeters : 0;
    }

    private static double TrainStart(WorldState before, TrainState end, EdgeMapItem edge)
    {
        var previous = before.Trains.FirstOrDefault(train => train.Id == end.Id);
        if (previous?.Position is TrainPosition.Edge at && at.Id == edge.Id) return at.DistanceMeters;
        var position = (TrainPosition.Edge)end.Position;
        return position.Direction == TravelDirection.Reverse ? edge.LengthMeters : 0;
    }

    private static bool Crosses(double leftStart, double leftEnd, double rightStart, double rightEnd)
    {
        var start = rightStart - leftStart;
        var end = rightEnd - leftEnd;
        return start == 0 || end == 0 || Math.Sign(start) != Math.Sign(end);
    }

    private static TrainState Reverse(TrainState train)
    {
        var position = (TrainPosition.Edge)train.Position;
        return train with
        {
            Position = position with { Direction = TrainDispatch.Opposite(position.Direction) },
            OrderFacing = train.OrderFacing == TrainOrderFacing.FirstEndLeading
                ? TrainOrderFacing.LastEndLeading : TrainOrderFacing.FirstEndLeading,
            Activity = TrainActivity.Retreating,
            RetreatDestination = null
        };
    }

    private static CombatSideSummary Side(WorldState world, CombatController controller)
        => new(CombatQueries.Owner(world, controller), [], [], [], new(0));

    private static ImmutableArray<CombatSideSummary> EnsureCreatures(ImmutableArray<CombatSideSummary> sides)
        => sides.Any(side => side.Owner is CombatOwner.Creatures) ? sides
            : sides.Add(new(new CombatOwner.Creatures(), [], [], [], new(0)))
                .OrderBy(side => OwnerKey(side.Owner)).ToImmutableArray();

    private static (int Kind, ulong Id) OwnerKey(CombatOwner owner) => owner switch
    {
        CombatOwner.Player => (0, 0),
        CombatOwner.Settlement settlement => (1, settlement.Id.Value),
        CombatOwner.Creatures => (2, 0),
        _ => throw new InvalidOperationException("战斗方种类无效。")
    };
}
