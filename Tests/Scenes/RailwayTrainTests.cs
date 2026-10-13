using System.Collections.Immutable;
using System.Numerics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes.Tests;

[TestClass]
public sealed class RailwayTrainTests
{
    private static readonly SceneFrameRequest Request = new(1, null, SceneDebugLayer.None);
    private static readonly SceneFrameStamp Stamp = new(new(Guid.NewGuid()), new(1), new(1), 1);
    private static readonly WorldSceneTarget Target = new WorldSceneTarget.Follow(new(1), new(1));

    public enum LoadingAlignment { AlongX, Diagonal, Curve, StartOverhang, EndOverhang }

    [TestMethod]
    [DataRow(TravelDirection.Forward, TrainOrderFacing.FirstEndLeading)]
    [DataRow(TravelDirection.Forward, TrainOrderFacing.LastEndLeading)]
    [DataRow(TravelDirection.Reverse, TrainOrderFacing.FirstEndLeading)]
    [DataRow(TravelDirection.Reverse, TrainOrderFacing.LastEndLeading)]
    public async Task WheelsAdvanceAlongVehiclePositiveZRegardlessOfFacing(TravelDirection direction, TrainOrderFacing facing)
    {
        var content = RailwayFixtures.Content(design: RailwayFixtures.Vehicle(), wheelsetMarkers: true);
        var train = RailwayFixtures.Train(10, direction: direction, facing: facing);
        var snapshot = RailwayFixtures.Snapshot(100, 100, 0, content: content, trains: [train], instances: [RailwayFixtures.Instance()]);
        await using var vehicles = new SceneVehicles();
        await using var scene = RailwayFixtures.Controlled(snapshot, vehicles);
        var before = await RailwayFixtures.Settled(scene, snapshot, Target, Request, Stamp);
        var forward = Vector3.Transform(Vector3.UnitZ, Pose(before, 1).Rotation);
        var nextTrain = train with { Position = ((TrainPosition.Edge)train.Position) with { DistanceMeters = 10 + .1 * Math.Sign(forward.Z) } };
        var next = RailwayFixtures.Snapshot(100, 100, 0, steps: 1, content: content, trains: [nextTrain], instances: [RailwayFixtures.Instance()]);
        var after = await RailwayFixtures.Settled(scene, next, Target, Request, Stamp with { Revision = 2 }, snapshot, before);
        Assert.IsTrue(Vector3.Dot(Pose(after, 1).Position - Pose(before, 1).Position, forward) > 0);
        var change = Wheel(after, 1).RotationRadians - Wheel(before, 1).RotationRadians;
        Assert.IsTrue(Math.Atan2(Math.Sin(change), Math.Cos(change)) > 0, "Forward along the vehicle must advance its local wheel rotation.");
    }

    [TestMethod]
    [DataRow(0.0, 1)]
    [DataRow(0.0, 3)]
    [DataRow(2.0, 1)]
    [DataRow(2.0, 3)]
    public async Task CurvedOffsetConsistUsesMemberLengthsOnItsOwnTrack(double relief, int track)
    {
        var rules = RailwayFixtures.Rules with { MinimumCurveRadiusMeters = 100 };
        var content = RailwayFixtures.Content(rules: rules, design: RailwayFixtures.Vehicle(SizeClass.Large, 64), wheelsetMarkers: true);
        var train = RailwayFixtures.Train(190, [new(1), new(2), new(3)], track: track);
        var instances = Enumerable.Range(1, 3).Select(id => RailwayFixtures.Instance((ulong)id, SizeClass.Large)).ToImmutableArray();
        var snapshot = RailwayFixtures.Snapshot(600, 300, relief, 3, content: content, trains: [train], instances: instances);
        await using var vehicles = new SceneVehicles();
        await using var scene = RailwayFixtures.Controlled(snapshot, vehicles);
        var frame = await RailwayFixtures.Settled(scene, snapshot, Target, Request, Stamp);
        var path = new RailwayPath(snapshot, RailwayFixtures.Edge);
        var offset = (track - 2) * 34.0;
        var errorBound = RailwayFixtures.TrackGradeErrorBound(64, relief, offset, rules);
        for (ulong member = 1; member < 3; member++)
        {
            var first = path.Locate(RailwayCoordinates.Add(frame.Origin, Pose(frame, member).Position));
            var last = path.Locate(RailwayCoordinates.Add(frame.Origin, Pose(frame, member + 1).Position));
            var measured = RailwayFixtures.MeasuredTrackLength(path, Math.Min(first, last), Math.Max(first, last), offset);
            // Public poses use float; double arc precision is checked independently in RailwayPathTests.
            Assert.AreEqual(64, measured, errorBound + 1e-4);
        }
        var centerArc = path.Locate(RailwayCoordinates.Add(frame.Origin, Pose(frame, 2).Position));
        Assert.AreEqual(380, centerArc, 1e-4, "The core distance still locates the consist center on the centerline.");
        foreach (var member in new ulong[] { 1, 2, 3 })
        {
            var arc = path.Locate(RailwayCoordinates.Add(frame.Origin, Pose(frame, member).Position));
            var wheel = Wheel(frame, member);
            Assert.AreEqual(Math.Sin(path.TrackArc(arc, offset) / wheel.RadiusCells), Math.Sin(wheel.RotationRadians), 1e-4);
            Assert.AreEqual(Math.Cos(path.TrackArc(arc, offset) / wheel.RadiusCells), Math.Cos(wheel.RotationRadians), 1e-4);
        }
    }

    [TestMethod]
    [DataRow(LoadingAlignment.AlongX)]
    [DataRow(LoadingAlignment.Diagonal)]
    [DataRow(LoadingAlignment.Curve)]
    [DataRow(LoadingAlignment.StartOverhang)]
    [DataRow(LoadingAlignment.EndOverhang)]
    public async Task LoadingVehicleFogCoversRotatedAndCurvedBody(LoadingAlignment alignment)
    {
        var vehicle = RailwayFixtures.Vehicle(SizeClass.Small, 64, [new(0, 0, -32), new(0, 0, 31)]);
        vehicle = vehicle with { Scene = vehicle.Scene with { OccupiedBounds = new(new(0, 0, -32), new(0, 0, 31)) } };
        var content = RailwayFixtures.Content(design: vehicle);
        using var gated = new SceneFixtures.BlockedAppearanceView(content);
        var distance = alignment switch
        {
            LoadingAlignment.Curve => 100,
            LoadingAlignment.StartOverhang => 0,
            LoadingAlignment.EndOverhang => 600,
            _ => 190
        };
        SceneWorldSnapshot Snapshot(bool train) => RailwayFixtures.Snapshot(600, alignment == LoadingAlignment.Curve ? 300 : 600, 0,
            tracks: alignment == LoadingAlignment.Curve ? 3 : 1, content: gated,
            trains: train ? [RailwayFixtures.Train(distance, track: alignment == LoadingAlignment.Curve ? 2 : 1)] : [], instances: train ? [RailwayFixtures.Instance()] : []);
        SceneWorldSnapshot Orient(SceneWorldSnapshot snapshot) => alignment switch
        {
            LoadingAlignment.AlongX => RailwayFixtures.ReplaceCore(snapshot, snapshot.Core.World.Nodes[1] with { Position = new(600, 0) }),
            LoadingAlignment.Diagonal => RailwayFixtures.ReplaceCore(snapshot, snapshot.Core.World.Nodes[1] with { Position = new(360, 480) }),
            _ => snapshot
        };
        var initial = Orient(Snapshot(false));
        await using var vehicles = new SceneVehicles();
        var scene = RailwayFixtures.Controlled(initial, vehicles);
        try
        {
            await RailwayFixtures.Settled(scene, initial, new WorldSceneTarget.Location(new WorldLocation.Edge(RailwayFixtures.Edge, distance)), Request, Stamp);
            var snapshot = Orient(Snapshot(true));
            scene.Present(snapshot, null, Target, Request, Stamp with { Revision = 2 }, null);
            await gated.Entered.Task;
            var frame = scene.Present(snapshot, null, Target, Request, Stamp with { Revision = 3 }, null);
            var pose = Pose(frame, 1);
            var front = pose.Position + Vector3.Transform(new Vector3(.5f, .5f, alignment == LoadingAlignment.StartOverhang ? -30 : 30), pose.Rotation);
            Assert.IsInstanceOfType<ScenePick.NotReady>(frame.Pick(new(front + Vector3.UnitY * 40, -Vector3.UnitY, 80)));
            Assert.IsTrue(frame.Update.Environment.FoggedRegions.Any(bounds => front.X >= bounds.Minimum.X && front.X <= bounds.Maximum.X &&
                front.Y >= bounds.Minimum.Y && front.Y <= bounds.Maximum.Y && front.Z >= bounds.Minimum.Z && front.Z <= bounds.Maximum.Z));
        }
        finally { gated.Release.Set(); await scene.DisposeAsync(); }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task MissingOrFailedMemberRetainsContentSourceWithoutMarkingValidMemberMissing(bool failed)
    {
        var hash = new DesignHash("unavailable-vehicle");
        var reference = new ContentReference.Design(hash);
        var package = new PackageId("vehicle-package");
        var diagnostic = new ContentDiagnostic("original_content_problem", ContentDiagnosticSeverity.Error, "lookup", reference,
            new(package, "vehicles.json"), "design", null, "Unavailable vehicle design.");
        var lookup = failed ? (ContentLookup<DesignEvaluation>)new ContentLookup<DesignEvaluation>.Found(new DesignEvaluation.Failed([diagnostic])) :
            new ContentLookup<DesignEvaluation>.Missing([diagnostic]);
        var content = RailwayFixtures.Content(design: RailwayFixtures.Vehicle(),
            designs: new Dictionary<DesignHash, ContentLookup<DesignEvaluation>> { [hash] = lookup });
        var snapshot = RailwayFixtures.Snapshot(100, 100, 0, content: content,
            trains: [RailwayFixtures.Train(50, [new(1), new(2)])], instances: [RailwayFixtures.Instance(), RailwayFixtures.Instance(2, design: hash)]);
        await using var vehicles = new SceneVehicles();
        await using var scene = RailwayFixtures.Controlled(snapshot, vehicles);
        var frame = await RailwayFixtures.Settled(scene, snapshot, Target, Request, Stamp);
        Assert.IsTrue(frame.Diagnostics.Any(problem => problem.Code == diagnostic.Code && problem.Content == reference && problem.Package == package));
        Assert.IsTrue(frame.Diagnostics.Any(problem => problem.Code == "content_missing" && problem.Content == reference && problem.Package == package));
        Assert.IsFalse(frame.Diagnostics.Any(problem => problem.Code == "vehicle_length_unknown"));
        var valid = frame.Objects.Single(item => item.Parent is null && item.Subject == new WorldObjectId.Instance(new InstanceId.Vehicle(new(1))));
        Assert.IsTrue(valid.Visuals.IsEmpty);
        Assert.AreEqual(Pose(frame, 1), Pose(frame, 2));
        Assert.IsTrue(frame.Objects.Where(item => item.Subject == new WorldObjectId.Instance(new InstanceId.Vehicle(new(2))))
            .SelectMany(item => item.Visuals).OfType<SceneVisual.Missing>().Any(item => item.Reference == reference));
    }

    [TestMethod]
    public async Task UnregisteredVehicleDesignProducesMissingWithoutFaultingTheSession()
    {
        var content = RailwayFixtures.Content(designs: new Dictionary<DesignHash, ContentLookup<DesignEvaluation>>
        { [SceneFixtures.Design] = new ContentLookup<DesignEvaluation>.NotFound() });
        var snapshot = RailwayFixtures.Snapshot(4, 4, 0, content: content,
            trains: [RailwayFixtures.Train(1)], instances: [RailwayFixtures.Instance()]);
        await using var session = (WorldSceneSession)((SceneResult<IWorldSceneSession>.Success)
            ScenesFactory.Create().OpenWorld(new(Guid.NewGuid()), snapshot)).Value;
        session.RequestView(Target);
        var frame = await RailwayFixtures.Settled(session, Request);
        Assert.IsTrue(frame.Diagnostics.Any(problem => problem.Code == "content_missing" &&
            problem.Content == new ContentReference.Design(SceneFixtures.Design)));
        Assert.IsFalse(frame.Diagnostics.Any(problem => problem.Code == "vehicle_length_unknown"));
        Assert.IsInstanceOfType<Lookup<ScenePose>.Found>(frame.FindPose(new WorldObjectId.Instance(new InstanceId.Vehicle(new(1)))));
    }

    [TestMethod]
    public async Task PrefetchCoversTheConfiguredTimeInMeters()
    {
        var train = RailwayFixtures.Train(512, speed: 40);
        var snapshot = RailwayFixtures.Snapshot(2000, 2000, 0, content: RailwayFixtures.Content(design: RailwayFixtures.Vehicle()),
            trains: [train], instances: [RailwayFixtures.Instance()]);
        var requested = new System.Collections.Concurrent.ConcurrentBag<int>();
        await using var vehicles = new SceneVehicles();
        await using var scene = RailwayFixtures.Controlled(snapshot, vehicles, (input, path, index, token) =>
        { requested.Add(index); return RailwayFixtures.EmptySegment(input, path, index, token); });
        await RailwayFixtures.Settled(scene, snapshot, Target, Request, Stamp);
        var segmentMeters = RailwayCoordinates.SegmentLengthCells / RailwayCoordinates.CellsPerMeter;
        var watched = (int)(((TrainPosition.Edge)train.Position).DistanceMeters / segmentMeters);
        var neededAhead = (int)Math.Ceiling(train.SpeedMetersPerSecond * RailwayCoordinates.PrefetchSeconds / segmentMeters);
        Assert.IsTrue(requested.Contains(watched + RailwayCoordinates.ViewRadiusSegments + neededAhead));
    }

    [TestMethod]
    public async Task NewTrackTrainReportsDeferredUpdatesAndNewTrainDoesNotInterpolateFromMissing()
    {
        var content = RailwayFixtures.Content(design: RailwayFixtures.Vehicle());
        var initial = RailwayFixtures.Snapshot(600, 600, 0, tracks: 1, trackLimit: 2, content: content,
            trains: [RailwayFixtures.Train(100)], instances: [RailwayFixtures.Instance()], missingTrains: [new(2)]);
        await using var vehicles = new SceneVehicles();
        await using var scene = RailwayFixtures.Controlled(initial, vehicles);
        var before = await RailwayFixtures.Settled(scene, initial, Target, Request, Stamp);
        var next = RailwayFixtures.Snapshot(600, 600, 0, tracks: 2, trackLimit: 2, steps: 1, content: content,
            trains: [RailwayFixtures.Train(102), RailwayFixtures.Train(200, [new(2)], 2, 2)],
            instances: [RailwayFixtures.Instance(), RailwayFixtures.Instance(2)]);
        var target = new WorldSceneTarget.Follow(new(2), new(2));
        var frame = await RailwayFixtures.Settled(scene, next, target, Request with { Interpolation = .25 }, Stamp with { Revision = 2 }, initial, before);
        var problem = frame.Diagnostics.Single(item => item.Code == "railway_update_pending");
        Assert.AreEqual(new WorldObjectId.Train(new(2)), problem.Subject);
        StringAssert.Contains(problem.Detail, "#345");
        var sameTrack = RailwayFixtures.Snapshot(600, 600, 0, tracks: 1, trackLimit: 2, steps: 1, content: content,
            trains: [RailwayFixtures.Train(102), RailwayFixtures.Train(200, [new(2)], 2)],
            instances: [RailwayFixtures.Instance(), RailwayFixtures.Instance(2)]);
        var visible = await RailwayFixtures.Settled(scene, sameTrack, target, Request with { Interpolation = .25 }, Stamp with { Revision = 3 }, initial, frame);
        Assert.AreEqual(400, Pose(visible, 2).Position.Z + visible.Origin.Z, 1e-4);
    }

    private static ScenePose Pose(SceneFrame frame, ulong vehicle) =>
        ((Lookup<ScenePose>.Found)frame.FindPose(new WorldObjectId.Instance(new InstanceId.Vehicle(new(vehicle))))).Value;
    private static SceneVisual.Wheelset Wheel(SceneFrame frame, ulong vehicle)
    {
        var root = frame.Objects.Single(item => item.Parent is null && item.Subject == new WorldObjectId.Instance(new InstanceId.Vehicle(new(vehicle))));
        return frame.Objects.Where(item => item.Parent == root.Id).SelectMany(item => item.Visuals).OfType<SceneVisual.Wheelset>().First();
    }
}
