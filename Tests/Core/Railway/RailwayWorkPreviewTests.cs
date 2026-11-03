using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.Sessions;
using Train.Core.Tests.Construction;
using Train.Core.Tests.World;
using static Train.Core.Tests.Railway.RailwayWorkFixture;
using static Train.Core.Tests.Railway.TrainDispatchFixture;

namespace Train.Core.Tests.Railway;

[TestClass]
public sealed class RailwayWorkPreviewTests
{
    [TestMethod]
    public void Expansion_preview_rounds_once_and_matches_the_started_work_without_mutating_the_read_boundary()
    {
        var state = Prepared(0, 1.5, 0.5, 0.025, 18, 1);
        using var session = new CoreSession(state);
        var read = session.Read();
        var preview = Preview(read, new RailwayWorkRequest.AddTrack(new(1)), Player(state).Id);
        Assert.AreEqual(1L, preview.RequiredMaterials.Single().Quantity);
        Assert.AreEqual(3UL, preview.DurationTicks);
        Assert.IsTrue(preview.Blockers.IsEmpty);
        Assert.AreEqual(20L, ConstructionFixture.Stock(session, Player(state).Inventory));
        Assert.IsTrue(((Lookup<EdgeView>.Found)read.FindEdge(new(1))).Value.Map.Works.IsEmpty);
        var work = Accepted(session, new CoreCommand.ExpandRail(new(1), Player(state).Id))
            .Events.Select(e => e.Event).OfType<CoreEvent.RailwayWorkStarted>().Single().Work;
        CollectionAssert.AreEqual(preview.RequiredMaterials.ToArray(), work.InvestedGoods.ToArray());
        Advance(session, preview.DurationTicks - 1);
        Assert.AreEqual(0, session.Read().World.Edges.Single(e => e.Id == new EdgeId(1)).Tracks);
        Advance(session, 1);
        Assert.AreEqual(1, session.Read().World.Edges.Single(e => e.Id == new EdgeId(1)).Tracks);
        Assert.IsTrue(((Lookup<EdgeView>.Found)read.FindEdge(new(1))).Value.Map.Works.IsEmpty);
    }

    [TestMethod]
    [DataRow("none", "invalid_state")]
    [DataRow("missing", "object_missing")]
    [DataRow("shortage", "insufficient_goods")]
    [DataRow("not-owned", "not_owned")]
    public void Unavailable_material_source_keeps_determinable_estimates(string scenario, string code)
    {
        var state = Prepared(0, 1.5, 0.5, 0.025, 18, 1, 0);
        var player = Player(state);
        if (scenario == "not-owned") state = state with { World = state.World with
        {
            Settlements = state.World.Settlements.Select(s => s.Id == player.Id ? s with { Relation = SettlementRelation.NonHostile } : s).ToImmutableArray(),
            Nodes = state.World.Nodes.Select(node => node.Id != player.Node ? node : node with
            { Occupant = new NodeOccupant.Settlement(player.Id, SettlementRelation.NonHostile) }).ToImmutableArray(),
            Instances = state.World.Instances.Select(instance => instance.Id is InstanceId.Building building
                && player.Plots.Any(plot => plot.Building == building.Id)
                ? instance with { Ownership = SettlementRelation.NonHostile } : instance).ToImmutableArray()
        } };
        using var session = new CoreSession(state);
        var capture = WorldFixture.Success(session.Capture(CapturePurpose.Save));
        WorldFixture.Success(CoreFactory.Create().DecodeState(capture.Encode()));
        SettlementId? source = scenario == "none" ? null : scenario == "missing" ? new SettlementId(999) : player.Id;
        var preview = Preview(session.Read(), new RailwayWorkRequest.AddTrack(new(1)), source);
        Assert.AreEqual(1L, preview.RequiredMaterials.Single().Quantity);
        Assert.AreEqual(3UL, preview.DurationTicks);
        Assert.AreEqual(code, preview.Blockers.Single().Code);
    }

    [TestMethod]
    public void Expansion_counts_work_in_progress_at_the_track_limit()
    {
        var state = Prepared(0, 1, 1, 1, 1, 1);
        state = state with { World = state.World with { Edges = [state.World.Edges[0] with { TrackLimit = 1 }] } };
        using var session = new CoreSession(state);
        Assert.IsTrue(Preview(session.Read(), new RailwayWorkRequest.AddTrack(new(1)), Player(state).Id).Blockers.IsEmpty);
        Accepted(session, new CoreCommand.ExpandRail(new(1), Player(state).Id));
        var blocked = Preview(session.Read(), new RailwayWorkRequest.AddTrack(new(1)), Player(state).Id);
        Assert.AreEqual("Edge.Tracks", blocked.Blockers.Single().Field);
        Advance(session, 60);
        var completed = ((Lookup<EdgeView>.Found)session.Read().FindEdge(new(1))).Value.Map;
        Assert.AreEqual(1, completed.Tracks);
        Assert.IsTrue(completed.Works.IsEmpty);
        Assert.AreEqual("Edge.Tracks", Preview(session.Read(), new RailwayWorkRequest.AddTrack(new(1)), Player(state).Id).Blockers.Single().Field);
    }

    [TestMethod]
    public void Expansion_preview_reports_both_queued_track_limit_and_missing_source()
    {
        var state = Prepared(0, 1, 1, 1, 1, 1);
        state = state with { World = state.World with { Edges = [state.World.Edges[0] with { TrackLimit = 1 }] } };
        using var session = new CoreSession(state);
        Accepted(session, new CoreCommand.ExpandRail(new(1), Player(state).Id));
        var preview = Preview(session.Read(), new RailwayWorkRequest.AddTrack(new(1)), null);
        CollectionAssert.AreEquivalent(new[] { ("invalid_state", "Edge.Tracks"), ("invalid_state", "MaterialSource") },
            preview.Blockers.Select(problem => (problem.Code, problem.Field)).ToArray());
    }

    [TestMethod]
    public void Missing_rule_does_not_report_missing_material_source()
    {
        var state = Prepared(0, 1, 1, 1, 1, 1);
        state = state with { Content = state.Content with { RailwayRules = [] } };
        using var session = new CoreSession(state);
        var preview = Preview(session.Read(), new RailwayWorkRequest.AddTrack(new(1)), null);
        Assert.AreEqual(("content_missing", "RailwayRule"), (preview.Blockers.Single().Code, preview.Blockers.Single().Field));
        Assert.IsTrue(preview.RequiredMaterials.IsEmpty);
        Assert.AreEqual(0UL, preview.DurationTicks);
    }

    [TestMethod]
    public void Repair_preview_uses_per_break_cost_and_reports_duplicate_work()
    {
        var state = Prepared(1, 100, 5, 8, 2, 0.025);
        using var session = new CoreSession(state);
        Accepted(session, new CoreCommand.DebugAddRailBreak(new(1), 1, 4));
        var broken = ((Lookup<EdgeView>.Found)session.Read().FindEdge(new(1))).Value.Map.Breaks.Single().Id;
        var request = new RailwayWorkRequest.RepairBreak(new(1), broken);
        var preview = Preview(session.Read(), request, Player(state).Id);
        Assert.AreEqual(2L, preview.RequiredMaterials.Single().Quantity);
        Assert.AreEqual(2UL, preview.DurationTicks);
        Assert.IsTrue(preview.Blockers.IsEmpty);
        var work = Accepted(session, new CoreCommand.RepairRail(new(1), broken, Player(state).Id))
            .Events.Select(e => e.Event).OfType<CoreEvent.RailwayWorkStarted>().Single().Work;
        CollectionAssert.AreEqual(preview.RequiredMaterials.ToArray(), work.InvestedGoods.ToArray());
        Assert.AreEqual("busy", Preview(session.Read(), request, Player(state).Id).Blockers.Single().Code);
        Advance(session, 1);
        Assert.AreEqual(1, ((Lookup<EdgeView>.Found)session.Read().FindEdge(new(1))).Value.Map.Breaks.Length);
        Advance(session, 1);
        Assert.AreEqual("object_missing", Preview(session.Read(), request, Player(state).Id).Blockers.Single().Code);
    }

    [TestMethod]
    [DataRow("edge", "object_missing", "Edge")]
    [DataRow("rule", "content_missing", "RailwayRule")]
    [DataRow("good", "content_missing", "RailwayRule.RailGood")]
    [DataRow("identity", "identity_exhausted", "World.Next.Job")]
    public void Preview_reports_missing_inputs_and_exhausted_job_identity(string scenario, string code, string field)
    {
        var state = Prepared(0, 1, 1, 1, 1, 1);
        if (scenario == "rule") state = state with { Content = state.Content with { RailwayRules = [] } };
        if (scenario == "good") state = state with { Content = state.Content with { Goods = [] } };
        if (scenario == "identity") state = state with { World = state.World with { Next = state.World.Next with { Job = ulong.MaxValue } } };
        using var session = new CoreSession(state);
        var preview = Preview(session.Read(), new RailwayWorkRequest.AddTrack(new(scenario == "edge" ? 999UL : 1UL)), Player(state).Id);
        Assert.AreEqual((code, field), (preview.Blockers.Single().Code, preview.Blockers.Single().Field));
        if (scenario is "edge" or "rule" or "good")
        {
            Assert.IsTrue(preview.RequiredMaterials.IsEmpty);
            Assert.AreEqual(0UL, preview.DurationTicks);
        }
    }

    [TestMethod]
    public void Preview_rejects_programming_errors_before_fault_translation()
    {
        using var session = new CoreSession(Prepared(0, 1, 1, 1, 1, 1));
        var read = session.Read();
        Assert.ThrowsExactly<ArgumentNullException>(() => read.PreviewRailwayWork(null!, null));
        Assert.ThrowsExactly<ArgumentException>(() => read.PreviewRailwayWork(new RailwayWorkRequest.AddTrack(default), null));
        Assert.ThrowsExactly<ArgumentException>(() => read.PreviewRailwayWork(new RailwayWorkRequest.RepairBreak(default, new(1)), null));
        Assert.ThrowsExactly<ArgumentException>(() => read.PreviewRailwayWork(new RailwayWorkRequest.RepairBreak(new(1), default), null));
        Assert.ThrowsExactly<ArgumentException>(() => read.PreviewRailwayWork(new RailwayWorkRequest.AddTrack(new(1)), default(SettlementId)));
    }

    [TestMethod]
    public void Exact_material_stock_is_sufficient_and_preview_does_not_consume_it()
    {
        var state = Prepared(0, 1, 1, 1, 1, 1, 1);
        using var session = new CoreSession(state);
        var preview = Preview(session.Read(), new RailwayWorkRequest.AddTrack(new(1)), Player(state).Id);
        Assert.IsTrue(preview.Blockers.IsEmpty);
        Assert.AreEqual(1L, ConstructionFixture.Stock(session, Player(state).Inventory));
    }

    [TestMethod]
    public void Computation_fault_returns_a_query_fault_and_leaves_the_session_usable()
    {
        var state = Prepared(0, double.MaxValue, 2, 1, 1, 1);
        using var session = new CoreSession(state);
        Assert.IsInstanceOfType<CoreResult<RailwayWorkPreview>.Faulted>(session.Read().PreviewRailwayWork(new RailwayWorkRequest.AddTrack(new(1)), Player(state).Id));
        Assert.IsNull(session.Read().Fault);
        Assert.IsInstanceOfType<Lookup<EdgeView>.Found>(session.Read().FindEdge(new(1)));
    }

    private static SubmissionResult.Completed Accepted(ICoreSession session, CoreCommand command)
    {
        var result = (SubmissionResult.Completed)session.Submit(command, CommandSource.UserInterface);
        Assert.IsInstanceOfType<CommandDecision.Accepted>(result.Receipt.Decision);
        return result;
    }

    private static RailwayWorkPreview Preview(ICoreReadView read, RailwayWorkRequest request, SettlementId? source)
        => ((CoreResult<RailwayWorkPreview>.Success)read.PreviewRailwayWork(request, source)).Value;
}
