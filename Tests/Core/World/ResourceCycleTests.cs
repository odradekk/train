using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.Sessions;
using static Train.Core.Tests.World.ResourceCycleFixture;

namespace Train.Core.Tests.World;

[TestClass]
public sealed class ResourceCycleTests
{
    [TestMethod]
    public void Recovery_accumulates_fractional_units_and_emits_only_the_zero_to_positive_transition()
    {
        using var session = new CoreSession(Remaining(State(renewable: true), 0));
        var old = session.Read();
        Assert.IsEmpty(Step(session).Events.Where(e => e.Event is CoreEvent.ResourceRecovered));
        Assert.AreEqual(0L, Quantity(session.Read()));
        var recovered = Step(session).Events.Select(e => e.Event).OfType<CoreEvent.ResourceRecovered>().Single();
        Assert.AreEqual(new CoreEvent.ResourceRecovered(new(1), Good, 1), recovered);
        Assert.AreEqual(1L, Quantity(session.Read()));
        Assert.IsEmpty(Step(session, 20).Events.Where(e => e.Event is CoreEvent.ResourceRecovered));
        Assert.AreEqual(10L, Quantity(session.Read()));
        Assert.AreEqual(0L, Quantity(old));
        var node = ((Lookup<NodeView>.Found)session.Read().FindNode(new(1))).Value;
        Assert.AreEqual(10L, node.Map.Resources.Single().Quantity);
    }

    [TestMethod]
    public void Recovery_uses_saved_node_parameters_instead_of_generation_parameters()
    {
        var state = State(renewable: true, rate: 0);
        var node = state.World.Nodes.Single();
        state = state with { World = state.World with { Nodes =
            [node with { Resources = [new(Good, 0, new ResourceRenewal.Renewable(3, 90))] }] } };
        using var session = new CoreSession(state);
        Assert.AreEqual(1L, Step(session).Events.Select(e => e.Event).OfType<CoreEvent.ResourceRecovered>().Single().Quantity);
        Step(session, 10);
        Assert.AreEqual(3L, Quantity(session.Read()));
    }

    [TestMethod]
    public void Recovery_discards_fractional_credit_at_the_cap()
    {
        using var session = new CoreSession(Remaining(State(renewable: true, rate: 90, reserve: 2), 1));
        Step(session);
        Assert.AreEqual(2L, Quantity(session.Read()));
        var atCap = session.State;
        using var depleted = new CoreSession(Remaining(atCap, 0));
        Step(depleted);
        Assert.AreEqual(1L, Quantity(depleted.Read()));
    }

    [TestMethod]
    public void Regeneration_checks_at_the_first_complete_period_and_preserves_old_views_and_graph()
    {
        var state = Remaining(State(multipleNodes: true), 1);
        Assert.IsTrue(state.World.Nodes.Length > 2);
        using var session = new CoreSession(state);
        var old = session.Read();
        Assert.IsEmpty(Step(session, 2).Events.Where(e => e.Event is CoreEvent.ResourceRegenerated));
        Assert.AreEqual(1L, Quantity(session.Read()));
        var first = Step(session).Events.Select(e => e.Event).OfType<CoreEvent.ResourceRegenerated>().Single();
        Assert.AreEqual(10L, first.Quantity);
        Assert.AreNotEqual(state.World.Nodes[0].Id, first.Node);
        var second = Step(session, 3).Events.Select(e => e.Event).OfType<CoreEvent.ResourceRegenerated>().Single();
        Assert.AreNotEqual(first.Node, second.Node);
        Assert.AreEqual(21L, Quantity(session.Read()));
        Assert.AreEqual(1L, Quantity(old));
        CollectionAssert.AreEqual(old.World.Edges.ToArray(), session.Read().World.Edges.ToArray());
        CollectionAssert.AreEqual(old.World.Nodes.Select(n => (n.Id, n.Position, n.Terrain, n.Occupant)).ToArray(),
            session.Read().World.Nodes.Select(n => (n.Id, n.Position, n.Terrain, n.Occupant)).ToArray());
    }

    [TestMethod]
    public void Regeneration_threshold_is_strictly_below_the_initial_world_total()
    {
        var state = State(multipleNodes: true);
        var half = state.World.Nodes.Sum(n => n.Resources.Single().Quantity) / 2;
        using var equal = new CoreSession(Remaining(state, half));
        Assert.IsEmpty(Step(equal, 3).Events.Where(e => e.Event is CoreEvent.ResourceRegenerated));
        Assert.AreEqual(half, Quantity(equal.Read()));
        using var below = new CoreSession(Remaining(state, half - 1));
        Assert.HasCount(1, Step(below, 3).Events.Where(e => e.Event is CoreEvent.ResourceRegenerated));
    }

    [TestMethod]
    public void No_candidate_advances_the_period_without_backlogging_checks()
    {
        var state = Remaining(State(multipleNodes: true), 1, allNodes: true);
        using var full = new CoreSession(state);
        Assert.IsEmpty(Step(full, 3).Events.Where(e => e.Event is CoreEvent.ResourceRegenerated));
        var node = full.State.World.Nodes[^1];
        using var freed = new CoreSession(full.State with { World = full.State.World with
        {
            Nodes = full.State.World.Nodes.SetItem(full.State.World.Nodes.Length - 1, node with { Resources = [] })
        } });
        Assert.IsEmpty(Step(freed, 2).Events.Where(e => e.Event is CoreEvent.ResourceRegenerated));
        Assert.AreEqual(node.Id, Step(freed).Events.Select(e => e.Event).OfType<CoreEvent.ResourceRegenerated>().Single().Node);
    }

    [TestMethod]
    public void Zero_initial_reserves_never_trigger_regeneration()
    {
        var state = State(reserve: 0, multipleNodes: true);
        state = state with { World = state.World with
            { Nodes = state.World.Nodes.Select(n => n with { Resources = [] }).ToImmutableArray() } };
        using var session = new CoreSession(state);
        Assert.IsEmpty(Step(session, 12).Events.Where(e => e.Event is CoreEvent.ResourceRegenerated));
        Assert.AreEqual(0L, Quantity(session.Read()));
    }

    [TestMethod]
    public void Regeneration_uses_only_empty_nodes_with_allowed_terrain_and_draws_within_the_frozen_range()
    {
        var state = Remaining(State(multipleNodes: true), 1);
        var eligible = state.World.Nodes[1].Id;
        var source = state.World.Source;
        state = state with { World = state.World with
        {
            Source = source with { Configuration = source.Configuration with
                { Resources = [source.Configuration.Resources.Single() with { MinimumReserve = 2, MaximumReserve = 7 }] } },
            Nodes = state.World.Nodes.Select((n, index) => index < 2 ? n
                : n with { Terrain = n.Terrain with { TypeId = WorldFixture.Hills } }).ToImmutableArray()
        } };
        using var session = new CoreSession(state);
        var regenerated = Step(session, 3).Events.Select(e => e.Event).OfType<CoreEvent.ResourceRegenerated>().Single();
        Assert.AreEqual(eligible, regenerated.Node);
        Assert.IsTrue(regenerated.Quantity is >= 2 and <= 7);
        Assert.IsEmpty(Step(session, 9).Events.Where(e => e.Event is CoreEvent.ResourceRegenerated));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void Save_restore_preserves_fractional_recovery_and_partial_regeneration_period(bool renewable)
    {
        using var continuous = new CoreSession(Remaining(State(renewable: renewable, multipleNodes: !renewable), renewable ? 0 : 1));
        Step(continuous);
        var saved = Capture(continuous).Encode();
        using var restored = Restore(continuous);
        var expected = Step(continuous, 8);
        var actual = Step(restored, 8);
        CollectionAssert.AreEqual(expected.Events.ToArray(), actual.Events.ToArray());
        Assert.AreEqual(Capture(continuous).Encode(), Capture(restored).Encode());
        Assert.AreEqual(Capture(continuous).Checkpoint, Capture(restored).Checkpoint);
        Assert.AreNotEqual(saved, Capture(continuous).Encode());
    }
}
