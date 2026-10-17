using System.Collections.Immutable;
using System.Numerics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes.Tests;

[TestClass]
public sealed class ModelProjectionTests
{
    private static readonly ImmutableArray<MaterialCategory> Materials =
        [MaterialCategory.Glass, MaterialCategory.PaintedMetal, MaterialCategory.Concrete];
    private static VoxelAppearanceProjection Appearance => SceneFixtures.Cube with
    { Presentation = new VoxelPresentation.Mesh(new("model-revision"), Materials) };

    [TestMethod]
    public void ModelCellsPreserveAssetSurfaceOrderPaintAndWholeCellPickingInEveryOrientation()
    {
        var at = new GridPosition(-2, 3, 4);
        foreach (var orientation in Enum.GetValues<VoxelOrientation>())
        {
            var cell = SceneFixtures.Cell(at) with { LocalOrientation = orientation, Paint = new("#123456") };
            var keys = new Dictionary<string, SceneObjectId>();
            var result = DesignGeometry.Build(SceneFixtures.Content(appearance: Appearance),
                SceneFixtures.Evaluation(SceneFixtures.Projection([cell])), SceneFixtures.Subject,
                SceneFixtures.Identity, key => keys[key] = new((ulong)keys.Count + 1));
            var modelObject = result.Objects.Single(item => item.Visuals.OfType<SceneVisual.Model>().Any());
            var model = modelObject.Visuals.OfType<SceneVisual.Model>().Single();
            Assert.AreEqual(new AssetRevision("model-revision"), model.Asset);
            CollectionAssert.AreEqual(Materials.ToArray(), model.Materials.ToArray());
            Assert.AreEqual(cell.Paint, model.Paint);
            Assert.AreEqual(keys[""], modelObject.Parent);
            Assert.AreEqual(keys[":model:" + at], modelObject.Id);
            Assert.AreEqual(SceneFixtures.Subject, modelObject.Subject);
            var name = orientation.ToString();
            var right = Axis(name[..2]);
            var up = Axis(name[2..]);
            var forward = Vector3.Cross(right, up);
            var center = new Vector3(at.X + 0.5f, at.Y + 0.5f, at.Z + 0.5f);
            Near(center - up * 0.5f, modelObject.Pose.Position);
            Near(right, Vector3.Transform(Vector3.UnitX, modelObject.Pose.Rotation));
            Near(up, Vector3.Transform(Vector3.UnitY, modelObject.Pose.Rotation));
            Near(forward, Vector3.Transform(Vector3.UnitZ, modelObject.Pose.Rotation));
            foreach (var local in new[] { Vector3.Zero, new Vector3(0.25f, 0.75f, -0.25f) })
            {
                var objectPoint = modelObject.Pose.Position + right * local.X + up * local.Y + forward * local.Z;
                var expected = objectPoint.Y - at.Y;
                var plane = model.RelativeHeightPlane;
                Assert.AreEqual(expected, Vector4.Dot(new(local, 1), plane), 0.0001f);
            }
            var hit = (ScenePick.Hit)SceneFixtures.Frame(result).Pick(
                new(new(at.X - 2, at.Y + 0.9f, at.Z + 0.1f), Vector3.UnitX, 5));
            Assert.AreEqual(at, hit.Value.Cell);
            Assert.AreEqual(2d, hit.Value.DistanceCells);
        }
    }

    [TestMethod]
    public void InstanceSeedIsFixedDistinctAndSharedByNestedModelsRegardlessOfRootPose()
    {
        var cells = ImmutableArray.Create(SceneFixtures.Cell(new(0, -2, 0)),
            SceneFixtures.Cell(new(0, 5, 0), 2), SceneFixtures.Cell(new(0, 1, 0), 3, path: [0]));
        var projection = SceneFixtures.Projection(cells,
            [new([0], new(10, 2, 3), VoxelOrientation.PzPy, new NestedObjectDetails.Assembly(null))]);
        var content = SceneFixtures.Content(appearance: Appearance);
        var subjects = new WorldObjectId[] { new WorldObjectId.Instance(new InstanceId.Vehicle(new(1))),
            new WorldObjectId.Instance(new InstanceId.Vehicle(new(2))),
            new WorldObjectId.Instance(new InstanceId.Building(new(1))) };
        var seeds = new List<ulong>();
        foreach (var subject in subjects)
        {
            foreach (var pose in new[] { SceneFixtures.Identity,
                new ScenePose(new(100, -50, 2), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.7f)) })
            {
                var result = DesignGeometry.Build(content, SceneFixtures.Evaluation(projection), subject, pose, SceneFixtures.NextId());
                var models = result.Objects.SelectMany(item => item.Visuals).OfType<SceneVisual.Model>().ToArray();
                Assert.HasCount(3, models);
                Assert.IsTrue(models.All(model => model.Surface.WearEnabled));
                Assert.IsTrue(models.All(model => model.Surface.WearSeed == models[0].Surface.WearSeed));
                if (pose == SceneFixtures.Identity) seeds.Add(models[0].Surface.WearSeed);
                else Assert.AreEqual(seeds[^1], models[0].Surface.WearSeed);
                var bottom = models.Single(model => Math.Abs(model.RelativeHeightPlane.W) < 0.0001f &&
                    Math.Abs(model.RelativeHeightPlane.Y - 0.125f) < 0.0001f);
                Assert.AreEqual(0.125f, bottom.RelativeHeightPlane.Y, 0.0001f);
            }
        }
        Assert.AreEqual(16254225818235762758UL, seeds[0]);
        Assert.AreEqual(3, seeds.Distinct().Count());
        using var preview = ((SceneResult<IDesignPreview>.Success)ScenesFactory.Create().PreviewDesign(content,
            SceneFixtures.Evaluation(projection))).Value;
        var frame = ((SceneResult<ISceneFrame>.Success)preview.Present()).Value;
        var previewModels = frame.Update.Upserts.SelectMany(item => item.Visuals).OfType<SceneVisual.Model>().ToArray();
        Assert.HasCount(3, previewModels);
        Assert.IsTrue(previewModels.All(model => model.Surface.WearSeed == 0));
        using var editor = EditingFixtures.Open(EditingFixtures.Draft(new GridPosition(0, 0, 0)), content: content);
        var editModels = EditingFixtures.Frame(editor).Update.Upserts.SelectMany(item => item.Visuals)
            .OfType<SceneVisual.Model>().ToArray();
        Assert.HasCount(1, editModels);
        Assert.AreEqual(0UL, editModels[0].Surface.WearSeed);
    }

    [TestMethod]
    public async Task VehicleGeometrySeedSurvivesRebindingAndSeparatesInstances()
    {
        var content = RailwayFixtures.Content(design: RailwayFixtures.Vehicle());
        var cache = new SceneVehicles.Cache();
        var seeds = new List<ulong>();
        await using (var vehicles = new SceneVehicles(cache))
            foreach (var id in new ulong[] { 1, 2 })
            {
                var instance = RailwayFixtures.Instance(id);
                Assert.IsNull(vehicles.Reconcile(content, instance, SceneFixtures.NextId()));
                await SceneFixtures.AwaitPendingWork(vehicles.PendingWork);
                var ready = vehicles.Reconcile(content, instance, SceneFixtures.NextId())!;
                var surfaces = ready.Objects.SelectMany(item => item.Visuals).OfType<SceneVisual.Geometry>()
                    .Select(visual => visual.Surface).ToArray();
                Assert.IsTrue(surfaces.Length > 0 && surfaces.All(surface => surface.WearEnabled));
                Assert.AreEqual(1, surfaces.Select(surface => surface.WearSeed).Distinct().Count());
                seeds.Add(surfaces[0].WearSeed);
            }
        Assert.AreEqual(16254225818235762758UL, seeds[0]);
        Assert.AreNotEqual(seeds[0], seeds[1]);
        await using var rebound = new SceneVehicles(cache, (_, _) => throw new AssertFailedException("The stable body must be reused."));
        var restoredContent = RailwayFixtures.Content(design: RailwayFixtures.Vehicle(), generation: 2);
        var restored = RailwayFixtures.Instance(1);
        Assert.IsNull(rebound.Reconcile(restoredContent, restored, SceneFixtures.NextId()));
        await SceneFixtures.AwaitPendingWork(rebound.PendingWork);
        var reused = rebound.Reconcile(restoredContent, restored, SceneFixtures.NextId())!;
        Assert.IsTrue(reused.Objects.SelectMany(item => item.Visuals).OfType<SceneVisual.Geometry>()
            .All(visual => visual.Surface.WearSeed == seeds[0]));
    }

    [TestMethod]
    public void RailwayModelPlacementSeedsIgnoreRenderOriginAndRemainDisabledOnTerrain()
    {
        var position = new ScenePoint(123, -4, 567);
        var seed = DesignGeometry.PlacementWearSeed(new(7), position);
        var evaluation = SceneFixtures.Evaluation(SceneFixtures.Projection([SceneFixtures.Cell(new(0, 0, 0))]));
        foreach (var pose in new[] { SceneFixtures.Identity, new ScenePose(new(-100, 0, 32), Quaternion.Identity) })
        {
            var geometry = DesignGeometry.Build(SceneFixtures.Content(appearance: Appearance), evaluation,
                null, pose, SceneFixtures.NextId(), wearSeed: seed);
            Assert.IsTrue(geometry.Objects.SelectMany(item => item.Visuals).OfType<SceneVisual.Model>()
                .All(model => model.Surface.WearSeed == seed));
        }
        Assert.AreEqual(seed, DesignGeometry.PlacementWearSeed(new(7), new(123, -4, 567)));
        Assert.AreNotEqual(seed, DesignGeometry.PlacementWearSeed(new(8), position));
        var grid = new VoxelGrid([new(new(0, 0, 0), new(VoxelOrientation.PxPy, Appearance, null, null, null, []))]);
        var terrainModel = DesignGeometry.Models(grid, new(1), null, _ => new(2), false).Single();
        Assert.IsFalse(((SceneVisual.Model)terrainModel.Visuals.Single()).Surface.WearEnabled);
    }

    [TestMethod]
    public void EditingPreservesUnchangedModelIdentityAcrossPresentCellEditAndUndo()
    {
        using var editor = EditingFixtures.Open(EditingFixtures.Draft(new GridPosition(0, 0, 0)),
            content: SceneFixtures.Content(appearance: Appearance));
        var initial = EditingFixtures.Frame(editor).Update.Upserts
            .Single(item => item.Visuals.OfType<SceneVisual.Model>().Any());
        AssertUnchanged(EditingFixtures.Frame(editor));
        EditingFixtures.Apply(editor, EditingFixtures.Add(new(2, 0, 0)));
        var changed = EditingFixtures.Frame(editor);
        Assert.HasCount(2, changed.Update.Upserts.SelectMany(item => item.Visuals).OfType<SceneVisual.Model>().ToArray());
        AssertUnchanged(changed);
        EditingFixtures.Value(editor.Undo());
        var undone = EditingFixtures.Frame(editor);
        Assert.HasCount(1, undone.Update.Upserts.SelectMany(item => item.Visuals).OfType<SceneVisual.Model>().ToArray());
        AssertUnchanged(undone);

        void AssertUnchanged(ISceneFrame frame)
        {
            var model = frame.Update.Upserts.Single(item => item.Pose == initial.Pose &&
                item.Visuals.OfType<SceneVisual.Model>().Any());
            Assert.AreEqual(initial.Id, model.Id);
            Assert.AreEqual(initial.Parent, model.Parent);
            Assert.AreEqual(0UL, model.Visuals.OfType<SceneVisual.Model>().Single().Surface.WearSeed);
        }
    }

    [TestMethod]
    public void NodeChunkProjectsTemplateModelWithOrderedMaterialsAndTerrainWearPolicy()
    {
        var at = new GridPosition(240, 3, 240);
        var template = NodeFixtures.Layout;
        template = template with
        {
            Plots = [],
            SceneModel = template.SceneModel with
            { Scene = SceneFixtures.Projection([SceneFixtures.Cell(at) with { Paint = new("#123456") }]) }
        };
        var terrain = RailwayFixtures.TerrainAppearance with
        {
            SurfaceVoxel = NodeFixtures.Concrete,
            SubsurfaceVoxel = NodeFixtures.Concrete,
            RockVoxel = NodeFixtures.Concrete
        };
        var content = NodeFixtures.Content(voxel: Appearance, layout: template, terrain: terrain);
        var snapshot = NodeFixtures.Snapshot(platforms: 0, trains: [], content: content);
        var layout = NodeLayout.Build(snapshot, NodeFixtures.NodeId);
        var prepared = NodeScene.Prepare(snapshot, layout, null, CancellationToken.None);
        var part = prepared.BuildChunk(new(224, 224));
        var objects = part.Geometry.Geometry.Objects;
        var child = objects.Single(item => item.Visuals.OfType<SceneVisual.Model>().Any());
        var parent = objects.Single(item => item.Id == child.Parent);
        Assert.IsNull(parent.Parent);
        var model = child.Visuals.OfType<SceneVisual.Model>().Single();
        Assert.AreEqual(new AssetRevision("model-revision"), model.Asset);
        CollectionAssert.AreEqual(Materials.ToArray(), model.Materials.ToArray());
        Assert.AreEqual(new PaintColor("#123456"), model.Paint);
        Assert.IsFalse(model.Surface.WearEnabled);
        Near(new Vector3(240.5f, 3, 240.5f), child.Pose.Position);
    }

    private static Vector3 Axis(string axis) => axis switch
    {
        "Px" => Vector3.UnitX, "Nx" => -Vector3.UnitX, "Py" => Vector3.UnitY,
        "Ny" => -Vector3.UnitY, "Pz" => Vector3.UnitZ, "Nz" => -Vector3.UnitZ,
        _ => throw new ArgumentException(nameof(axis))
    };
    private static void Near(Vector3 expected, Vector3 actual) =>
        Assert.IsTrue(Vector3.Distance(expected, actual) < 0.0001f, $"Expected {expected}, got {actual}.");
}

