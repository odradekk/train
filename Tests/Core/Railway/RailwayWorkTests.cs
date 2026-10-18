using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.Railway;
using Train.Core.Sessions;
using Train.Core.Tests.World;
using Train.Core.World;
using static Train.Core.Tests.Railway.TrainDispatchFixture;

namespace Train.Core.Tests.Railway;

[TestClass]
public sealed class RailwayWorkTests
{
    [TestMethod]
    public void Expansion_rounds_the_product_once_and_keeps_stored_works_empty()
    {
        var state = Prepared(0, 1.5, 0.5, 0.025, 18, 1);
        using var session = new CoreSession(state);
        var started = Accepted(session, new CoreCommand.ExpandRail(new(1), Player(state).Id));
        var work = started.Events.Select(item => item.Event).OfType<CoreEvent.RailwayWorkStarted>().Single().Work;
        Assert.AreEqual(1, work.Track);
        Assert.AreEqual((long)Math.Ceiling(1.5 * 0.5), work.InvestedGoods.Single().Quantity);
        Assert.AreEqual(0L, (long)Math.Truncate(1.5 * 0.5));
        Assert.AreEqual(checked((ulong)Math.Ceiling(1.5 * 0.025 * SimulationTime.StepsPerSecond)), Duration(session));
        Assert.AreEqual(0, ((CoreSession)session).State.World.Edges.Single().Tracks);
        Assert.IsTrue(((CoreSession)session).State.World.Edges.Single().Works.IsEmpty);
        Assert.AreEqual(work.Track, ((Lookup<EdgeView>.Found)session.Read().FindEdge(new(1))).Value.Map.Works.Single().Track);
        var finished = Advance(session, Duration(session));
        Assert.AreEqual(1, finished.Events.Select(item => item.Event).OfType<CoreEvent.RailExpanded>().Single().Tracks);
        Assert.AreEqual(1, session.Read().World.Edges.Single(edge => edge.Id == new EdgeId(1)).Tracks);
        Assert.IsInstanceOfType<Lookup<JobView>.Missing>(session.Read().FindJob(work.Id));
    }

    [TestMethod]
    public void A_zero_track_edge_releases_a_no_route_train_after_expansion()
    {
        var state = Prepared(0, 1, 1, 1.0 / 60, 1, 1);
        using var session = new CoreSession(state);
        Accepted(session, new CoreCommand.StartTrain(new(1)));
        Assert.AreEqual(TrainActivity.NoRoute, View(session, 1).Activity);
        Accepted(session, new CoreCommand.ExpandRail(new(1), Player(state).Id));
        Advance(session, Duration(session));
        Assert.AreEqual(TrainActivity.NoRoute, View(session, 1).Activity);
        Until(session, () => View(session, 1).Position is TrainPosition.Edge { Track: 1 });
    }

    [TestMethod]
    public void Later_add_track_completion_moves_the_earlier_jobs_displayed_number_forward()
    {
        var state = Prepared(0, 1, 1, 1, 1, 1);
        using var session = new CoreSession(state);
        Accepted(session, new CoreCommand.ExpandRail(new(1), Player(state).Id));
        Accepted(session, new CoreCommand.ExpandRail(new(1), Player(state).Id));
        var jobs = ((CoreSession)session).State.World.RailwayJobs;
        Assert.AreEqual(1, RailwayWorks.Read(((CoreSession)session).State.World, jobs[0]).Track);
        Assert.AreEqual(2, RailwayWorks.Read(((CoreSession)session).State.World, jobs[1]).Track);
        var faster = ((CoreSession)session).State with
        {
            World = ((CoreSession)session).State.World with { RailwayJobs = [jobs[0], jobs[1] with { FrozenDurationTicks = 1 }] }
        };
        using var shortened = new CoreSession(faster);
        var finished = Advance(shortened, 1);
        Assert.AreEqual(1, finished.Events.Select(item => item.Event).OfType<CoreEvent.RailExpanded>().Single().Tracks);
        Assert.AreEqual(2, ((JobView.Railway)((Lookup<JobView>.Found)shortened.Read().FindJob(jobs[0].Id)).Value).Value.Track);
    }

    [TestMethod]
    public void Manual_repair_clears_the_break_and_return_mark_without_reversing_direction()
    {
        var state = Prepared(1, 20, 1, 1, 1, 1.0 / 60);
        var broken = new TrackBreakView(new(1), 1, 10);
        var world = Stock(state.World with
        {
            Edges = [state.World.Edges[0] with { Breaks = [broken] }],
            Trains = [state.World.Trains[0] with
            {
                Activity = TrainActivity.Running,
                Position = new TrainPosition.Edge(new(1), 1, TravelDirection.Forward, 4),
                ReturningFromBreak = broken.Id
            }],
            Next = state.World.Next with { Break = 2 }
        }, Player(state).Id, 5);
        using var session = new CoreSession(state with { World = world });
        Assert.IsInstanceOfType<CoreEvent.RailwayWorkStarted>(Accepted(session,
            new CoreCommand.RepairRail(new(1), broken.Id, Player(state).Id)).Events.Select(item => item.Event).Single());
        if (Duration(session) > 1) Advance(session, Duration(session) - 1);
        Assert.AreEqual(TravelDirection.Forward, ((TrainPosition.Edge)((CoreSession)session).State.World.Trains[0].Position).Direction);
        Assert.AreEqual(broken.Id, ((CoreSession)session).State.World.Trains[0].ReturningFromBreak);
        var finished = Advance(session, 1);
        var train = ((CoreSession)session).State.World.Trains[0];
        Assert.IsNull(train.ReturningFromBreak);
        Assert.AreEqual(TravelDirection.Forward, ((TrainPosition.Edge)train.Position).Direction);
        Assert.AreEqual(0, ((CoreSession)session).State.World.Edges[0].Breaks.Length);
        Assert.AreEqual(broken.Id, finished.Events.Select(item => item.Event).OfType<CoreEvent.RailRepaired>().Single().Break);
    }

    [TestMethod]
    public void Repair_reopens_the_only_track_for_a_following_departure()
    {
        var state = Prepared(1, 1, 1, 1, 1, 1.0 / 60);
        var world = state.World with
        {
            Edges = [state.World.Edges[0] with { Breaks = [new(new(1), 1, 0.5)] }],
            Next = state.World.Next with { Break = 2 }
        };
        using var session = new CoreSession(state with { World = world });
        Accepted(session, new CoreCommand.StartTrain(new(1)));
        Assert.AreEqual(TrainActivity.NoRoute, View(session, 1).Activity);
        Accepted(session, new CoreCommand.RepairRail(new(1), new(1), Player(state).Id));
        Advance(session, Duration(session));
        Assert.AreEqual(TrainActivity.NoRoute, View(session, 1).Activity);
        Until(session, () => View(session, 1).Position is TrainPosition.Edge { Track: 1 });
    }

    [TestMethod]
    public void Automatic_repair_holds_a_job_until_materials_arrive_and_only_repeats_when_blockers_change()
    {
        var state = Prepared(1, 20, 1, 1, 4, 1, 0);
        using var session = new CoreSession(state);
        var player = Player(state).Id;
        Assert.AreEqual(0, Accepted(session, new CoreCommand.SetAutomaticRailRepair(new(1), player)).Events.Length);
        Assert.AreEqual(player, ((Lookup<EdgeView>.Found)session.Read().FindEdge(new(1))).Value.AutoRepairMaterialSource);
        Assert.AreEqual(0, Accepted(session, new CoreCommand.SetAutomaticRailRepair(new(1), player)).Events.Length);
        var broken = Accepted(session, new CoreCommand.DebugAddRailBreak(new(1), 1, 5));
        var events = broken.Events.Select(item => item.Event).ToArray();
        Assert.IsInstanceOfType<CoreEvent.RailBroken>(events[0]);
        Assert.AreEqual("insufficient_goods", ((CoreEvent.RailwayWorkChanged)events[1]).Reasons.Single().Code);
        Assert.AreEqual(0, Advance(session, 1).Events.Count(item => item.Event is CoreEvent.RailwayWorkChanged));
        using var supplied = new CoreSession(((CoreSession)session).State with
            { World = Stock(((CoreSession)session).State.World, player, 4) });
        Assert.IsTrue(Advance(supplied, 1).Events.Any(item => item.Event is CoreEvent.RailwayWorkStarted));
        Assert.AreEqual(0, ((CoreSession)supplied).State.World.RailwayJobs.Single().Blockers.Length);
        Assert.AreEqual("busy", Rejected(supplied, new CoreCommand.RepairRail(new(1), new(1), player)).Code);
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
        var state = Prepared(1, 20, 1, 1, 2, 1, 0);
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
        var changed = Accepted(session, new CoreCommand.SetAutomaticRailRepair(new(1), other.Id));
        Assert.IsInstanceOfType<CoreEvent.RailwayWorkStarted>(changed.Events.Select(item => item.Event).OfType<CoreEvent.RailwayWorkStarted>().Single());
        Assert.AreEqual(other.Id, ((CoreSession)session).State.World.RailwayJobs.Single().MaterialSource);
    }

    [TestMethod]
    public void Manual_shortage_rejects_without_a_job_and_exhausted_auto_break_writes_nothing()
    {
        var state = Prepared(1, 20, 1, 1, 3, 1, 0);
        using var session = new CoreSession(state);
        var shortage = Rejected(session, new CoreCommand.ExpandRail(new(1), Player(state).Id));
        Assert.AreEqual(("insufficient_goods", "MaterialSource.Inventory"), (shortage.Code, shortage.Field));
        Assert.AreEqual(0, ((CoreSession)session).State.World.RailwayJobs.Length);
        var missing = Rejected(session, new CoreCommand.RepairRail(new(1), new(9), Player(state).Id));
        Assert.AreEqual(("object_missing", "Break"), (missing.Code, missing.Field));
        var foreign = ((CoreSession)session).State.World with
        {
            Settlements = ((CoreSession)session).State.World.Settlements.Select(settlement =>
                settlement.Id == Player(state).Id ? settlement with { Relation = SettlementRelation.Hostile } : settlement).ToImmutableArray()
        };
        using var hostile = new CoreSession(((CoreSession)session).State with { World = foreign });
        Assert.AreEqual("not_owned", Rejected(hostile, new CoreCommand.ExpandRail(new(1), Player(state).Id)).Code);
        using var full = new CoreSession(Prepared(2, 20, 1, 1, 1, 1));
        var limit = Rejected(full, new CoreCommand.ExpandRail(new(1), Player(((CoreSession)full).State).Id));
        Assert.AreEqual(("invalid_state", "Edge.Tracks"), (limit.Code, limit.Field));
        Accepted(session, new CoreCommand.SetAutomaticRailRepair(new(1), Player(state).Id));
        var exhaustedWorld = ((CoreSession)session).State.World with { Next = ((CoreSession)session).State.World.Next with { Job = ulong.MaxValue } };
        using var exhausted = new CoreSession(((CoreSession)session).State with { World = exhaustedWorld });
        var refusal = Rejected(exhausted, new CoreCommand.DebugAddRailBreak(new(1), 1, 2));
        Assert.AreEqual(("identity_exhausted", "World.Next.Job"), (refusal.Code, refusal.Field));
        Assert.AreEqual(0, ((CoreSession)exhausted).State.World.Edges[0].Breaks.Length);
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
        var state = Prepared(1, 20, 1, 1, 2, 1, 0);
        using var session = new CoreSession(state);
        Accepted(session, new CoreCommand.SetAutomaticRailRepair(new(1), Player(state).Id));
        Accepted(session, new CoreCommand.DebugAddRailBreak(new(1), 1, 4));
        Rejected(session, new CoreCommand.RepairRail(new(1), new(1), Player(state).Id));
        var capture = WorldFixture.Success(session.Capture(CapturePurpose.Save));
        Assert.AreEqual(capture.Encode(), WorldFixture.Success(CoreFactory.Create().DecodeState(capture.Encode())).Encode());
        var json = JsonNode.Parse(capture.Encode())!;
        json["world"]!["edges"]![0]!["works"] = new JsonArray(new JsonObject());
        Assert.AreEqual("invalid_state", ((CoreResult<ICoreCapture>.Failure)CoreFactory.Create().DecodeState(json.ToJsonString())).Problems[0].Code);
        var window = session.ReadRecording();
        using var replay = WorldFixture.Success(CoreFactory.Create().PrepareReplay(new(2, WorldFixture.Options.Runtime,
            window.Starts[0].State.Encode(), window.Commands, window.Checkpoints, window.EndPosition, null),
            WorldFixture.Options.Runtime, [((CoreSession)session).State.Content]));
        var result = replay.Advance(uint.MaxValue);
        Assert.AreEqual(ReplayStop.EndOfRecording, result.Stop);
        Assert.IsNull(result.Difference);
        Assert.AreEqual(capture.Checkpoint.CanonicalText, replay.CaptureBoundary().Checkpoint.CanonicalText);
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

    private static CoreState Prepared(int tracks, double length, double units, double buildSeconds,
        long repairUnits, double repairSeconds, long stock = 20)
    {
        var basis = State(1, tracks, length);
        var rule = basis.Content.RailwayRules[0] with
        {
            RailUnitsPerMeter = units, BuildSecondsPerMeter = buildSeconds,
            RepairRailUnitsPerBreak = repairUnits, RepairSecondsPerBreak = repairSeconds
        };
        var world = Claim(basis.World);
        var player = world.Settlements.Single(settlement => settlement.Relation == SettlementRelation.PlayerOwned);
        return basis with
        {
            Content = basis.Content with { RailwayRules = [rule] },
            World = stock > 0 ? Stock(world, player.Id, stock) : world
        };
    }

    private static WorldState Claim(WorldState world)
    {
        var settlement = world.Settlements.Single(item => item.Node == world.Nodes[0].Id);
        if (settlement.Relation == SettlementRelation.PlayerOwned) return world;
        var claimed = settlement with { Relation = SettlementRelation.PlayerOwned };
        return world with
        {
            Settlements = world.Settlements.Select(item => item.Id == claimed.Id ? claimed : item).ToImmutableArray(),
            Nodes = world.Nodes.Select(node => node.Id != claimed.Node ? node : node with
            {
                Occupant = new NodeOccupant.Settlement(claimed.Id, SettlementRelation.PlayerOwned)
            }).ToImmutableArray(),
            Instances = world.Instances.Select(instance => instance.Id is InstanceId.Building building
                && claimed.Plots.Any(plot => plot.Building == building.Id)
                ? instance with { Ownership = SettlementRelation.PlayerOwned } : instance).ToImmutableArray()
        };
    }

    private static SettlementState Player(CoreState state)
        => state.World.Settlements.Single(settlement => settlement.Relation == SettlementRelation.PlayerOwned);

    private static WorldState Stock(WorldState world, SettlementId settlement, long quantity)
    {
        var owner = world.Settlements.Single(item => item.Id == settlement);
        var good = new GoodId.Voxel(WorldFixture.Material);
        return world with
        {
            Inventories = world.Inventories.Select(inventory => inventory.Id != owner.Inventory ? inventory : inventory with
            {
                Goods = inventory.Goods.Where(stack => stack.Good != good).Append(new GoodsQuantity(good, quantity))
                    .OrderBy(stack => WorldReferences.GoodKey(stack.Good), StringComparer.Ordinal).ToImmutableArray()
            }).ToImmutableArray()
        };
    }

    private static ulong Duration(ICoreSession session)
        => ((CoreSession)session).State.World.RailwayJobs.Single().FrozenDurationTicks;

    private static AdvanceResult Advance(ICoreSession session, ulong steps)
    {
        AdvanceResult? last = null;
        for (ulong step = 0; step < steps; step++)
        {
            last = session.Advance(1);
            Assert.IsNull(last.Fault);
            Assert.AreEqual(1u, last.CompletedSteps);
        }
        return last ?? throw new AssertFailedException("没有推进。");
    }

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
