using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core;
using Train.Core.Contracts;
using Train.Core.Creatures;
using Train.Core.Sessions;
using Train.Core.Tests.World;
using static Train.Core.Tests.Creatures.CreatureTestSupport;

namespace Train.Core.Tests.Creatures;

[TestClass]
public sealed class CreaturePersistenceTests
{
    [TestMethod]
    public void Spawned_group_roundtrips_and_each_creature_is_a_direct_reference()
    {
        var baseline = Multi;
        var content = Content(baseline.Content, 10, 0, maximum: 2, spawnSeconds: Tick);
        var nextGroup = baseline.World.Next.CreatureGroup;
        var nextCreature = baseline.World.Next.Creature;
        using var session = Open(baseline, StopTrains(baseline.World), content);
        Step(session);
        var saved = WorldFixture.Success(session.Capture(CapturePurpose.Save));
        var reference = saved.References.Definitions.Single(item =>
            item.Field.StartsWith("World.CreatureGroups", StringComparison.Ordinal));
        Assert.IsInstanceOfType<CoreDefinitionReference.Creature>(reference.Definition);
        Assert.AreEqual($"World.CreatureGroups[{nextGroup}].Creatures[{nextCreature}].Definition", reference.Field);
        Assert.IsFalse(reference.WasMissing);
        var factory = CoreFactory.Create();
        var decoded = WorldFixture.Success(factory.DecodeState(saved.Encode()));
        using var candidate = WorldFixture.Success(factory.PrepareRestore(decoded, content, WorldFixture.Options));
        using var restored = (CoreSession)candidate.Activate();
        Assert.AreEqual(saved.Encode(), WorldFixture.Success(restored.Capture(CapturePurpose.Save)).Encode());
        Assert.AreEqual(10, Individual(restored, nextCreature).Health);
        Assert.AreEqual(10, Individual(restored, nextCreature).MaximumHealth);
    }

    [TestMethod]
    public void Railway_objective_roundtrips()
    {
        var baseline = Multi;
        var lair = baseline.World.Lairs.Single();
        var edge = baseline.World.Edges.First(item => item.LengthMeters > 2);
        var content = Content(baseline.Content, 10, 0, maximum: 1);
        var world = WithPacks(StopTrains(baseline.World), Pack(1, lair.Id, new WorldLocation.Edge(edge.Id, 1),
            TravelDirection.Forward, CreatureActivity.RaidingRailway, 0, new CreatureObjective.Railway(edge.Id, 1),
            Creature(1, "fixture:creature-a", 7)));
        using var session = Open(baseline, world, content);
        var saved = WorldFixture.Success(session.Capture(CapturePurpose.Save));
        var factory = CoreFactory.Create();
        var decoded = WorldFixture.Success(factory.DecodeState(saved.Encode()));
        using var candidate = WorldFixture.Success(factory.PrepareRestore(decoded, content, WorldFixture.Options));
        using var restored = (CoreSession)candidate.Activate();
        Assert.AreEqual(saved.Encode(), WorldFixture.Success(restored.Capture(CapturePurpose.Save)).Encode());
        var objective = (CreatureObjective.Railway)State(restored, 1).Objective!;
        Assert.AreEqual(edge.Id, objective.Edge);
        Assert.AreEqual(1d, objective.DistanceMeters);
    }

    [TestMethod]
    [DataRow("health", "/world/creature_groups/1/creatures/health")]
    [DataRow("guarding_node", "/world/creature_groups/1/activity")]
    [DataRow("duplicate_creature", "/world/creature_groups/1/creatures")]
    [DataRow("next_creature", "/world/creature_groups/1/creatures")]
    public void Corrupt_creature_payload_is_rejected_without_changing_the_session(string corruption, string field)
    {
        using var session = Spawned();
        Reject(session, field, world =>
        {
            var group = world["creature_groups"]![0]!;
            switch (corruption)
            {
                case "health":
                    group["creatures"]![0]!["health"] = 0;
                    break;
                case "guarding_node":
                    var current = group["location"]!["id"]!["value"]!.GetValue<ulong>();
                    group["location"]!["id"]!["value"] = world["nodes"]!.AsArray()
                        .Select(node => node!["id"]!["value"]!.GetValue<ulong>()).First(id => id != current);
                    break;
                case "duplicate_creature":
                    group["creatures"]!.AsArray().Add(group["creatures"]![0]!.DeepClone());
                    break;
                case "next_creature":
                    world["next"]!["creature"] = group["creatures"]![0]!["id"]!["value"]!.GetValue<ulong>();
                    break;
                default:
                    Assert.Fail(corruption);
                    break;
            }
        });
    }

    [TestMethod]
    public void Combat_creature_away_from_the_combat_node_is_rejected()
    {
        using var session = Fighting();
        Reject(session, "/world/combats/0/creatures", world =>
        {
            var group = world["creature_groups"]![0]!;
            var current = group["location"]!["id"]!["value"]!.GetValue<ulong>();
            group["location"]!["id"]!["value"] = world["nodes"]!.AsArray()
                .Select(node => node!["id"]!["value"]!.GetValue<ulong>()).First(id => id != current);
        });
    }

    [TestMethod]
    public void Combat_activity_without_a_combatant_record_is_rejected()
    {
        using var session = Fighting();
        Reject(session, "/world/creature_groups/activity", world => world["combats"]![0]!["creatures"] = new JsonArray());
    }

    [TestMethod]
    public void Queries_reject_zero_report_missing_and_list_an_edge_by_its_id()
    {
        var baseline = Multi;
        var lair = baseline.World.Lairs.Single();
        var edge = baseline.World.Edges.First(item => item.LengthMeters > 2);
        var other = baseline.World.Edges.First(item => item.Id != edge.Id);
        var content = Content(baseline.Content, 10, 4, maximum: 2);
        content = content with
        {
            Creatures = content.Creatures.Select(creature => creature.Id == new CreatureDefinitionId("fixture:creature-b")
                ? creature with { SpeedMetersPerSecond = 9 } : creature).ToImmutableArray()
        };
        var world = WithPacks(StopTrains(baseline.World),
            Pack(1, lair.Id, new WorldLocation.Edge(edge.Id, 1), TravelDirection.Forward, CreatureActivity.Wandering, 0, null,
                Creature(1, "fixture:creature-a", 50), Creature(2, "fixture:creature-b", 20)),
            Pack(2, lair.Id, new WorldLocation.Node(lair.Node), null, CreatureActivity.Guarding, 0, null,
                Creature(3, "fixture:creature-a", 10)));
        using var session = Open(baseline, world, content);
        Assert.AreEqual("生物编号必须非零。", Start(Assert.ThrowsExactly<ArgumentException>(() => session.Read().FindCreature(new(0)))));
        Assert.AreEqual("生物群编号必须非零。", Start(Assert.ThrowsExactly<ArgumentException>(() => session.Read().FindCreatureGroup(new(0)))));
        Assert.AreEqual("聚落编号必须非零。", Start(Assert.ThrowsExactly<ArgumentException>(() => session.Read().FindLair(new(0)))));
        Assert.IsInstanceOfType<Lookup<CreatureView>.Missing>(session.Read().FindCreature(new(99)));
        Assert.IsInstanceOfType<Lookup<CreatureGroupView>.Missing>(session.Read().FindCreatureGroup(new(99)));
        Assert.IsInstanceOfType<Lookup<LairView>.Missing>(session.Read().FindLair(new(99)));

        var slowed = Individual(session, 1);
        Assert.AreEqual(10, slowed.Health);
        Assert.AreEqual(10, slowed.MaximumHealth);
        Assert.AreEqual(4, Group(session, 1).SpeedMetersPerSecond);
        var sameEdge = ((Lookup<LocationView>.Found)session.Read().FindLocation(new WorldLocation.Edge(edge.Id, 1.5))).Value;
        Assert.AreEqual(new CreatureGroupId(1), sameEdge.CreatureGroups.Single());
        var elsewhere = ((Lookup<LocationView>.Found)session.Read().FindLocation(new WorldLocation.Edge(other.Id, 1))).Value;
        Assert.IsEmpty(elsewhere.CreatureGroups);
        CollectionAssert.AreEqual(new[] { new CreatureGroupId(1), new CreatureGroupId(2) },
            session.Read().World.CreatureGroups.Select(group => group.Id).ToArray());
    }

    [TestMethod]
    public void Missing_projection_reports_saved_health_and_the_group_stays_put()
    {
        var baseline = Multi;
        var lair = baseline.World.Lairs.Single();
        var edge = baseline.World.Edges.First(item => item.LengthMeters > 3);
        var content = Content(baseline.Content, 10, 60, maximum: 1) with { Creatures = [] };
        var world = WithPacks(StopTrains(baseline.World), Pack(1, lair.Id, new WorldLocation.Edge(edge.Id, 2),
            TravelDirection.Forward, CreatureActivity.Wandering, 0, null, Creature(1, "fixture:creature-missing", 7)));
        using var session = Open(baseline, world, content);
        Assert.AreEqual(7, Individual(session, 1).Health);
        Assert.AreEqual(7, Individual(session, 1).MaximumHealth);
        Assert.AreEqual(0, Group(session, 1).SpeedMetersPerSecond);
        Step(session);
        Assert.AreEqual(2d, ((WorldLocation.Edge)Group(session, 1).Location).DistanceMeters);
        Assert.IsEmpty(session.State.World.CreatureGroups.SelectMany(group => group.Creatures).Where(creature => creature.Id != new CreatureId(1)));
    }

    [TestMethod]
    public void A_projected_member_still_moves_when_another_member_has_no_projection()
    {
        var baseline = Multi;
        var lair = baseline.World.Lairs.Single();
        var edge = baseline.World.Edges.First(item => item.LengthMeters > 3);
        var content = Content(baseline.Content, 10, 60, maximum: 2);
        var world = WithPacks(StopTrains(baseline.World), Pack(1, lair.Id, new WorldLocation.Edge(edge.Id, 1),
            TravelDirection.Forward, CreatureActivity.Wandering, 0, null,
            Creature(1, "fixture:creature-missing", 7), Creature(2, "fixture:creature-a", 10)));
        using var session = Open(baseline, world, content);
        Assert.AreEqual(7, Individual(session, 1).MaximumHealth);
        Assert.AreEqual(60, Group(session, 1).SpeedMetersPerSecond);
        Step(session);
        var delta = 60 * Tick;
        Assert.AreEqual(1 + delta, ((WorldLocation.Edge)Group(session, 1).Location).DistanceMeters);
        Assert.AreEqual(7, Individual(session, 1).Health);
        Assert.AreEqual(10, Individual(session, 2).Health);
    }

    [TestMethod]
    public void Missing_member_projection_does_not_spawn()
    {
        var baseline = Multi;
        var before = baseline.World.Next.Creature;
        var content = Content(baseline.Content, 10, 0, maximum: 3, spawnSeconds: Tick) with { Creatures = [] };
        using var session = Open(baseline, StopTrains(baseline.World), content);
        Step(session);
        Assert.IsEmpty(session.State.World.CreatureGroups);
        Assert.AreEqual(before, session.State.World.Next.Creature);
    }

    [TestMethod]
    public void Depleting_the_creature_identity_freezes_at_the_pre_step_boundary()
    {
        var baseline = Multi;
        var content = Content(baseline.Content, 10, 0, maximum: 3, spawnSeconds: Tick);
        var world = StopTrains(baseline.World) with { Next = baseline.World.Next with { Creature = ulong.MaxValue } };
        using var session = Open(baseline, world, content);
        var before = session.State;
        var result = session.Advance(1);
        Assert.AreEqual(0u, result.CompletedSteps);
        Assert.AreEqual("internal_error", result.Fault?.Code);
        Assert.AreEqual("Advance", result.Fault?.Operation);
        StringAssert.Contains(result.Diagnostic?.Message, "生物身份已耗尽");
        Assert.AreSame(before.World, session.State.World);
        Assert.AreEqual(before.Position, session.State.Position);
    }

    [TestMethod]
    public void Depleting_the_group_identity_freezes_at_the_pre_step_boundary()
    {
        var baseline = Multi;
        var content = Content(baseline.Content, 10, 0, maximum: 3, spawnSeconds: Tick);
        var world = StopTrains(baseline.World) with { Next = baseline.World.Next with { CreatureGroup = ulong.MaxValue } };
        using var session = Open(baseline, world, content);
        var before = session.State;
        var result = session.Advance(1);
        Assert.AreEqual(0u, result.CompletedSteps);
        Assert.AreEqual("internal_error", result.Fault?.Code);
        StringAssert.Contains(result.Diagnostic?.Message, "生物群身份已耗尽");
        Assert.AreSame(before.World, session.State.World);
    }

    private static CoreContentProjection Content(CoreContentProjection content, long health, double speed, int maximum,
        double spawnSeconds = Idle, double raidSeconds = Idle)
    {
        var primary = Animal(content, "fixture:creature-a", health, speed, 1, Idle, null);
        var secondary = Animal(content, "fixture:creature-b", health, speed, 1, Idle, null);
        return Lairs(content, primary, secondary, maximum, spawnSeconds, raidSeconds, 0, 0);
    }

    private static CoreSession Spawned()
    {
        var baseline = Multi;
        var content = Content(baseline.Content, 10, 0, maximum: 2, spawnSeconds: Tick);
        var session = Open(baseline, StopTrains(baseline.World), content);
        Step(session);
        return session;
    }

    private static CoreSession Fighting()
    {
        var baseline = CombatWorld;
        var primary = Animal(baseline.Content, "fixture:creature-a", 10, 0, 1, Idle, null);
        var secondary = Animal(baseline.Content, "fixture:creature-b", 10, 0, 1, Idle, null);
        var content = Lairs(baseline.Content, primary, secondary, 1, Idle, Idle, 0, 0);
        var world = StopTrains(baseline.World);
        var fight = EmptyNode(world);
        var (withLair, lair) = AddLair(world, EmptyNode(world, fight), content.LairTypes[0].Id);
        var engagement = new TrainEngagementRules(TargetPriority.ArmedFirst, TargetScope.All, EncounterAction.Engage, 0);
        world = PlaceTrain(withLair, new(fight, null, null, null, null, NodeTravelPhase.Waiting, 0, null),
            TrainActivity.Stopped, 0, null, engagement);
        world = WithPacks(world, Pack(1, lair.Id, new WorldLocation.Node(fight), null, CreatureActivity.Wandering, 0, null,
            Creature(1, "fixture:creature-a", 10)));
        var session = Open(baseline, world, content);
        Step(session);
        Assert.IsTrue(session.Read().World.Combats.Length == 1);
        return session;
    }

    private static void Reject(CoreSession session, string field, Action<JsonNode> mutate)
    {
        var original = WorldFixture.Success(session.Capture(CapturePurpose.Save));
        var payload = original.Encode();
        var root = JsonNode.Parse(payload)!.AsObject();
        mutate(root["world"]!);
        var result = CoreFactory.Create().DecodeState(root.ToJsonString());
        Assert.IsInstanceOfType<CoreResult<ICoreCapture>.Failure>(result, field);
        var problem = ((CoreResult<ICoreCapture>.Failure)result).Problems.Single();
        Assert.AreEqual("invalid_state", problem.Code);
        Assert.AreEqual(field, problem.Field);
        var after = WorldFixture.Success(session.Capture(CapturePurpose.Save));
        Assert.AreEqual(payload, after.Encode());
        Assert.AreEqual(original.Checkpoint, after.Checkpoint);
    }

    private static string Start(ArgumentException exception)
    {
        var message = exception.Message;
        var marker = message.IndexOf(" (Parameter", StringComparison.Ordinal);
        return marker < 0 ? message : message[..marker];
    }
}
