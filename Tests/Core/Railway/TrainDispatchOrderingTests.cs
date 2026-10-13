using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.Sessions;
using Train.Core.World;
using static Train.Core.Tests.Railway.TrainDispatchFixture;

namespace Train.Core.Tests.Railway;

[TestClass]
public sealed class TrainDispatchOrderingTests
{
    [TestMethod]
    public void Earlier_same_direction_waiter_enters_before_a_smaller_train_id()
    {
        var state = State(length: 1) with { Position = new(5, 0) };
        var trains = state.World.Trains;
        using var session = new CoreSession(state with { World = state.World with { Trains =
            [OnEdge(trains[0], 1, 0.99, 2), Queued(trains[1], 1, 3, 2), Queued(trains[2], 1, 1, 2)] } });
        Step(session);
        Assert.IsInstanceOfType<TrainPosition.Node>(View(session, 1).Position);
        Assert.AreEqual(TrainActivity.Waiting, View(session, 2).Activity);
        Assert.AreEqual(TrainActivity.Waiting, View(session, 3).Activity);
        Step(session);
        Assert.IsInstanceOfType<TrainPosition.Edge>(View(session, 3).Position);
        Assert.AreEqual(new TrainPosition.Node(new(1)), View(session, 2).Position);
        Assert.AreEqual(TrainActivity.Waiting, View(session, 2).Activity);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Cleared_track_goes_to_opposite_waiter_whether_the_other_old_direction_track_stays_occupied_or_clears(bool otherClears)
    {
        var state = State(4, 2) with { Position = new(5, 0) };
        var trains = state.World.Trains;
        using var session = new CoreSession(state with { World = state.World with { Trains =
            [OnEdge(trains[0], 1, 19.99, 2), OnEdge(trains[1], 2, otherClears ? 19.99 : 10, otherClears ? 2 : 1),
                Queued(trains[2], 1, 1, 2), Queued(trains[3], 2, 2, 1)] } });
        Step(session);
        Assert.IsInstanceOfType<TrainPosition.Node>(View(session, 1).Position);
        if (otherClears) Assert.IsInstanceOfType<TrainPosition.Node>(View(session, 2).Position);
        else Assert.IsInstanceOfType<TrainPosition.Edge>(View(session, 2).Position);
        Assert.AreEqual(TrainActivity.Waiting, View(session, 3).Activity);
        Assert.AreEqual(TrainActivity.Waiting, View(session, 4).Activity);
        Step(session);
        var admitted = (TrainPosition.Edge)View(session, 4).Position;
        Assert.AreEqual(1, admitted.Track);
        Assert.AreEqual(TravelDirection.Reverse, admitted.Direction);
        if (!otherClears)
        {
            var remaining = (TrainPosition.Edge)View(session, 2).Position;
            Assert.AreEqual(2, remaining.Track);
            Assert.AreEqual(TravelDirection.Forward, remaining.Direction);
        }
        Assert.AreEqual(new TrainPosition.Node(new(1)), View(session, 3).Position);
        Assert.AreEqual(TrainActivity.Waiting, View(session, 3).Activity);
    }

    [TestMethod]
    public void Same_step_platform_waiters_use_train_id_independent_of_list_order()
    {
        foreach (var reversed in new[] { false, true })
        {
            var state = State();
            var plan = new RunPlan(false, [new PlanStop.Station(new(1), 2, []), new PlanStop.Waypoint(new(2))]);
            var trains = state.World.Trains.Select(t => t with { Activity = t.Id.Value == 3 ? TrainActivity.Servicing : TrainActivity.Running,
                Plan = plan, RemainingDwellSteps = t.Id.Value == 3 ? 1UL : 0UL }).ToImmutableArray();
            if (reversed) trains = trains.Reverse().ToImmutableArray();
            using var session = new CoreSession(state with { World = state.World with { Trains = trains } });
            Step(session);
            Assert.AreEqual(TrainActivity.Waiting, View(session, 1).Activity);
            Assert.AreEqual(TrainActivity.Waiting, View(session, 2).Activity);
            Assert.AreEqual("platform_wait", View(session, 1).Blockers.Single().Code);
            Assert.AreEqual("platform_wait", View(session, 2).Blockers.Single().Code);
            Assert.AreEqual(0, session.Read().World.Nodes[0].OccupiedPlatforms);
            Step(session);
            Assert.AreEqual(TrainActivity.Servicing, View(session, 1).Activity);
            Assert.AreEqual(2UL, View(session, 1).RemainingDwellSteps);
            Assert.AreEqual(TrainActivity.Waiting, View(session, 2).Activity);
            Assert.AreEqual(1, session.Read().World.Nodes[0].OccupiedPlatforms);
        }
    }

    private static TrainState OnEdge(TrainState train, int track, double distance, double speed) => train with
    { Position = new TrainPosition.Edge(new(1), track, TravelDirection.Forward, distance), Transit = null,
        Activity = TrainActivity.Running, SpeedMetersPerSecond = speed };

    private static TrainState Queued(TrainState train, ulong node, ulong age, ulong target) => train with
    { Position = new TrainPosition.Node(new(node)), Activity = TrainActivity.Waiting, QueueEnteredStep = age,
        Plan = new(false, [new PlanStop.Waypoint(new(target))]) };
}
