using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes.Tests;

[TestClass]
public sealed class NodeLifecycleTests
{
    private static readonly SceneFrameRequest Request = new(1, null, SceneDebugLayer.None);
    private static SceneFrame Present(WorldSceneSession session, ISceneFrame? displayed = null) =>
        (SceneFrame)((SceneResult<ISceneFrame>.Success)session.Present(Request, displayed)).Value;
    private static WorldSceneSession Open(SceneWorldSnapshot snapshot,
        Func<SceneWorldSnapshot, NodeId, NodeScene.Built?, CancellationToken, NodeScene.Built>? build = null) =>
        new(new(Guid.NewGuid()), snapshot, WorldSceneSession.Generate, new(), new(), build);

    [TestMethod]
    public async Task MissingRailwayAppearanceReturnsFailedFrameWithoutThrowingRequest()
    {
        var content = NodeFixtures.Content();
        content = new()
        {
            Generation = content.Generation, Core = content.Core, Voxels = content.Voxels,
            Designs = new Dictionary<DesignHash, ContentLookup<DesignEvaluation>>
            { [SceneFixtures.Design] = content.FindDesign(SceneFixtures.Design) },
            Terrains = new Dictionary<TerrainTypeId, ContentLookup<TerrainAppearanceProjection>>
            { [RailwayFixtures.Terrain] = content.FindTerrain(RailwayFixtures.Terrain) },
            VoxelAppearances = new Dictionary<VoxelDefinitionId, ContentLookup<VoxelAppearanceProjection>>
            {
                [SceneFixtures.Voxel] = content.FindVoxelAppearance(SceneFixtures.Voxel),
                [NodeFixtures.Concrete] = content.FindVoxelAppearance(NodeFixtures.Concrete)
            },
            RailwayAppearances = new Dictionary<RuleDefinitionId, ContentLookup<RailwayAppearanceProjection>>
            { [RailwayFixtures.Rule] = new ContentLookup<RailwayAppearanceProjection>.Missing([]) }
        };
        await using var session = Open(NodeFixtures.Snapshot(trains: [], content: content));
        Assert.IsInstanceOfType<SceneResult<SceneRequestId>.Success>(session.RequestView(new WorldSceneTarget.Location(NodeFixtures.Location)));
        await AwaitExpectedBuildFailure(session.PendingBuild);
        AssertFailed(Present(session));
    }

    [TestMethod]
    public async Task BackgroundNodeBuildFailureReturnsFailedFrame()
    {
        await using var session = Open(NodeFixtures.Snapshot(trains: []),
            (_, _, _, _) => throw new RailwayBuildException("Controlled node build failure."));
        Assert.IsInstanceOfType<SceneResult<SceneRequestId>.Success>(session.RequestView(new WorldSceneTarget.Location(NodeFixtures.Location)));
        await AwaitExpectedBuildFailure(session.PendingBuild);
        AssertFailed(Present(session));
    }

    [TestMethod]
    public async Task FailedCandidateIsDiscardedWithoutFaultingCurrentRailway()
    {
        var train = RailwayFixtures.Train(1, direction: TravelDirection.Reverse, speed: 1);
        var snapshot = Small(train, 0);
        var attempts = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = Open(snapshot, (_, _, _, _) =>
        {
            Interlocked.Increment(ref attempts);
            entered.TrySetResult();
            throw new RailwayBuildException("Controlled candidate failure.");
        });
        session.RequestView(new WorldSceneTarget.Follow(train.Id, new(1)));
        var line = await RailwayFixtures.Settled(session, Request);
        await entered.Task;
        foreach (var work in session.PendingPrebuilds) await AwaitExpectedBuildFailure(work);
        line = Present(session, line);
        Assert.AreNotEqual(SceneReadiness.Failed, line.Readiness);
        Assert.IsFalse(line.Diagnostics.Any(problem => problem.Code == "scene_internal_fault" || problem.Code == "build_failed"));
        Assert.IsNull(session.PrebuiltLocation);
        Assert.AreEqual(1, attempts);
        session.Observe(Small(train with { SpeedMetersPerSecond = 0 }, 1), []);
        line = Present(session, line);
        session.Observe(Small(train, 2), []);
        line = Present(session, line);
        foreach (var work in session.PendingPrebuilds) await AwaitExpectedBuildFailure(work);
        line = Present(session, line);
        Assert.AreEqual(2, attempts, "Reentering the prefetch range permits a new candidate after leaving it.");
        Assert.IsNull(session.PrebuiltLocation);
        session.Observe(Small(NodeFixtures.Train(enteredFrom: new(1)), 3), []);
        Present(session, line);
        await AwaitExpectedBuildFailure(session.PendingBuild);
        AssertFailed(Present(session));
    }

    [TestMethod]
    [DataRow(true, 1)]
    [DataRow(false, 1)]
    [DataRow(false, 0)]
    public async Task CountChangesPreserveOpeningGeometryAndReportPendingTrain(bool platforms, int openingTracks)
    {
        var train = NodeFixtures.Train(enteredFrom: new(1),
            activity: platforms ? TrainActivity.Servicing : TrainActivity.Waiting);
        var initial = NodeFixtures.Snapshot(platforms: 1, tracks: openingTracks, trackLimit: 2, trains: openingTracks == 0 ? [] : [train]);
        var builds = 0;
        await using var session = Open(initial, (input, node, _, token) =>
        {
            Interlocked.Increment(ref builds);
            var layout = NodeLayout.Build(input, node, token);
            return new(layout, ImmutableDictionary<string, NodeScene.Part>.Empty.Add("platform:0",
                NodeScene.PlatformPart(layout, 0, NodeFixtures.ConcreteCube)), []);
        });
        session.RequestView(new WorldSceneTarget.Location(NodeFixtures.Location));
        var before = await RailwayFixtures.Settled(session, Request);
        Assert.AreEqual(SceneReadiness.Ready, before.Readiness);
        var geometry = before.Update.Upserts.SelectMany(item => item.Visuals).OfType<SceneVisual.Geometry>().Select(item => item.Mesh).ToArray();
        Assert.IsTrue(geometry.Length > 0);
        var changed = NodeFixtures.Snapshot(platforms: platforms ? 2 : 1, tracks: platforms ? 1 : openingTracks + 1,
            trackLimit: 2, trains: [train], steps: 1);
        session.Observe(changed, []);
        var after = Present(session);
        Assert.AreEqual(SceneReadiness.Ready, after.Readiness);
        Assert.AreEqual(1, builds);
        var afterGeometry = after.Update.Upserts.SelectMany(item => item.Visuals).OfType<SceneVisual.Geometry>().Select(item => item.Mesh).ToArray();
        Assert.AreEqual(geometry.Length, afterGeometry.Length);
        for (var index = 0; index < geometry.Length; index++) Assert.AreSame(geometry[index], afterGeometry[index]);
        Assert.IsTrue(after.Diagnostics.Any(problem => problem.Code == "railway_update_pending"));
        Assert.AreEqual(before.NodePanBounds, after.NodePanBounds);
        if (openingTracks == 0)
        {
            var subject = new WorldObjectId.Instance(new InstanceId.Vehicle(new(1)));
            Assert.IsInstanceOfType<Lookup<ScenePose>.Found>(after.FindPose(subject));
            Assert.IsTrue(after.Objects.Where(item => item.Subject == subject).All(item => item.Visuals.IsEmpty));
        }
    }

    private static SceneWorldSnapshot Small(TrainView train, ulong steps)
    {
        var snapshot = NodeFixtures.Snapshot(platforms: 0, trains: [train], steps: steps,
            content: NodeFixtures.Content(rules: RailwayFixtures.Rules with { MinimumCurveRadiusMeters = 1 }));
        snapshot = RailwayFixtures.ReplaceCore(snapshot, node: snapshot.Core.World.Nodes[1] with { Position = new(4, 0) });
        snapshot = RailwayFixtures.ReplaceCore(snapshot, node: snapshot.Core.World.Nodes[2] with { Position = new(0, -4) });
        snapshot = RailwayFixtures.ReplaceCore(snapshot, edge: snapshot.Core.World.Edges[0] with { LengthMeters = 4 });
        return RailwayFixtures.ReplaceCore(snapshot, edge: snapshot.Core.World.Edges[1] with { LengthMeters = 4 });
    }

    private static async Task AwaitExpectedBuildFailure(Task? work)
    {
        if (work is null) return;
        try { await work; }
        catch (RailwayBuildException) { }
    }

    private static void AssertFailed(SceneFrame frame)
    {
        Assert.AreEqual(SceneReadiness.Failed, frame.Readiness);
        Assert.IsTrue(frame.Diagnostics.Any(problem => problem.Code == "build_failed"));
        Assert.IsFalse(frame.Diagnostics.Any(problem => problem.Code == "scene_internal_fault"));
    }
}
