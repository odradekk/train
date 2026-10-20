using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Combat;
using Train.Core.Contracts;
using Train.Core.Creatures;
using Train.Core.Sessions;
using Train.Core.Tests.Combat;
using Train.Core.Tests.World;
using static Train.Core.Tests.Creatures.CreatureTestSupport;

namespace Train.Core.Tests.Creatures;

[TestClass]
public sealed class CreatureBehaviorTests
{
    [TestMethod]
    public void Spawn_fills_the_garrison_up_to_the_maximum_then_stops()
    {
        using var session = Ecology(speed: 0, maximum: 2, spawnSeconds: Tick, raidSeconds: Idle);
        var lair = session.State.World.Lairs.Single();
        var nextGroup = session.State.World.Next.CreatureGroup;
        var nextCreature = session.State.World.Next.Creature;
        Step(session);
        var group = Group(session, nextGroup);
        Assert.AreEqual(CreatureActivity.Guarding, group.Activity);
        Assert.AreEqual(new WorldLocation.Node(lair.Node), group.Location);
        Assert.IsNull(group.Direction);
        Assert.AreEqual(10, group.Creatures.Single().Health);
        Assert.AreEqual(10, group.Creatures.Single().MaximumHealth);
        Assert.AreEqual(new CreatureId(nextCreature), group.Creatures.Single().Id);
        Assert.AreEqual(1UL, State(session, nextGroup).ActivityStartedAtStep);
        var listed = ((Lookup<LairView>.Found)session.Read().FindLair(lair.Id)).Value;
        Assert.AreEqual(new CreatureGroupId(nextGroup), listed.Groups.Single());

        Step(session);
        group = Group(session, nextGroup);
        CollectionAssert.AreEqual(
            new[] { new CreatureId(nextCreature), new CreatureId(nextCreature + 1) },
            group.Creatures.Select(creature => creature.Id).ToArray());
        Assert.IsTrue(group.Creatures.All(creature => creature.Health == 10 && creature.MaximumHealth == 10));

        Step(session);
        Assert.AreEqual(2, Group(session, nextGroup).Creatures.Length);
        Assert.AreEqual(1, session.Read().World.CreatureGroups.Length);
    }

    [TestMethod]
    public void Spawn_while_a_group_is_away_opens_a_new_garrison()
    {
        var baseline = Multi;
        var lair = baseline.World.Lairs.Single();
        var away = EmptyNode(baseline.World, lair.Node);
        var content = Tune(baseline.Content, 10, 0, 1, Idle, null, maximum: 3, spawnSeconds: Tick);
        var world = WithPacks(StopTrains(baseline.World), Pack(1, lair.Id, new WorldLocation.Node(away), null,
            CreatureActivity.Wandering, 0, null, Creature(1, "fixture:creature-a", 10)));
        using var session = Open(baseline, world, content);
        Step(session);
        var original = Group(session, 1);
        Assert.AreEqual(CreatureActivity.Wandering, original.Activity);
        Assert.AreEqual(new WorldLocation.Node(away), original.Location);
        Assert.AreEqual(new CreatureId(1), original.Creatures.Single().Id);
        var garrison = Group(session, 2);
        Assert.AreEqual(CreatureActivity.Guarding, garrison.Activity);
        Assert.AreEqual(new WorldLocation.Node(lair.Node), garrison.Location);
        Assert.AreEqual(10, garrison.Creatures.Single().Health);
        Assert.AreEqual(new CreatureId(2), garrison.Creatures.Single().Id);
    }

    [TestMethod]
    public void Spawn_on_the_sortie_step_leaves_with_the_new_garrison()
    {
        var baseline = Multi;
        var lair = baseline.World.Lairs.Single();
        var content = Tune(baseline.Content, 10, 0, 1, Idle, null, maximum: 3, spawnSeconds: Tick, raidSeconds: Tick, roaming: 1);
        var world = StopTrains(baseline.World) with
        {
            Edges = baseline.World.Edges.Select(edge => edge with { Tracks = 0 }).ToImmutableArray()
        };
        var nextGroup = world.Next.CreatureGroup;
        var nextCreature = world.Next.Creature;
        using var session = Open(baseline, world, content);
        Step(session);
        var group = Group(session, nextGroup);
        Assert.AreEqual(CreatureActivity.Wandering, group.Activity);
        Assert.AreEqual(new WorldLocation.Node(lair.Node), group.Location);
        Assert.AreEqual(new CreatureId(nextCreature), group.Creatures.Single().Id);
        Assert.AreEqual(1UL, State(session, nextGroup).ActivityStartedAtStep);
        Assert.AreEqual(1, session.State.World.CreatureGroups.Length);
    }

    [TestMethod]
    public void Returning_group_merges_into_the_garrison_and_keeps_that_id()
    {
        var baseline = Multi;
        var lair = baseline.World.Lairs.Single();
        var content = Tune(baseline.Content, 10, 0, 1, Idle, null, maximum: 2);
        var home = new WorldLocation.Node(lair.Node);
        var world = WithPacks(StopTrains(baseline.World),
            Pack(1, lair.Id, home, null, CreatureActivity.Retreating, 0, null, Creature(1, "fixture:creature-a", 10)),
            Pack(2, lair.Id, home, null, CreatureActivity.Guarding, 0, null, Creature(2, "fixture:creature-a", 8)));
        using var session = Open(baseline, world, content);
        Step(session);
        Assert.IsInstanceOfType<Lookup<CreatureGroupView>.Missing>(session.Read().FindCreatureGroup(new(1)));
        var garrison = Group(session, 2);
        Assert.AreEqual(CreatureActivity.Guarding, garrison.Activity);
        Assert.AreEqual(home, garrison.Location);
        CollectionAssert.AreEqual(new[] { new CreatureId(1), new CreatureId(2) },
            garrison.Creatures.Select(creature => creature.Id).ToArray());
        Assert.AreEqual(10, garrison.Creatures[0].Health);
        Assert.AreEqual(8, garrison.Creatures[1].Health);
        Assert.AreEqual(0UL, State(session, 2).ActivityStartedAtStep);
    }

    [TestMethod]
    public void Wandering_onto_the_lair_does_not_merge()
    {
        var baseline = Multi;
        var lair = baseline.World.Lairs.Single();
        var other = EmptyNode(baseline.World, lair.Node);
        var content = Tune(baseline.Content, 10, 60, 1, Idle, null, maximum: 2);
        var edgeWorld = SingleEdge(StopTrains(baseline.World), other, lair.Node, 10, 0);
        var edge = edgeWorld.Edges.Single();
        var world = WithPacks(edgeWorld,
            Pack(1, lair.Id, new WorldLocation.Edge(edge.Id, 9.75), TravelDirection.Forward,
                CreatureActivity.Wandering, 0, null, Creature(1, "fixture:creature-a", 10)),
            Pack(2, lair.Id, new WorldLocation.Node(lair.Node), null, CreatureActivity.Guarding, 0, null,
                Creature(2, "fixture:creature-a", 8)));
        using var session = Open(baseline, world, content);
        Step(session);
        var wanderer = Group(session, 1);
        Assert.AreEqual(CreatureActivity.Wandering, wanderer.Activity);
        Assert.AreEqual(new WorldLocation.Node(lair.Node), wanderer.Location);
        Assert.AreEqual(new CreatureId(1), wanderer.Creatures.Single().Id);
        var garrison = Group(session, 2);
        Assert.AreEqual(CreatureActivity.Guarding, garrison.Activity);
        Assert.AreEqual(new CreatureId(2), garrison.Creatures.Single().Id);
        Assert.AreEqual(8, garrison.Creatures.Single().Health);
    }

    [TestMethod]
    public void Group_advances_by_speed_times_one_sixtieth_of_a_second()
    {
        var baseline = Multi;
        var lair = baseline.World.Lairs.Single();
        var edge = baseline.World.Edges.OrderByDescending(item => item.LengthMeters).First();
        const double speed = 60;
        var delta = speed * Tick;
        Assert.IsTrue(edge.LengthMeters > 1 + delta, $"夹具最长边只有 {edge.LengthMeters} 米。");
        var content = Tune(baseline.Content, 10, speed, 1, Idle, null, maximum: 1);
        var world = WithPacks(StopTrains(baseline.World), Pack(1, lair.Id, new WorldLocation.Edge(edge.Id, 1),
            TravelDirection.Forward, CreatureActivity.Wandering, 0, null, Creature(1, "fixture:creature-a", 10)));
        using var session = Open(baseline, world, content);
        Step(session);
        var group = Group(session, 1);
        Assert.AreEqual(speed, group.SpeedMetersPerSecond);
        var location = (WorldLocation.Edge)group.Location;
        Assert.AreEqual(edge.Id, location.Id);
        Assert.AreEqual(1 + delta, location.DistanceMeters);
        Assert.AreEqual(TravelDirection.Forward, group.Direction);
    }

    [TestMethod]
    public void Group_arrives_at_the_far_node_when_the_remainder_is_shorter_than_the_step()
    {
        var baseline = Multi;
        var world = StopTrains(baseline.World);
        var lair = world.Lairs.Single();
        const double speed = 60;
        var delta = speed * Tick;
        var choice = world.Edges.OrderByDescending(edge => edge.LengthMeters)
            .Select(edge => (edge, forward: !Npc(world, edge.End), far: Npc(world, edge.End) ? edge.Start : edge.End))
            .FirstOrDefault(item => item.edge.LengthMeters > delta && !Npc(world, item.far));
        Assert.IsNotNull(choice.edge, "夹具里没有可以到达的非 NPC 端点。");
        var distance = choice.forward ? choice.edge.LengthMeters - delta / 2 : delta / 2;
        var direction = choice.forward ? TravelDirection.Forward : TravelDirection.Reverse;
        var content = Tune(baseline.Content, 10, speed, 1, Idle, null, maximum: 1);
        world = WithPacks(world, Pack(1, lair.Id, new WorldLocation.Edge(choice.edge.Id, distance), direction,
            CreatureActivity.Wandering, 0, null, Creature(1, "fixture:creature-a", 10)));
        using var session = Open(baseline, world, content);
        Step(session);
        var group = Group(session, 1);
        Assert.AreEqual(new WorldLocation.Node(choice.far), group.Location);
        Assert.IsNull(group.Direction);
    }

    [TestMethod]
    public void Wander_does_not_enter_the_only_neighbor_when_it_is_an_npc_settlement()
    {
        var baseline = Multi;
        var lair = baseline.World.Lairs.Single();
        var npc = baseline.World.Settlements.OrderBy(item => item.Id.Value).First();
        Assert.AreNotEqual(lair.Node, npc.Node);
        var content = Tune(baseline.Content, 10, 60, 1, Idle, null, maximum: 1, roaming: 4);
        var world = SingleEdge(StopTrains(baseline.World), lair.Node, npc.Node, 10, 0);
        world = WithPacks(world, Pack(1, lair.Id, new WorldLocation.Node(lair.Node), null,
            CreatureActivity.Wandering, 0, null, Creature(1, "fixture:creature-a", 10)));
        using var session = Open(baseline, world, content);
        Step(session);
        Assert.AreEqual(new WorldLocation.Node(lair.Node), Group(session, 1).Location);
    }

    [TestMethod]
    public void Group_already_on_an_edge_does_not_arrive_at_an_npc_settlement()
    {
        var baseline = Multi;
        var lair = baseline.World.Lairs.Single();
        var npc = baseline.World.Settlements.OrderBy(item => item.Id.Value).First();
        var content = Tune(baseline.Content, 10, 60, 1, Idle, null, maximum: 1);
        var world = SingleEdge(StopTrains(baseline.World), lair.Node, npc.Node, 10, 0);
        var edge = world.Edges.Single();
        world = WithPacks(world, Pack(1, lair.Id, new WorldLocation.Edge(edge.Id, 9.75), TravelDirection.Forward,
            CreatureActivity.Wandering, 0, null, Creature(1, "fixture:creature-a", 10)));
        using var session = Open(baseline, world, content);
        Step(session);
        var location = (WorldLocation.Edge)Group(session, 1).Location;
        Assert.AreEqual(edge.Id, location.Id);
        Assert.AreEqual(8.75, location.DistanceMeters);
        Assert.AreEqual(TravelDirection.Reverse, Group(session, 1).Direction);
        for (var step = 0; step < 20 && Group(session, 1).Location is WorldLocation.Edge; step++)
            Step(session);
        var group = Group(session, 1);
        Assert.AreEqual(CreatureActivity.Wandering, group.Activity);
        Assert.AreEqual(new WorldLocation.Node(lair.Node), group.Location);
        Assert.IsNull(group.Direction);
    }

    [TestMethod]
    public void Retreating_group_facing_an_npc_endpoint_merges_into_the_home_garrison()
    {
        var baseline = Multi;
        var lair = baseline.World.Lairs.Single();
        var npc = baseline.World.Settlements.OrderBy(item => item.Id.Value).First();
        var mid = EmptyNode(baseline.World, lair.Node);
        Assert.AreNotEqual(npc.Node, mid);
        Assert.IsTrue(Npc(baseline.World, npc.Node));
        var content = Tune(baseline.Content, 10, 60, 1, Idle, null, maximum: 2);
        var stopped = StopTrains(baseline.World);
        var templates = stopped.Edges.OrderBy(edge => edge.Id.Value).Take(2).ToArray();
        var homeEdge = templates[0] with
        {
            Start = lair.Node, End = mid, LengthMeters = 10, Tracks = 0, Breaks = [], Works = []
        };
        var npcEdge = templates[1] with
        {
            Start = npc.Node, End = mid, LengthMeters = 10, Tracks = 0, Breaks = [], Works = []
        };
        var distance = Math.BitIncrement(0d);
        var world = WithPacks(stopped with { Edges = [homeEdge, npcEdge], TrackHandoffs = [] },
            Pack(1, lair.Id, new WorldLocation.Edge(npcEdge.Id, distance), TravelDirection.Reverse,
                CreatureActivity.Retreating, 0, null, Creature(1, "fixture:creature-a", 10)),
            Pack(2, lair.Id, new WorldLocation.Node(lair.Node), null, CreatureActivity.Guarding, 0, null,
                Creature(2, "fixture:creature-a", 10)));
        using var session = Open(baseline, world, content);
        Step(session);
        var moved = Group(session, 1);
        var onEdge = (WorldLocation.Edge)moved.Location;
        Assert.AreEqual(npcEdge.Id, onEdge.Id);
        Assert.IsTrue(onEdge.DistanceMeters > distance);
        Assert.AreEqual(TravelDirection.Forward, moved.Direction);
        var merged = false;
        for (var step = 0; step < 30 && !merged; step++)
        {
            Step(session);
            merged = session.State.World.CreatureGroups.All(group => group.Id.Value != 1);
        }
        Assert.IsTrue(merged);
        var guard = Group(session, 2);
        Assert.AreEqual(CreatureActivity.Guarding, guard.Activity);
        Assert.AreEqual(new WorldLocation.Node(lair.Node), guard.Location);
        CollectionAssert.AreEqual(new[] { 1UL, 2UL }, guard.Creatures.Select(creature => creature.Id.Value).ToArray());
    }

    [TestMethod]
    public void Sortie_raids_the_only_player_settlement_and_returns_when_the_duration_elapses()
    {
        var baseline = Multi;
        var lair = baseline.World.Lairs.Single();
        var settlement = baseline.World.Settlements.OrderBy(item => item.Id.Value).First();
        Assert.AreNotEqual(lair.Node, settlement.Node);
        var content = Tune(baseline.Content, 10, 600, 1, 2 * Tick, null, maximum: 1, raidSeconds: 2 * Tick, radius: 5);
        var world = SingleEdge(PlayerOwned(StopTrains(baseline.World), settlement.Id), lair.Node, settlement.Node, 10, 0);
        world = WithPacks(world, Pack(1, lair.Id, new WorldLocation.Node(lair.Node), null,
            CreatureActivity.Guarding, 0, null, Creature(1, "fixture:creature-a", 10)));
        using var session = Open(baseline, world, content);

        Step(session);
        Assert.AreEqual(CreatureActivity.Guarding, Group(session, 1).Activity);
        Assert.AreEqual(new WorldLocation.Node(lair.Node), Group(session, 1).Location);

        Step(session);
        Assert.AreEqual(CreatureActivity.RaidingSettlement, Group(session, 1).Activity);
        Assert.AreEqual(new WorldLocation.Node(lair.Node), Group(session, 1).Location);
        var objective = (CreatureObjective.Settlement)State(session, 1).Objective!;
        Assert.AreEqual(settlement.Node, objective.Node);
        Assert.AreEqual(2UL, State(session, 1).ActivityStartedAtStep);

        Step(session);
        Assert.AreEqual(CreatureActivity.RaidingSettlement, Group(session, 1).Activity);
        Assert.AreEqual(new WorldLocation.Node(settlement.Node), Group(session, 1).Location);
        Assert.AreEqual(3UL, State(session, 1).ActivityStartedAtStep);

        Step(session);
        Assert.AreEqual(CreatureActivity.RaidingSettlement, Group(session, 1).Activity);
        Assert.AreEqual(new WorldLocation.Node(settlement.Node), Group(session, 1).Location);
        Assert.AreEqual(3UL, State(session, 1).ActivityStartedAtStep);

        Step(session);
        Assert.AreEqual(CreatureActivity.Guarding, Group(session, 1).Activity);
        Assert.AreEqual(new WorldLocation.Node(lair.Node), Group(session, 1).Location);
        Assert.AreEqual(new CreatureId(1), Group(session, 1).Creatures.Single().Id);
        Assert.AreEqual(5UL, State(session, 1).ActivityStartedAtStep);
        Assert.IsNull(State(session, 1).Objective);
    }

    [TestMethod]
    public void Sortie_without_a_player_settlement_selects_the_only_tracked_edge()
    {
        var baseline = Multi;
        var lair = baseline.World.Lairs.Single();
        var other = EmptyNode(baseline.World, lair.Node);
        var content = Tune(baseline.Content, 10, 0, 1, Idle, null, maximum: 1, raidSeconds: Tick, radius: 5);
        var world = SingleEdge(StopTrains(baseline.World), lair.Node, other, 10, 1);
        var edge = world.Edges.Single();
        world = WithPacks(world, Pack(1, lair.Id, new WorldLocation.Node(lair.Node), null,
            CreatureActivity.Guarding, 0, null, Creature(1, "fixture:creature-a", 10)));
        using var session = Open(baseline, world, content);
        Step(session);
        Assert.AreEqual(CreatureActivity.RaidingRailway, Group(session, 1).Activity);
        Assert.AreEqual(new WorldLocation.Node(lair.Node), Group(session, 1).Location);
        var objective = (CreatureObjective.Railway)State(session, 1).Objective!;
        Assert.AreEqual(edge.Id, objective.Edge);
        Assert.IsTrue(objective.DistanceMeters > 0 && objective.DistanceMeters < edge.LengthMeters);
        Assert.AreEqual(1UL, State(session, 1).ActivityStartedAtStep);
    }

    [TestMethod]
    public void Railway_raid_anchors_its_clock_only_when_it_reaches_the_chosen_distance()
    {
        const double speed = 60;
        var delta = speed * Tick;
        using var arrived = RailwayApproach(speed, 1, 1 + delta);
        Assert.AreEqual(1 + delta, ((WorldLocation.Edge)Group(arrived, 1).Location).DistanceMeters);
        Assert.AreEqual(1UL, State(arrived, 1).ActivityStartedAtStep);

        using var shortfall = RailwayApproach(speed, 1, 1 + delta + delta);
        Assert.AreEqual(1 + delta, ((WorldLocation.Edge)Group(shortfall, 1).Location).DistanceMeters);
        Assert.AreEqual(0UL, State(shortfall, 1).ActivityStartedAtStep);
    }

    [TestMethod]
    public void Creature_shots_in_one_step_sum_on_structure_and_do_not_touch_the_part()
    {
        using var single = StructureFight(4, 1);
        Assert.AreEqual(96, Vehicle(single, single.State.World.Trains.Single().Vehicles[0]).Structure.EffectiveCurrent);
        Assert.AreEqual(100, Vehicle(single, single.State.World.Trains.Single().Vehicles[0]).Parts.Single().Durability.EffectiveCurrent);
        Assert.AreEqual(10, Group(single, 1).Creatures.Single().Health);

        using var pair = StructureFight(3, 2);
        Assert.AreEqual(94, Vehicle(pair, pair.State.World.Trains.Single().Vehicles[0]).Structure.EffectiveCurrent);
        Assert.AreEqual(100, Vehicle(pair, pair.State.World.Trains.Single().Vehicles[0]).Parts.Single().Durability.EffectiveCurrent);
        Assert.IsTrue(Group(pair, 1).Creatures.All(creature => creature.Health == 10));
    }

    [TestMethod]
    public void Killing_the_only_member_removes_the_group_once()
    {
        using var session = LossFight(trainDamage: 10, ratio: 1, atLair: false, members: 1, duration: Idle, out var result);
        Assert.IsInstanceOfType<Lookup<CreatureGroupView>.Missing>(session.Read().FindCreatureGroup(new(1)));
        Assert.IsInstanceOfType<Lookup<CreatureView>.Missing>(session.Read().FindCreature(new(1)));
        Assert.IsEmpty(session.Read().World.Combats);
        var events = result.Events.Select(envelope => envelope.Event).ToArray();
        var destruction = events.OfType<CoreEvent.CombatDestruction>().Single();
        var creature = destruction.Units.OfType<DestroyedUnit.Creature>().Single();
        Assert.AreEqual(1, destruction.Units.Length);
        Assert.AreEqual(new CreatureId(1), creature.Value.Creature);
        Assert.AreEqual(new CreatureGroupId(1), creature.Value.Group);
        Assert.IsTrue(events.OfType<CoreEvent.CombatEnded>().Any());
    }

    [TestMethod]
    public void Loss_above_the_shared_retreat_ratio_retreats_the_group_without_transferring_damage()
    {
        using var session = LossFight(trainDamage: 10, ratio: 0.4, atLair: false, members: 2, duration: Idle, out _);
        var group = Group(session, 1);
        Assert.AreEqual(1, group.Creatures.Length);
        Assert.AreEqual(10, group.Creatures.Single().Health);
        Assert.AreEqual(CreatureActivity.Retreating, group.Activity);
        var participant = CombatFixture.Participant(session, new CombatUnitId.CreatureGroup(new(1)));
        Assert.IsTrue(participant.Retreating);
        Assert.AreEqual(60UL, participant.RemainingDisengagementSteps);
        var payload = WorldFixture.Success(session.Capture(CapturePurpose.Save)).Encode();
        var count = System.Text.Json.Nodes.JsonNode.Parse(payload)!["world"]!["combats"]![0]!["creatures"]![0]!["count_at_join"]!.GetValue<int>();
        Assert.AreEqual(2, count);
    }

    [TestMethod]
    public void Loss_equal_to_the_retreat_ratio_does_not_retreat()
    {
        using var session = LossFight(trainDamage: 10, ratio: 0.5, atLair: false, members: 2, duration: Idle, out _);
        Assert.AreEqual(1, Group(session, 1).Creatures.Length);
        Assert.AreEqual(10, Group(session, 1).Creatures.Single().Health);
        Assert.AreEqual(CreatureActivity.Combat, Group(session, 1).Activity);
        Assert.IsFalse(CombatFixture.Participant(session, new CombatUnitId.CreatureGroup(new(1))).Retreating);
    }

    [TestMethod]
    public void A_group_fighting_at_its_own_lair_does_not_retreat()
    {
        using var session = LossFight(trainDamage: 10, ratio: 0.4, atLair: true, members: 2, duration: Tick, out _);
        Assert.AreEqual(1, Group(session, 1).Creatures.Length);
        Assert.AreEqual(CreatureActivity.Combat, Group(session, 1).Activity);
        Assert.IsFalse(CombatFixture.Participant(session, new CombatUnitId.CreatureGroup(new(1))).Retreating);
    }

    [TestMethod]
    public void Mixed_group_uses_the_shorter_current_raid_duration()
    {
        var baseline = Multi;
        var lair = baseline.World.Lairs.Single();
        var away = EmptyNode(baseline.World, lair.Node);
        var primary = Animal(baseline.Content, "fixture:creature-a", 10, 0, 1, Tick, null);
        var secondary = Animal(baseline.Content, "fixture:creature-b", 20, 0, 1, Idle, null);
        var content = Lairs(baseline.Content, primary, secondary, 2, Idle, Idle, 0, 0);
        var world = WithPacks(StopTrains(baseline.World), Pack(1, lair.Id, new WorldLocation.Node(away), null,
            CreatureActivity.Wandering, 0, null,
            Creature(1, "fixture:creature-a", 10), Creature(2, "fixture:creature-b", 20)));
        using var session = Open(baseline, world, content);
        Step(session);
        var group = Group(session, 1);
        Assert.AreEqual(CreatureActivity.Retreating, group.Activity);
        Assert.AreEqual(new WorldLocation.Node(away), group.Location);
        Assert.AreEqual(2, group.Creatures.Length);
        Assert.AreEqual(10, group.Creatures[0].Health);
        Assert.AreEqual(20, group.Creatures[1].Health);
        Assert.AreEqual(1UL, State(session, 1).ActivityStartedAtStep);
    }

    [TestMethod]
    public void Focus_fire_overrides_armed_first_on_the_following_step()
    {
        var baseline = CombatWorld;
        var primary = Animal(baseline.Content, "fixture:creature-a", 100, 0, 1, Idle, new PartCapability.Weapon(1, Tick));
        var secondary = Animal(baseline.Content, "fixture:creature-b", 10, 0, 1, Idle, null);
        var content = Lairs(baseline.Content, primary, secondary, 2, Idle, Idle, 0, 0);
        var world = StopTrains(StructureOnly(baseline.World, 1, Tick));
        var fight = EmptyNode(world);
        var (withLair, lair) = AddLair(world, EmptyNode(world, fight), content.LairTypes[0].Id);
        var engagement = new TrainEngagementRules(TargetPriority.ArmedFirst, TargetScope.All, EncounterAction.Engage, 0);
        world = PlaceTrain(withLair, new(fight, null, null, null, null, NodeTravelPhase.Waiting, 0, null),
            TrainActivity.Stopped, 0, null, engagement);
        world = WithPacks(world,
            Pack(1, lair.Id, new WorldLocation.Node(fight), null, CreatureActivity.Wandering, 0, null,
                Creature(1, "fixture:creature-a", 100)),
            Pack(2, lair.Id, new WorldLocation.Node(fight), null, CreatureActivity.Wandering, 0, null,
                Creature(2, "fixture:creature-b", 10)));
        using var session = Open(baseline, world, content);
        var train = world.Trains.Single().Id;
        Step(session);
        Assert.AreEqual(99, Individual(session, 1).Health);
        Assert.AreEqual(10, Individual(session, 2).Health);
        var combat = session.Read().World.Combats.Single().Id;
        CombatFixture.Accepted(session.Submit(new CoreCommand.FocusFire(combat, new CombatController.Train(train),
            new WorldObjectId.CreatureGroup(new(2))), CommandSource.AutomationTest));
        Step(session);
        Assert.AreEqual(99, Individual(session, 1).Health);
        Assert.AreEqual(9, Individual(session, 2).Health);
    }

    [TestMethod]
    public void Combat_at_a_player_settlement_freezes_and_resumes_station_dwell()
    {
        var baseline = CombatWorld;
        var settlement = Friendly(baseline.World);
        var primary = Animal(baseline.Content, "fixture:creature-a", 5, 0, 0.9, Idle, new PartCapability.Weapon(1, Tick));
        var secondary = Animal(baseline.Content, "fixture:creature-b", 5, 0, 0.9, Idle, null);
        var content = Lairs(baseline.Content, primary, secondary, 1, Idle, Idle, 0, 0);
        var owned = PlayerOwned(StopTrains(StructureOnly(baseline.World, 1, Tick)), settlement.Id);
        var (withLair, lair) = AddLair(owned, EmptyNode(owned, settlement.Node), content.LairTypes[0].Id);
        var preview = Open(baseline, withLair, content);
        var node = ((Lookup<NodeView>.Found)preview.Read().FindNode(settlement.Node)).Value;
        var elsewhere = EmptyNode(withLair, settlement.Node, lair.Node);
        preview.Dispose();
        var plan = new RunPlan(false, [new PlanStop.Station(settlement.Node, 120, []), new PlanStop.Waypoint(elsewhere)]);
        var world = PlaceTrain(withLair, new(settlement.Node, null, null, null, null, NodeTravelPhase.Docked,
            node.ApproachLengthMeters + node.PlatformLengthMeters, 1), TrainActivity.Servicing, 120, plan);
        world = WithPacks(world, Pack(1, lair.Id, new WorldLocation.Node(settlement.Node), null,
            CreatureActivity.RaidingSettlement, 0, new CreatureObjective.Settlement(settlement.Node),
            Creature(1, "fixture:creature-a", 5)));
        using var session = Open(baseline, world, content);
        var train = world.Trains.Single().Id;

        Step(session);
        Assert.AreEqual(TrainActivity.Combat, ReadTrain(session, train).Activity);
        Assert.AreEqual(119UL, ReadTrain(session, train).RemainingDwellSteps);
        Assert.AreEqual(4, Individual(session, 1).Health);
        Assert.IsTrue(session.Read().World.Combats.Length == 1);

        Step(session);
        Step(session);
        Step(session);
        Assert.AreEqual(TrainActivity.Combat, ReadTrain(session, train).Activity);
        Assert.AreEqual(119UL, ReadTrain(session, train).RemainingDwellSteps);
        Assert.AreEqual(1, Individual(session, 1).Health);

        Step(session);
        Assert.IsInstanceOfType<Lookup<CreatureGroupView>.Missing>(session.Read().FindCreatureGroup(new(1)));
        Assert.AreEqual(TrainActivity.Servicing, ReadTrain(session, train).Activity);
        Assert.AreEqual(119UL, ReadTrain(session, train).RemainingDwellSteps);
        Assert.IsEmpty(session.Read().World.Combats);

        Step(session);
        Assert.AreEqual(TrainActivity.Servicing, ReadTrain(session, train).Activity);
        Assert.AreEqual(118UL, ReadTrain(session, train).RemainingDwellSteps);
    }

    [TestMethod]
    public void Settlement_raid_keeps_its_clock_when_combat_ends_before_the_duration()
    {
        var baseline = CombatWorld;
        var settlement = Friendly(baseline.World);
        var content = Tune(baseline.Content, 10, 0, 1, 3 * Tick, null, maximum: 1);
        var world = StopTrains(StructureOnly(baseline.World, 0, Tick));
        var (withLair, lair) = AddLair(world, EmptyNode(world, settlement.Node), content.LairTypes[0].Id);
        var engagement = new TrainEngagementRules(TargetPriority.ArmedFirst, TargetScope.All, EncounterAction.Engage, 0);
        world = PlaceTrain(withLair, new(settlement.Node, null, null, null, null, NodeTravelPhase.Waiting, 0, null),
            TrainActivity.Stopped, 0, null, engagement);
        world = WithPacks(world, Pack(1, lair.Id, new WorldLocation.Node(settlement.Node), null,
            CreatureActivity.RaidingSettlement, 0, new CreatureObjective.Settlement(settlement.Node),
            Creature(1, "fixture:creature-a", 10)));
        using var session = Open(baseline, world, content);
        var train = world.Trains.Single().Id;
        Step(session);
        Assert.AreEqual(CreatureActivity.Combat, Group(session, 1).Activity);
        Assert.AreEqual(0UL, State(session, 1).ActivityStartedAtStep);
        Assert.AreEqual(10, Individual(session, 1).Health);
        CombatFixture.Accepted(session.Submit(new CoreCommand.Retreat(train), CommandSource.AutomationTest));
        Step(session);
        Assert.IsEmpty(session.Read().World.Combats);
        Assert.AreEqual(CreatureActivity.RaidingSettlement, Group(session, 1).Activity);
        Assert.AreEqual(new WorldLocation.Node(settlement.Node), Group(session, 1).Location);
        Assert.AreEqual(0UL, State(session, 1).ActivityStartedAtStep);
        Step(session);
        Assert.AreEqual(CreatureActivity.Retreating, Group(session, 1).Activity);
        Assert.AreEqual(new WorldLocation.Node(settlement.Node), Group(session, 1).Location);
        Assert.AreEqual(3UL, State(session, 1).ActivityStartedAtStep);
        Assert.AreEqual(10, Individual(session, 1).Health);
    }

    [TestMethod]
    public void Railway_raid_returns_home_when_combat_ends_with_the_group_alive()
    {
        var baseline = CombatWorld;
        var content = Tune(baseline.Content, 10, 0, 1, Idle, null, maximum: 1);
        var world = StopTrains(StructureOnly(baseline.World, 0, Tick));
        var fight = EmptyNode(world);
        var (withLair, lair) = AddLair(world, EmptyNode(world, fight), content.LairTypes[0].Id);
        var edge = withLair.Edges.First(item => item.LengthMeters > 0);
        var engagement = new TrainEngagementRules(TargetPriority.ArmedFirst, TargetScope.All, EncounterAction.Engage, 0);
        world = PlaceTrain(withLair, new(fight, null, null, null, null, NodeTravelPhase.Waiting, 0, null),
            TrainActivity.Stopped, 0, null, engagement);
        world = WithPacks(world, Pack(1, lair.Id, new WorldLocation.Node(fight), null,
            CreatureActivity.RaidingRailway, 0, new CreatureObjective.Railway(edge.Id, edge.LengthMeters / 2),
            Creature(1, "fixture:creature-a", 10)));
        using var session = Open(baseline, world, content);
        var train = world.Trains.Single().Id;
        Step(session);
        Assert.AreEqual(CreatureActivity.Combat, Group(session, 1).Activity);
        Assert.IsInstanceOfType<CreatureObjective.Railway>(State(session, 1).Objective);
        CombatFixture.Accepted(session.Submit(new CoreCommand.Retreat(train), CommandSource.AutomationTest));
        Step(session);
        Assert.IsEmpty(session.Read().World.Combats);
        Assert.AreEqual(CreatureActivity.Retreating, Group(session, 1).Activity);
        Assert.IsNull(State(session, 1).Objective);
        Assert.AreEqual(2UL, State(session, 1).ActivityStartedAtStep);
        Assert.AreEqual(new WorldLocation.Node(fight), Group(session, 1).Location);
        Assert.AreEqual(10, Individual(session, 1).Health);
    }

    [TestMethod]
    public void Spawn_into_a_fighting_garrison_does_not_increase_the_join_count()
    {
        var baseline = CombatWorld;
        var content = Tune(baseline.Content, 10, 0, 1, Idle, null, maximum: 5, spawnSeconds: Tick);
        var world = StopTrains(StructureOnly(baseline.World, 0, Tick));
        var node = EmptyNode(world);
        var (withLair, lair) = AddLair(world, node, content.LairTypes[0].Id);
        var engagement = new TrainEngagementRules(TargetPriority.ArmedFirst, TargetScope.All, EncounterAction.Engage, 0);
        world = PlaceTrain(withLair, new(node, null, null, null, null, NodeTravelPhase.Waiting, 0, null),
            TrainActivity.Stopped, 0, null, engagement);
        var train = world.Trains.Single().Id;
        world = WithPacks(world, Pack(1, lair.Id, new WorldLocation.Node(node), null, CreatureActivity.Combat, 0, null,
            Creature(1, "fixture:creature-a", 10)));
        var combatId = world.Next.Combat;
        world = world with
        {
            Combats =
            [
                new CombatState(new(combatId), new WorldLocation.Node(node), 0,
                    [new CombatControllerState(new CombatController.Train(train), 0, false, 0, null)],
                    [new CombatSideSummary(new CombatOwner.Player(), [], [], [], new(0)),
                        new CombatSideSummary(new CombatOwner.Creatures(), [], [], [], new(0))])
                {
                    Creatures = [new CreatureCombatantState(new(1), 0, 1, false, 0)]
                }
            ],
            Next = world.Next with { Combat = combatId + 1 }
        };
        using var session = Open(baseline, world, content);
        Step(session);
        var group = Group(session, 1);
        Assert.AreEqual(2, group.Creatures.Length);
        Assert.AreEqual(10, group.Creatures[1].Health);
        Assert.AreEqual(new CreatureId(2), group.Creatures[1].Id);
        Assert.AreEqual(CreatureActivity.Combat, group.Activity);
        var payload = WorldFixture.Success(session.Capture(CapturePurpose.Save)).Encode();
        var count = System.Text.Json.Nodes.JsonNode.Parse(payload)!["world"]!["combats"]![0]!["creatures"]![0]!["count_at_join"]!.GetValue<int>();
        Assert.AreEqual(1, count);
    }

    private static CoreSession Ecology(double speed, int maximum, double spawnSeconds, double raidSeconds, int roaming = 0, int radius = 0)
    {
        var baseline = Multi;
        var content = Tune(baseline.Content, 10, speed, 1, Idle, null, maximum, spawnSeconds, raidSeconds, roaming, radius);
        return Open(baseline, StopTrains(baseline.World), content);
    }

    private static CoreContentProjection Tune(CoreContentProjection content, long health, double speed, double retreat,
        double duration, PartCapability.Weapon? weapon, int maximum = 3, double spawnSeconds = Idle, double raidSeconds = Idle,
        int roaming = 0, int radius = 0)
    {
        var primary = Animal(content, "fixture:creature-a", health, speed, retreat, duration, weapon);
        var secondary = Animal(content, "fixture:creature-b", health, speed, retreat, duration, null);
        return Lairs(content, primary, secondary, maximum, spawnSeconds, raidSeconds, roaming, radius);
    }

    private static CoreSession RailwayApproach(double speed, double distance, double target)
    {
        var baseline = Multi;
        var lair = baseline.World.Lairs.Single();
        var other = EmptyNode(baseline.World, lair.Node);
        var content = Tune(baseline.Content, 10, speed, 1, Idle, null, maximum: 1);
        var world = SingleEdge(StopTrains(baseline.World), lair.Node, other, 10, 0);
        var edge = world.Edges.Single();
        world = WithPacks(world, Pack(1, lair.Id, new WorldLocation.Edge(edge.Id, distance), TravelDirection.Forward,
            CreatureActivity.RaidingRailway, 0, new CreatureObjective.Railway(edge.Id, target),
            Creature(1, "fixture:creature-a", 10)));
        var session = Open(baseline, world, content);
        Step(session);
        return session;
    }

    private static CoreSession StructureFight(long creatureDamage, int members)
    {
        var baseline = CombatWorld;
        var weapon = new PartCapability.Weapon(creatureDamage, Tick);
        var content = Tune(baseline.Content, 10, 0, 1, Idle, weapon, maximum: members);
        var world = StopTrains(StructureOnly(baseline.World, 0, Tick));
        var fight = EmptyNode(world);
        var (withLair, lair) = AddLair(world, EmptyNode(world, fight), content.LairTypes[0].Id);
        world = PlaceTrain(withLair, new(fight, null, null, null, null, NodeTravelPhase.Waiting, 0, null),
            TrainActivity.Stopped, 0, null);
        var creatures = Enumerable.Range(1, members)
            .Select(id => Creature((ulong)id, "fixture:creature-a", 10)).ToArray();
        world = WithPacks(world, Pack(1, lair.Id, new WorldLocation.Node(fight), null, CreatureActivity.Wandering, 0, null, creatures));
        var before = Open(baseline, world, content);
        var vehicle = Vehicle(before, before.State.World.Trains.Single().Vehicles[0]);
        Assert.AreEqual(100, vehicle.Structure.EffectiveCurrent);
        Assert.AreEqual(100, vehicle.Parts.Single().Durability.EffectiveCurrent);
        before.Dispose();
        var session = Open(baseline, world, content);
        Step(session);
        return session;
    }

    private static CoreSession LossFight(long trainDamage, double ratio, bool atLair, int members, double duration,
        out AdvanceResult result)
    {
        var baseline = CombatWorld;
        var content = Tune(baseline.Content, 10, 0, ratio, duration, null, maximum: members);
        var world = StopTrains(StructureOnly(baseline.World, trainDamage, Tick));
        var home = EmptyNode(world);
        var fight = atLair ? home : EmptyNode(world, home);
        var (withLair, lair) = AddLair(world, home, content.LairTypes[0].Id);
        var engagement = new TrainEngagementRules(TargetPriority.ArmedFirst, TargetScope.All, EncounterAction.Engage, 0);
        world = PlaceTrain(withLair, new(fight, null, null, null, null, NodeTravelPhase.Waiting, 0, null),
            TrainActivity.Stopped, 0, null, engagement);
        var creatures = Enumerable.Range(1, members).Select(id => Creature((ulong)id, "fixture:creature-a", 10)).ToArray();
        var activity = atLair ? CreatureActivity.Guarding : CreatureActivity.Wandering;
        world = WithPacks(world, Pack(1, lair.Id, new WorldLocation.Node(fight), null, activity, 0, null, creatures));
        var session = Open(baseline, world, content);
        result = Step(session);
        return session;
    }
}
