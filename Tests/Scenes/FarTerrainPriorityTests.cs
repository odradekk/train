using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes.Tests;

[TestClass]
public sealed class FarTerrainPriorityTests
{
    [TestMethod]
    public async Task Active_L0_blocks_other_far_work_until_session_presents_completed_builds()
    {
        var snapshot = NodeFixtures.Snapshot(platforms: 0, trains: [], instances: []);
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = new WorldSceneSession(new(Guid.NewGuid()), snapshot, new(), new(),
            (data, id, previous, token) =>
            {
                started.TrySetResult();
                release.Wait(token);
                return RailwayFixtures.EmptyNode(data, id, previous, token);
            });
        var permission = Field<FarTerrainPermission>(session, "farPermission");
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var units = 0;
        await using var otherFar = new FarTerrainWorker((budget, _) =>
        {
            Assert.AreEqual(128, budget);
            if (Interlocked.Increment(ref units) != 3) return false;
            finished.TrySetResult();
            return true;
        }, permission: permission);
        try
        {
            session.RequestView(new WorldSceneTarget.Location(NodeFixtures.Location));
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Present(session);
            Assert.IsTrue(Field<NodeScene>(session, "node").HasPendingBuilds);
            otherFar.Pump(0, true);
            Assert.IsFalse(permission.Allowed);
            Assert.AreEqual(0, otherFar.RunningCount);
            Assert.AreEqual(0, Volatile.Read(ref units));

            release.Set();
            await RailwayFixtures.Settled(session, new(1, null, SceneDebugLayer.None));
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await otherFar.PendingWork.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(3, units, "Present must wake queued far work and let it continue without another worker Pump.");
        }
        finally { release.Set(); }
    }

    [TestMethod]
    public async Task Candidate_L0_and_cancelled_candidate_drain_keep_other_far_work_blocked()
    {
        var snapshot = RailwayFixtures.Snapshot(4, 4, 0,
            content: RailwayFixtures.Content(design: RailwayFixtures.Vehicle(),
                rules: RailwayFixtures.Rules with { MinimumCurveRadiusMeters = 1 }),
            trains: [RailwayFixtures.Train(3, speed: 10)], instances: [RailwayFixtures.Instance()]);
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = new WorldSceneSession(new(Guid.NewGuid()), snapshot, new(), new(),
            (data, id, previous, token) =>
            {
                started.TrySetResult();
                // Keep cancellation draining until the test explicitly releases this build.
                release.Wait();
                return RailwayFixtures.EmptyNode(data, id, previous, token);
            });
        var permission = Field<FarTerrainPermission>(session, "farPermission");
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var units = 0;
        await using var otherFar = new FarTerrainWorker((_, _) =>
        {
            if (Interlocked.Increment(ref units) != 3) return false;
            finished.TrySetResult();
            return true;
        }, permission: permission);
        try
        {
            session.RequestView(new WorldSceneTarget.Follow(new(1), new(1)));
            var displayed = await RailwayFixtures.Settled(session, new(1, null, SceneDebugLayer.None));
            Assert.IsFalse(Field<RailwayScene>(session, "railway").HasPendingBuilds,
                "The active scene must have no L0 work masking candidate priority.");
            Present(session, displayed);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var candidate = Field<object>(session, "candidate");
            var candidateNode = (NodeScene)candidate.GetType().GetProperty("Node")!.GetValue(candidate)!;
            Assert.IsTrue(candidateNode.HasPendingBuilds);
            otherFar.Pump(0, true);
            Assert.IsFalse(permission.Allowed);
            Assert.AreEqual(0, otherFar.RunningCount);
            Assert.AreEqual(0, Volatile.Read(ref units));

            session.RequestView(new WorldSceneTarget.Location(new WorldLocation.Edge(RailwayFixtures.Edge, 0)));
            Assert.IsNull(session.PrebuiltLocation);
            var oldWorker = Field<FarTerrainWorker>(candidateNode, "farBuilds");
            var oldWork = oldWorker.PendingWork;
            var drain = session.PendingPrebuilds;
            Assert.IsTrue(drain.Any(task => !task.IsCompleted));
            displayed = await RailwayFixtures.Settled(session, new(1, null, SceneDebugLayer.None));
            Assert.IsFalse(Field<RailwayScene>(session, "railway").HasPendingBuilds);
            Assert.IsFalse(permission.Allowed, "Retired candidate L0 must still block far work while draining.");
            Assert.AreEqual(0, otherFar.RunningCount);
            Assert.AreEqual(0, Volatile.Read(ref units));

            release.Set();
            await SceneFixtures.AwaitPendingWork(drain);
            Present(session, displayed);
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await otherFar.PendingWork.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(3, units);
            Assert.AreSame(oldWork, oldWorker.PendingWork, "Restoring permission must not restart a retired worker.");
            Assert.AreEqual(0, oldWorker.RunningCount);
            Assert.IsNull(session.PrebuiltLocation);
        }
        finally { release.Set(); }
    }

    [TestMethod]
    public async Task Successor_L0_blocks_other_far_work_until_handover_retires_old_worker()
    {
        var founded = NodeFixtures.EmptyNodeSnapshot(NodeFixtures.Layout with { Plots = [] }, []);
        var empty = RailwayFixtures.ReplaceCore(founded, node: founded.Core.World.Nodes[0] with
            { Occupant = new NodeOccupant.Empty(), Platforms = 0 });
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var builds = 0;
        await using var session = new WorldSceneSession(new(Guid.NewGuid()), empty, new(), new(),
            (data, id, previous, token) =>
            {
                if (Interlocked.Increment(ref builds) == 2)
                {
                    started.TrySetResult();
                    release.Wait(token);
                }
                return RailwayFixtures.EmptyNode(data, id, previous, token);
            });
        var permission = Field<FarTerrainPermission>(session, "farPermission");
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var units = 0;
        await using var otherFar = new FarTerrainWorker((_, _) =>
        {
            if (Interlocked.Increment(ref units) != 3) return false;
            finished.TrySetResult();
            return true;
        }, permission: permission);
        try
        {
            session.RequestView(new WorldSceneTarget.Location(NodeFixtures.Location));
            var displayed = await RailwayFixtures.Settled(session, new(1, null, SceneDebugLayer.None));
            var oldNode = Field<NodeScene>(session, "node");
            Assert.IsFalse(oldNode.HasPendingBuilds);
            var oldWorker = Field<FarTerrainWorker>(oldNode, "farBuilds");
            var oldWork = oldWorker.PendingWork;

            session.Observe(founded, []);
            displayed = Present(session, displayed);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var successor = Field<NodeScene>(session, "successor");
            Assert.IsFalse(oldNode.HasPendingBuilds,
                "Only successor L0 may block far work in this fixture.");
            Assert.IsTrue(successor.HasPendingBuilds);
            otherFar.Pump(0, true);
            Assert.IsFalse(permission.Allowed);
            Assert.AreEqual(0, otherFar.RunningCount);
            Assert.AreEqual(0, Volatile.Read(ref units));

            release.Set();
            displayed = await RailwayFixtures.Settled(session, new(1, null, SceneDebugLayer.None), displayed);
            Assert.AreSame(successor, Field<NodeScene>(session, "node"));
            Assert.AreSame(successor.Layout, displayed.Node);
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await otherFar.PendingWork.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(3, units, "Successor handover must restore continuous far work without another worker Pump.");
            Assert.AreSame(oldWork, oldWorker.PendingWork,
                "Restoring permission after handover must not restart the retired active worker.");
            Assert.AreEqual(0, oldWorker.RunningCount);
        }
        finally { release.Set(); }
    }
    private static T Field<T>(object value, string name) =>
        (T)value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(value)!;

    private static SceneFrame Present(WorldSceneSession session, ISceneFrame? displayed = null) =>
        (SceneFrame)((SceneResult<ISceneFrame>.Success)session.Present(
            new(1, null, SceneDebugLayer.None), displayed)).Value;
}


