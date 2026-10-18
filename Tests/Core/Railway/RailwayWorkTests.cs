using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.Sessions;
using Train.Core.Tests.Construction;
using Train.Core.Tests.World;
using Train.Core.World;
using static Train.Core.Tests.Railway.RailwayWorkFixture;
using static Train.Core.Tests.Railway.TrainDispatchFixture;

namespace Train.Core.Tests.Railway;

[TestClass]
public sealed class RailwayWorkTests
{
    [TestMethod]
    public void Expansion_rounds_the_product_once_and_keeps_stored_works_empty()
    {
        var state = Prepared(0, 1.5, 0.5, 0.025, 18, 1);
        var inventory = Player(state).Inventory;
        using var session = new CoreSession(state);
        Assert.AreEqual(20L, ConstructionFixture.Stock(session, inventory));
        var started = Accepted(session, new CoreCommand.ExpandRail(new(1), Player(state).Id));
        var work = started.Events.Select(item => item.Event).OfType<CoreEvent.RailwayWorkStarted>().Single().Work;
        Assert.AreEqual(1, work.Track);
        Assert.AreEqual(1L, work.InvestedGoods.Single().Quantity);
        Assert.AreEqual(3UL, ((CoreSession)session).State.World.RailwayJobs.Single().FrozenDurationTicks);
        Assert.AreEqual(19L, ConstructionFixture.Stock(session, inventory));
        Assert.AreEqual(0, ((CoreSession)session).State.World.Edges.Single().Tracks);
        Assert.IsTrue(((CoreSession)session).State.World.Edges.Single().Works.IsEmpty);
        Assert.AreEqual(work.Track, ((Lookup<EdgeView>.Found)session.Read().FindEdge(new(1))).Value.Map.Works.Single().Track);
        var finished = Advance(session, 3);
        Assert.AreEqual(1, finished.Events.Select(item => item.Event).OfType<CoreEvent.RailExpanded>().Single().Tracks);
        Assert.AreEqual(1, session.Read().World.Edges.Single(edge => edge.Id == new EdgeId(1)).Tracks);
        Assert.IsInstanceOfType<Lookup<JobView>.Missing>(session.Read().FindJob(work.Id));
        Assert.AreEqual(19L, ConstructionFixture.Stock(session, inventory));
    }

    [TestMethod]
    public void A_zero_track_edge_releases_a_no_route_train_after_expansion()
    {
        var state = Prepared(0, 1, 1, 1.0 / 60, 1, 1);
        using var session = new CoreSession(state);
        Accepted(session, new CoreCommand.StartTrain(new(1)));
        Assert.AreEqual(TrainActivity.NoRoute, View(session, 1).Activity);
        Accepted(session, new CoreCommand.ExpandRail(new(1), Player(state).Id));
        Advance(session, 1);
        Assert.AreEqual(TrainActivity.NoRoute, View(session, 1).Activity);
        Until(session, () => View(session, 1).Position is TrainPosition.Edge { Track: 1 });
    }

    [TestMethod]
    public void A_published_faster_rule_does_not_shorten_work_already_started()
    {
        var state = Prepared(0, 2, 0.5, 2.0 / 60, 1, 1, 5);
        var inventory = Player(state).Inventory;
        using var session = new CoreSession(state);
        Assert.AreEqual(5L, ConstructionFixture.Stock(session, inventory));
        var first = Created(Accepted(session, new CoreCommand.ExpandRail(new(1), Player(state).Id)));
        Assert.AreEqual(4L, ConstructionFixture.Stock(session, inventory));
        var content = ((CoreSession)session).State.Content;
        var faster = content with
        {
            Identity = new(new string('F', 64)),
            RailwayRules = [content.RailwayRules[0] with { BuildSecondsPerMeter = 0.5 / 60 }]
        };
        Accepted(session, new CoreCommand.PublishContent(faster));
        Assert.AreEqual(4L, ConstructionFixture.Stock(session, inventory));
        var second = Created(Accepted(session, new CoreCommand.ExpandRail(new(1), Player(state).Id)));
        Assert.AreEqual(3L, ConstructionFixture.Stock(session, inventory));
        var step = Advance(session, 1);
        Assert.AreEqual(1, step.Events.Select(item => item.Event).OfType<CoreEvent.RailExpanded>().Single().Tracks);
        Assert.IsInstanceOfType<Lookup<JobView>.Missing>(session.Read().FindJob(second));
        var earlier = Work(session, first);
        Assert.AreEqual(2, earlier.Track);
        Assert.AreEqual(0.25, earlier.Progress, 1e-12);
        Advance(session, 2);
        Assert.AreEqual(0.75, Work(session, first).Progress, 1e-12);
        var done = Advance(session, 1);
        Assert.AreEqual(2, done.Events.Select(item => item.Event).OfType<CoreEvent.RailExpanded>().Single().Tracks);
        Assert.IsInstanceOfType<Lookup<JobView>.Missing>(session.Read().FindJob(first));
        Assert.AreEqual(3L, ConstructionFixture.Stock(session, inventory));
    }

    [TestMethod]
    public void Manual_repair_clears_the_break_and_return_mark_without_reversing_direction()
    {
        var state = Prepared(1, 20, 1, 1, 1, 2.0 / 60, 5);
        var inventory = Player(state).Inventory;
        var broken = new TrackBreakView(new(1), 1, 10);
        var world = state.World with
        {
            Edges = [state.World.Edges[0] with { Breaks = [broken] }],
            Trains = [state.World.Trains[0] with
            {
                Activity = TrainActivity.Running,
                Position = new TrainPosition.Edge(new(1), 1, TravelDirection.Forward, 4),
                ReturningFromBreak = broken.Id
            }],
            Next = state.World.Next with { Break = 2 }
        };
        using var session = new CoreSession(state with { World = world });
        Assert.AreEqual(5L, ConstructionFixture.Stock(session, inventory));
        Assert.IsInstanceOfType<CoreEvent.RailwayWorkStarted>(Accepted(session,
            new CoreCommand.RepairRail(new(1), broken.Id, Player(state).Id)).Events.Select(item => item.Event).Single());
        Assert.AreEqual(4L, ConstructionFixture.Stock(session, inventory));
        Advance(session, 1);
        Assert.AreEqual(TravelDirection.Forward, ((TrainPosition.Edge)((CoreSession)session).State.World.Trains[0].Position).Direction);
        Assert.AreEqual(broken.Id, ((CoreSession)session).State.World.Trains[0].ReturningFromBreak);
        Assert.AreEqual(4L, ConstructionFixture.Stock(session, inventory));
        var finished = Advance(session, 1);
        var train = ((CoreSession)session).State.World.Trains[0];
        Assert.IsNull(train.ReturningFromBreak);
        Assert.AreEqual(TravelDirection.Forward, ((TrainPosition.Edge)train.Position).Direction);
        Assert.AreEqual(0, ((CoreSession)session).State.World.Edges[0].Breaks.Length);
        Assert.AreEqual(broken.Id, finished.Events.Select(item => item.Event).OfType<CoreEvent.RailRepaired>().Single().Break);
        Assert.AreEqual(4L, ConstructionFixture.Stock(session, inventory));
    }

    [TestMethod]
    public void Repair_reopens_the_only_track_for_a_following_departure()
    {
        var state = Prepared(1, 1, 1, 1, 1, 2.0 / 60);
        var world = state.World with
        {
            Edges = [state.World.Edges[0] with { Breaks = [new(new(1), 1, 0.5)] }],
            Next = state.World.Next with { Break = 2 }
        };
        using var session = new CoreSession(state with { World = world });
        Accepted(session, new CoreCommand.StartTrain(new(1)));
        Assert.AreEqual(TrainActivity.NoRoute, View(session, 1).Activity);
        Accepted(session, new CoreCommand.RepairRail(new(1), new(1), Player(state).Id));
        Advance(session, 2);
        Assert.AreEqual(TrainActivity.NoRoute, View(session, 1).Activity);
        Until(session, () => View(session, 1).Position is TrainPosition.Edge { Track: 1 });
    }

    [TestMethod]
    public void Automatic_repair_holds_a_job_until_materials_arrive_and_only_repeats_when_blockers_change()
    {
        var state = Prepared(1, 20, 1, 1, 2, 2.0 / 60, 0);
        using var session = new CoreSession(state);
        var player = Player(state);
        Assert.AreEqual(0, Accepted(session, new CoreCommand.SetAutomaticRailRepair(new(1), player.Id)).Events.Length);
        Assert.AreEqual(player.Id, ((Lookup<EdgeView>.Found)session.Read().FindEdge(new(1))).Value.AutoRepairMaterialSource);
        Assert.AreEqual(0, Accepted(session, new CoreCommand.SetAutomaticRailRepair(new(1), player.Id)).Events.Length);
        var broken = Accepted(session, new CoreCommand.DebugAddRailBreak(new(1), 1, 5));
        var events = broken.Events.Select(item => item.Event).ToArray();
        Assert.IsInstanceOfType<CoreEvent.RailBroken>(events[0]);
        Assert.AreEqual("insufficient_goods", ((CoreEvent.RailwayWorkChanged)events[1]).Reasons.Single().Code);
        Assert.AreEqual(0, Advance(session, 1).Events.Count(item => item.Event is CoreEvent.RailwayWorkChanged));
        var pending = ((CoreSession)session).State.World.RailwayJobs.Single();
        var before = Work(session, pending.Id).Blockers.Single().Subject;
        var other = player with { Id = new(player.Id.Value + 100), Inventory = new(900) };
        var basis = ((CoreSession)session).State;
        var world = basis.World with
        {
            Settlements = basis.World.Settlements.Add(other),
            Inventories = basis.World.Inventories.Add(new(other.Inventory, new InventoryOwner.Settlement(other.Id), [], []))
        };
        using var changed = new CoreSession(basis with { World = world });
        var switched = Accepted(changed, new CoreCommand.SetAutomaticRailRepair(new(1), other.Id));
        var again = switched.Events.Select(item => item.Event).OfType<CoreEvent.RailwayWorkChanged>().Single();
        var reason = again.Reasons.Single();
        Assert.AreEqual(("insufficient_goods", "MaterialSource.Inventory"), (reason.Code, reason.Field));
        Assert.AreEqual(new WorldObjectId.Settlement(other.Id), reason.Subject);
        Assert.AreNotEqual(before, reason.Subject);
        Assert.AreEqual(0, Advance(changed, 1).Events.Count(item => item.Event is CoreEvent.RailwayWorkChanged));
        using var supplied = new CoreSession(((CoreSession)session).State with
            { World = Stock(((CoreSession)session).State.World, player.Id, 2) });
        Assert.AreEqual(2L, ConstructionFixture.Stock(supplied, player.Inventory));
        var started = Advance(supplied, 1);
        Assert.IsTrue(started.Events.Any(item => item.Event is CoreEvent.RailwayWorkStarted));
        var job = Work(supplied, pending.Id);
        Assert.AreEqual(0, job.Blockers.Length);
        Assert.AreEqual(0, job.Progress);
        Assert.AreEqual(0L, ConstructionFixture.Stock(supplied, player.Inventory));
        Advance(supplied, 1);
        Assert.AreEqual(0.5, Work(supplied, pending.Id).Progress, 1e-12);
        Assert.AreEqual(0L, ConstructionFixture.Stock(supplied, player.Inventory));
        Advance(supplied, 1);
        Assert.IsInstanceOfType<Lookup<JobView>.Missing>(supplied.Read().FindJob(pending.Id));
        Assert.AreEqual(0L, ConstructionFixture.Stock(supplied, player.Inventory));
    }

    [TestMethod]
    public void Turning_automatic_repair_off_drops_unstarted_jobs_and_keeps_started_ones()
    {
        var state = Prepared(2, 20, 1, 1, 1, 1, 0);
        var player = Player(state).Id;
        using var session = new CoreSession(state);
        Accepted(session, new CoreCommand.SetAutomaticRailRepair(new(1), player));
        Accepted(session, new CoreCommand.DebugAddRailBreak(new(1), 1, 4));
        var pending = ((CoreSession)session).State.World.RailwayJobs.Single().Id;
        using var supplied = new CoreSession(((CoreSession)session).State with
            { World = Stock(((CoreSession)session).State.World, player, 5) });
        Accepted(supplied, new CoreCommand.DebugAddRailBreak(new(1), 2, 6));
        var started = ((CoreSession)supplied).State.World.RailwayJobs.Single(job => job.Blockers.IsEmpty).Id;
        var cleared = Accepted(supplied, new CoreCommand.SetAutomaticRailRepair(new(1), null));
        Assert.AreEqual(0, cleared.Events.Length);
        var remaining = ((CoreSession)supplied).State.World.RailwayJobs.Single();
        Assert.AreEqual(started, remaining.Id);
        Assert.AreNotEqual(pending, remaining.Id);
        Assert.IsNull(((Lookup<EdgeView>.Found)supplied.Read().FindEdge(new(1))).Value.AutoRepairMaterialSource);
    }

    [TestMethod]
    public void Changing_the_automatic_source_reevaluates_unstarted_jobs_immediately()
    {
        var state = Prepared(1, 20, 1, 1, 2, 2.0 / 60, 0);
        var player = Player(state);
        var other = player with { Id = new(player.Id.Value + 100), Inventory = new(900) };
        var world = state.World with
        {
            Settlements = state.World.Settlements.Add(other),
            Inventories = state.World.Inventories.Add(new(other.Inventory, new InventoryOwner.Settlement(other.Id),
                [new(new GoodId.Voxel(WorldFixture.Material), 2)], []))
        };
        using var session = new CoreSession(state with { World = world });
        Accepted(session, new CoreCommand.SetAutomaticRailRepair(new(1), player.Id));
        Accepted(session, new CoreCommand.DebugAddRailBreak(new(1), 1, 3));
        Assert.IsFalse(((CoreSession)session).State.World.RailwayJobs.Single().Blockers.IsEmpty);
        var funded = ((CoreSession)session).State with { World = Stock(((CoreSession)session).State.World, player.Id, 5) };
        using var ready = new CoreSession(funded);
        Assert.AreEqual(5L, ConstructionFixture.Stock(ready, player.Inventory));
        Assert.AreEqual(2L, ConstructionFixture.Stock(ready, other.Inventory));
        var changed = Accepted(ready, new CoreCommand.SetAutomaticRailRepair(new(1), other.Id));
        Assert.IsInstanceOfType<CoreEvent.RailwayWorkStarted>(changed.Events.Select(item => item.Event).OfType<CoreEvent.RailwayWorkStarted>().Single());
        Assert.AreEqual(other.Id, ((CoreSession)ready).State.World.RailwayJobs.Single().MaterialSource);
        Assert.AreEqual(5L, ConstructionFixture.Stock(ready, player.Inventory));
        Assert.AreEqual(0L, ConstructionFixture.Stock(ready, other.Inventory));
        Advance(ready, 2);
        Assert.IsInstanceOfType<Lookup<JobView>.Missing>(ready.Read().FindJob(((CoreSession)session).State.World.RailwayJobs.Single().Id));
        Assert.AreEqual(5L, ConstructionFixture.Stock(ready, player.Inventory));
        Assert.AreEqual(0L, ConstructionFixture.Stock(ready, other.Inventory));
    }

    [TestMethod]
    public void Enabling_automatic_repair_opens_existing_breaks_in_break_order_or_rejects_the_whole_command()
    {
        var state = Prepared(2, 20, 1, 1, 2, 2.0 / 60, 0);
        var player = Player(state).Id;
        using var session = new CoreSession(state);
        Accepted(session, new CoreCommand.DebugAddRailBreak(new(1), 1, 4));
        Accepted(session, new CoreCommand.DebugAddRailBreak(new(1), 2, 6));
        var opened = Accepted(session, new CoreCommand.SetAutomaticRailRepair(new(1), player));
        var created = ((CommandDecision.Accepted)opened.Receipt.Decision).Created;
        var jobs = ((CoreSession)session).State.World.RailwayJobs;
        var breaks = ((CoreSession)session).State.World.Edges[0].Breaks;
        Assert.AreEqual(2, created.Length);
        Assert.IsTrue(breaks[0].Id.Value < breaks[1].Id.Value);
        Assert.AreEqual(breaks[0].Id, jobs[0].Break);
        Assert.AreEqual(breaks[1].Id, jobs[1].Break);
        CollectionAssert.AreEqual(jobs.Select(job => new WorldObjectId.Job(job.Id)).ToArray(), created.ToArray());
        var before = ((CoreSession)session).State.World with { Next = ((CoreSession)session).State.World.Next with { Job = ulong.MaxValue - 1 } };
        before = before with { AutomaticRailRepairs = [], RailwayJobs = [] };
        using var exhausted = new CoreSession(((CoreSession)session).State with { World = before });
        var problem = Rejected(exhausted, new CoreCommand.SetAutomaticRailRepair(new(1), player));
        Assert.AreEqual(("identity_exhausted", "World.Next.Job"), (problem.Code, problem.Field));
        Assert.IsNull(problem.Subject);
        var unchanged = ((CoreSession)exhausted).State.World;
        Assert.AreEqual(0, unchanged.AutomaticRailRepairs.Length);
        Assert.AreEqual(0, unchanged.RailwayJobs.Length);
        Assert.AreEqual(before.Edges[0].Breaks.Length, unchanged.Edges[0].Breaks.Length);
    }

    [TestMethod]
    public void Manual_repair_of_an_unstarted_job_is_busy_when_stock_is_sufficient()
    {
        var pending = Pending();
        var player = Player(pending);
        var broken = pending.World.Edges[0].Breaks.Single().Id;
        var funded = pending with { World = Stock(pending.World, player.Id, 5) };
        using var session = new CoreSession(funded);
        var problem = Rejected(session, new CoreCommand.RepairRail(new(1), broken, player.Id));
        Assert.AreEqual(("busy", "Break", new WorldObjectId.Edge(new(1))), (problem.Code, problem.Field, problem.Subject));
        Assert.AreEqual(1, ((CoreSession)session).State.World.RailwayJobs.Length);
        Assert.AreEqual(5L, ConstructionFixture.Stock(session, player.Inventory));
    }

    [TestMethod]
    public void Manual_shortage_rejects_without_a_job_and_exhausted_auto_break_writes_nothing()
    {
        var state = Prepared(1, 20, 1, 1, 3, 1, 0);
        using var session = new CoreSession(state);
        var shortage = Rejected(session, new CoreCommand.ExpandRail(new(1), Player(state).Id));
        Assert.AreEqual(("insufficient_goods", "MaterialSource.Inventory"), (shortage.Code, shortage.Field));
        Assert.AreEqual(0, ((CoreSession)session).State.World.RailwayJobs.Length);
        Accepted(session, new CoreCommand.SetAutomaticRailRepair(new(1), Player(state).Id));
        var exhaustedWorld = ((CoreSession)session).State.World with { Next = ((CoreSession)session).State.World.Next with { Job = ulong.MaxValue } };
        using var exhausted = new CoreSession(((CoreSession)session).State with { World = exhaustedWorld });
        var refusal = Rejected(exhausted, new CoreCommand.DebugAddRailBreak(new(1), 1, 2));
        Assert.AreEqual(("identity_exhausted", "World.Next.Job"), (refusal.Code, refusal.Field));
        Assert.AreEqual(0, ((CoreSession)exhausted).State.World.Edges[0].Breaks.Length);
    }

    [TestMethod]
    [DataRow("missing-edge", "object_missing", "Edge")]
    [DataRow("missing-rule", "content_missing", "RailwayRule")]
    [DataRow("missing-good", "content_missing", "RailwayRule.RailGood")]
    [DataRow("track-limit", "invalid_state", "Edge.Tracks")]
    [DataRow("queued-limit", "invalid_state", "Edge.Tracks")]
    [DataRow("missing-source", "object_missing", "MaterialSource")]
    [DataRow("not-owned", "not_owned", "MaterialSource")]
    [DataRow("shortage", "insufficient_goods", "MaterialSource.Inventory")]
    [DataRow("exhausted", "identity_exhausted", "World.Next.Job")]
    public void Expand_returns_the_first_of_two_problems(string scenario, string code, string field)
    {
        var (session, command, subject) = ExpandCase(scenario);
        using (session)
            AssertProblem(Rejected(session, command), code, field, subject);
    }

    [TestMethod]
    [DataRow("missing-edge", "object_missing", "Edge")]
    [DataRow("missing-break", "object_missing", "Break")]
    [DataRow("busy", "busy", "Break")]
    [DataRow("missing-rule", "content_missing", "RailwayRule")]
    [DataRow("missing-good", "content_missing", "RailwayRule.RailGood")]
    [DataRow("missing-source", "object_missing", "MaterialSource")]
    [DataRow("not-owned", "not_owned", "MaterialSource")]
    [DataRow("shortage", "insufficient_goods", "MaterialSource.Inventory")]
    [DataRow("exhausted", "identity_exhausted", "World.Next.Job")]
    public void Repair_returns_the_first_of_two_problems(string scenario, string code, string field)
    {
        var (session, command, subject) = RepairCase(scenario);
        using (session)
            AssertProblem(Rejected(session, command), code, field, subject);
    }

    [TestMethod]
    [DataRow("missing-edge", "object_missing", "Edge")]
    [DataRow("off-missing-edge", "object_missing", "Edge")]
    [DataRow("missing-source", "object_missing", "MaterialSource")]
    [DataRow("not-owned", "not_owned", "MaterialSource")]
    [DataRow("exhausted", "identity_exhausted", "World.Next.Job")]
    public void Set_automatic_repair_returns_the_first_of_two_problems(string scenario, string code, string field)
    {
        var (session, command, subject) = AutomaticCase(scenario);
        using (session)
            AssertProblem(Rejected(session, command), code, field, subject);
    }

    [TestMethod]
    public void Missing_railway_projection_rejects_before_the_rail_good_check()
    {
        var state = Prepared(1, 20, 1, 1, 1, 1);
        using var noRule = new CoreSession(state with { Content = state.Content with { RailwayRules = [] } });
        var rule = Rejected(noRule, new CoreCommand.ExpandRail(new(1), Player(state).Id));
        Assert.AreEqual(("content_missing", "RailwayRule"), (rule.Code, rule.Field));
        var goods = state.Content.Goods.Where(good => good.Good != new GoodId.Voxel(WorldFixture.Material)).ToImmutableArray();
        using var noGood = new CoreSession(state with { Content = state.Content with { Goods = goods } });
        var good = Rejected(noGood, new CoreCommand.ExpandRail(new(1), Player(state).Id));
        Assert.AreEqual(("content_missing", "RailwayRule.RailGood"), (good.Code, good.Field));
    }

    [TestMethod]
    public void Railway_jobs_round_trip_and_strict_replay_keeps_the_receipts()
    {
        var state = Prepared(1, 1.5, 0.5, 0.025, 2, 2.0 / 60, 1);
        using var session = new CoreSession(state);
        var player = Player(state).Id;
        Accepted(session, new CoreCommand.ExpandRail(new(1), player));
        Accepted(session, new CoreCommand.SetAutomaticRailRepair(new(1), player));
        Accepted(session, new CoreCommand.DebugAddRailBreak(new(1), 1, 0.5));
        Rejected(session, new CoreCommand.RepairRail(new(1), ((CoreSession)session).State.World.Edges[0].Breaks.Single().Id, player));
        Advance(session, 1);
        var capture = WorldFixture.Success(session.Capture(CapturePurpose.Save));
        Assert.AreEqual(capture.Encode(), WorldFixture.Success(CoreFactory.Create().DecodeState(capture.Encode())).Encode());
        var json = JsonNode.Parse(capture.Encode())!;
        json["world"]!["edges"]![0]!["works"] = new JsonArray(JsonNode.Parse(
            """{"id":{"value":1},"kind":0,"track":1,"repairing_break":null,"material_source":{"value":1},"invested_goods":[],"progress":0,"blockers":[]}"""));
        var failure = (CoreResult<ICoreCapture>.Failure)CoreFactory.Create().DecodeState(json.ToJsonString());
        Assert.AreEqual(("invalid_state", "/world/edges/works"), (failure.Problems[0].Code, failure.Problems[0].Field));
        var window = session.ReadRecording();
        Assert.AreEqual(1UL, window.EndPosition.CompletedSteps);
        using var replay = WorldFixture.Success(CoreFactory.Create().PrepareReplay(new(2, WorldFixture.Options.Runtime,
            window.Starts[0].State.Encode(), window.Commands, window.Checkpoints, window.EndPosition, null),
            WorldFixture.Options.Runtime, [((CoreSession)session).State.Content]));
        var result = replay.Advance(uint.MaxValue);
        Assert.AreEqual(ReplayStop.EndOfRecording, result.Stop);
        Assert.IsNull(result.Difference);
        Assert.AreEqual(capture.Checkpoint.CanonicalText, replay.CaptureBoundary().Checkpoint.CanonicalText);
    }

    [TestMethod]
    [DataRow("unordered")]
    [DataRow("break-missing")]
    [DataRow("duplicate-break")]
    [DataRow("add-blockers")]
    [DataRow("elapsed-done")]
    [DataRow("pending-invested")]
    [DataRow("not-player")]
    [DataRow("add-over-limit")]
    [DataRow("job-collision")]
    [DataRow("next-job")]
    [DataRow("elapsed-late")]
    [DataRow("auto-unordered")]
    [DataRow("auto-missing-edge")]
    [DataRow("pending-without-auto")]
    [DataRow("pending-other-source")]
    public void Damaged_railway_state_is_rejected(string damage)
    {
        var (json, field) = Damage(damage);
        var decoded = CoreFactory.Create().DecodeState(json.ToJsonString());
        var problem = ((CoreResult<ICoreCapture>.Failure)decoded).Problems[0];
        Assert.AreEqual(("invalid_state", field), (problem.Code, problem.Field));
    }

    [TestMethod]
    public void Zero_railway_command_identities_are_rejected_before_a_command_number()
    {
        using var session = new CoreSession(Prepared(1, 20, 1, 1, 1, 1, 0));
        Assert.ThrowsExactly<ArgumentException>(() => session.Submit(new CoreCommand.ExpandRail(new(0), new(1)), CommandSource.AutomationTest));
        Assert.ThrowsExactly<ArgumentException>(() => session.Submit(new CoreCommand.RepairRail(new(1), new(0), new(1)), CommandSource.AutomationTest));
        Assert.ThrowsExactly<ArgumentException>(() => session.Submit(new CoreCommand.SetAutomaticRailRepair(new(1), new(0)), CommandSource.AutomationTest));
        Assert.AreEqual(1UL, ((CoreSession)session).State.NextCommandNumber);
    }

    private static (CoreSession Session, CoreCommand Command, WorldObjectId? Subject) ExpandCase(string scenario)
    {
        var state = Prepared(scenario is "track-limit" or "missing-good" ? 2 : scenario == "queued-limit" ? 1 : 0, 2, 0.5, 1, 1, 1,
            scenario is "shortage" or "not-owned" ? 0 : 5);
        var player = Player(state);
        if (scenario is "missing-rule") state = state with { Content = state.Content with { RailwayRules = [], Goods = Without(state).Content.Goods } };
        if (scenario is "missing-good") state = Without(state);
        if (scenario is "missing-source" or "shortage" or "exhausted")
            state = state with { World = state.World with { Next = state.World.Next with { Job = ulong.MaxValue } } };
        if (scenario is "not-owned")
            state = state with { World = state.World with { Settlements = state.World.Settlements.Select(item =>
                item.Id == player.Id ? item with { Relation = SettlementRelation.Hostile } : item).ToImmutableArray() } };
        if (scenario == "queued-limit")
        {
            using var started = new CoreSession(state);
            Accepted(started, new CoreCommand.ExpandRail(new(1), player.Id));
            var hostile = player with { Id = new(80), Relation = SettlementRelation.Hostile };
            var world = ((CoreSession)started).State.World with { Settlements = ((CoreSession)started).State.World.Settlements.Add(hostile) };
            return (new CoreSession(((CoreSession)started).State with { World = world }),
                new CoreCommand.ExpandRail(new(1), hostile.Id), new WorldObjectId.Edge(new(1)));
        }
        var source = scenario is "missing-edge" or "missing-source" or "track-limit" ? new SettlementId(999) : player.Id;
        var edge = scenario == "missing-edge" ? new EdgeId(9) : new EdgeId(1);
        WorldObjectId? subject = scenario switch
        {
            "missing-source" or "not-owned" or "shortage" => new WorldObjectId.Settlement(source),
            "exhausted" => null,
            _ => new WorldObjectId.Edge(edge)
        };
        return (new CoreSession(state), new CoreCommand.ExpandRail(edge, source), subject);
    }

    private static (CoreSession Session, CoreCommand Command, WorldObjectId? Subject) RepairCase(string scenario)
    {
        if (scenario == "busy")
        {
            var pending = Pending();
            var owner = Player(pending);
            var broken = pending.World.Edges[0].Breaks.Single().Id;
            var funded = pending with { World = Stock(pending.World, owner.Id, 5) with { Next = pending.World.Next with { Job = ulong.MaxValue } } };
            return (new CoreSession(funded), new CoreCommand.RepairRail(new(1), broken, owner.Id), new WorldObjectId.Edge(new(1)));
        }
        var state = Prepared(1, 20, 1, 1, 1, 1, scenario is "shortage" or "not-owned" ? 0 : 5);
        state = WithBreak(state);
        var player = Player(state);
        if (scenario is "missing-rule" or "missing-break")
            state = state with { Content = state.Content with { RailwayRules = [] } };
        if (scenario is "missing-rule" or "missing-good") state = Without(state);
        if (scenario is "missing-source" or "shortage" or "exhausted")
            state = state with { World = state.World with { Next = state.World.Next with { Job = ulong.MaxValue } } };
        if (scenario is "not-owned" or "missing-good")
            state = state with { World = state.World with { Settlements = state.World.Settlements.Select(item =>
                item.Id == player.Id ? item with { Relation = SettlementRelation.Hostile } : item).ToImmutableArray() } };
        var source = scenario is "missing-edge" or "missing-source" ? new SettlementId(999) : player.Id;
        var edge = scenario == "missing-edge" ? new EdgeId(9) : new EdgeId(1);
        var brokenId = scenario == "missing-break" ? new BreakId(9) : new BreakId(1);
        WorldObjectId? subject = scenario switch
        {
            "missing-source" or "not-owned" or "shortage" => new WorldObjectId.Settlement(source),
            "exhausted" => null,
            _ => new WorldObjectId.Edge(edge)
        };
        return (new CoreSession(state), new CoreCommand.RepairRail(edge, brokenId, source), subject);
    }

    private static (CoreSession Session, CoreCommand Command, WorldObjectId? Subject) AutomaticCase(string scenario)
    {
        var state = WithBreak(Prepared(2, 20, 1, 1, 1, 1, 0));
        var edge = state.World.Edges[0];
        state = state with { World = state.World with { Edges = [edge with { Breaks = edge.Breaks.Add(new(new(2), 2, 2)) }] } };
        var player = Player(state);
        if (scenario is "missing-source" or "exhausted" or "off-missing-edge")
            state = state with { Content = state.Content with { RailwayRules = [] } };
        if (scenario is "not-owned")
            state = state with { World = state.World with { Settlements = state.World.Settlements.Select(item =>
                item.Id == player.Id ? item with { Relation = SettlementRelation.Hostile } : item).ToImmutableArray() } };
        if (scenario is "not-owned" or "exhausted")
            state = state with { World = state.World with { Next = state.World.Next with { Job = ulong.MaxValue - 1 } } };
        SettlementId? source = scenario switch
        {
            "off-missing-edge" => null,
            "missing-edge" or "missing-source" => new SettlementId(999),
            _ => player.Id
        };
        var target = scenario is "missing-edge" or "off-missing-edge" ? new EdgeId(9) : new EdgeId(1);
        WorldObjectId? subject = scenario switch
        {
            "missing-source" or "not-owned" => new WorldObjectId.Settlement(source!.Value),
            "exhausted" => null,
            _ => new WorldObjectId.Edge(target)
        };
        return (new CoreSession(state), new CoreCommand.SetAutomaticRailRepair(target, source), subject);
    }

    private static (JsonNode Json, string Field) Damage(string damage) => damage switch
    {
        "unordered" => UnorderedJobs(),
        "break-missing" => BreakMissing(),
        "duplicate-break" => DuplicateBreak(),
        "add-blockers" => AddBlockers(),
        "elapsed-done" => ElapsedDone(),
        "pending-invested" => PendingInvested(),
        "not-player" => NotPlayer(),
        "add-over-limit" => AddOverLimit(),
        "job-collision" => CollidingJob(),
        "next-job" => NextJob(),
        "elapsed-late" => ElapsedLate(),
        "auto-unordered" => UnorderedAutomatic(),
        "auto-missing-edge" => AutomaticMissingEdge(),
        "pending-without-auto" => PendingWithoutAutomatic(clear: true),
        "pending-other-source" => PendingWithoutAutomatic(clear: false),
        _ => throw new ArgumentOutOfRangeException(nameof(damage))
    };

    private static (JsonNode Json, string Field) UnorderedJobs()
    {
        var state = Prepared(0, 2, 0.5, 2.0 / 60, 1, 1, 5);
        using var session = new CoreSession(state);
        Accepted(session, new CoreCommand.ExpandRail(new(1), Player(state).Id));
        Accepted(session, new CoreCommand.ExpandRail(new(1), Player(state).Id));
        var json = Capture(session);
        var jobs = json["world"]!["railway_jobs"]!.AsArray();
        var first = jobs[0]!;
        jobs.RemoveAt(0);
        jobs.Add(first);
        return (json, "/world/railway_jobs");
    }

    private static (JsonNode Json, string Field) BreakMissing()
    {
        var (json, id) = StartedRepair();
        json["world"]!["railway_jobs"]![0]!["break"]!["value"] = 99;
        return (json, $"/world/railway_jobs/{id}/break");
    }

    private static (JsonNode Json, string Field) DuplicateBreak()
    {
        var state = Prepared(2, 20, 1, 1, 1, 2.0 / 60, 5);
        using var session = new CoreSession(WithBreaks(state, 2));
        var player = Player(state).Id;
        Accepted(session, new CoreCommand.RepairRail(new(1), new(1), player));
        Accepted(session, new CoreCommand.RepairRail(new(1), new(2), player));
        var json = Capture(session);
        var jobs = json["world"]!["railway_jobs"]!.AsArray();
        var second = jobs[1]!["id"]!["value"]!.GetValue<ulong>();
        jobs[1]!["break"]!["value"] = jobs[0]!["break"]!["value"]!.GetValue<ulong>();
        return (json, $"/world/railway_jobs/{second}/break");
    }

    private static (JsonNode Json, string Field) AddBlockers()
    {
        var (json, id) = StartedExpansion();
        json["world"]!["railway_jobs"]![0]!["blockers"] = JsonNode.Parse(
            """[{"code":"insufficient_goods","field":"MaterialSource.Inventory","subject":null,"related_identities":["voxel:fixture:material"]}]""");
        return (json, $"/world/railway_jobs/{id}/break");
    }

    private static (JsonNode Json, string Field) ElapsedDone()
    {
        var (json, id) = StartedRepair();
        var job = json["world"]!["railway_jobs"]![0]!;
        job["elapsed_ticks"] = job["frozen_duration_ticks"]!.GetValue<ulong>();
        return (json, $"/world/railway_jobs/{id}/elapsed_ticks");
    }

    private static (JsonNode Json, string Field) PendingInvested()
    {
        var (json, id) = PendingCapture();
        var good = JsonNode.Parse("""{"good":{"$kind":"voxel","definition":{"value":"fixture:material"}},"quantity":1}""")!;
        json["world"]!["railway_jobs"]![0]!["invested_goods"] = new JsonArray(good);
        return (json, $"/world/railway_jobs/{id}/blockers");
    }

    private static (JsonNode Json, string Field) NotPlayer()
    {
        var (json, id) = StartedRepair();
        var source = json["world"]!["railway_jobs"]![0]!["material_source"]!["value"]!.GetValue<ulong>();
        foreach (var settlement in json["world"]!["settlements"]!.AsArray())
            if (settlement!["id"]!["value"]!.GetValue<ulong>() == source) settlement["relation"] = 2;
        foreach (var node in json["world"]!["nodes"]!.AsArray())
        {
            var occupant = node!["occupant"]!;
            if (occupant["$kind"]?.GetValue<string>() == "settlement" && occupant["id"]!["value"]!.GetValue<ulong>() == source)
                occupant["relation"] = 2;
        }
        var buildings = new HashSet<ulong>();
        foreach (var settlement in json["world"]!["settlements"]!.AsArray())
        {
            if (settlement!["id"]!["value"]!.GetValue<ulong>() != source) continue;
            foreach (var plot in settlement["plots"]!.AsArray())
                if (plot!["building"]?["value"] is JsonNode building) buildings.Add(building.GetValue<ulong>());
        }
        foreach (var instance in json["world"]!["instances"]!.AsArray())
        {
            var identity = instance!["id"]!;
            if (identity["$kind"]?.GetValue<string>() == "building"
                && buildings.Contains(identity["id"]!["value"]!.GetValue<ulong>()))
                instance["ownership"] = 2;
        }
        return (json, $"/world/railway_jobs/{id}/material_source");
    }

    private static (JsonNode Json, string Field) AddOverLimit()
    {
        var (json, _) = StartedExpansion();
        var edge = json["world"]!["edges"]![0]!;
        edge["tracks"] = edge["track_limit"]!.GetValue<int>();
        return (json, "/world/railway_jobs/edge");
    }

    private static (JsonNode Json, string Field) CollidingJob()
    {
        var material = new GoodId.Voxel(WorldFixture.Material);
        var state = Prepared(0, 2, 0.5, 1.0 / 60, 1, 1, 100_000);
        var designs = state.Content.Designs.Select(design => design.Design == WorldFixture.Station
            ? design with
            {
                Parts = design.Parts.Select(part => part with
                {
                    Capabilities = [..part.Capabilities, new PartCapability.Construction(60, PlotSize.Small, SizeClass.Small)]
                }).ToImmutableArray()
            }
            : design with { BillOfMaterials = [new(material, 2)], BillOfMaterialsItemCount = 2, StructuralGoods = [new(material, 2)] })
            .ToImmutableArray();
        state = state with
        {
            Content = state.Content with { Designs = designs },
            World = state.World with { Designs = designs }
        };
        using var session = new CoreSession(state);
        var settlement = Player(state);
        var building = settlement.Plots.Single(plot => plot.Building is not null).Building!.Value;
        var started = ConstructionFixture.Start(session, new CoreCommand.StartOwnedJob(new JobRequest.BuildVehicle(
            new(building, CapabilityKind.Construction), ConstructionFixture.Design(session.State.Content), [settlement.Inventory], 0)));
        Accepted(session, new CoreCommand.ExpandRail(new(1), settlement.Id));
        var json = Capture(session);
        json["world"]!["railway_jobs"]![0]!["id"]!["value"] = started.Id.Value;
        return (json, "/world/railway_jobs/" + started.Id.Value);
    }

    private static (JsonNode Json, string Field) NextJob()
    {
        var (json, id) = StartedRepair();
        json["world"]!["next"]!["job"] = id;
        return (json, "/world/next/job");
    }

    private static (JsonNode Json, string Field) ElapsedLate()
    {
        var (json, _) = StartedRepair();
        json["world"]!["railway_jobs"]![0]!["elapsed_ticks"] = 1;
        return (json, "/world/railway_jobs/elapsed_ticks");
    }

    private static (JsonNode Json, string Field) UnorderedAutomatic()
    {
        var json = TwoAutomatic();
        var repairs = json["world"]!["automatic_rail_repairs"]!.AsArray();
        var first = repairs[0]!;
        repairs.RemoveAt(0);
        repairs.Add(first);
        return (json, "/world/automatic_rail_repairs");
    }

    private static (JsonNode Json, string Field) AutomaticMissingEdge()
    {
        var json = TwoAutomatic();
        var repairs = json["world"]!["automatic_rail_repairs"]!.AsArray();
        repairs[^1]!["edge"]!["value"] = 99;
        return (json, "/world/automatic_rail_repairs/edge");
    }

    private static (JsonNode Json, string Field) PendingWithoutAutomatic(bool clear)
    {
        var (json, id) = PendingCapture();
        if (clear) json["world"]!["automatic_rail_repairs"] = new JsonArray();
        else json["world"]!["automatic_rail_repairs"]![0]!["material_source"]!["value"] = 999;
        return (json, $"/world/railway_jobs/{id}/material_source");
    }

    private static (JsonNode Json, ulong Id) StartedRepair()
    {
        var state = Prepared(1, 20, 1, 1, 1, 2.0 / 60, 5);
        using var session = new CoreSession(WithBreak(state));
        var result = Accepted(session, new CoreCommand.RepairRail(new(1), new(1), Player(state).Id));
        return (Capture(session), Created(result).Value);
    }

    private static (JsonNode Json, ulong Id) StartedExpansion()
    {
        var state = Prepared(0, 2, 0.5, 2.0 / 60, 1, 1, 5);
        using var session = new CoreSession(state);
        var result = Accepted(session, new CoreCommand.ExpandRail(new(1), Player(state).Id));
        return (Capture(session), Created(result).Value);
    }

    private static (JsonNode Json, ulong Id) PendingCapture()
    {
        var state = Pending();
        using var session = new CoreSession(state);
        return (Capture(session), state.World.RailwayJobs.Single().Id.Value);
    }

    private static JsonNode TwoAutomatic()
    {
        var state = Prepared(1, 20, 1, 1, 1, 1, 0);
        var world = state.World;
        var node = world.Nodes[0] with { Id = new(3), Position = new(0, 10), Occupant = new NodeOccupant.Empty(),
            OccupiedPlatforms = 0, Platforms = 0, FoundingJob = null };
        var edge = world.Edges[0] with { Id = new(2), Start = new(1), End = new(3), LengthMeters = 10, Breaks = [] };
        world = world with
        {
            Nodes = world.Nodes.Add(node), Edges = world.Edges.Add(edge), Next = world.Next with { Node = 4, Edge = 3 }
        };
        using var session = new CoreSession(state with { World = world });
        var player = Player(state).Id;
        Accepted(session, new CoreCommand.SetAutomaticRailRepair(new(1), player));
        Accepted(session, new CoreCommand.SetAutomaticRailRepair(new(2), player));
        return Capture(session);
    }

    private static CoreState Pending()
    {
        var state = Prepared(1, 20, 1, 1, 2, 2.0 / 60, 0);
        using var session = new CoreSession(state);
        var player = Player(state).Id;
        Accepted(session, new CoreCommand.SetAutomaticRailRepair(new(1), player));
        Accepted(session, new CoreCommand.DebugAddRailBreak(new(1), 1, 1));
        return ((CoreSession)session).State;
    }

    private static CoreState WithBreak(CoreState state) => WithBreaks(state, 1);

    private static CoreState WithBreaks(CoreState state, int count)
    {
        ImmutableArray<TrackBreakView> breaks = [..Enumerable.Range(1, count).Select(id => new TrackBreakView(new((ulong)id), id, 1))];
        return state with { World = state.World with
        {
            Edges = state.World.Edges.Select(edge => edge.Id != new EdgeId(1) ? edge : edge with { Breaks = breaks }).ToImmutableArray(),
            Next = state.World.Next with { Break = (ulong)count + 1 }
        } };
    }

    private static CoreState Without(CoreState state)
    {
        var material = new GoodId.Voxel(WorldFixture.Material);
        return state with { Content = state.Content with { Goods = state.Content.Goods.Where(good => good.Good != material).ToImmutableArray() } };
    }

    private static JsonNode Capture(ICoreSession session)
        => JsonNode.Parse(WorldFixture.Success(session.Capture(CapturePurpose.Save)).Encode())!;

    private static RailwayWorkView Work(ICoreSession session, JobId id)
        => ((JobView.Railway)((Lookup<JobView>.Found)session.Read().FindJob(id)).Value).Value;

    private static JobId Created(SubmissionResult.Completed result)
        => ((WorldObjectId.Job)((CommandDecision.Accepted)result.Receipt.Decision).Created.Single()).Id;

    private static void AssertProblem(CoreProblem problem, string code, string field, WorldObjectId? subject)
        => Assert.AreEqual((code, field, subject), (problem.Code, problem.Field, problem.Subject));

    private static SubmissionResult.Completed Accepted(ICoreSession session, CoreCommand command)
        => Submit(session, command);

    private static CoreProblem Rejected(ICoreSession session, CoreCommand command)
    {
        var result = (SubmissionResult.Completed)session.Submit(command, CommandSource.AutomationTest);
        Assert.IsNull(result.Diagnostic);
        return ((CommandDecision.Rejected)result.Receipt.Decision).Problems.Single();
    }
}

[TestClass]
public sealed class RailwayCommandEncodingTests
{
    [TestMethod]
    public void Railway_commands_keep_a_fixed_format_2_vector()
    {
        AssertRoundTrip("""{"input_format_version":2,"kind":"rail.expand","edge":18446744073709551615,"material_source":2}""",
            new CoreCommand.ExpandRail(new(ulong.MaxValue), new(2)));
        AssertRoundTrip("""{"input_format_version":2,"kind":"rail.repair","edge":3,"break":4,"material_source":5}""",
            new CoreCommand.RepairRail(new(3), new(4), new(5)));
        AssertRoundTrip("""{"input_format_version":2,"kind":"rail.set_automatic_repair","edge":6,"material_source":null}""",
            new CoreCommand.SetAutomaticRailRepair(new(6), null));
        AssertRoundTrip("""{"input_format_version":2,"kind":"rail.set_automatic_repair","edge":6,"material_source":7}""",
            new CoreCommand.SetAutomaticRailRepair(new(6), new(7)));
    }

    [TestMethod]
    [DataRow("rail.expand", "\"edge\":1", "/material_source")]
    [DataRow("rail.repair", "\"edge\":1,\"material_source\":2", "/break")]
    [DataRow("rail.set_automatic_repair", "\"edge\":1,\"material_source\":0", "/material_source")]
    [DataRow("rail.expand", "\"edge\":1,\"material_source\":2,\"extra\":1", "/extra")]
    public void Railway_payload_fields_are_strict(string kind, string fields, string field)
    {
        var failure = (CoreResult<DecodedCommand>.Failure)CoreCommandEncoding.Decode(
            "{\"input_format_version\":2,\"kind\":\"" + kind + "\"," + fields + "}");
        Assert.AreEqual("invalid_state", failure.Problems.Single().Code);
        Assert.AreEqual(field, failure.Problems.Single().Field);
    }

    private static void AssertRoundTrip(string payload, CoreCommand command)
    {
        Assert.AreEqual(payload, CoreCommandEncoding.Encode(command));
        var decoded = (CoreResult<DecodedCommand>.Success)CoreCommandEncoding.Decode(payload);
        Assert.AreEqual(command, decoded.Value.Command);
    }
}
