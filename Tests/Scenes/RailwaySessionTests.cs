using System.Collections.Immutable;
using System.Numerics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes.Tests;

[TestClass]
public sealed class RailwaySessionTests
{
    private static readonly SceneFrameRequest Request = new(1, null, SceneDebugLayer.None);
    private static readonly SceneSessionId SessionId = new(Guid.NewGuid());
    private static SceneFrameStamp Stamp(ulong revision) => new(SessionId, new(1), new(1), revision);
    private static SceneWorldSnapshot Short(DesignEvaluation? design = null, TrainView? train = null, ulong steps = 0) =>
        RailwayFixtures.Snapshot(4, 4, 0, steps: steps, content: Content(design ?? RailwayFixtures.Vehicle()),
            trains: [train ?? RailwayFixtures.Train(1)], instances: [RailwayFixtures.Instance()]);
    private static FixedContentView Content(DesignEvaluation? design, ulong generation = 1, TerrainAppearanceProjection? terrain = null) => new()
    {
        Generation = new(generation), Core = SceneFixtures.Content().Core,
        Terrains = new Dictionary<TerrainTypeId, ContentLookup<TerrainAppearanceProjection>>
        { [RailwayFixtures.Terrain] = new ContentLookup<TerrainAppearanceProjection>.Found(terrain ?? RailwayFixtures.TerrainAppearance) },
        RailwayAppearances = new Dictionary<RuleDefinitionId, ContentLookup<RailwayAppearanceProjection>>
        { [RailwayFixtures.Rule] = new ContentLookup<RailwayAppearanceProjection>.Found(RailwayFixtures.Rules) },
        VoxelAppearances = new Dictionary<VoxelDefinitionId, ContentLookup<VoxelAppearanceProjection>>
        { [SceneFixtures.Voxel] = new ContentLookup<VoxelAppearanceProjection>.Found(SceneFixtures.Cube with { Behaviors = new(true, null) }) },
        Designs = new Dictionary<DesignHash, ContentLookup<DesignEvaluation>>
        { [SceneFixtures.Design] = design is null ? new ContentLookup<DesignEvaluation>.Missing([]) : new ContentLookup<DesignEvaluation>.Found(design) }
    };
    private static WorldSceneSession Open(SceneWorldSnapshot snapshot) =>
        (WorldSceneSession)((SceneResult<IWorldSceneSession>.Success)ScenesFactory.Create().OpenWorld(new(Guid.NewGuid()), snapshot)).Value;
    private static ISceneFrame Present(WorldSceneSession session, ISceneFrame? displayed = null, double interpolation = 1) =>
        ((SceneResult<ISceneFrame>.Success)session.Present(Request with { Interpolation = interpolation }, displayed)).Value;
    private static async Task<ISceneFrame> Settled(WorldSceneSession session, ISceneFrame? displayed = null, double interpolation = 1)
    {
        var frame = Present(session, displayed, interpolation);
        while (session.PendingRailwayBuilds.Length > 0)
        {
            await Task.WhenAll(session.PendingRailwayBuilds);
            frame = Present(session, displayed, interpolation);
        }
        return frame;
    }
    private static RailwayScene Controlled(SceneWorldSnapshot snapshot,
        Func<SceneWorldSnapshot, RailwayPath, int, CancellationToken, RailwaySegment>? build = null)
    {
        var ids = new Dictionary<string, SceneObjectId>();
        ulong next = 0;
        return new(snapshot, RailwayFixtures.Edge, new(), key =>
        {
            if (!ids.TryGetValue(key, out var id)) ids.Add(key, id = new(++next));
            return id;
        }, build ?? EmptySegment);
    }
    private static RailwaySegment EmptySegment(SceneWorldSnapshot snapshot, RailwayPath path, int index, CancellationToken token) =>
        new(index, path.Sample(index * 128).Position, new([], [], []), ImmutableDictionary<GridColumn, double>.Empty,
            RailwayDependencies.Capture(snapshot, path, index, [], [], [], [], false, false), []);
    private static async Task<SceneFrame> Settled(RailwayScene scene, SceneWorldSnapshot snapshot,
        SceneWorldSnapshot? previous = null, SceneFrame? displayed = null, double interpolation = 1, ulong revision = 1,
        VehicleId? followed = null)
    {
        var target = new WorldSceneTarget.Follow(new(1), followed ?? new(1));
        var frame = scene.Present(snapshot, previous, target, Request with { Interpolation = interpolation }, Stamp(revision), displayed);
        while (scene.PendingWork.Length > 0)
        {
            await Task.WhenAll(scene.PendingWork);
            frame = scene.Present(snapshot, previous, target, Request with { Interpolation = interpolation }, Stamp(++revision), displayed);
        }
        return frame;
    }

    [TestMethod]
    public async Task ProductionFactoryBuildsRailwayAndMapsTerrainAndVehicleInTheSameFrame()
    {
        await using var session = Open(Short());
        session.RequestView(new WorldSceneTarget.Follow(new(1), new(1)));
        var frame = await Settled(session);
        Assert.AreEqual(SceneReadiness.Ready, frame.Readiness);
        var pose = ((Lookup<ScenePose>.Found)frame.FindPose(new WorldObjectId.Instance(new InstanceId.Vehicle(new(1))))).Value;
        Assert.AreEqual(new Vector3(0, 0, 2), pose.Position);
        Assert.AreEqual(Quaternion.Identity, pose.Rotation);
        Assert.AreEqual(1.0, ((WorldLocation.Edge)((Lookup<WorldLocation>.Found)frame.Locate(new(0, 0, 2))).Value).DistanceMeters, 1e-6);
        Assert.IsInstanceOfType<Lookup<double>.Found>(frame.TerrainHeight(0.5, 2.5));
        var hit = (ScenePick.Hit)frame.Pick(new(new(20.5f, 10, 2.5f), -Vector3.UnitY, 30));
        Assert.IsInstanceOfType<PickSubject.Terrain>(hit.Value.Subject);
        Assert.IsInstanceOfType<SceneResult<PickSubject>.Success>(session.Resolve(hit.Value));
        Assert.IsTrue(frame.Update.Upserts.SelectMany(item => item.Visuals).OfType<SceneVisual.Wheelset>().Any());
        Assert.IsInstanceOfType<Lookup<double>.Missing>(frame.TerrainHeight(1e100, 1e100));
    }

    [TestMethod]
    public async Task AllTracksAndMixedVehicleLengthsAreCenteredAndReversalKeepsBodyAndOrder()
    {
        var first = RailwayFixtures.Vehicle(lengthCells: 8);
        var second = RailwayFixtures.Vehicle(lengthCells: 12, design: new("second"));
        var content = Content(first);
        var view = new FixedContentView
        {
            Generation = content.Generation, Core = content.Core,
            Terrains = new Dictionary<TerrainTypeId, ContentLookup<TerrainAppearanceProjection>>
            { [RailwayFixtures.Terrain] = content.FindTerrain(RailwayFixtures.Terrain) },
            RailwayAppearances = new Dictionary<RuleDefinitionId, ContentLookup<RailwayAppearanceProjection>>
            { [RailwayFixtures.Rule] = content.FindRailwayAppearance(RailwayFixtures.Rule) },
            VoxelAppearances = new Dictionary<VoxelDefinitionId, ContentLookup<VoxelAppearanceProjection>>
            { [SceneFixtures.Voxel] = content.FindVoxelAppearance(SceneFixtures.Voxel) },
            Designs = new Dictionary<DesignHash, ContentLookup<DesignEvaluation>>
            { [first.Design] = new ContentLookup<DesignEvaluation>.Found(first), [second.Design] = new ContentLookup<DesignEvaluation>.Found(second) }
        };
        var train = RailwayFixtures.Train(40, [new(1), new(2)]) with { LengthMeters = 10 };
        var other = RailwayFixtures.Train(50, [new(3)], 2, 2);
        var instances = ImmutableArray.Create(RailwayFixtures.Instance(), RailwayFixtures.Instance(2, design: second.Design), RailwayFixtures.Instance(3));
        var snapshot = RailwayFixtures.Snapshot(300, 300, 0, 2, content: view, trains: [train, other], instances: instances);
        await using var scene = Controlled(snapshot);
        var before = await Settled(scene, snapshot);
        AssertPosition(before, 1, new(-17, 0, 86));
        AssertPosition(before, 2, new(-17, 0, 76));
        AssertPosition(before, 3, new(17, 0, 100));
        var reversed = train with
        {
            Position = new TrainPosition.Edge(RailwayFixtures.Edge, 1, TravelDirection.Reverse, 40),
            OrderFacing = TrainOrderFacing.LastEndLeading
        };
        var next = RailwayFixtures.Snapshot(300, 300, 0, 2, content: view, trains: [reversed, other], instances: instances);
        var after = await Settled(scene, next, displayed: before, revision: before.Update.Frame.Revision + 1);
        foreach (var id in new ulong[] { 1, 2, 3 }) Assert.AreEqual(Pose(before, id), Pose(after, id));
    }

    [TestMethod]
    public async Task AdjacentStepsInterpolateButSkippedStepsAndChangedOrderUseCurrentBoundary()
    {
        var content = Content(RailwayFixtures.Vehicle());
        var old = RailwayFixtures.Snapshot(300, 300, 0, content: content, trains: [RailwayFixtures.Train(100)], instances: [RailwayFixtures.Instance()]);
        await using var scene = Controlled(old);
        var newer = RailwayFixtures.Snapshot(300, 300, 0, steps: 1, content: content, trains: [RailwayFixtures.Train(102)], instances: [RailwayFixtures.Instance()]);
        var middle = await Settled(scene, newer, old, interpolation: 0.25);
        AssertPosition(middle, 1, new(0, 0, 201));
        Assert.AreEqual(0.25 * SimulationTime.StepSeconds, middle.SimulationSeconds, 1e-12);
        var skipped = RailwayFixtures.Snapshot(300, 300, 0, steps: 8, content: content, trains: [RailwayFixtures.Train(120)], instances: [RailwayFixtures.Instance()]);
        var current = await Settled(scene, skipped, newer, interpolation: 0.25);
        AssertPosition(current, 1, new(0, 0, 240));
        Assert.AreEqual(8 * SimulationTime.StepSeconds, current.SimulationSeconds);
        var reverse = RailwayFixtures.Train(102, direction: TravelDirection.Reverse, facing: TrainOrderFacing.LastEndLeading);
        var changed = RailwayFixtures.Snapshot(300, 300, 0, steps: 1, content: content, trains: [reverse], instances: [RailwayFixtures.Instance()]);
        var changedFrame = await Settled(scene, changed, old, interpolation: 0.25);
        AssertPosition(changedFrame, 1, new(0, 0, 204));
    }

    [TestMethod]
    public async Task FloatingOriginShiftsRootsWithoutReplacingBodyGeometryOrOldFrame()
    {
        var content = Content(RailwayFixtures.Vehicle());
        var old = RailwayFixtures.Snapshot(600, 600, 0, content: content, trains: [RailwayFixtures.Train(127)], instances: [RailwayFixtures.Instance()]);
        await using var scene = Controlled(old);
        var before = await Settled(scene, old);
        var body = before.Objects.Single(item => item.Subject is WorldObjectId.Instance && item.Parent is null);
        var next = RailwayFixtures.Snapshot(600, 600, 0, content: content, trains: [RailwayFixtures.Train(129)], instances: [RailwayFixtures.Instance()]);
        var after = await Settled(scene, next, displayed: before, revision: before.Update.Frame.Revision + 1);
        Assert.AreEqual(new ScenePoint(0, 0, 256), after.Origin);
        Assert.AreEqual(new Vector3(0, 0, -256), after.Update.ShiftExistingRoots);
        Assert.AreEqual(before.Update.Frame, after.Update.Base);
        var newBody = after.Objects.Single(item => item.Id == body.Id);
        Assert.AreSame(((SceneVisual.Geometry)body.Visuals[0]).Mesh, ((SceneVisual.Geometry)newBody.Visuals[0]).Mesh);
        AssertPosition(after, 1, new(0, 0, 2));
        AssertPosition(before, 1, new(0, 0, 254));
    }

    [TestMethod]
    public async Task LongConsistKeepsEveryBodyAndPickPoseAcrossSegmentsAndOrigin()
    {
        var content = Content(RailwayFixtures.Vehicle());
        var members = Enumerable.Range(1, 21).Select(id => new VehicleId((ulong)id)).ToImmutableArray();
        var instances = members.Select(id => RailwayFixtures.Instance(id.Value)).ToImmutableArray();
        var train = RailwayFixtures.Train(127, members);
        var old = RailwayFixtures.Snapshot(600, 600, 0, content: content, trains: [train], instances: instances);
        await using var scene = Controlled(old);
        var before = await Settled(scene, old, followed: new(11));
        var next = RailwayFixtures.Snapshot(600, 600, 0, content: content,
            trains: [RailwayFixtures.Train(129, members)], instances: instances);
        var after = await Settled(scene, next, displayed: before,
            revision: before.Update.Frame.Revision + 1, followed: new(11));
        Assert.AreEqual(new ScenePoint(0, 0, 256), after.Origin);
        Assert.AreEqual(new Vector3(0, 0, -256), after.Update.ShiftExistingRoots);
        foreach (var id in members)
        {
            var index = (int)id.Value - 1;
            var subject = new WorldObjectId.Instance(new InstanceId.Vehicle(id));
            var oldBody = before.Objects.Single(item => item.Parent is null && item.Subject == subject);
            var body = after.Objects.Single(item => item.Id == oldBody.Id);
            Assert.AreSame(((SceneVisual.Geometry)oldBody.Visuals[0]).Mesh, ((SceneVisual.Geometry)body.Visuals[0]).Mesh);
            AssertPosition(before, id.Value, new(0, 0, 254 + 80 - 8 * index));
            AssertPosition(after, id.Value, new(0, 0, 258 + 80 - 8 * index - 256));
            var pose = Pose(after, id.Value);
            var hit = (ScenePick.Hit)after.Pick(new(pose.Position + new Vector3(.5f, 10, .5f), -Vector3.UnitY, 20));
            Assert.AreEqual(subject, ((PickSubject.WorldObject)hit.Value.Subject).Object);
            Assert.AreEqual(after.Update.Frame, hit.Value.Frame);
        }
    }

    [TestMethod]
    public async Task EightTimesProgressDoesNotWaitForBlockedSegmentsAndOldTasksCannotPublishAfterClose()
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var snapshot = Short();
        var scene = Controlled(snapshot, (input, path, index, token) =>
        { entered.TrySetResult(); release.Wait(token); return EmptySegment(input, path, index, token); });
        try
        {
            await Task.WhenAll(scene.PendingWork);
            var target = new WorldSceneTarget.Location(new WorldLocation.Edge(RailwayFixtures.Edge, 1));
            scene.Present(snapshot, null, target, Request, Stamp(1), null);
            await entered.Task;
            var advanced = Short(steps: 480);
            var frame = scene.Present(advanced, snapshot, target, Request, Stamp(2), null);
            Assert.AreEqual(8.0, frame.SimulationSeconds);
            Assert.AreEqual(SceneReadiness.Loading, frame.Readiness);
            Assert.IsTrue(frame.Update.Environment.FoggedRegions.Length > 0);
            Assert.IsInstanceOfType<ScenePick.NotReady>(frame.Pick(new(Vector3.Zero, Vector3.UnitZ, 1)));
            await scene.DisposeAsync();
        }
        finally { release.Set(); }
    }

    [TestMethod]
    public async Task UnknownVehicleLengthUsesMarkersAndBuildFailureHasItsOwnDiagnostic()
    {
        var evaluation = RailwayFixtures.Vehicle() with { Core = null };
        await using var session = Open(Short(evaluation));
        session.RequestView(new WorldSceneTarget.Follow(new(1), new(1)));
        var frame = await Settled(session);
        Assert.IsTrue(frame.Diagnostics.Any(problem => problem.Code == "vehicle_length_unknown"));
        Assert.IsTrue(frame.Update.Upserts.Where(item => item.Subject is WorldObjectId.Instance).SelectMany(item => item.Visuals)
            .All(visual => visual is SceneVisual.Missing));
        var invalid = RailwayFixtures.Snapshot(10, 20);
        await using var failed = Open(invalid);
        failed.RequestView(new WorldSceneTarget.Location(new WorldLocation.Edge(RailwayFixtures.Edge, 0)));
        await Assert.ThrowsExactlyAsync<RailwayBuildException>(() => Task.WhenAll(failed.PendingRailwayBuilds));
        var rejected = Present(failed);
        Assert.AreEqual(SceneReadiness.Failed, rejected.Readiness);
        Assert.IsTrue(rejected.Diagnostics.Any(problem => problem.Code == "build_failed"));
    }

    [TestMethod]
    public async Task NewBindingReusesEquivalentTerrainAndMissingDesignNeverInventsAPlacedBody()
    {
        var factory = ScenesFactory.Create();
        var evaluation = RailwayFixtures.Vehicle();
        var terrain = RailwayFixtures.TerrainAppearance with { LocalHeightNoise = [new(4, 0)] };
        var snapshot = Short() with { Content = Content(evaluation, terrain: terrain) };
        await using var first = (WorldSceneSession)((SceneResult<IWorldSceneSession>.Success)factory.OpenWorld(new(Guid.NewGuid()), snapshot)).Value;
        var target = new WorldSceneTarget.Follow(new(1), new(1));
        first.RequestView(target);
        var old = await Settled(first);
        var oldMesh = old.Update.Upserts.First(item => item.Subject is null && item.Parent is null).Visuals.OfType<SceneVisual.Geometry>().First().Mesh;
        await first.DisposeAsync();
        var replacement = snapshot with { Content = Content(evaluation, 2, terrain with { LocalHeightNoise = [new(4, 0)] }) };
        await using var next = (WorldSceneSession)((SceneResult<IWorldSceneSession>.Success)factory.OpenWorld(new(Guid.NewGuid()), replacement)).Value;
        next.RequestView(target);
        var current = await Settled(next);
        var newMesh = current.Update.Upserts.First(item => item.Subject is null && item.Parent is null).Visuals.OfType<SceneVisual.Geometry>().First().Mesh;
        Assert.AreSame(oldMesh, newMesh);
        AssertPosition(old, 1, new(0, 0, 2));
        await using var missing = Open(Short() with { Content = Content(null) });
        missing.RequestView(target);
        var unavailable = await Settled(missing);
        Assert.IsTrue(unavailable.Diagnostics.Any(problem => problem.Code == "vehicle_length_unknown"));
        Assert.IsTrue(unavailable.Update.Upserts.Where(item => item.Subject is WorldObjectId.Instance).SelectMany(item => item.Visuals)
            .Any(visual => visual is SceneVisual.Missing { Reference: ContentReference.Design { Hash: var hash } } && hash == SceneFixtures.Design));
        Assert.IsFalse(unavailable.Update.Upserts.Where(item => item.Subject is WorldObjectId.Instance).SelectMany(item => item.Visuals)
            .Any(visual => visual is SceneVisual.Geometry));
    }

    [TestMethod]
    public async Task FollowingIntoNodeReportsTheDeferredNodeImplementation()
    {
        await using var session = Open(Short());
        session.RequestView(new WorldSceneTarget.Follow(new(1), new(1)));
        await Settled(session);
        var node = Short(train: RailwayFixtures.Train(1) with { Position = new TrainPosition.Node(new(2)) }, steps: 1);
        session.Observe(node, []);
        var fault = (SceneResult<ISceneFrame>.Faulted)session.Present(Request, null);
        StringAssert.Contains(fault.Problem.Detail, "#316");
    }

    private static ScenePose Pose(ISceneFrame frame, ulong vehicle) =>
        ((Lookup<ScenePose>.Found)frame.FindPose(new WorldObjectId.Instance(new InstanceId.Vehicle(new(vehicle))))).Value;
    private static void AssertPosition(ISceneFrame frame, ulong vehicle, Vector3 position) =>
        Assert.IsTrue(Vector3.Distance(position, Pose(frame, vehicle).Position) < 0.0001f);
}
