using System.Collections.Immutable;
using System.Numerics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes.Tests;

[TestClass]
public sealed class RailwayVehicleTests
{
    [TestMethod]
    public void WheelsetsUseDistinctVehicleRootRowsRegardlessOfCellOrientationOrNestedLocalPosition()
    {
        var evaluation = RailwayFixtures.Vehicle();
        var cells = ImmutableArray.Create(
            SceneFixtures.Cell(new(-2, 0, 2), 1),
            SceneFixtures.Cell(new(2, 0, 2), 2) with { Orientation = VoxelOrientation.PzPx, LocalOrientation = VoxelOrientation.PzPx },
            SceneFixtures.Cell(new(0, 0, 9), 3, path: [0]) with { LocalAt = new(0, 0, 1) });
        evaluation = evaluation with
        {
            Scene = SceneFixtures.Projection(cells,
            [new([0], new(0, 0, 8), VoxelOrientation.PxPy, new NestedObjectDetails.Assembly(null))])
        };
        var geometry = RailwayVehicles.Build(RailwayFixtures.Content(vehicle: evaluation, wheelsetMarkers: true), RailwayFixtures.Instance());
        var wheelsets = WheelObjects(geometry.Geometry.Geometry);
        Assert.AreEqual(2, wheelsets.Length);
        CollectionAssert.AreEqual(new[] { 2.5f, 9.5f }, wheelsets.Select(item => item.Pose.Position.Z).ToArray());
        var root = geometry.Geometry.Geometry.Objects.Single(item => item.Parent is null);
        foreach (var wheelset in wheelsets)
        {
            Assert.AreEqual(root.Id, wheelset.Parent);
            Assert.AreEqual(0f, wheelset.Pose.Position.X);
            Assert.AreEqual((float)((SceneVisual.Wheelset)wheelset.Visuals.Single()).RadiusCells, wheelset.Pose.Position.Y);
            AssertVector(Vector3.UnitX, Vector3.Transform(Vector3.UnitX, wheelset.Pose.Rotation));
        }
    }

    [TestMethod]
    public void DestroyedAndMissingMarkerCellsDoNotCreateWheelsets()
    {
        var evaluation = RailwayFixtures.Vehicle(cells: [new(0, 0, 0), new(0, 0, 3), new(0, 0, 6)]);
        evaluation = evaluation with
        {
            Scene = evaluation.Scene with
            {
                Voxels = evaluation.Scene.Voxels.SetItem(2, evaluation.Scene.Voxels[2] with { Missing = true })
            }
        };
        var instance = RailwayFixtures.Instance() with { Parts = [new(new(1), new(0, 100), false, true)] };
        var geometry = RailwayVehicles.Build(RailwayFixtures.Content(vehicle: evaluation, wheelsetMarkers: true), instance);
        var wheelsets = WheelObjects(geometry.Geometry.Geometry);
        Assert.AreEqual(1, wheelsets.Length);
        Assert.AreEqual(3.5f, wheelsets[0].Pose.Position.Z);
        var unmarked = RailwayVehicles.Build(RailwayFixtures.Content(vehicle: evaluation), instance);
        Assert.AreEqual(0, WheelObjects(unmarked.Geometry.Geometry).Length);
    }

    [TestMethod]
    public void DestroyedCompositeRemovesItsDescendantWheelsetsButKeepsRootMarkers()
    {
        var evaluation = RailwayFixtures.Vehicle();
        evaluation = evaluation with
        {
            Scene = SceneFixtures.Projection(
            [SceneFixtures.Cell(new(0, 0, 1), 1),
                SceneFixtures.Cell(new(0, 0, 5), 2, path: [0]) with { LocalAt = new(0, 0, 0) },
                SceneFixtures.Cell(new(0, 0, 8), 3, path: [0, 0]) with { LocalAt = new(0, 0, 0) }],
            [new([0], new(0, 0, 5), VoxelOrientation.PxPy,
                new NestedObjectDetails.FunctionalComposite(new("test:composite"), new(10), null)),
                new([0, 0], new(0, 0, 3), VoxelOrientation.PxPy, new NestedObjectDetails.Assembly(null))])
        };
        var instance = RailwayFixtures.Instance() with { Parts = [new(new(10), new(0, 100), false, true)] };
        var geometry = RailwayVehicles.Build(RailwayFixtures.Content(vehicle: evaluation, wheelsetMarkers: true), instance);
        var wheelsets = WheelObjects(geometry.Geometry.Geometry);
        Assert.AreEqual(1, wheelsets.Length);
        Assert.AreEqual(1.5f, wheelsets[0].Pose.Position.Z);
        Assert.AreEqual(1, geometry.Geometry.Geometry.Picking.Length);
    }

    [TestMethod]
    [DataRow(SizeClass.Small, 3.0)]
    [DataRow(SizeClass.Medium, 7.0)]
    [DataRow(SizeClass.Large, 7.0)]
    [DataRow(SizeClass.ExtraLarge, 15.0)]
    [DataRow(SizeClass.Titan, 15.0)]
    public void VehicleSizeDeterminesGaugeAndAxleRestsOnRailSurface(SizeClass size, double gauge)
    {
        var evaluation = RailwayFixtures.Vehicle(size, cells: [new(3, 1, 4)]);
        var geometry = RailwayVehicles.Build(RailwayFixtures.Content(vehicle: evaluation, wheelsetMarkers: true), RailwayFixtures.Instance(size: size));
        var axle = WheelObjects(geometry.Geometry.Geometry).Single();
        var visual = (SceneVisual.Wheelset)axle.Visuals.Single();
        Assert.AreEqual(gauge, visual.GaugeCells);
        Assert.IsTrue(visual.RadiusCells > 0);
        Assert.IsTrue(visual.WidthCells > 0);
        AssertVector(new(0, (float)visual.RadiusCells, 4.5f), axle.Pose.Position);
        Assert.AreEqual(Quaternion.Identity, axle.Pose.Rotation);
    }

    [TestMethod]
    public void AbsoluteWheelRotationSurvivesPauseReverseAndRebuildIncludingNegativeDistance()
    {
        var evaluation = RailwayFixtures.Vehicle(cells: [new(0, 0, 2)]);
        var content = RailwayFixtures.Content(vehicle: evaluation, wheelsetMarkers: true);
        var geometry = RailwayVehicles.Build(content, RailwayFixtures.Instance()).Geometry.Geometry;
        foreach (var distance in new[] { 1.0, 1, 4, 1, -1, 0 })
        {
            var placed = RailwayVehicles.Place(geometry, SceneFixtures.Identity, distance);
            var wheel = (SceneVisual.Wheelset)WheelObjects(placed).Single().Visuals.Single();
            Assert.IsTrue(wheel.RotationRadians >= 0 && wheel.RotationRadians < 2 * Math.PI);
            Assert.AreEqual(Math.Sin(distance / wheel.RadiusCells), Math.Sin(wheel.RotationRadians), 1e-12);
            Assert.AreEqual(Math.Cos(distance / wheel.RadiusCells), Math.Cos(wheel.RotationRadians), 1e-12);
            var reopened = RailwayVehicles.Place(RailwayVehicles.Build(content, RailwayFixtures.Instance()).Geometry.Geometry,
                SceneFixtures.Identity, distance);
            Assert.AreEqual(wheel, (SceneVisual.Wheelset)WheelObjects(reopened).Single().Visuals.Single());
        }
        Assert.AreEqual(0.0, ((SceneVisual.Wheelset)WheelObjects(geometry).Single().Visuals.Single()).RotationRadians);
    }

    [TestMethod]
    public void PlacementReusesBodyMeshesAndMovesRenderingAndPickingWithTheSamePose()
    {
        var evaluation = RailwayFixtures.Vehicle();
        evaluation = evaluation with
        {
            Scene = SceneFixtures.Projection(
            [SceneFixtures.Cell(new(0, 0, 0)),
                SceneFixtures.Cell(new(1, 2, 3), 2, path: [0]) with { LocalAt = new(0, 0, 0) }],
            [new([0], new(1, 2, 3), VoxelOrientation.PxPy, new NestedObjectDetails.Assembly(null))])
        };
        var geometry = RailwayVehicles.Build(RailwayFixtures.Content(vehicle: evaluation), RailwayFixtures.Instance()).Geometry.Geometry;
        var pose = new ScenePose(new(10, 20, 30), Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2));
        var placed = BuiltGeometry.Place(geometry, pose);
        var root = placed.Objects.Single(item => item.Parent is null);
        AssertVector(pose.Position, root.Pose.Position);
        AssertVector(Vector3.UnitX, Vector3.Transform(Vector3.UnitZ, root.Pose.Rotation));
        for (var i = 0; i < geometry.Objects.Length; i++)
        {
            var before = geometry.Objects[i].Visuals.OfType<SceneVisual.Geometry>().ToArray();
            var after = placed.Objects[i].Visuals.OfType<SceneVisual.Geometry>().ToArray();
            Assert.AreEqual(before.Length, after.Length);
            for (var j = 0; j < before.Length; j++) Assert.AreSame(before[j].Mesh, after[j].Mesh);
        }
        foreach (var pick in placed.Picking)
        {
            var child = placed.Objects.Single(item => item.Id == pick.Object);
            var expected = child.Parent is null ? new Vector3(10, 20, 30) : new Vector3(13, 22, 29);
            AssertVector(expected, pick.Pose.Position);
            if (child.Parent is not null) AssertVector(new(1, 2, 3), child.Pose.Position);
            AssertVector(Vector3.UnitX, Vector3.Transform(Vector3.UnitZ, pick.Pose.Rotation));
            Assert.AreSame(geometry.Picking.Single(item => item.Object == pick.Object).Grid, pick.Grid);
        }
        AssertVector(Vector3.Zero, geometry.Objects.Single(item => item.Parent is null).Pose.Position);
    }

    [TestMethod]
    public void GeneralPlacementPreservesWheelRotationAndChildLocalPose()
    {
        var content = RailwayFixtures.Content(vehicle: RailwayFixtures.Vehicle(cells: [new(0, 0, 2)]), wheelsetMarkers: true);
        var built = RailwayVehicles.Build(content, RailwayFixtures.Instance()).Geometry.Geometry;
        var rotated = RailwayVehicles.Place(built, SceneFixtures.Identity, 1);
        var before = WheelObjects(rotated).Single();
        var pose = new ScenePose(new(10, 20, 30), Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2));
        var placed = BuiltGeometry.Place(rotated, pose);
        var after = WheelObjects(placed).Single();
        Assert.AreEqual(before.Pose, after.Pose);
        Assert.AreSame(before.Visuals[0], after.Visuals[0]);
        AssertVector(pose.Position, placed.Objects.Single(item => item.Parent is null).Pose.Position);
    }

    [TestMethod]
    public void VehiclePoseUsesFloatingOriginAndKeepsLocalPositiveZAtFirstEnd()
    {
        var forward = Vector3.Normalize(new Vector3(3, 1, 4));
        var sample = new RailwaySample(new(150, 70, -20), forward, new(0.8f, 0, -0.6f));
        foreach (var positive in new[] { true, false })
        {
            var pose = RailwayVehicles.Pose(sample, new(100, 40, -30), positive);
            AssertVector(new(50, 30, 10), pose.Position);
            AssertVector(positive ? forward : -forward, Vector3.Transform(Vector3.UnitZ, pose.Rotation));
            Assert.AreEqual(0f, Vector3.Transform(Vector3.UnitX, pose.Rotation).Y, 1e-6f);
        }
    }

    [TestMethod]
    public void LengthRequiresKnownVehicleLengthAndDoesNotInventFallbackGeometry()
    {
        var complete = RailwayFixtures.Vehicle(lengthCells: 12);
        var known = new DesignEvaluation.Partial(complete.Definition,
            new(new ComputedValue<double>.Known(1), new ComputedValue<Money>.Known(new(1)),
                new ComputedValue<long>.Known(100), new ComputedValue<ImmutableArray<PartProjection>>.Known([]),
                new ComputedValue<ImmutableArray<GoodsQuantity>>.Known([]), new ComputedValue<int>.Known(17)),
            new(1, new(1), 100, [], []), complete.Core!.Design, complete.Scene, []);
        var unknown = known with { Metrics = known.Metrics with { VehicleLengthCells = new ComputedValue<int>.Unknown([]) } };
        Assert.AreEqual(12, RailwayVehicles.Length(complete));
        Assert.AreEqual(17, RailwayVehicles.Length(known));
        Assert.IsNull(RailwayVehicles.Length(unknown));
        Assert.IsNull(RailwayVehicles.Length(new DesignEvaluation.Failed([])));
        Assert.IsNull(RailwayVehicles.Length(complete with { Core = null }));
        Assert.IsNull(RailwayVehicles.Length(null));
    }

    private static SceneObject[] WheelObjects(RenderedDesign geometry) => geometry.Objects
        .Where(item => item.Visuals.Any(visual => visual is SceneVisual.Wheelset)).ToArray();
    private static void AssertVector(Vector3 expected, Vector3 actual) => Assert.IsTrue(Vector3.Distance(expected, actual) <= 1e-5,
        $"Expected {expected}, actual {actual}");
}
