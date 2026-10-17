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
    public void AddsRemovesAndChangesVisualKindWithoutRepeatingUnchangedRanges()
    {
        var recorder = new RouteRangeRecorder();
        var frame = Frame();
        var item = Object(Mesh(new SceneMaterial.Voxel(new("texture"), MaterialCategory.StoneSoil, null)));
        var first = recorder.Update(1, frame, new(1), default, default, [item])!;
        Assert.IsNull(first.Base);
        Assert.AreEqual(1, first.Terrain.Length);
        Assert.AreEqual(new ScenePoint(1, 2, 3), first.Terrain[0].Bounds.Minimum);
        Assert.IsNull(recorder.Update(2, frame, new(1), default, default, [item]));
        Assert.AreEqual(1L, recorder.Id);
        var rail = item with { Visuals = [new SceneVisual.Geometry(Mesh(new SceneMaterial.BuiltIn(BuiltInMaterial.RailSteel)), Surface)] };
        var changed = recorder.Update(2, frame, new(1), default, default, [rail])!;
        Assert.AreEqual(1L, changed.Base);
        Assert.AreEqual(1, changed.Rails.Length);
        CollectionAssert.AreEqual(new[] { new RouteRangeKey(1, 0) }, changed.Removed);
        Assert.IsNull(changed.Fog);
        var removed = recorder.Update(3, frame, new(1), default, default, [])!;
        CollectionAssert.AreEqual(new[] { new RouteRangeKey(1, 0) }, removed.Removed);
    }

    [TestMethod]
    public void OriginAndRootShiftCancellationAndSurfaceChangesDoNotWriteBounds()
    {
        var recorder = new RouteRangeRecorder();
        var item = Object(Mesh(new SceneMaterial.BuiltIn(BuiltInMaterial.RailSteel)));
        var frame = Frame(fog: [new(new(0, 0, 0), new(4, 4, 4))]);
        recorder.Update(1, frame, new(1), new(100, 0, 0), default, [item]);
        var surfaceChanged = item with { Visuals = [((SceneVisual.Geometry)item.Visuals[0]) with { Surface = Surface with { Damage = .5f } }] };
        Assert.IsNull(recorder.Update(2, frame, new(1), new(356, 0, 0), new(-256, 0, 0), [surfaceChanged]));
        Assert.AreEqual(1L, recorder.Id);
    }

    [TestMethod]
    public void IdentityChangesResetTheDeltaBaseButRevisionChangesDoNot()
    {
        var recorder = new RouteRangeRecorder();
        var stamp = Frame().Update.Frame;
        var item = Object(Mesh(new SceneMaterial.BuiltIn(BuiltInMaterial.RailSteel)));
        recorder.Update(1, Frame(stamp), new(1), default, default, [item]);
        Assert.IsNull(recorder.Update(2, Frame(stamp with { Revision = 2 }), new(1), default, default, [item]));
        foreach (var changed in new[] { stamp with { Session = new(Guid.NewGuid()) }, stamp with { Request = new(2) }, stamp with { Space = new(2) }, stamp })
        {
            var change = recorder.Update(2, Frame(changed), new(1), default, default, [item])!;
            Assert.IsNull(change.Base);
            Assert.AreEqual(1, change.Rails.Length);
        }
        Assert.IsNull(recorder.Update(3, Frame(stamp), new(2), default, default, [item])!.Base);
    }

    [TestMethod]
    public void IgnoresChildrenBusinessObjectsAndOtherBuiltInMaterialsAndUpdatesFogAlone()
    {
        var recorder = new RouteRangeRecorder();
        var item = Object(Mesh(new SceneMaterial.BuiltIn(BuiltInMaterial.Sleeper)));
        var geometry = Object(Mesh(new SceneMaterial.BuiltIn(BuiltInMaterial.RailSteel)));
        var change = recorder.Update(1, Frame(), new(1), default, default,
            [item, geometry with { Id = new(2), Parent = new(1) }, geometry with { Id = new(3), Subject = new WorldObjectId.Node(new(1)) }])!;
        Assert.AreEqual(0, change.Rails.Length);
        Assert.AreEqual(0, change.Terrain.Length);
        var fog = recorder.Update(2, Frame(fog: [new(new(-1), new(1))]), new(1), default, default, [])!;
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
        recorder.Update(1, Frame(), new(1), default, default, [item]);
        var rotated = item with { Pose = item.Pose with { Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2) } };
        var change = recorder.Update(2, Frame(), new(1), new(1_000_000_000_000, 0, 0), default, [rotated])!;
        var bounds = change.Rails.Single().Bounds;
        Assert.AreEqual(1_000_000_000_001d, bounds.Minimum.X, .001);
        Assert.AreEqual(1d, bounds.Minimum.Z, .001);
        Assert.AreEqual(1_000_000_000_007d, bounds.Maximum.X, .001);
        Assert.AreEqual(3d, bounds.Maximum.Z, .001);
        var replacement = rotated with { Visuals = [new SceneVisual.Geometry(Mesh(material), Surface)] };
        Assert.IsNull(recorder.Update(3, Frame(), new(1), new(1_000_000_000_000, 0, 0), default, [replacement]));
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
        public ScenePoint Origin => default;
        public ScenePanBounds? NodePanBounds => null;
        public SceneReadiness Readiness => SceneReadiness.Ready;
        public double SimulationSeconds => 0;
        public ImmutableArray<SceneProblem> Diagnostics => [];
        public ImmutableArray<SceneDebugLine> DebugLines => [];
        public ScenePick Pick(SceneRay ray) => throw new NotSupportedException();
        public Lookup<ScenePose> FindPose(WorldObjectId subject) => throw new NotSupportedException();
        public Lookup<WorldLocation> Locate(Vector3 point) => throw new NotSupportedException();
        public Lookup<double> TerrainHeight(double x, double z) => throw new NotSupportedException();
    }
}
