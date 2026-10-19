using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core;
using Train.Core.Combat;
using Train.Core.Contracts;
using Train.Core.Creatures;
using Train.Core.Sessions;
using Train.Core.World;
using Train.Core.Railway;
using Train.Core.Tests.Combat;
using Train.Core.Tests.World;
using static Train.Core.Tests.Creatures.CreatureTestSupport;

namespace Train.Core.Tests.Creatures;

[TestClass]
public sealed class CreatureRaidTests
{
    [TestMethod]
    public void Returning_group_merges_into_the_fighting_garrison_before_combat_ends()
    {
        var (state, content, lair) = FightingLair();
        var world = state.World;
        world = WithPacks(world,
            Pack(1, lair.Id, new WorldLocation.Node(lair.Node), null, CreatureActivity.Combat, 0, null,
                Creature(1, "fixture:creature-a", 10)),
            Pack(2, lair.Id, new WorldLocation.Node(lair.Node), null, CreatureActivity.Retreating, 0, null,
                Creature(2, "fixture:creature-a", 10)));
        world = CombatAt(world, lair.Node, world.Trains.Single().Id, 1);
        world = world with { Trains = world.Trains.Select(train => train with { Activity = TrainActivity.Combat }).ToImmutableArray() };
        using var opened = new CoreSession(state with { World = world, Content = content, Position = new(1, 0) });
        Step(opened);
        Assert.AreEqual(1, opened.State.World.CreatureGroups.Length);
        var merged = Group(opened, 1);
        CollectionAssert.AreEqual(new[] { new CreatureId(1), new CreatureId(2) },
            merged.Creatures.Select(creature => creature.Id).ToArray());
        Assert.AreEqual(CreatureActivity.Combat, merged.Activity);
        using var restored = Reload(opened, content);
        Assert.AreEqual(1, restored.State.World.CreatureGroups.Length);
        CombatFixture.Accepted(restored.Submit(new CoreCommand.Retreat(world.Trains.Single().Id), CommandSource.AutomationTest));
        Step(restored);
        Assert.IsEmpty(restored.Read().World.Combats);
        var guard = restored.State.World.CreatureGroups.Single();
        Assert.AreEqual(CreatureActivity.Guarding, guard.Activity);
        Assert.AreEqual(new CreatureGroupId(1), guard.Id);
        CollectionAssert.AreEqual(new[] { 1UL, 2UL }, guard.Creatures.Select(creature => creature.Id.Value).ToArray());
    }

    [TestMethod]
    public void Wanderer_arriving_at_its_lair_fight_becomes_one_garrison_after_combat()
    {
        var (state, content, lair) = FightingLair(speed: 600);
        var edge = state.World.Edges.First(item => item.Start == lair.Node || item.End == lair.Node);
        var towardHome = edge.End == lair.Node;
        Assert.IsTrue(edge.LengthMeters > 5, "通向聚落的边短于 5 米。");
        var world = WithPacks(state.World,
            Pack(1, lair.Id, new WorldLocation.Node(lair.Node), null, CreatureActivity.Combat, 0, null,
                Creature(1, "fixture:creature-a", 10)),
            Pack(2, lair.Id, new WorldLocation.Edge(edge.Id, towardHome ? edge.LengthMeters - 5 : 5),
                towardHome ? TravelDirection.Forward : TravelDirection.Reverse,
                CreatureActivity.Wandering, 0, null, Creature(2, "fixture:creature-a", 10)));
        world = CombatAt(world, lair.Node, world.Trains.Single().Id, 1);
        world = world with { Trains = world.Trains.Select(train => train with { Activity = TrainActivity.Combat }).ToImmutableArray() };
        using var opened = new CoreSession(state with { World = world, Content = content, Position = new(1, 0) });
        Step(opened);
        Assert.AreEqual(2, opened.State.World.Combats.Single().Creatures.Length);
        Assert.AreEqual(new WorldLocation.Node(lair.Node), Group(opened, 2).Location);
        using var restored = Reload(opened, content);
        Assert.AreEqual(2, restored.State.World.CreatureGroups.Length);
        CombatFixture.Accepted(restored.Submit(new CoreCommand.Retreat(world.Trains.Single().Id), CommandSource.AutomationTest));
        Step(restored);
        var guard = restored.State.World.CreatureGroups.Single();
        Assert.AreEqual(CreatureActivity.Guarding, guard.Activity);
        CollectionAssert.AreEqual(new[] { 1UL, 2UL }, guard.Creatures.Select(creature => creature.Id.Value).ToArray());
    }

    [TestMethod]
    public void Retreating_group_leaves_the_fight_and_merges_at_home()
    {
        using var baseline = CombatFixture.Session(armedBuilding: false, damage: 10);
        var origin = baseline.State;
        var primary = Animal(origin.Content, "fixture:creature-a", 10, 600, 0.4, Idle, null);
        var secondary = Animal(origin.Content, "fixture:creature-b", 1000, 0, 1, Idle, null);
        var content = Lairs(origin.Content, primary, secondary, 4, Idle, Idle, 0, 0) with
        {
            CombatRules = [origin.Content.CombatRules[0] with { RetreatSeconds = Tick }]
        };
        var world = StopTrains(StructureOnly(origin.World, 10, Tick));
        var home = EmptyNode(world);
        var fight = EmptyNode(world, home);
        var (withLair, lair) = AddLair(world, home, content.LairTypes[0].Id);
        world = SingleEdge(withLair, home, fight, 10, 1);
        var engagement = new TrainEngagementRules(TargetPriority.ArmedFirst, TargetScope.All, EncounterAction.Engage, 0);
        world = PlaceTrain(world, new(fight, null, null, null, null, NodeTravelPhase.Waiting, 0, null),
            TrainActivity.Combat, 0, null, engagement);
        var train = world.Trains.Single().Id;
        world = WithPacks(world,
            Pack(1, lair.Id, new WorldLocation.Node(fight), null, CreatureActivity.Combat, 0, null,
                Creature(1, "fixture:creature-a", 10), Creature(2, "fixture:creature-a", 10)),
            Pack(2, lair.Id, new WorldLocation.Node(fight), null, CreatureActivity.Combat, 0, null,
                Creature(4, "fixture:creature-b", 1000)),
            Pack(3, lair.Id, new WorldLocation.Node(home), null, CreatureActivity.Guarding, 0, null,
                Creature(3, "fixture:creature-a", 10)));
        world = CombatAt(world, fight, train, 1, 2);
        using var session = Open(origin, world, content);
        Step(session);
        var wounded = Group(session, 1);
        Assert.AreEqual(1, wounded.Creatures.Length);
        Assert.AreEqual(10, wounded.Creatures.Single().Health);
        Assert.AreEqual(CreatureActivity.Retreating, wounded.Activity);
        Assert.AreEqual(1UL, CombatFixture.Participant(session, new CombatUnitId.CreatureGroup(new(1))).RemainingDisengagementSteps);
        Step(session);
        Assert.AreEqual(new WorldLocation.Node(fight), Group(session, 1).Location);
        var combat = ((Lookup<CombatView>.Found)session.Read().FindCombat(session.State.World.Combats.Single().Id)).Value;
        Assert.IsFalse(combat.Participants.Any(participant => participant.Unit is CombatUnitId.CreatureGroup creature
            && creature.Id == new CreatureGroupId(1)));
        Assert.IsTrue(combat.Participants.Any(participant => participant.Unit is CombatUnitId.Instance));
        Step(session);
        Assert.IsFalse(session.State.World.CreatureGroups.Any(group => group.Id == new CreatureGroupId(1)));
        var guard = Group(session, 3);
        Assert.AreEqual(CreatureActivity.Guarding, guard.Activity);
        Assert.AreEqual(new WorldLocation.Node(home), guard.Location);
        var survivors = guard.Creatures.Select(creature => creature.Id.Value).OrderBy(id => id).ToArray();
        Assert.AreEqual(2, survivors.Length);
        Assert.IsTrue(survivors.Contains(3UL));
        var lived = survivors.Single(id => id != 3);
        Assert.IsTrue(lived is 1UL or 2UL);
        Assert.IsInstanceOfType<Lookup<CreatureView>.Missing>(session.Read().FindCreature(new(lived == 1 ? 2UL : 1UL)));
    }

    [TestMethod]
    public void Crossing_on_an_edge_stops_the_creatures_and_the_train_brakes()
    {
        using var generated = CombatFixture.Session(armedBuilding: false, damage: 1);
        var content = Brake(Tune(generated.State.Content, 10, 0, 1, Idle, null, maximum: 1), 1);
        var world = StopTrains(generated.State.World);
        var ends = world.Nodes.Where(node => node.Occupant is NodeOccupant.Empty).Take(2).Select(node => node.Id).ToArray();
        var (withLair, lair) = AddLair(world, EmptyNode(world, ends), content.LairTypes[0].Id);
        const double length = 10000;
        world = SingleEdge(withLair, ends[0], ends[1], length, 1);
        var edge = world.Edges.Single();
        const double meeting = 500;
        var engagement = new TrainEngagementRules(TargetPriority.ArmedFirst, TargetScope.All, EncounterAction.Engage, 0);
        var train = world.Trains.Single() with
        {
            Position = new TrainPosition.Edge(edge.Id, 1, TravelDirection.Forward, meeting),
            Activity = TrainActivity.Running, SpeedMetersPerSecond = 10, Plan = new RunPlan(false, [new PlanStop.Waypoint(ends[1])]),
            NextStopIndex = 0, Engagement = engagement
        };
        world = world with { Trains = [train] };
        world = WithPacks(world, Pack(1, lair.Id, new WorldLocation.Edge(edge.Id, meeting), TravelDirection.Forward,
            CreatureActivity.Wandering, 0, null, Creature(1, "fixture:creature-a", 10)));
        using var session = Open(generated.State, world, content);
        Step(session);
        var combat = session.State.World.Combats.Single();
        Assert.AreEqual(new WorldLocation.Edge(edge.Id, meeting), combat.Location);
        Assert.AreEqual(new WorldLocation.Edge(edge.Id, meeting), Group(session, 1).Location);
        Assert.AreEqual(CreatureActivity.Combat, Group(session, 1).Activity);
        var moving = (TrainPosition.Edge)ReadTrain(session, train.Id).Position;
        Assert.AreEqual(edge.Id, moving.Id);
        Assert.AreEqual(10, ReadTrain(session, train.Id).SpeedMetersPerSecond, 1e-9);
        const double stepSeconds = 1.0 / 60;
        var expectedDistance = moving.DistanceMeters + 10 * stepSeconds - stepSeconds * stepSeconds / 2;
        Step(session);
        var braked = (TrainPosition.Edge)ReadTrain(session, train.Id).Position;
        Assert.AreEqual(expectedDistance, braked.DistanceMeters, 1e-9);
        Assert.AreEqual(10 - stepSeconds, ReadTrain(session, train.Id).SpeedMetersPerSecond, 1e-9);
        Assert.AreEqual(TrainActivity.Combat, ReadTrain(session, train.Id).Activity);
    }

    [TestMethod]
    public void A_train_that_does_not_cross_the_group_does_not_start_combat()
    {
        using var generated = CombatFixture.Session(armedBuilding: false, damage: 1);
        var content = Tune(generated.State.Content, 10, 0, 1, Idle, null, maximum: 1);
        var world = StopTrains(generated.State.World);
        var ends = world.Nodes.Where(node => node.Occupant is NodeOccupant.Empty).Take(2).Select(node => node.Id).ToArray();
        var (withLair, lair) = AddLair(world, EmptyNode(world, ends), content.LairTypes[0].Id);
        world = SingleEdge(withLair, ends[0], ends[1], 10000, 1);
        var edge = world.Edges.Single();
        var train = world.Trains.Single() with
        {
            Position = new TrainPosition.Edge(edge.Id, 1, TravelDirection.Forward, 100),
            Activity = TrainActivity.Running, SpeedMetersPerSecond = 10,
            Plan = new RunPlan(false, [new PlanStop.Waypoint(ends[1])]), NextStopIndex = 0,
            Engagement = new TrainEngagementRules(TargetPriority.ArmedFirst, TargetScope.All, EncounterAction.Engage, 0)
        };
        world = world with { Trains = [train] };
        world = WithPacks(world, Pack(1, lair.Id, new WorldLocation.Edge(edge.Id, 5000), TravelDirection.Forward,
            CreatureActivity.Wandering, 0, null, Creature(1, "fixture:creature-a", 10)));
        using var session = Open(generated.State, world, content);
        Step(session);
        Assert.IsEmpty(session.State.World.Combats);
        Assert.AreEqual(CreatureActivity.Wandering, Group(session, 1).Activity);
    }

    [TestMethod]
    public void A_moving_follower_does_not_join_until_it_stops_behind_the_combatant()
    {
        using var generated = CombatFixture.Session(trains: 2, armedBuilding: false, damage: 1);
        var content = Tune(generated.State.Content, 10, 0, 1, Idle, null, maximum: 1);
        var world = StopTrains(generated.State.World);
        var ends = world.Nodes.Where(node => node.Occupant is NodeOccupant.Empty).Take(2).Select(node => node.Id).ToArray();
        var (withLair, lair) = AddLair(world, EmptyNode(world, ends), content.LairTypes[0].Id);
        world = SingleEdge(withLair, ends[0], ends[1], 10000, 1);
        var edge = world.Edges.Single();
        var engagement = new TrainEngagementRules(TargetPriority.ArmedFirst, TargetScope.All, EncounterAction.Engage, 0);
        var plan = new RunPlan(false, [new PlanStop.Waypoint(ends[1])]);
        var trains = world.Trains.OrderBy(train => train.Id.Value).ToArray();
        TrainState Place(TrainState train, double distance, double speed) => train with
        {
            Position = new TrainPosition.Edge(edge.Id, 1, TravelDirection.Forward, distance),
            Activity = TrainActivity.Running, SpeedMetersPerSecond = speed, Plan = plan, NextStopIndex = 0,
            Engagement = engagement
        };
        world = world with { Trains = [Place(trains[0], 500, 10), Place(trains[1], 100, 10)] };
        world = WithPacks(world, Pack(1, lair.Id, new WorldLocation.Edge(edge.Id, 500), TravelDirection.Forward,
            CreatureActivity.Wandering, 0, null, Creature(1, "fixture:creature-a", 10)));
        using var moving = Open(generated.State, world, content);
        Step(moving);
        Assert.IsTrue(moving.State.World.Combats.Single().Controllers.Any(state =>
            state.Controller == new CombatController.Train(trains[0].Id)));
        Assert.IsFalse(moving.State.World.Combats.Single().Controllers.Any(state =>
            state.Controller == new CombatController.Train(trains[1].Id)));

        world = world with { Trains = [Place(trains[0], 500, 10), Place(trains[1], 499, 0)] };
        using var queued = Open(generated.State, world, content);
        Step(queued);
        Assert.AreEqual(0, ReadTrain(queued, trains[1].Id).SpeedMetersPerSecond, 1e-9);
        Assert.IsTrue(queued.State.World.Combats.Single().Controllers.Any(state =>
            state.Controller == new CombatController.Train(trains[1].Id)));
    }

    [TestMethod]
    public void Edge_retreat_waits_until_the_queue_stops_then_returns_to_the_departure_node()
    {
        using var generated = CombatFixture.Session(trains: 2, armedBuilding: false, damage: 1);
        var content = Brake(Tune(generated.State.Content, 100, 0, 1, Idle, new PartCapability.Weapon(1, Tick), maximum: 1) with
        {
            CombatRules = [generated.State.Content.CombatRules[0] with { RetreatSeconds = Tick }]
        }, 600);
        var world = StopTrains(generated.State.World);
        var ends = world.Nodes.Where(node => node.Occupant is NodeOccupant.Empty).Take(2).Select(node => node.Id).ToArray();
        var (withLair, lair) = AddLair(world, EmptyNode(world, ends), content.LairTypes[0].Id);
        world = SingleEdge(withLair, ends[0], ends[1], 10000, 1);
        var edge = world.Edges.Single();
        var trains = world.Trains.OrderBy(train => train.Id.Value).ToArray();
        var gap = (TrainOperations.Capabilities(world, content, trains[0]).LengthMeters
            + TrainOperations.Capabilities(world, content, trains[1]).LengthMeters) / 2
            + TrainOperations.Rule(world, content).MinimumGapMeters;
        const double leadDistance = 80;
        var engagement = new TrainEngagementRules(TargetPriority.ArmedFirst, TargetScope.All, EncounterAction.Retreat, 0);
        var plan = new RunPlan(false, [new PlanStop.Waypoint(ends[1])]);
        world = world with
        {
            Trains =
            [
                trains[0] with
                {
                    Position = new TrainPosition.Edge(edge.Id, 1, TravelDirection.Forward, leadDistance),
                    Activity = TrainActivity.Running, SpeedMetersPerSecond = 10, Plan = plan, NextStopIndex = 0,
                    Engagement = engagement, OrderFacing = TrainOrderFacing.FirstEndLeading
                },
                trains[1] with
                {
                    Position = new TrainPosition.Edge(edge.Id, 1, TravelDirection.Forward, leadDistance - 1),
                    Activity = TrainActivity.Running, SpeedMetersPerSecond = 0, Plan = plan, NextStopIndex = 0,
                    Engagement = engagement, OrderFacing = TrainOrderFacing.FirstEndLeading
                }
            ]
        };
        world = WithPacks(world, Pack(1, lair.Id, new WorldLocation.Edge(edge.Id, leadDistance), TravelDirection.Forward,
            CreatureActivity.Wandering, 0, null, Creature(1, "fixture:creature-a", 100)));
        using var session = Open(generated.State, world, content);
        var events = new List<CoreEvent>();
        double previousLead = 10;
        double previousFollower = 0;
        var previousForward = true;
        var flipped = false;
        for (var step = 0; step < 8 && !flipped; step++)
        {
            var stepEvents = Step(session).Events.Select(envelope => envelope.Event).ToArray();
            var leadNow = (TrainPosition.Edge)ReadTrain(session, trains[0].Id).Position;
            var followerNow = (TrainPosition.Edge)ReadTrain(session, trains[1].Id).Position;
            if (leadNow.Direction == TravelDirection.Reverse)
            {
                Assert.IsTrue(previousForward);
                Assert.AreEqual(0, previousLead, 1e-9);
                Assert.AreEqual(0, previousFollower, 1e-9);
                Assert.IsEmpty(stepEvents.OfType<CoreEvent.CombatHits>().ToArray());
                Assert.AreEqual(TravelDirection.Reverse, followerNow.Direction);
                Assert.AreEqual(0, ReadTrain(session, trains[0].Id).SpeedMetersPerSecond, 1e-9);
                Assert.AreEqual(0, ReadTrain(session, trains[1].Id).SpeedMetersPerSecond, 1e-9);
                Assert.AreEqual(edge.Start, session.State.World.Trains.Single(item => item.Id == trains[0].Id).RetreatDestination);
                Assert.AreEqual(edge.Start, session.State.World.Trains.Single(item => item.Id == trains[1].Id).RetreatDestination);
                Assert.AreEqual(TrainOrderFacing.LastEndLeading, ReadTrain(session, trains[0].Id).OrderFacing);
                Assert.AreEqual(TrainOrderFacing.LastEndLeading, ReadTrain(session, trains[1].Id).OrderFacing);
                Assert.IsFalse(session.State.World.Combats.Any(combat => combat.Controllers.Any(state =>
                    state.Controller == new CombatController.Train(trains[0].Id))));
                flipped = true;
            }
            else
            {
                Assert.AreEqual(TravelDirection.Forward, leadNow.Direction);
                Assert.AreEqual(TravelDirection.Forward, followerNow.Direction);
                previousLead = ReadTrain(session, trains[0].Id).SpeedMetersPerSecond;
                previousFollower = ReadTrain(session, trains[1].Id).SpeedMetersPerSecond;
            }
        }
        Assert.IsTrue(flipped);
        events.Clear();
        double? previousRear = null;
        for (var step = 0; step < 4000; step++)
        {
            var leadTrain = ReadTrain(session, trains[0].Id);
            var followerTrain = ReadTrain(session, trains[1].Id);
            if (leadTrain.Activity == TrainActivity.Stopped && followerTrain.Activity == TrainActivity.Stopped)
                break;
            if (leadTrain.Position is TrainPosition.Edge rear && followerTrain.Position is TrainPosition.Edge front)
            {
                var behind = Math.Max(rear.DistanceMeters, front.DistanceMeters);
                var ahead = Math.Min(rear.DistanceMeters, front.DistanceMeters);
                if (previousRear is { } seen)
                    Assert.IsTrue(behind + 1e-6 >= Math.Min(seen, ahead + gap));
                previousRear = behind;
            }
            events.AddRange(Step(session).Events.Select(envelope => envelope.Event));
        }
        Assert.IsEmpty(events.OfType<CoreEvent.CombatHits>());
        foreach (var id in new[] { trains[0].Id, trains[1].Id })
        {
            var train = ReadTrain(session, id);
            Assert.AreEqual(TrainActivity.Stopped, train.Activity);
            Assert.AreEqual(edge.Start, ((TrainPosition.Node)train.Position).Id);
            Assert.AreEqual(1, events.OfType<CoreEvent.TrainArrived>().Count(arrived => arrived.Train == id));
            Assert.AreEqual(1, events.OfType<CoreEvent.RetreatCompleted>().Count(completed => completed.Train == id));
        }
    }

    [TestMethod]
    public void Railway_raid_writes_one_break_per_track_then_retreats()
    {
        var baseline = Multi;
        var content = Tune(baseline.Content, 10, 0, 1, Idle, null, maximum: 1);
        content = content with
        {
            LairTypes = content.LairTypes.Select(type => type with { RailDamageWorkSeconds = Tick }).ToImmutableArray()
        };
        var lair = baseline.World.Lairs.Single();
        var world = SingleEdge(StopTrains(baseline.World), lair.Node, EmptyNode(baseline.World, lair.Node), 10, 2);
        var edge = world.Edges.Single();
        const double distance = 4;
        world = WithPacks(world, Pack(1, lair.Id, new WorldLocation.Edge(edge.Id, distance), TravelDirection.Forward,
            CreatureActivity.RaidingRailway, 0, new CreatureObjective.Railway(edge.Id, distance),
            Creature(1, "fixture:creature-a", 10)));
        using var session = Open(baseline, world, content);
        var result = Step(session);
        Assert.AreEqual(2, session.State.World.Edges.Single().Breaks.Length);
        Assert.IsTrue(session.State.World.Edges.Single().Breaks.All(broken => broken.DistanceMeters.Equals(distance)));
        CollectionAssert.AreEqual(new[] { 1, 2 }, session.State.World.Edges.Single().Breaks.Select(broken => broken.Track).ToArray());
        Assert.AreEqual(2, result.Events.Select(envelope => envelope.Event).OfType<CoreEvent.RailBroken>().Count());
        Assert.AreEqual(CreatureActivity.Retreating, Group(session, 1).Activity);
        Assert.IsNull(State(session, 1).Objective);
    }

    [TestMethod]
    public void Railway_raid_writes_nothing_when_break_ids_cannot_cover_every_track()
    {
        var baseline = Multi;
        var content = Tune(baseline.Content, 10, 0, 1, Idle, null, maximum: 1) with
        {
            LairTypes = baseline.Content.LairTypes.Select(type => type with { RailDamageWorkSeconds = Tick }).ToImmutableArray()
        };
        var lair = baseline.World.Lairs.Single();
        var world = SingleEdge(StopTrains(baseline.World), lair.Node, EmptyNode(baseline.World, lair.Node), 10, 2);
        var edge = world.Edges.Single();
        world = world with { Next = world.Next with { Break = ulong.MaxValue - 1 } };
        world = WithPacks(world, Pack(1, lair.Id, new WorldLocation.Edge(edge.Id, 4), TravelDirection.Forward,
            CreatureActivity.RaidingRailway, 0, new CreatureObjective.Railway(edge.Id, 4),
            Creature(1, "fixture:creature-a", 10)));
        using var session = Open(baseline, world, content);
        var result = Step(session);
        Assert.IsEmpty(session.State.World.Edges.Single().Breaks);
        Assert.AreEqual(0, result.Events.OfType<CoreEvent.RailBroken>().Count());
        Assert.AreEqual(CreatureActivity.RaidingRailway, Group(session, 1).Activity);
    }

    [TestMethod]
    public void Clearing_the_lair_pays_once_and_orphans_groups_still_outside()
    {
        using var baseline = CombatFixture.Session(armedBuilding: false, damage: 10);
        var origin = baseline.State;
        var reward = new Money(40);
        var content = Tune(origin.Content, 10, 0, 1, Idle, null, maximum: 1) with
        {
            LairTypes = origin.Content.LairTypes.Select(type => type with { ClearanceReward = reward }).ToImmutableArray()
        };
        var world = StopTrains(StructureOnly(origin.World, 10, Tick));
        var home = EmptyNode(world);
        var away = EmptyNode(world, home);
        var (withLair, lair) = AddLair(world, home, content.LairTypes[0].Id);
        var engagement = new TrainEngagementRules(TargetPriority.ArmedFirst, TargetScope.All, EncounterAction.Engage, 0);
        world = PlaceTrain(withLair, new(home, null, null, null, null, NodeTravelPhase.Waiting, 0, null),
            TrainActivity.Stopped, 0, null, engagement);
        world = WithPacks(world,
            Pack(1, lair.Id, new WorldLocation.Node(home), null, CreatureActivity.Guarding, 0, null,
                Creature(1, "fixture:creature-a", 10)),
            Pack(2, lair.Id, new WorldLocation.Node(away), null, CreatureActivity.Wandering, 0, null,
                Creature(2, "fixture:creature-a", 10)));
        var money = world.Money.Value;
        using var session = Open(origin, world, content);
        var result = Step(session);
        Assert.IsFalse(session.State.World.Lairs.Any(item => item.Id == lair.Id));
        Assert.IsInstanceOfType<NodeOccupant.Empty>(session.State.World.Nodes.Single(node => node.Id == home).Occupant);
        Assert.AreEqual(money + reward.Value, session.State.World.Money.Value);
        var events = result.Events.Select(envelope => envelope.Event).ToArray();
        var cleared = events.OfType<CoreEvent.LairCleared>().Single();
        Assert.AreEqual(reward, cleared.Reward);
        var ended = events.OfType<CoreEvent.CombatEnded>().Single();
        Assert.AreEqual(reward, ended.Summary.Sides.Single(side => side.Owner is CombatOwner.Player).Reward);
        var orphan = State(session, 2);
        Assert.IsNull(orphan.Home);
        Assert.AreEqual(CreatureActivity.Wandering, orphan.Activity);
        Assert.IsNull(orphan.Objective);
    }

    [TestMethod]
    public void A_due_step_founds_one_lair_on_the_only_legal_empty_node()
    {
        var baseline = Multi;
        var weighted = baseline.Content.LairTypes[0].Id;
        var content = Tune(baseline.Content, 10, 0, 1, Idle, null, maximum: 1) with
        {
            CombatRules = baseline.Content.CombatRules.Select(rule => rule with { NewLairIntervalSeconds = Tick }).ToImmutableArray(),
            LairTypes = baseline.Content.LairTypes.Select(type => type with
            {
                GenerationWeight = type.Id == weighted ? 3 : 0
            }).ToImmutableArray()
        };
        var settlement = baseline.World.Settlements.OrderBy(item => item.Id.Value).First();
        var world = PlayerOwned(StopTrains(baseline.World), settlement.Id);
        var players = world.Settlements.Where(item => item.Relation == SettlementRelation.PlayerOwned)
            .Select(item => item.Node).ToHashSet();
        bool BesidePlayer(NodeId node) => world.Edges.Any(edge =>
            (edge.Start == node && players.Contains(edge.End)) || (edge.End == node && players.Contains(edge.Start)));
        var candidate = world.Nodes.Where(node => node.Occupant is NodeOccupant.Empty && node.FoundingJob is null
                && !BesidePlayer(node.Id))
            .OrderBy(node => node.Id.Value).First().Id;
        var template = world.Edges[0];
        var next = world.Next.Edge;
        var added = world.Nodes.Where(node => node.Occupant is NodeOccupant.Empty && node.Id != candidate
                && node.FoundingJob is null && !BesidePlayer(node.Id))
            .Select(node => template with
            {
                Id = new EdgeId(next++), Start = settlement.Node, End = node.Id, LengthMeters = 10,
                Tracks = 1, Breaks = [], Works = []
            }).ToArray();
        world = world with
        {
            Edges = world.Edges.AddRange(added).OrderBy(edge => edge.Id.Value).ToImmutableArray(),
            Next = world.Next with { Edge = next }
        };
        var groups = world.CreatureGroups.Length;
        var lairs = world.Lairs.Length;
        var lairId = world.Next.Lair;
        using var session = Open(baseline, world, content);
        var result = Step(session);
        Assert.AreEqual(lairs + 1, session.State.World.Lairs.Length);
        Assert.AreEqual(groups, session.State.World.CreatureGroups.Length);
        var created = session.State.World.Lairs.Single(lair => lair.Id == new LairId(lairId));
        Assert.AreEqual(candidate, created.Node);
        Assert.AreEqual(weighted, created.Type);
        Assert.IsInstanceOfType<NodeOccupant.Lair>(session.State.World.Nodes.Single(node => node.Id == candidate).Occupant);
        var announced = result.Events.Select(envelope => envelope.Event).OfType<CoreEvent.LairCreated>().Single();
        Assert.AreEqual(created.Id, announced.Lair);
        Assert.AreEqual(candidate, announced.Node);
    }

    [TestMethod]
    public void A_group_that_lost_its_lair_wanders_after_disengaging()
    {
        using var generated = CombatFixture.Session(trains: 2, armedBuilding: false, damage: 10);
        var origin = generated.State;
        var content = Tune(origin.Content, 10000, 600, 1, Idle, null, maximum: 1);
        var world = StopTrains(StructureOnly(origin.World, 10, Tick));
        var home = EmptyNode(world);
        var away = world.Nodes.Where(node => node.Occupant is NodeOccupant.Empty && node.Id != home
                && world.Edges.Any(edge => edge.Start == node.Id || edge.End == node.Id))
            .OrderBy(node => node.Id.Value).First().Id;
        var (withLair, lair) = AddLair(world, home, content.LairTypes[0].Id);
        var engagement = new TrainEngagementRules(TargetPriority.ArmedFirst, TargetScope.All, EncounterAction.Engage, 0);
        var trains = withLair.Trains.OrderBy(train => train.Id.Value).ToArray();
        TrainState At(TrainState train, NodeId node, TrainActivity activity) => train with
        {
            Position = new TrainPosition.Node(node, null, null, null, null, NodeTravelPhase.Waiting, 0, null),
            Activity = activity, SpeedMetersPerSecond = 0, Plan = null, NextStopIndex = null,
            Engagement = engagement
        };
        world = WithPacks(withLair with { Trains = [At(trains[0], home, TrainActivity.Combat), At(trains[1], away, TrainActivity.Combat)] },
            Pack(1, lair.Id, new WorldLocation.Node(home), null, CreatureActivity.Combat, 1, null,
                Creature(1, "fixture:creature-a", 10)),
            Pack(2, lair.Id, new WorldLocation.Node(away), null, CreatureActivity.Retreating, 1, null,
                Creature(2, "fixture:creature-a", 10000)));
        var homeCombat = Fight(world, new WorldLocation.Node(home), trains[0].Id, 1, false, 0);
        world = world with { Combats = [homeCombat], Next = world.Next with { Combat = homeCombat.Id.Value + 1 } };
        var awayCombat = Fight(world, new WorldLocation.Node(away), trains[1].Id, 2, true, 2UL);
        world = world with
        {
            Combats = [homeCombat, awayCombat],
            Next = world.Next with { Combat = awayCombat.Id.Value + 1 }
        };
        using var session = new CoreSession(origin with { World = world, Content = content, Position = new(1, 0) });
        Step(session);
        Assert.IsFalse(session.State.World.Lairs.Any(item => item.Id == lair.Id));
        var orphan = State(session, 2);
        Assert.IsNull(orphan.Home);
        Assert.AreEqual(CreatureActivity.Retreating, orphan.Activity);
        Assert.AreEqual(new WorldLocation.Node(away), orphan.Location);
        Assert.IsTrue(session.State.World.Combats.Any(combat => combat.Creatures.Any(creature => creature.Group == orphan.Id)));
        CombatFixture.Accepted(session.Submit(new CoreCommand.Retreat(trains[1].Id), CommandSource.AutomationTest));
        Step(session);
        Assert.AreEqual(CreatureActivity.Wandering, State(session, 2).Activity);
        Assert.AreEqual(new WorldLocation.Node(away), State(session, 2).Location);
        Step(session);
        Assert.AreEqual(CreatureActivity.Wandering, State(session, 2).Activity);
        Assert.AreNotEqual(new WorldLocation.Node(away), State(session, 2).Location);
    }

    [TestMethod]
    public void Opposite_directions_that_exchange_order_start_combat_and_a_chase_that_does_not_pass_does_not()
    {
        using var generated = CombatFixture.Session(armedBuilding: false, damage: 1);
        var content = Tune(generated.State.Content, 10, 600, 1, Idle, null, maximum: 1);
        var world = StopTrains(generated.State.World);
        var ends = world.Nodes.Where(node => node.Occupant is NodeOccupant.Empty).Take(2).Select(node => node.Id).ToArray();
        var (withLair, lair) = AddLair(world, EmptyNode(world, ends), content.LairTypes[0].Id);
        world = SingleEdge(withLair, ends[0], ends[1], 10000, 1);
        var edge = world.Edges.Single();
        var engagement = new TrainEngagementRules(TargetPriority.ArmedFirst, TargetScope.All, EncounterAction.Engage, 0);
        var train = world.Trains.Single() with
        {
            Position = new TrainPosition.Edge(edge.Id, 1, TravelDirection.Forward, 100),
            Activity = TrainActivity.Running, SpeedMetersPerSecond = 10,
            Plan = new RunPlan(false, [new PlanStop.Waypoint(ends[1])]), NextStopIndex = 0, Engagement = engagement
        };
        var crossing = WithPacks(world with { Trains = [train] }, Pack(1, lair.Id, new WorldLocation.Edge(edge.Id, 105),
            TravelDirection.Reverse, CreatureActivity.Wandering, 0, null, Creature(1, "fixture:creature-a", 10)));
        using var exchanged = Open(generated.State, crossing, content);
        Step(exchanged);
        var creature = (WorldLocation.Edge)Group(exchanged, 1).Location;
        var moved = (TrainPosition.Edge)ReadTrain(exchanged, train.Id).Position;
        Assert.IsTrue(105 - 100 > 0 && creature.DistanceMeters - moved.DistanceMeters < 0);
        Assert.AreEqual(1, exchanged.State.World.Combats.Length);

        var chase = WithPacks(world with { Trains = [train] }, Pack(1, lair.Id, new WorldLocation.Edge(edge.Id, 120),
            TravelDirection.Forward, CreatureActivity.Wandering, 0, null, Creature(1, "fixture:creature-a", 10)));
        var slow = Tune(generated.State.Content, 10, 0, 1, Idle, null, maximum: 1);
        using var following = Open(generated.State, chase, slow);
        Step(following);
        var followed = (TrainPosition.Edge)ReadTrain(following, train.Id).Position;
        Assert.IsTrue(followed.DistanceMeters > 100 && followed.DistanceMeters < 120);
        Assert.AreEqual(120, ((WorldLocation.Edge)Group(following, 1).Location).DistanceMeters, 1e-9);
        Assert.IsEmpty(following.State.World.Combats);
        Assert.AreEqual(CreatureActivity.Wandering, Group(following, 1).Activity);
    }

    [TestMethod]
    public void Railway_raid_opens_repair_jobs_that_start_counting_on_the_next_step()
    {
        var baseline = Multi;
        var content = Tune(baseline.Content, 10, 0, 1, Idle, null, maximum: 1) with
        {
            LairTypes = baseline.Content.LairTypes.Select(type => type with { RailDamageWorkSeconds = Tick }).ToImmutableArray(),
            RailwayRules = baseline.Content.RailwayRules.Select(rule => rule with
            {
                RepairRailUnitsPerBreak = 1, RepairSecondsPerBreak = 10
            }).ToImmutableArray()
        };
        var lair = baseline.World.Lairs.Single();
        var settlement = baseline.World.Settlements.OrderBy(item => item.Id.Value).First();
        var world = PlayerOwned(StopTrains(baseline.World), settlement.Id);
        world = SingleEdge(world, lair.Node, EmptyNode(world, lair.Node), 10, 2);
        var edge = world.Edges.Single();
        var owner = world.Settlements.Single(item => item.Id == settlement.Id);
        var rail = content.RailwayRules.Single(rule => rule.Id == world.Source.Configuration.Rules.Railway);
        var good = new GoodId.Voxel(rail.RailGood);
        world = world with
        {
            RailwayJobs = world.RailwayJobs.Where(job => job.Edge != edge.Id).ToImmutableArray(),
            AutomaticRailRepairs = [new AutomaticRailRepair(edge.Id, settlement.Id)],
            Inventories = world.Inventories.Select(inventory => inventory.Id != owner.Inventory ? inventory : inventory with
            {
                Goods = inventory.Goods.Where(stack => stack.Good != good).Append(new GoodsQuantity(good, 40))
                    .OrderBy(stack => WorldReferences.GoodKey(stack.Good), StringComparer.Ordinal).ToImmutableArray()
            }).ToImmutableArray()
        };
        const double distance = 4;
        world = WithPacks(world, Pack(1, lair.Id, new WorldLocation.Edge(edge.Id, distance), TravelDirection.Forward,
            CreatureActivity.RaidingRailway, 0, new CreatureObjective.Railway(edge.Id, distance),
            Creature(1, "fixture:creature-a", 10)));
        using var session = Open(baseline, world, content);
        Step(session);
        var jobs = session.State.World.RailwayJobs.Where(job => job.Edge == edge.Id).OrderBy(job => job.Id.Value).ToArray();
        Assert.AreEqual(2, session.State.World.Edges.Single().Breaks.Length);
        Assert.AreEqual(2, jobs.Length);
        Assert.IsTrue(jobs.All(job => job.ElapsedTicks == 0 && job.Blockers.IsEmpty),
            string.Join("; ", jobs.Select(job => $"{job.ElapsedTicks}:{string.Join(",", job.Blockers.Select(problem => problem.Code + " " + problem.Field))}")));
        Step(session);
        jobs = session.State.World.RailwayJobs.Where(job => job.Edge == edge.Id).OrderBy(job => job.Id.Value).ToArray();
        Assert.IsTrue(jobs.All(job => job.ElapsedTicks == 1));
    }

    [TestMethod]
    public void A_railway_raid_interrupted_by_a_meeting_writes_no_breaks()
    {
        var baseline = Multi;
        var content = Tune(baseline.Content, 10, 0, 1, Idle, new PartCapability.Weapon(1, Tick), maximum: 1) with
        {
            LairTypes = baseline.Content.LairTypes.Select(type => type with { RailDamageWorkSeconds = Tick }).ToImmutableArray()
        };
        var lair = baseline.World.Lairs.Single();
        var away = EmptyNode(baseline.World, lair.Node);
        var world = SingleEdge(StopTrains(baseline.World), lair.Node, away, 10, 1);
        var edge = world.Edges.Single();
        const double distance = 4;
        var train = world.Trains.OrderBy(item => item.Id.Value).First() with
        {
            Position = new TrainPosition.Edge(edge.Id, 1, TravelDirection.Forward, distance),
            Activity = TrainActivity.Running, SpeedMetersPerSecond = 0,
            Plan = new RunPlan(false, [new PlanStop.Waypoint(away)]), NextStopIndex = 0,
            Engagement = new TrainEngagementRules(TargetPriority.ArmedFirst, TargetScope.All, EncounterAction.Engage, 0)
        };
        world = world with { Trains = world.Trains.Select(item => item.Id == train.Id ? train : item).ToImmutableArray() };
        world = WithPacks(world, Pack(1, lair.Id, new WorldLocation.Edge(edge.Id, distance), TravelDirection.Forward,
            CreatureActivity.RaidingRailway, 0, new CreatureObjective.Railway(edge.Id, distance),
            Creature(1, "fixture:creature-a", 10)));
        using var session = Open(baseline, world, content);
        Step(session);
        Assert.IsEmpty(session.State.World.Edges.Single().Breaks);
        Assert.AreEqual(CreatureActivity.Combat, Group(session, 1).Activity);
    }

    [TestMethod]
    public void An_edge_fight_restores_to_the_same_bytes_and_a_creature_focus_replays()
    {
        var evidence = EdgeCombatEvidence();
        using var continuous = new CoreSession(evidence);
        using var paused = new CoreSession(evidence);
        Step(continuous);
        Step(paused);
        var combat = paused.State.World.Combats.Single();
        Assert.IsInstanceOfType<CombatUnitId.CreatureGroup>(combat.Controllers.Single().FocusTarget);
        Assert.IsTrue(combat.Controllers.Single().Retreating && combat.Controllers.Single().RemainingDisengagementSteps > 0);
        var raid = paused.State.World.CreatureGroups.Single(group => group.Id == new CreatureGroupId(2));
        Assert.AreEqual(CreatureActivity.RaidingRailway, raid.Activity);
        Assert.IsNotNull(raid.Objective);
        var saved = WorldFixture.Success(paused.Capture(CapturePurpose.Save));
        var factory = CoreFactory.Create();
        var decoded = WorldFixture.Success(factory.DecodeState(saved.Encode()));
        using var candidate = WorldFixture.Success(factory.PrepareRestore(decoded, evidence.Content, WorldFixture.Options));
        using var restored = (CoreSession)candidate.Activate();
        Step(continuous);
        Step(restored);
        Step(continuous);
        Step(restored);
        Assert.AreEqual(WorldFixture.Success(continuous.Capture(CapturePurpose.Save)).Encode(),
            WorldFixture.Success(restored.Capture(CapturePurpose.Save)).Encode());
    }

    [TestMethod]
    public void Focus_fire_on_a_creature_group_strictly_replays()
    {
        using var generated = CombatFixture.Session(armedBuilding: false, damage: 1);
        var content = Tune(generated.State.Content, 10, 0, 1, Idle, null, maximum: 1);
        var world = StopTrains(generated.State.World);
        var edge = world.Edges.Where(item => item.Tracks > 0 && item.LengthMeters > 2)
            .OrderByDescending(item => item.LengthMeters).First();
        if (edge.Start.Value > edge.End.Value)
        {
            edge = edge with { Start = edge.End, End = edge.Start };
            world = world with { Edges = world.Edges.Select(item => item.Id == edge.Id ? edge : item).ToImmutableArray() };
        }
        var meeting = edge.LengthMeters / 2;
        var engagement = new TrainEngagementRules(TargetPriority.ArmedFirst, TargetScope.All, EncounterAction.Engage, 0);
        var train = world.Trains.OrderBy(item => item.Id.Value).First() with
        {
            Position = new TrainPosition.Edge(edge.Id, 1, TravelDirection.Forward, meeting),
            Activity = TrainActivity.Running, SpeedMetersPerSecond = 10,
            Plan = new RunPlan(false, [new PlanStop.Waypoint(edge.End)]), NextStopIndex = 0,
            CurrentRun = new RunAccumulatorState(new(0, 0), new(0), new(0), 0, 0, 0, []),
            Engagement = engagement
        };
        world = world with { Trains = world.Trains.Select(item => item.Id == train.Id ? train : item).ToImmutableArray() };
        var (withLair, lair) = AddLair(world, EmptyNode(world, edge.Start, edge.End), content.LairTypes[0].Id);
        world = WithPacks(withLair, Pack(1, lair.Id, new WorldLocation.Edge(edge.Id, meeting), TravelDirection.Forward,
            CreatureActivity.Wandering, 0, null, Creature(1, "fixture:creature-a", 10)));
        using var session = Open(generated.State, world, content);
        Step(session);
        var combat = session.State.World.Combats.Single();
        CombatFixture.Accepted(session.Submit(new CoreCommand.FocusFire(combat.Id, new CombatController.Train(train.Id),
            new WorldObjectId.CreatureGroup(new(1))), CommandSource.AutomationTest));
        var saved = WorldFixture.Success(session.Capture(CapturePurpose.Save));
        var recording = session.ReadRecording();
        var input = new CoreReplayInput(2, WorldFixture.Options.Runtime, recording.Starts[0].State.Encode(),
            recording.Commands, [saved.Checkpoint], recording.EndPosition, null);
        var prepared = CoreFactory.Create().PrepareReplay(input, WorldFixture.Options.Runtime, [content]);
        if (prepared is CoreResult<ICoreReplay>.Failure failed)
            Assert.Fail(string.Join("; ", failed.Problems.Select(problem =>
                $"{problem.Field} {problem.Code} {string.Join(",", problem.RelatedIdentities)}")));
        using var replay = ((CoreResult<ICoreReplay>.Success)prepared).Value;
        var result = replay.Advance(uint.MaxValue, saved.Position);
        Assert.IsNull(result.Difference);
        Assert.IsNull(result.Fault);
        Assert.AreEqual(saved.Encode(), replay.CaptureBoundary().Encode());
    }

    private static CoreContentProjection Tune(CoreContentProjection content, long health, double speed, double retreat,
        double duration, PartCapability.Weapon? weapon, int maximum = 3, double spawnSeconds = Idle, double raidSeconds = Idle,
        int roaming = 0, int radius = 0)
    {
        var primary = Animal(content, "fixture:creature-a", health, speed, retreat, duration, weapon);
        var secondary = Animal(content, "fixture:creature-b", health, speed, retreat, duration, null);
        return Lairs(content, primary, secondary, maximum, spawnSeconds, raidSeconds, roaming, radius);
    }

    private static (CoreState State, CoreContentProjection Content, LairState Lair) FightingLair(double speed = 0)
    {
        using var baseline = CombatFixture.Session(armedBuilding: false, damage: 1);
        var content = Tune(baseline.State.Content, 10, speed, 1, Idle, null, maximum: 4);
        var world = StopTrains(baseline.State.World);
        var node = EmptyNode(world);
        var (withLair, lair) = AddLair(world, node, content.LairTypes[0].Id);
        var engagement = new TrainEngagementRules(TargetPriority.ArmedFirst, TargetScope.All, EncounterAction.Engage, 0);
        world = PlaceTrain(withLair, new(node, null, null, null, null, NodeTravelPhase.Waiting, 0, null),
            TrainActivity.Stopped, 0, null, engagement);
        return (baseline.State with { World = world, Content = content }, content, lair);
    }

    private static WorldState CombatAt(WorldState world, NodeId node, TrainId train, params ulong[] groups)
    {
        var id = world.Next.Combat;
        return world with
        {
            Combats =
            [
                new CombatState(new(id), new WorldLocation.Node(node), 1,
                    [new CombatControllerState(new CombatController.Train(train), 1, false, 0, null)],
                    [new CombatSideSummary(new CombatOwner.Player(), [], [], [], new(0)),
                        new CombatSideSummary(new CombatOwner.Creatures(), [], [], [], new(0))])
                {
                    Creatures = groups.Select(group => new CreatureCombatantState(new(group), 1,
                    world.CreatureGroups.Single(item => item.Id == new CreatureGroupId(group)).Creatures.Length, false, 0))
                        .ToImmutableArray()
                }
            ],
            Next = world.Next with { Combat = id + 1 }
        };
    }

    private static CoreSession Reload(CoreSession session, CoreContentProjection content)
    {
        var saved = WorldFixture.Success(session.Capture(CapturePurpose.Save));
        var factory = CoreFactory.Create();
        var decodedResult = factory.DecodeState(saved.Encode());
        if (decodedResult is CoreResult<ICoreCapture>.Failure failure)
            Assert.Fail(string.Join("; ", failure.Problems.Select(problem => $"{problem.Field} {problem.Code}")));
        var decoded = ((CoreResult<ICoreCapture>.Success)decodedResult).Value;
        var candidate = WorldFixture.Success(factory.PrepareRestore(decoded, content, WorldFixture.Options));
        return (CoreSession)candidate.Activate();
    }

    private static CoreContentProjection Brake(CoreContentProjection content, double deceleration)
        => content with
        {
            RailwayRules = content.RailwayRules.Select(rule => rule with { BrakingDeceleration = deceleration })
                .ToImmutableArray()
        };

    private static CombatState Fight(WorldState world, WorldLocation location, TrainId train, ulong group,
        bool retreating, ulong remaining, bool trainRetreating = false)
    {
        var id = world.Next.Combat;
        return new CombatState(new(id), location, 1,
            [new CombatControllerState(new CombatController.Train(train), 1, trainRetreating, trainRetreating ? 5UL : 0, null)],
            [new CombatSideSummary(new CombatOwner.Player(), [], [], [], new(0)),
                new CombatSideSummary(new CombatOwner.Creatures(), [], [], [], new(0))])
        {
            Creatures = [new CreatureCombatantState(new(group), 1, 1, retreating, remaining)]
        };
    }
}
