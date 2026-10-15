using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.Sessions;
using Train.Core.Tests.Inventory;
using Train.Core.Tests.World;
using Train.Core.Trading;
using static Train.Core.Tests.Inventory.InventoryFixture;

namespace Train.Core.Tests.Trading;

[TestClass]
public sealed class RunReportBoundaryTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Shrinking_node_geometry_reports_a_consumed_loop_on_publish_or_restore(bool restore)
    {
        using var original = InventoryFixture.Session();
        var state = original.State;
        var home = state.World.Nodes.Single(n => n.Id == state.World.Settlements.Single().Node) with { Position = new(0, 0) };
        var away = home with { Id = new(2), Position = new(1, 0), Occupant = new NodeOccupant.Empty(), Platforms = 0 };
        using var moving = new CoreSession(state with
        {
            Position = new(0, 1), NextCommandNumber = 2,
            World = state.World with
            {
                Nodes = [home, away],
                Edges = [new(new(1), home.Id, away.Id, 10, home.Terrain, 1, 2, [], [])],
                Next = state.World.Next with { Node = 3, Edge = 2 },
                Trains = [state.World.Trains.Single() with
                {
                    Position = WorldFixture.Node(away.Id, NodeTravelPhase.Approaching, WorldFixture.ApproachLength * .75,
                        nextEdge: new(1)),
                    Activity = TrainActivity.Running,
                    SpeedMetersPerSecond = .1,
                    Plan = new(true, [new PlanStop.Waypoint(home.Id), new PlanStop.Waypoint(away.Id)]),
                    NextStopIndex = 1,
                    CurrentRun = RunReports.Begin(new(0, 1))
                }]
            }
        });
        var changed = Changed(state.Content, 'F') with
        {
            RailwayRules = state.Content.RailwayRules.Select(r => r with
                { ApproachLengthMeters = WorldFixture.ApproachLength / 2 }).ToImmutableArray()
        };
        if (restore)
        {
            var saved = WorldFixture.Success(CoreFactory.Create().DecodeState(WorldFixture.Success(moving.Capture(CapturePurpose.Save)).Encode()));
            using var candidate = WorldFixture.Success(CoreFactory.Create().PrepareRestore(saved, changed, WorldFixture.Options));
            using var resumed = candidate.Activate();
            AssertLoop(resumed, saved.Position);
            var roundTrip = WorldFixture.Success(CoreFactory.Create().DecodeState(WorldFixture.Success(resumed.Capture(CapturePurpose.Save)).Encode()));
            using var second = WorldFixture.Success(CoreFactory.Create().PrepareRestore(roundTrip, changed, WorldFixture.Options));
            using var unchanged = second.Activate();
            AssertLoop(unchanged, saved.Position);
        }
        else
        {
            var published = Accept(moving, new CoreCommand.PublishContent(changed));
            var envelope = published.Events.Single(e => e.Event is CoreEvent.RunReported);
            var report = ((CoreEvent.RunReported)envelope.Event).Report;
            Assert.AreEqual(published.Receipt.After, report.EndedAt);
            Assert.AreEqual(envelope.Position, report.EndedAt);
            AssertLoop(moving, published.Receipt.After);
        }
    }

    [TestMethod]
    public void Completing_a_docked_plan_during_advance_reports_the_new_step_boundary()
    {
        using var original = InventoryFixture.Session();
        var state = original.State;
        var train = state.World.Trains.Single();
        using var session = new CoreSession(state with
        {
            Position = new(0, 1), NextCommandNumber = 2,
            World = state.World with { Trains = [train with
            {
                Activity = TrainActivity.Running,
                Plan = new(false, [new PlanStop.Station(((TrainPosition.Node)train.Position).Id, 0, [])]),
                NextStopIndex = 0,
                CurrentRun = RunReports.Begin(new(0, 1))
            }] }
        });
        var result = Step(session);
        var envelope = result.Events.Single(e => e.Event is CoreEvent.RunReported);
        var report = ((CoreEvent.RunReported)envelope.Event).Report;
        Assert.AreEqual(new SimulationPosition(1, 0), report.EndedAt);
        Assert.AreEqual(envelope.Position, report.EndedAt);
        Assert.AreEqual(RunEndReason.PlanCompleted, report.Reason);
        Assert.IsNull(((Lookup<TrainView>.Found)session.Read().FindTrain(train.Id)).Value.CurrentRun);
        WorldFixture.Success(CoreFactory.Create().DecodeState(WorldFixture.Success(session.Capture(CapturePurpose.Save)).Encode()));
    }

    private static void AssertLoop(ICoreSession session, SimulationPosition boundary)
    {
        var train = ((Lookup<TrainView>.Found)session.Read().FindTrain(new(1))).Value;
        var report = session.Read().Economy.RecentRunReports.Single();
        Assert.AreEqual(RunEndReason.LoopCompleted, report.Reason);
        Assert.AreEqual(new SimulationPosition(0, 1), report.Totals.StartedAt);
        Assert.AreEqual(boundary, report.EndedAt);
        Assert.AreEqual(boundary, train.CurrentRun!.StartedAt);
        Assert.AreEqual(0, train.NextStopIndex);
    }
}
