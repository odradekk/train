using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.Sessions;
using Train.Core.World;
using static Train.Core.Tests.World.ResourceCycleFixture;

namespace Train.Core.Tests.World;

[TestClass]
public sealed class ResourceStateTests
{
    [TestMethod]
    public void Initial_totals_are_generated_quantities_per_good_and_support_more_than_long_maximum()
    {
        var state = State(multipleNodes: true);
        Assert.AreEqual((Int128)state.World.Nodes.Sum(n => n.Resources.Single().Quantity),
            state.World.ResourceRegenerations.Single().InitialQuantity);
        var other = new VoxelDefinitionId("fixture:other-resource");
        var entry = state.World.Source.Configuration.Resources.Single();
        var nodes = state.World.Nodes.Take(2).Select(n => n with { Resources =
            [new(Good, long.MaxValue, new ResourceRenewal.NonRenewable()),
                new(new GoodId.Voxel(other), 3, new ResourceRenewal.NonRenewable())] }).ToArray();
        var totals = WorldResources.Initial([entry, entry with { Good = other },
            entry with { Good = new("fixture:renewable"), Renewable = true }], nodes);
        Assert.HasCount(2, totals);
        Assert.AreEqual((Int128)long.MaxValue * 2, totals.Single(r => r.Good == WorldFixture.Material).InitialQuantity);
        Assert.AreEqual((Int128)6, totals.Single(r => r.Good == other).InitialQuantity);
    }

    [TestMethod]
    [DataRow("resource_regenerations")]
    [DataRow("resource_recovery")]
    public void Missing_authoritative_resource_collections_are_rejected(string property)
    {
        using var session = new CoreSession(State());
        var payload = JsonNode.Parse(Capture(session).Encode())!;
        payload["world"]!.AsObject().Remove(property);
        AssertRejected(payload);
    }

    [TestMethod]
    [DataRow(false, "good")]
    [DataRow(false, "initial_quantity")]
    [DataRow(false, "sequence")]
    [DataRow(true, "node")]
    [DataRow(true, "good")]
    [DataRow(true, "remaining")]
    public void Missing_authoritative_resource_record_fields_are_rejected(bool renewable, string property)
    {
        using var session = new CoreSession(Remaining(State(renewable: renewable), 0));
        Step(session);
        var payload = JsonNode.Parse(Capture(session).Encode())!;
        payload["world"]![renewable ? "resource_recovery" : "resource_regenerations"]![0]!.AsObject().Remove(property);
        AssertRejected(payload);
    }

    [TestMethod]
    [DataRow("initial_quantity", "-1")]
    [DataRow("sequence", "1")]
    [DataRow("good", "{\"value\":\"fixture:absent\"}")]
    public void Invalid_regeneration_state_is_rejected(string property, string value)
    {
        using var session = new CoreSession(State());
        var payload = JsonNode.Parse(Capture(session).Encode())!;
        payload["world"]!["resource_regenerations"]![0]![property] = JsonNode.Parse(value);
        AssertRejected(payload);
    }

    [TestMethod]
    [DataRow("remaining", "0")]
    [DataRow("remaining", "-0.5")]
    [DataRow("remaining", "1")]
    [DataRow("remaining", "\"NaN\"")]
    [DataRow("node", "{\"value\":99999}")]
    public void Invalid_recovery_state_is_rejected(string property, string value)
    {
        using var session = new CoreSession(Remaining(State(renewable: true), 0));
        Step(session);
        var payload = JsonNode.Parse(Capture(session).Encode())!;
        payload["world"]!["resource_recovery"]![0]![property] = JsonNode.Parse(value);
        AssertRejected(payload);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void Duplicate_resource_state_is_rejected(bool renewable)
    {
        using var session = new CoreSession(Remaining(State(renewable: renewable), 0));
        Step(session);
        var payload = JsonNode.Parse(Capture(session).Encode())!;
        var values = payload["world"]![renewable ? "resource_recovery" : "resource_regenerations"]!.AsArray();
        values.Add(values[0]!.DeepClone());
        AssertRejected(payload);
    }

    [TestMethod]
    public void Recovery_credit_requires_an_existing_renewable_resource_below_its_cap()
    {
        var state = Remaining(State(renewable: true), 0);
        using var session = new CoreSession(state);
        Step(session);
        var world = session.State.World;
        foreach (var resources in new ImmutableArray<NodeResourceView>[]
        {
            [], [new(Good, 0, new ResourceRenewal.NonRenewable())],
            [new(Good, 10, new ResourceRenewal.Renewable(10, 30))]
        })
        {
            using var invalid = new CoreSession(session.State with { World = world with
                { Nodes = [world.Nodes[0] with { Resources = resources }] } });
            AssertRejected(JsonNode.Parse(Capture(invalid).Encode())!);
        }
    }

    [TestMethod]
    public void Malformed_node_resources_return_a_failure_before_recovery_links_are_checked()
    {
        using var session = new CoreSession(Remaining(State(renewable: true), 0));
        Step(session);
        var payload = JsonNode.Parse(Capture(session).Encode())!;
        payload["world"]!["nodes"]![0]!["resources"] = new JsonArray((JsonNode?)null);
        AssertRejected(payload);
    }

    [TestMethod]
    public void Every_authoritative_resource_field_affects_the_checkpoint()
    {
        var state = State();
        using var original = new CoreSession(state);
        var baseline = Capture(original).Checkpoint;
        var regeneration = state.World.ResourceRegenerations.Single();
        foreach (var changed in new[]
        {
            regeneration with { InitialQuantity = regeneration.InitialQuantity + 1 },
            regeneration with { Sequence = 1 }
        })
        {
            using var session = new CoreSession(state with { World = state.World with { ResourceRegenerations = [changed] } });
            Assert.AreNotEqual(baseline, Capture(session).Checkpoint);
        }
        var renewable = Remaining(State(renewable: true), 0);
        using var half = new CoreSession(renewable with { World = renewable.World with
            { ResourceRecovery = [new(new(1), Good, .5)] } });
        using var quarter = new CoreSession(renewable with { World = renewable.World with
            { ResourceRecovery = [new(new(1), Good, .25)] } });
        Assert.AreNotEqual(Capture(half).Checkpoint, Capture(quarter).Checkpoint);
    }

    [TestMethod]
    public void Large_generated_initial_totals_survive_encoding_and_restoration()
    {
        using var session = new CoreSession(State(reserve: long.MaxValue, multipleNodes: true));
        var expected = (Int128)long.MaxValue * session.State.World.Nodes.Length;
        Assert.AreEqual(expected, session.State.World.ResourceRegenerations.Single().InitialQuantity);
        using var restored = (CoreSession)Restore(session);
        Assert.AreEqual(expected, restored.State.World.ResourceRegenerations.Single().InitialQuantity);
        Assert.AreEqual(Capture(session).Encode(), Capture(restored).Encode());
    }

    [TestMethod]
    public void Large_world_total_does_not_round_a_below_threshold_quantity_up_to_the_threshold()
    {
        var state = State(reserve: long.MaxValue, multipleNodes: true);
        var initial = state.World.ResourceRegenerations.Single().InitialQuantity;
        var remaining = (initial - 1) / 2;
        var nodes = state.World.Nodes.Select(n =>
        {
            var quantity = (long)Int128.Min(remaining, long.MaxValue);
            remaining -= quantity;
            return n with { Resources = quantity == 0 ? [] : [n.Resources.Single() with { Quantity = quantity }] };
        }).ToImmutableArray();
        Assert.AreEqual((Int128)0, remaining);
        state = state with { World = state.World with { Nodes = nodes } };
        using var session = new CoreSession(state);
        Assert.HasCount(1, Step(session, 3).Events.Where(e => e.Event is CoreEvent.ResourceRegenerated));
    }

    [TestMethod]
    public void Save_restore_retains_initial_totals_and_node_renewal_parameters()
    {
        using var finite = new CoreSession(Remaining(State(multipleNodes: true), 1));
        Step(finite);
        using var restored = (CoreSession)Restore(finite);
        Assert.AreEqual(finite.State.World.ResourceRegenerations.Single(), restored.State.World.ResourceRegenerations.Single());
        Assert.IsTrue(restored.State.World.ResourceRegenerations.Single().InitialQuantity > Quantity(restored.Read()));
        var state = Remaining(State(renewable: true, rate: 0), 0);
        state = state with { World = state.World with { Nodes = [state.World.Nodes[0] with
            { Resources = [new(Good, 0, new ResourceRenewal.Renewable(3, 30))] }] } };
        using var renewable = new CoreSession(state);
        Step(renewable);
        using var renewed = Restore(renewable);
        Assert.AreEqual(new ResourceRenewal.Renewable(3, 30), renewed.Read().World.Nodes.Single().Resources.Single().Renewal);
        Assert.AreEqual(1L, Step(renewed).Events.Select(e => e.Event).OfType<CoreEvent.ResourceRecovered>().Single().Quantity);
    }

    [TestMethod]
    public void Each_good_keeps_its_own_period_across_publication_and_restore()
    {
        var input = State(multipleNodes: true).World.Source;
        var entry = input.Configuration.Resources.Single();
        input = input with { Configuration = input.Configuration with { Resources =
            [entry with { RegenerationIntervalSeconds = 2d / 60 },
                entry with { Good = WorldFixture.Ore, RegenerationIntervalSeconds = 3d / 60 }] } };
        var content = WorldFixture.Content;
        content = content with { Goods = [..content.Goods, content.Goods[0] with { Good = new GoodId.Voxel(WorldFixture.Ore) }] };
        using var candidate = WorldFixture.Prepare(input, content);
        using var generated = (CoreSession)candidate.Activate();
        var initial = generated.State;
        using var session = new CoreSession(initial with { World = initial.World with
        {
            Nodes = initial.World.Nodes.Select((node, index) => node with { Resources = index == 0
                ? node.Resources.Select(r => r with { Quantity = 1 }).ToImmutableArray() : [] }).ToImmutableArray()
        } });
        Step(session);
        var updated = content with { Identity = new(new string('E', 64)) };
        Assert.IsInstanceOfType<CommandDecision.Accepted>(((SubmissionResult.Completed)session.Submit(
            new CoreCommand.PublishContent(updated), CommandSource.AutomationTest)).Receipt.Decision);
        CollectionAssert.AreEqual(initial.World.ResourceRegenerations.Select(r => r.InitialQuantity).ToArray(),
            session.State.World.ResourceRegenerations.Select(r => r.InitialQuantity).ToArray());
        var factory = CoreFactory.Create();
        using var restoredCandidate = WorldFixture.Success(factory.PrepareRestore(WorldFixture.Success(factory.DecodeState(
            Capture(session).Encode())), updated, WorldFixture.Options));
        using var restored = restoredCandidate.Activate();
        var expected = Step(session, 2).Events.Where(e => e.Event is CoreEvent.ResourceRegenerated).ToArray();
        CollectionAssert.AreEqual(new[] { Good, new GoodId.Voxel(WorldFixture.Ore) },
            expected.Select(e => ((CoreEvent.ResourceRegenerated)e.Event).Good).ToArray());
        CollectionAssert.AreEqual(new ulong[] { 2, 3 }, expected.Select(e => e.Position.CompletedSteps).ToArray());
        CollectionAssert.AreEqual(expected, Step(restored, 2).Events.Where(e => e.Event is CoreEvent.ResourceRegenerated).ToArray());
        Assert.AreEqual(Capture(session).Encode(), Capture(restored).Encode());
    }

    [TestMethod]
    public void Zero_reserve_draw_consumes_sequence_without_inserting_or_emitting_and_next_draw_can_succeed()
    {
        var state = Remaining(State(multipleNodes: true), 1);
        var source = state.World.Source;
        state = state with { World = state.World with { Source = source with { Configuration = source.Configuration with
            { Resources = [source.Configuration.Resources.Single() with { MinimumReserve = 0, MaximumReserve = 1 }] } } } };
        // Independently computed reserve samples: sequence 0 is below 2^63; sequence 3 is above it.
        using var session = new CoreSession(state);
        Assert.IsEmpty(Step(session, 3).Events.Where(e => e.Event is CoreEvent.ResourceRegenerated));
        Assert.AreEqual(1UL, session.State.World.ResourceRegenerations.Single().Sequence);
        Assert.AreEqual(1L, Quantity(session.Read()));
        var later = Step(session, 9).Events.Select(e => e.Event).OfType<CoreEvent.ResourceRegenerated>().ToArray();
        Assert.IsTrue(later.Length > 0);
        Assert.AreEqual(4UL, session.State.World.ResourceRegenerations.Single().Sequence);
    }

    [TestMethod]
    public void No_candidate_leaves_sequence_unchanged()
    {
        using var session = new CoreSession(Remaining(State(multipleNodes: true), 1, allNodes: true));
        Step(session, 6);
        Assert.AreEqual(0UL, session.State.World.ResourceRegenerations.Single().Sequence);
    }

    [TestMethod]
    public void Resource_random_keys_have_independent_fixed_vectors()
    {
        // Oracle: Python struct little-endian encoding plus an independent unsigned SplitMix64 implementation.
        Assert.AreEqual(0x5FE6786D6697B5D5UL, WorldResources.Sample(42, WorldFixture.Material, 0, false));
        Assert.AreEqual(0x69201A70D9426AC3UL, WorldResources.Sample(42, WorldFixture.Material, 0, true));
        Assert.AreEqual(0xC9E999E8829AF0E0UL, WorldResources.Sample(42, WorldFixture.Material, 1, false));
        Assert.AreEqual(0x3105A44C0DF39D94UL, WorldResources.Sample(42, WorldFixture.Material, 1, true));
        Assert.AreEqual(0x3BFA1DE6F8B6848AUL, WorldResources.Sample(42, new("矿:铁"), 0, false));
        Assert.AreEqual(0x19EE80378D714C78UL, WorldResources.Sample(42, new("矿:铁"), 0, true));
    }

    [TestMethod]
    public void Interval_conversion_uses_ceiling_and_handles_unreachable_periods()
    {
        Assert.AreEqual(1UL, WorldResources.IntervalSteps(double.Epsilon));
        Assert.AreEqual(3UL, WorldResources.IntervalSteps(.05));
        Assert.AreEqual(4UL, WorldResources.IntervalSteps(.050001));
        Assert.IsNull(WorldResources.IntervalSteps(double.MaxValue));
    }

    [TestMethod]
    public void Resource_events_and_candidate_selection_do_not_depend_on_input_enumeration_order()
    {
        var state = State(multipleNodes: true);
        var b = new VoxelDefinitionId("fixture:b-resource");
        var c = new VoxelDefinitionId("fixture:c-resource");
        var renewable = new GoodId.Voxel(WorldFixture.Ore);
        var entry = state.World.Source.Configuration.Resources.Single();
        var before = state.World with
        {
            Nodes = state.World.Nodes.Take(2).Select(n => n with { Resources =
                [new(renewable, 1, new ResourceRenewal.Renewable(3, 60)),
                    new(Good, 1, new ResourceRenewal.NonRenewable())] }).ToImmutableArray(),
            Source = state.World.Source with { Configuration = state.World.Source.Configuration with
                { Resources = [entry with { Good = c, RegenerationIntervalSeconds = 1.0 / 60, GlobalRegenerationRemainingReserveRatio = 1 },
                    entry with { Good = b, RegenerationIntervalSeconds = 1.0 / 60, GlobalRegenerationRemainingReserveRatio = 1 }] } },
            ResourceRegenerations = [new(c, 10, 0), new(b, 10, 0)]
        };
        var committed = before with { Nodes = before.Nodes.Select(n => n with
            { Resources = [new(renewable, 0, new ResourceRenewal.Renewable(3, 60))] }).ToImmutableArray() };
        var normal = WorldResources.Advance(before, committed, 0);
        var reversed = WorldResources.Advance(before with
        {
            Nodes = before.Nodes.Reverse().Select(n => n with { Resources = n.Resources.Reverse().ToImmutableArray() }).ToImmutableArray(),
            ResourceRegenerations = before.ResourceRegenerations.Reverse().ToImmutableArray()
        }, committed with { Nodes = committed.Nodes.Reverse().ToImmutableArray() }, 0);
        var expected = new CoreEvent[]
        {
            new CoreEvent.ResourceExhausted(new(1), Good, false),
            new CoreEvent.ResourceExhausted(new(1), renewable, true),
            new CoreEvent.ResourceExhausted(new(2), Good, false),
            new CoreEvent.ResourceExhausted(new(2), renewable, true),
            new CoreEvent.ResourceRecovered(new(1), renewable, 1),
            new CoreEvent.ResourceRecovered(new(2), renewable, 1)
        };
        CollectionAssert.AreEqual(expected, normal.Events.Take(6).ToArray());
        CollectionAssert.AreEqual(new GoodId[] { new GoodId.Voxel(b), new GoodId.Voxel(c) },
            normal.Events.OfType<CoreEvent.ResourceRegenerated>().Select(e => e.Good).ToArray());
        CollectionAssert.AreEqual(normal.Events.ToArray(), reversed.Events.ToArray());
        foreach (var node in normal.World.Nodes)
            CollectionAssert.AreEqual(node.Resources.ToArray(), reversed.World.Nodes.Single(n => n.Id == node.Id).Resources.ToArray());
    }

    private static void AssertRejected(JsonNode payload)
        => Assert.IsInstanceOfType<CoreResult<ICoreCapture>.Failure>(CoreFactory.Create().DecodeState(payload.ToJsonString()));
}
