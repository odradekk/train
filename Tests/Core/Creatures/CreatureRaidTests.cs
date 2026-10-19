using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core;
using Train.Core.Combat;
using Train.Core.Contracts;
using Train.Core.Creatures;
using Train.Core.Sessions;
using Train.Core.World;
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
        var content = Tune(generated.State.Content, 10, 0, 1, Idle, null, maximum: 1);
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
        Assert.AreNotEqual(meeting, moving.DistanceMeters);
        var speed = ReadTrain(session, train.Id).SpeedMetersPerSecond;
        var deceleration = content.RailwayRules[0].BrakingDeceleration;
        var (distance, slowed) = ExpectedBrake(moving.DistanceMeters, speed, deceleration, moving.Direction == TravelDirection.Forward, length);
        Step(session);
        var braked = (TrainPosition.Edge)ReadTrain(session, train.Id).Position;
        Assert.AreEqual(distance, braked.DistanceMeters, 1e-9);
        Assert.AreEqual(slowed, ReadTrain(session, train.Id).SpeedMetersPerSecond, 1e-9);
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
    public void Edge_retreat_turns_the_train_and_the_follower_back()
    {
        using var generated = CombatFixture.Session(trains: 2, armedBuilding: false, damage: 1);
        var content = Tune(generated.State.Content, 10, 0, 1, Idle, new PartCapability.Weapon(1, Tick), maximum: 1) with
        {
            CombatRules = [generated.State.Content.CombatRules[0] with { RetreatSeconds = Tick }]
        };
        var world = StopTrains(generated.State.World);
        var ends = world.Nodes.Where(node => node.Occupant is NodeOccupant.Empty).Take(2).Select(node => node.Id).ToArray();
        var (withLair, lair) = AddLair(world, EmptyNode(world, ends), content.LairTypes[0].Id);
        world = SingleEdge(withLair, ends[0], ends[1], 10000, 1);
        var edge = world.Edges.Single();
        var engagement = new TrainEngagementRules(TargetPriority.ArmedFirst, TargetScope.All, EncounterAction.Retreat, 0);
        var plan = new RunPlan(false, [new PlanStop.Waypoint(ends[1])]);
        var trains = world.Trains.OrderBy(train => train.Id.Value).ToArray();
        world = world with
        {
            Trains =
            [
                trains[0] with
                {
                    Position = new TrainPosition.Edge(edge.Id, 1, TravelDirection.Forward, 500),
                    Activity = TrainActivity.Running, SpeedMetersPerSecond = 10, Plan = plan, NextStopIndex = 0,
                    Engagement = engagement, OrderFacing = TrainOrderFacing.FirstEndLeading
                },
                trains[1] with
                {
                    Position = new TrainPosition.Edge(edge.Id, 1, TravelDirection.Forward, 100),
                    Activity = TrainActivity.Running, SpeedMetersPerSecond = 10, Plan = plan, NextStopIndex = 0,
                    Engagement = engagement, OrderFacing = TrainOrderFacing.FirstEndLeading
                }
            ]
        };
        world = WithPacks(world, Pack(1, lair.Id, new WorldLocation.Edge(edge.Id, 500), TravelDirection.Forward,
            CreatureActivity.Wandering, 0, null, Creature(1, "fixture:creature-a", 10)));
        using var session = Open(generated.State, world, content);
        Step(session);
        Assert.AreEqual(TravelDirection.Forward, ((TrainPosition.Edge)ReadTrain(session, trains[0].Id).Position).Direction);
        Assert.AreEqual(TrainActivity.Retreating, ReadTrain(session, trains[0].Id).Activity);
        Step(session);
        var lead = (TrainPosition.Edge)ReadTrain(session, trains[0].Id).Position;
        var follower = (TrainPosition.Edge)ReadTrain(session, trains[1].Id).Position;
        Assert.AreEqual(TravelDirection.Reverse, lead.Direction);
        Assert.AreEqual(TravelDirection.Reverse, follower.Direction);
        Assert.AreEqual(TrainOrderFacing.LastEndLeading, ReadTrain(session, trains[0].Id).OrderFacing);
        Assert.AreEqual(TrainOrderFacing.LastEndLeading, ReadTrain(session, trains[1].Id).OrderFacing);
        Assert.AreEqual(edge.Id, lead.Id);
        Assert.AreEqual(edge.Id, follower.Id);
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
    public void A_restored_creature_run_matches_the_continuous_run()
    {
        var baseline = Multi;
        var content = Tune(baseline.Content, 10, 0, 1, Idle, null, maximum: 3, spawnSeconds: Tick);
        using var continuous = Open(baseline, StopTrains(baseline.World), content);
        using var paused = Open(baseline, StopTrains(baseline.World), content);
        for (var step = 0; step < 3; step++)
        {
            Step(continuous);
            Step(paused);
        }
        var saved = WorldFixture.Success(paused.Capture(CapturePurpose.Save));
        var factory = CoreFactory.Create();
        var decoded = WorldFixture.Success(factory.DecodeState(saved.Encode()));
        using var candidate = WorldFixture.Success(factory.PrepareRestore(decoded, content, WorldFixture.Options));
        using var restored = (CoreSession)candidate.Activate();
        Step(continuous);
        Step(restored);
        Step(continuous);
        Step(restored);
        Assert.AreEqual(WorldFixture.Success(continuous.Capture(CapturePurpose.Save)).Encode(),
            WorldFixture.Success(restored.Capture(CapturePurpose.Save)).Encode());
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

    private static (double Distance, double Speed) ExpectedBrake(double distance, double speed, double deceleration,
        bool forward, double length)
    {
        var stopping = speed * speed / (2 * deceleration);
        var coast = speed * Tick - deceleration * Tick * Tick / 2;
        var travel = coast >= stopping ? stopping : coast;
        var next = forward ? distance + travel : distance - travel;
        if (next <= 0 || next >= length) return (next <= 0 ? 0 : length, 0);
        return (next, coast >= stopping ? 0 : speed - deceleration * Tick);
    }
}
