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
    private static FixedContentView Content(DesignEvaluation? design, ulong generation = 1, TerrainAppearanceProjection? terrain = null) =>
        RailwayFixtures.Content(terrain, design: design, wheelsetMarkers: true, generation: generation,
            designs: design is null ? new Dictionary<DesignHash, ContentLookup<DesignEvaluation>>
            { [SceneFixtures.Design] = new ContentLookup<DesignEvaluation>.Missing([]) } : null);
    private static WorldSceneSession Open(SceneWorldSnapshot snapshot) =>
        (WorldSceneSession)((SceneResult<IWorldSceneSession>.Success)ScenesFactory.Create().OpenWorld(new(Guid.NewGuid()), snapshot)).Value;
    private static ISceneFrame Present(WorldSceneSession session, ISceneFrame? displayed = null, double interpolation = 1) =>
        ((SceneResult<ISceneFrame>.Success)session.Present(Request with { Interpolation = interpolation }, displayed)).Value;
    private static Task<SceneFrame> Settled(WorldSceneSession session, ISceneFrame? displayed = null, double interpolation = 1) =>
        RailwayFixtures.Settled(session, Request with { Interpolation = interpolation }, displayed);
    private static Task<SceneFrame> Settled(RailwayScene scene, SceneWorldSnapshot snapshot,
        SceneWorldSnapshot? previous = null, SceneFrame? displayed = null, double interpolation = 1, ulong revision = 1,
        VehicleId? followed = null) => RailwayFixtures.Settled(scene, snapshot,
            new WorldSceneTarget.Follow(new(1), followed ?? new(1)), Request with { Interpolation = interpolation }, Stamp(revision), previous, displayed);

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
    public async Task LocationFocusMapsToRequestedDistanceAndKeepsScheduledSegmentsCentered()
    {
        const double distance = 306.75;
        var snapshot = RailwayFixtures.Snapshot(800, 800, relief: 0);
        var target = new WorldSceneTarget.Location(new WorldLocation.Edge(RailwayFixtures.Edge, distance));
        var request = Request with { DebugLayers = SceneDebugLayer.RailwaySegments };
        await using var vehicles = new SceneVehicles();
        await using var scene = RailwayFixtures.Controlled(snapshot, vehicles);
        var first = await RailwayFixtures.Settled(scene, snapshot, target, request, Stamp(1));
        AssertLocation(first, Vector3.Zero);

        var next = await RailwayFixtures.Settled(scene, snapshot, target,
            request with { Camera = new(first, Vector3.Zero) },
            Stamp(first.Update.Frame.Revision + 1), displayed: first);
        AssertLocation(next, next.Update.ShiftExistingRoots);
        var centers = next.DebugLines.Chunk(2).Select(lines =>
            ((Lookup<WorldLocation>.Found)next.Locate(
                (lines[0].From + lines[0].To + lines[1].From + lines[1].To) / 4)).Value)
            .Cast<WorldLocation.Edge>().Select(location => location.DistanceMeters).Order().ToArray();
        Assert.IsTrue(Math.Abs(centers[centers.Length / 2] - distance) <
            RailwayCoordinates.SegmentLengthCells / RailwayCoordinates.CellsPerMeter / 2,
            "The middle scheduled segment must still contain the requested location.");

        void AssertLocation(ISceneFrame frame, Vector3 focus)
        {
            var location = (WorldLocation.Edge)((Lookup<WorldLocation>.Found)frame.Locate(focus)).Value;
            Assert.AreEqual(RailwayFixtures.Edge, location.Id);
            Assert.AreEqual(distance, location.DistanceMeters, 0.001);
        }
    }

    [TestMethod]
    public async Task AllTracksAndMixedVehicleLengthsAreCenteredAndReversalKeepsBodyAndOrder()
    {
        var first = RailwayFixtures.Vehicle(lengthCells: 8);
        var second = RailwayFixtures.Vehicle(lengthCells: 12, design: new("second"));
        var view = RailwayFixtures.Content(design: first, wheelsetMarkers: true,
            designs: new Dictionary<DesignHash, ContentLookup<DesignEvaluation>>
            { [second.Design] = new ContentLookup<DesignEvaluation>.Found(second) });
        var train = RailwayFixtures.Train(40, [new(1), new(2)]) with { LengthMeters = 10 };
        var other = RailwayFixtures.Train(50, [new(3)], 2, 2);
        var instances = ImmutableArray.Create(RailwayFixtures.Instance(), RailwayFixtures.Instance(2, design: second.Design), RailwayFixtures.Instance(3));
        var snapshot = RailwayFixtures.Snapshot(300, 300, 0, 2, content: view, trains: [train, other], instances: instances);
        await using var vehicles = new SceneVehicles();
        await using var scene = RailwayFixtures.Controlled(snapshot, vehicles);
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
        await using var vehicles = new SceneVehicles();
        await using var scene = RailwayFixtures.Controlled(old, vehicles);
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
        await using var vehicles = new SceneVehicles();
        await using var scene = RailwayFixtures.Controlled(old, vehicles);
        var before = await Settled(scene, old);
        var body = before.Objects.Single(item => item.Subject is WorldObjectId.Instance && item.Parent is null);
        var next = RailwayFixtures.Snapshot(600, 600, 0, content: content, trains: [RailwayFixtures.Train(129)], instances: [RailwayFixtures.Instance()]);
        var after = await Settled(scene, next, displayed: before, revision: before.Update.Frame.Revision + 1);
        Assert.AreEqual(new ScenePoint(0, 0, RailwayCoordinates.OriginStepCells), after.Origin);
        Assert.AreEqual(new Vector3(0, 0, (float)-RailwayCoordinates.OriginStepCells), after.Update.ShiftExistingRoots);
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
        await using var vehicles = new SceneVehicles();
        await using var scene = RailwayFixtures.Controlled(old, vehicles);
        var before = await Settled(scene, old, followed: new(11));
        var next = RailwayFixtures.Snapshot(600, 600, 0, content: content,
            trains: [RailwayFixtures.Train(129, members)], instances: instances);
        var after = await Settled(scene, next, displayed: before,
            revision: before.Update.Frame.Revision + 1, followed: new(11));
        Assert.AreEqual(new ScenePoint(0, 0, RailwayCoordinates.OriginStepCells), after.Origin);
        Assert.AreEqual(new Vector3(0, 0, (float)-RailwayCoordinates.OriginStepCells), after.Update.ShiftExistingRoots);
        foreach (var id in members)
        {
            var index = (int)id.Value - 1;
            var subject = new WorldObjectId.Instance(new InstanceId.Vehicle(id));
            var oldBody = before.Objects.Single(item => item.Parent is null && item.Subject == subject);
            var body = after.Objects.Single(item => item.Id == oldBody.Id);
            Assert.AreSame(((SceneVisual.Geometry)oldBody.Visuals[0]).Mesh, ((SceneVisual.Geometry)body.Visuals[0]).Mesh);
            AssertPosition(before, id.Value, new(0, 0, 254 + 80 - 8 * index));
            AssertPosition(after, id.Value, new(0, 0, (float)(258 + 80 - 8 * index - RailwayCoordinates.OriginStepCells)));
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
        await using var vehicles = new SceneVehicles();
        var scene = RailwayFixtures.Controlled(snapshot, vehicles, (input, path, index, token) =>
        { entered.TrySetResult(); release.Wait(token); return RailwayFixtures.EmptySegment(input, path, index, token); });
        try
        {
            await SceneFixtures.AwaitPendingWork(scene.PendingWork);
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
        Assert.IsInstanceOfType<Lookup<ScenePose>.Found>(frame.FindPose(new WorldObjectId.Instance(new InstanceId.Vehicle(new(1)))));
        Assert.IsTrue(frame.Update.Upserts.Single(item => item.Subject is WorldObjectId.Instance).Visuals.IsEmpty);
        var invalid = RailwayFixtures.Snapshot(10, 20);
        await using var failed = Open(invalid);
        failed.RequestView(new WorldSceneTarget.Location(new WorldLocation.Edge(RailwayFixtures.Edge, 0)));
        await SceneFixtures.AwaitPendingWork(failed.PendingRailwayBuilds);
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
        Assert.IsTrue(unavailable.Diagnostics.Any(problem => problem.Code == "content_missing"));
        Assert.IsTrue(unavailable.Update.Upserts.Where(item => item.Subject is WorldObjectId.Instance).SelectMany(item => item.Visuals)
            .Any(visual => visual is SceneVisual.Missing { Reference: ContentReference.Design { Hash: var hash } } && hash == SceneFixtures.Design));
        Assert.IsFalse(unavailable.Update.Upserts.Where(item => item.Subject is WorldObjectId.Instance).SelectMany(item => item.Visuals)
            .Any(visual => visual is SceneVisual.Geometry));
    }

    private static ScenePose Pose(ISceneFrame frame, ulong vehicle) =>
        ((Lookup<ScenePose>.Found)frame.FindPose(new WorldObjectId.Instance(new InstanceId.Vehicle(new(vehicle))))).Value;
    private static void AssertPosition(ISceneFrame frame, ulong vehicle, Vector3 position) =>
        Assert.IsTrue(Vector3.Distance(position, Pose(frame, vehicle).Position) < 0.0001f);
}
