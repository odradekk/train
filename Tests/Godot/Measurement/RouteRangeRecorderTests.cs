using System.Collections.Immutable;
using System.Numerics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.App.Measurement;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Godot.Tests.Measurement;

[TestClass]
public sealed class RouteRangeRecorderTests
{
    [TestMethod]
    public void PresentedBranchesRestoreUncommittedAddsRemovalsAndChangesWhileDisplayLags()
    {
        var incremental = new RouteRangeRecorder();
        var oracle = new FullRouteRangeRecorder();
        var a = Object(Mesh(new SceneMaterial.BuiltIn(BuiltInMaterial.RailSteel)));
        var b = a with { Id = new(2) };
        var stamp = Frame().Update.Frame;
        var d = DeltaFrame(stamp, null, [a]);
        var display = incremental.Capture(d, null, () => [a]);
        var previousOverrides = ImmutableHashSet<ulong>.Empty;
        long id = 0;
        void Present(TestFrame frame, SceneObject[] expected, IEnumerable<ulong>? displayChanges = null)
        {
            var snapshot = incremental.CapturePresented(frame, display)!;
            var changed = previousOverrides.Concat(snapshot.Changed).Concat(displayChanges ?? []);
            var actual = incremental.Record(++id, snapshot, changed, frame.Update.Frame.Space, default);
            var full = oracle.Update(id, frame, frame.Update.Frame.Space, frame.Origin, default, expected);
            AssertSameChange(full, actual);
            previousOverrides = snapshot.Changed;
        }
        Present(DeltaFrame(stamp with { Revision = 2 }, stamp, [b]), [a, b]);
        Present(DeltaFrame(stamp with { Revision = 3 }, stamp, []), [a]);
        var moved = a with { Pose = a.Pose with { Position = new(100, 2, 3) } };
        var changedFrame = DeltaFrame(stamp with { Revision = 4 }, stamp, [moved]);
        Present(changedFrame, [moved]);
        Present(DeltaFrame(stamp with { Revision = 5 }, stamp, []), [a]);
        Present(DeltaFrame(stamp with { Revision = 6 }, stamp, [], [a.Id]), []);
        Present(DeltaFrame(stamp with { Revision = 7 }, stamp, []), [a]);
        // D 接受较早的 P，而不是最后一次观察到的 P。
        display = incremental.Capture(changedFrame, display, () => throw new AssertFailedException("Expected a displayed delta."));
        Present(DeltaFrame(stamp with { Revision = 8 }, changedFrame.Update.Frame, [b]), [moved, b], display.Changed);
    }

    [TestMethod]
    public void PresentedBaseMismatchRemainsUnknownAndRecoversFromTheRealDisplaySnapshot()
    {
        var recorder = new RouteRangeRecorder();
        var item = Object(Mesh(new SceneMaterial.BuiltIn(BuiltInMaterial.RailSteel)));
        var stamp = Frame().Update.Frame;
        var old = recorder.Capture(DeltaFrame(stamp, null, [item]), null, () => [item]);
        var actualStamp = stamp with { Revision = 4 };
        var presented = DeltaFrame(stamp with { Revision = 5 }, actualStamp, []);
        Assert.IsNull(recorder.CapturePresented(presented, old));
        var calls = 0;
        var actual = recorder.Capture(DeltaFrame(actualStamp, stamp with { Revision = 3 }, []), old,
            () => { calls++; return [item]; });
        Assert.AreEqual(1, calls, "A missing D base must read the real display snapshot.");
        var recovered = recorder.CapturePresented(presented, actual)!;
        var change = recorder.Record(1, recovered, null, stamp.Space, default)!;
        Assert.AreEqual(1, change.Rails.Length);
        Assert.AreEqual(item.Id.Value, change.Rails[0].Object);
    }

    [TestMethod]
    public void StableDisplayedAndPresentedDeltasNeverEnumerateTheFullGeometrySnapshot()
    {
        var recorder = new RouteRangeRecorder();
        var item = Object(Mesh(new SceneMaterial.BuiltIn(BuiltInMaterial.RailSteel)));
        var stamp = Frame().Update.Frame;
        var initial = recorder.Capture(DeltaFrame(stamp, null, [item]), null, () => [item]);
        recorder.Record(1, initial, null, stamp.Space, default);
        var stable = recorder.Capture(DeltaFrame(stamp with { Revision = 2 }, stamp, []), initial,
            () => throw new AssertFailedException("Stable D must not enumerate Display.Objects."));
        Assert.AreSame(initial.Objects, stable.Objects);
        Assert.AreEqual(0, stable.Changed.Count);
        var presented = recorder.CapturePresented(DeltaFrame(stamp with { Revision = 3 }, stable.Frame.Update.Frame, []), stable)!;
        Assert.AreSame(initial.Objects, presented.Objects);
        Assert.IsNull(recorder.Record(2, presented, presented.Changed, stamp.Space, default));
    }

    [TestMethod]
    public void IncrementalCoordinatesSpaceHandoffAndVisualKindsMatchFullSnapshots()
    {
        var recorder = new RouteRangeRecorder();
        var oracle = new FullRouteRangeRecorder();
        var item = Object(Mesh(new SceneMaterial.BuiltIn(BuiltInMaterial.RailSteel)));
        var stamp = Frame().Update.Frame;
        var d = DeltaFrame(stamp, null, [item], origin: new(100, 0, 0), fog: [new(new(0), new(4))]);
        var displayed = recorder.Capture(d, null, () => [item]);
        AssertSameChange(oracle.Update(1, d, stamp.Space, d.Origin, default, [item]),
            recorder.Record(1, displayed, null, stamp.Space, default));
        var shifted = DeltaFrame(stamp with { Revision = 2 }, stamp, [], origin: new(356, 0, 0), shift: new(-256, 0, 0),
            fog: [new(new(-256, 0, 0), new(-252, 4, 4))]);
        var next = recorder.CapturePresented(shifted, displayed)!;
        var shiftedItem = item with { Pose = item.Pose with { Position = item.Pose.Position + new Vector3(-256, 0, 0) } };
        AssertSameChange(oracle.Update(2, shifted, stamp.Space, shifted.Origin, default, [shiftedItem]),
            recorder.Record(2, next, next.Changed, stamp.Space, default));
        // 旧 D 仍在显示，但记录坐标已进入新空间，并存在真实显示根平移。
        var coordinates = new SceneSpaceId(2);
        var translation = new ScenePoint(7, 0, 0);
        AssertSameChange(oracle.Update(3, d, coordinates, new(356, 0, 0), new(-249, 0, 0), [item]),
            recorder.Record(3, displayed, null, coordinates, translation));
        var terrain = item with { Visuals = [new SceneVisual.Geometry(Mesh(new SceneMaterial.Voxel(new("stone"), MaterialCategory.StoneSoil, null)), Surface)] };
        var changed = DeltaFrame(stamp with { Revision = 3 }, stamp, [terrain], origin: d.Origin);
        var terrainState = recorder.CapturePresented(changed, displayed)!;
        AssertSameChange(oracle.Update(4, changed, coordinates, changed.Origin, default, [terrain]),
            recorder.Record(4, terrainState, terrainState.Changed, coordinates, default));
        var child = terrain with { Parent = new(99) };
        var removed = DeltaFrame(stamp with { Revision = 4 }, changed.Update.Frame, [child], origin: d.Origin);
        var removedState = recorder.CapturePresented(removed, terrainState)!;
        AssertSameChange(oracle.Update(5, removed, coordinates, removed.Origin, default, [child]),
            recorder.Record(5, removedState, removedState.Changed, coordinates, default));
        var newSpace = DeltaFrame(stamp with { Space = coordinates, Revision = 5 }, null, [item]);
        var reset = recorder.CapturePresented(newSpace, removedState)!;
        AssertSameChange(oracle.Update(6, newSpace, coordinates, default, default, [item]),
            recorder.Record(6, reset, null, coordinates, default));
        var moved = DeltaFrame(newSpace.Update.Frame with { Revision = 6 }, newSpace.Update.Frame, [], shift: new(7, 0, 0));
        var movedState = recorder.CapturePresented(moved, reset)!;
        var movedItem = item with { Pose = item.Pose with { Position = item.Pose.Position + new Vector3(7, 0, 0) } };
        AssertSameChange(oracle.Update(7, moved, coordinates, default, default, [movedItem]),
            recorder.Record(7, movedState, movedState.Changed, coordinates, default));
        var business = movedItem with { Subject = new WorldObjectId.Node(new(1)) };
        var businessFrame = DeltaFrame(newSpace.Update.Frame with { Revision = 7 }, moved.Update.Frame, [business]);
        var businessState = recorder.CapturePresented(businessFrame, movedState)!;
        AssertSameChange(oracle.Update(8, businessFrame, coordinates, default, default, [business]),
            recorder.Record(8, businessState, businessState.Changed, coordinates, default));
    }

    private static TestFrame DeltaFrame(SceneFrameStamp stamp, SceneFrameStamp? baseline,
        ImmutableArray<SceneObject> upserts, ImmutableArray<SceneObjectId> removed = default,
        ScenePoint origin = default, Vector3 shift = default, ImmutableArray<SceneBounds> fog = default) => new()
    {
        Origin = origin,
        Update = new(baseline, stamp, shift, upserts, removed.IsDefault ? [] : removed, new(fog.IsDefault ? [] : fog, []), [])
    };

    private static void AssertSameChange(RouteRangeChange? expected, RouteRangeChange? actual)
    {
        if (expected is null) { Assert.IsNull(actual); return; }
        Assert.IsNotNull(actual);
        Assert.AreEqual(expected.Id, actual.Id);
        Assert.AreEqual(expected.Base, actual.Base);
        Assert.AreEqual(expected.Frame, actual.Frame);
        Assert.AreEqual(expected.Coordinates, actual.Coordinates);
        CollectionAssert.AreEquivalent(expected.Terrain, actual.Terrain);
        CollectionAssert.AreEquivalent(expected.Rails, actual.Rails);
        CollectionAssert.AreEquivalent(expected.Removed, actual.Removed);
        if (expected.Fog is null) Assert.IsNull(actual.Fog);
        else CollectionAssert.AreEqual(expected.Fog, actual.Fog!);
    }

    [TestMethod]
    public void AddsRemovesAndChangesVisualKindWithoutRepeatingUnchangedRanges()
    {
        var recorder = new RouteRangeRecorder();
        var frame = Frame();
        var item = Object(Mesh(new SceneMaterial.Voxel(new("texture"), MaterialCategory.StoneSoil, null)));
        var first = UpdateFromFullSnapshot(recorder, 1, frame, new(1), default, default, [item])!;
        Assert.IsNull(first.Base);
        Assert.AreEqual(1, first.Terrain.Length);
        Assert.AreEqual(new ScenePoint(1, 2, 3), first.Terrain[0].Bounds.Minimum);
        Assert.IsNull(UpdateFromFullSnapshot(recorder, 2, frame, new(1), default, default, [item]));
        Assert.AreEqual(1L, recorder.Id);
        var rail = item with { Visuals = [new SceneVisual.Geometry(Mesh(new SceneMaterial.BuiltIn(BuiltInMaterial.RailSteel)), Surface)] };
        var changed = UpdateFromFullSnapshot(recorder, 2, frame, new(1), default, default, [rail])!;
        Assert.AreEqual(1L, changed.Base);
        Assert.AreEqual(1, changed.Rails.Length);
        CollectionAssert.AreEqual(new[] { new RouteRangeKey(1, 0) }, changed.Removed);
        Assert.IsNull(changed.Fog);
        var removed = UpdateFromFullSnapshot(recorder, 3, frame, new(1), default, default, [])!;
        CollectionAssert.AreEqual(new[] { new RouteRangeKey(1, 0) }, removed.Removed);
    }

    [TestMethod]
    public void OriginAndRootShiftCancellationAndSurfaceChangesDoNotWriteBounds()
    {
        var recorder = new RouteRangeRecorder();
        var item = Object(Mesh(new SceneMaterial.BuiltIn(BuiltInMaterial.RailSteel)));
        var frame = Frame(fog: [new(new(0, 0, 0), new(4, 4, 4))]);
        UpdateFromFullSnapshot(recorder, 1, frame, new(1), new(100, 0, 0), default, [item]);
        var surfaceChanged = item with { Visuals = [((SceneVisual.Geometry)item.Visuals[0]) with { Surface = Surface with { Damage = .5f } }] };
        Assert.IsNull(UpdateFromFullSnapshot(recorder, 2, frame, new(1), new(356, 0, 0), new(-256, 0, 0), [surfaceChanged]));
        Assert.AreEqual(1L, recorder.Id);
    }

    [TestMethod]
    public void IdentityChangesResetTheDeltaBaseButRevisionChangesDoNot()
    {
        var recorder = new RouteRangeRecorder();
        var stamp = Frame().Update.Frame;
        var item = Object(Mesh(new SceneMaterial.BuiltIn(BuiltInMaterial.RailSteel)));
        UpdateFromFullSnapshot(recorder, 1, Frame(stamp), new(1), default, default, [item]);
        Assert.IsNull(UpdateFromFullSnapshot(recorder, 2, Frame(stamp with { Revision = 2 }), new(1), default, default, [item]));
        foreach (var changed in new[] { stamp with { Session = new(Guid.NewGuid()) }, stamp with { Request = new(2) }, stamp with { Space = new(2) }, stamp })
        {
            var change = UpdateFromFullSnapshot(recorder, 2, Frame(changed), new(1), default, default, [item])!;
            Assert.IsNull(change.Base);
            Assert.AreEqual(1, change.Rails.Length);
        }
        Assert.IsNull(UpdateFromFullSnapshot(recorder, 3, Frame(stamp), new(2), default, default, [item])!.Base);
    }

    [TestMethod]
    public void IgnoresChildrenBusinessObjectsAndOtherBuiltInMaterialsAndUpdatesFogAlone()
    {
        var recorder = new RouteRangeRecorder();
        var item = Object(Mesh(new SceneMaterial.BuiltIn(BuiltInMaterial.Sleeper)));
        var geometry = Object(Mesh(new SceneMaterial.BuiltIn(BuiltInMaterial.RailSteel)));
        var change = UpdateFromFullSnapshot(recorder, 1, Frame(), new(1), default, default,
            [item, geometry with { Id = new(2), Parent = new(1) }, geometry with { Id = new(3), Subject = new WorldObjectId.Node(new(1)) }])!;
        Assert.AreEqual(0, change.Rails.Length);
        Assert.AreEqual(0, change.Terrain.Length);
        var fog = UpdateFromFullSnapshot(recorder, 2, Frame(fog: [new(new(-1), new(1))]), new(1), default, default, [])!;
        Assert.AreEqual(1L, fog.Base);
        Assert.AreEqual(1, fog.Fog!.Length);
        Assert.AreEqual(0, fog.Removed.Length);
    }

    [TestMethod]
    public void PoseChangesTransformAllCornersAndEquivalentReplacementMeshesDoNotWriteDeltas()
    {
        var recorder = new RouteRangeRecorder();
        var material = new SceneMaterial.BuiltIn(BuiltInMaterial.RailSteel);
        var item = Object(Mesh(material));
        UpdateFromFullSnapshot(recorder, 1, Frame(), new(1), default, default, [item]);
        var rotated = item with { Pose = item.Pose with { Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2) } };
        var change = UpdateFromFullSnapshot(recorder, 2, Frame(), new(1), new(1_000_000_000_000, 0, 0), default, [rotated])!;
        var bounds = change.Rails.Single().Bounds;
        Assert.AreEqual(1_000_000_000_001d, bounds.Minimum.X, .001);
        Assert.AreEqual(1d, bounds.Minimum.Z, .001);
        Assert.AreEqual(1_000_000_000_007d, bounds.Maximum.X, .001);
        Assert.AreEqual(3d, bounds.Maximum.Z, .001);
        var replacement = rotated with { Visuals = [new SceneVisual.Geometry(Mesh(material), Surface)] };
        Assert.IsNull(UpdateFromFullSnapshot(recorder, 3, Frame(), new(1), new(1_000_000_000_000, 0, 0), default, [replacement]));
    }

    // 适配既有完整输入测试到新路径；独立预期由 FullRouteRangeRecorder 提供。
    private static RouteRangeChange? UpdateFromFullSnapshot(RouteRangeRecorder recorder, long nextId, ISceneFrame frame,
        SceneSpaceId coordinates, ScenePoint origin, Vector3 shift, IEnumerable<SceneObject> objects)
    {
        var snapshot = recorder.Capture(frame, null, () => objects);
        var translation = new ScenePoint(origin.X + shift.X - frame.Origin.X,
            origin.Y + shift.Y - frame.Origin.Y, origin.Z + shift.Z - frame.Origin.Z);
        return recorder.Record(nextId, snapshot, null, coordinates, translation);
    }

    private static readonly SurfaceState Surface = new(false, 0, 0, false);
    private static SceneObject Object(SceneMesh mesh) => new(new(1), null, new(new(1, 2, 3), Quaternion.Identity), null,
        [new SceneVisual.Geometry(mesh, Surface)], [], []);
    private static SceneMesh Mesh(SceneMaterial material) => new(material,
        [new(Vector3.Zero, Vector3.UnitY, default, default, ExposedFaceEdges.None, 0, 0),
            new(new(2, 4, 6), Vector3.UnitY, default, default, ExposedFaceEdges.None, 0, 0)], []);
    private static TestFrame Frame(SceneFrameStamp? stamp = null, ImmutableArray<SceneBounds> fog = default) => new()
    {
        Update = new(null, stamp ?? new(new(Guid.Empty), new(1), new(1), 1), default, [], [], new(fog.IsDefault ? [] : fog, []), [])
    };
    private sealed class TestFrame : ISceneFrame
    {
        public required SceneUpdate Update { get; init; }
        public ScenePoint Origin { get; init; }
        public ScenePanBounds? NodePanBounds => null;
        public SceneNodeOverview? NodeOverview => null;
        public SceneReadiness Readiness => SceneReadiness.Ready;
        public double SimulationSeconds => 0;
        public ImmutableArray<SceneProblem> Diagnostics => [];
        public ImmutableArray<SceneDebugLine> DebugLines => [];
        public ScenePick Pick(SceneRay ray) => throw new NotSupportedException();
        public Lookup<ScenePose> FindPose(WorldObjectId subject) => throw new NotSupportedException();
        public Lookup<WorldLocation> Locate(Vector3 point) => throw new NotSupportedException();
        public Lookup<double> TerrainHeight(double x, double z) => throw new NotSupportedException();
        public double? DisplayTerrainHeight(double x, double z) => throw new NotSupportedException();
    }
}
