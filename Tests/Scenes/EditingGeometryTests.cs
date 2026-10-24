using System.Collections.Immutable;
using System.Numerics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Scenes.Contracts;
using Train.Scenes.Editing;

namespace Train.Scenes.Tests;

[TestClass]
public sealed class EditingGeometryTests
{
    [TestMethod]
    public void LocalEditsMatchFullMeshesAndReuseDistantChunksWithoutChangingOldFrames()
    {
        var content = SceneFixtures.Content(appearance: SceneFixtures.Cube with
        {
            Presentation = ((VoxelPresentation.Cube)SceneFixtures.Cube.Presentation) with
            { Shapes = [new("cube"), new("wedge")] }
        });
        using var session = EditingFixtures.Open(EditingFixtures.Draft(
            new(-33, 0, 0), new(-32, 0, 0), new(-1, 0, 0), new(0, 0, 0), new(96, 0, 0)), content: content);
        var comparison = new FullComparison(session, content);
        var first = comparison.Check();
        var original = session.Read();
        var removedId = original.Elements.Single(element => element.At == new GridPosition(-32, 0, 0)).Id;
        var neighborId = original.Elements.Single(element => element.At == new GridPosition(-33, 0, 0)).Id;
        var removedRay = EditingFixtures.Ray(-31.5f);
        var neighborRay = EditingFixtures.Ray(-32.5f);
        Assert.AreNotEqual(removedId, neighborId);
        EditingHit(first, removedRay, original.Snapshot.Stamp, removedId, new(-32, 0, 0));
        EditingHit(first, neighborRay, original.Snapshot.Stamp, neighborId, new(-33, 0, 0));
        var distant = Meshes(first).Single(mesh => mesh.Vertices.All(vertex => vertex.Position.X >= 96));

        Edit(new(-32, 0, 0), new CellEdit.Remove());
        var deleted = comparison.Frame;
        Assert.IsInstanceOfType<ScenePick.Miss>(deleted.Pick(removedRay));
        EditingHit(deleted, neighborRay, session.Read().Snapshot.Stamp, neighborId, new(-33, 0, 0));
        EditingHit(first, removedRay, original.Snapshot.Stamp, removedId, new(-32, 0, 0));
        Assert.AreSame(distant, Meshes(comparison.Frame).Single(mesh => mesh.Vertices.All(vertex => vertex.Position.X >= 96)));
        EditingFixtures.Value(session.Undo());
        var undone = comparison.Check();
        var undoStamp = session.Read().Snapshot.Stamp;
        EditingHit(undone, removedRay, undoStamp, removedId, new(-32, 0, 0));
        EditingHit(first, removedRay, original.Snapshot.Stamp, removedId, new(-32, 0, 0));
        Assert.IsInstanceOfType<ScenePick.Miss>(deleted.Pick(removedRay));
        EditingFixtures.Value(session.Redo());
        comparison.Check();
        Assert.IsInstanceOfType<ScenePick.Miss>(comparison.Frame.Pick(removedRay));
        EditingHit(comparison.Frame, neighborRay, session.Read().Snapshot.Stamp, neighborId, new(-33, 0, 0));
        EditingHit(undone, removedRay, undoStamp, removedId, new(-32, 0, 0));
        EditingHit(first, removedRay, original.Snapshot.Stamp, removedId, new(-32, 0, 0));
        Edit(new(-32, 0, 0), new CellEdit.Add(EditingFixtures.Cube));
        var replacement = session.Read();
        var replacementId = replacement.Elements.Single(element => element.At == new GridPosition(-32, 0, 0)).Id;
        Assert.AreNotEqual(removedId, replacementId);
        EditingHit(comparison.Frame, removedRay, replacement.Snapshot.Stamp, replacementId, new(-32, 0, 0));
        Edit(new(-1, 0, 0), new CellEdit.Paint(new("#ff0000")));
        Edit(new(0, 0, 0), new CellEdit.Shape(new("wedge")));
        Edit(new(-33, 0, 0), new CellEdit.Remove());
        Edit(new(-33, 0, 0), new CellEdit.Add(EditingFixtures.Cube));

        var random = new Random(770);
        for (var i = 0; i < 6; i++)
            Edit(new(random.Next(-2, 2), 0, random.Next(1, 3)),
                new CellEdit.Add(EditingFixtures.Cube with
                { Paint = i % 2 == 0 ? new("#00ff00") : null, Orientation = (VoxelOrientation)random.Next(24) }));

        var beforeHeightChange = Meshes(comparison.Frame).Single(mesh => mesh.Vertices.All(vertex => vertex.Position.X >= 96));
        Edit(new(-32, 3, 0), new CellEdit.Add(EditingFixtures.Cube));
        Assert.AreNotSame(beforeHeightChange,
            Meshes(comparison.Frame).Single(mesh => mesh.Vertices.All(vertex => vertex.Position.X >= 96)));
        EditingFixtures.Value(session.Undo());
        comparison.Check();
        EditingFixtures.Value(session.Redo());
        comparison.Check();
        comparison.Check(0);
        comparison.Check();
        EditingHit(first, removedRay, original.Snapshot.Stamp, removedId, new(-32, 0, 0));
        EditingHit(undone, removedRay, undoStamp, removedId, new(-32, 0, 0));
        Assert.IsInstanceOfType<ScenePick.Miss>(deleted.Pick(removedRay));

        void Edit(GridPosition at, CellEdit action)
        {
            EditingFixtures.Apply(session, new EditOperation.Cells(new EditFootprint.Cell(at), action));
            comparison.Check();
        }
    }

    [TestMethod]
    public void NestedCopiesCompositesAndEnteredClippingMatchFullFramesThroughHistory()
    {
        var definition = new CompositeDefinitionId("test:geometry-composite");
        var modelDefinition = new VoxelDefinitionId("test:geometry-model");
        var good = new GoodId.Composite(definition);
        var hash = new DesignHash("geometry-composite");
        var composite = EditingFixtures.Draft(new(0, 0, 0), new(1, 0, 0)) with
        { Specification = new DesignSpecification.FunctionalComposite([], null) };
        var content = new FixedContentView
        {
            Generation = new(1), Core = SceneFixtures.Content().Core,
            VoxelAppearances = new Dictionary<VoxelDefinitionId, ContentLookup<VoxelAppearanceProjection>>
            {
                [SceneFixtures.Voxel] = new ContentLookup<VoxelAppearanceProjection>.Found(SceneFixtures.Cube),
                [modelDefinition] = new ContentLookup<VoxelAppearanceProjection>.Found(SceneFixtures.Cube with
                {
                    Definition = modelDefinition,
                    Presentation = new VoxelPresentation.Mesh(new("geometry-model"), [MaterialCategory.PaintedMetal])
                })
            },
            Goods = new Dictionary<GoodId, ContentLookup<GoodPresentation>>
            {
                [good] = new ContentLookup<GoodPresentation>.Found(new(good, new(new("Composite", ""), null, null, false), hash, false)),
                [new GoodId.Composite(new("test:missing"))] = new ContentLookup<GoodPresentation>.Missing([])
            },
            Designs = new Dictionary<DesignHash, ContentLookup<DesignEvaluation>>
            {
                [hash] = new ContentLookup<DesignEvaluation>.Found(EditingFixtures.Evaluation(composite) with
                { Scene = SceneFixtures.Projection([SceneFixtures.Cell(new(0, 0, 0)), SceneFixtures.Cell(new(1, 0, 0), 2)]) })
            }
        };
        var inner = EditingFixtures.Draft(new(-1, 0, 0), new(0, 0, 0)) with
        { Specification = new DesignSpecification.Assembly(null) };
        var outer = EditingFixtures.Draft(new GridPosition(0, 2, 0)) with
        {
            Specification = new DesignSpecification.Assembly(null),
            Layout = new([EditingFixtures.Cube], [new(new(0, 2, 0), 0)],
                [new NestedDesignItem.AssemblyCopy(new(0, 0, 3), VoxelOrientation.PzPy, inner)])
        };
        var draft = EditingFixtures.Draft(new GridPosition(-32, 0, 0)) with
        {
            Layout = new([EditingFixtures.Cube, new(modelDefinition, VoxelOrientation.PxPy, null, null)],
                [new(new(-32, 0, 0), 0), new(new(-64, 0, 0), 1)],
                [new NestedDesignItem.AssemblyCopy(new(8, 1, 0), VoxelOrientation.PzPy, outer),
                 new NestedDesignItem.FunctionalCompositeReference(new(16, 0, 0), VoxelOrientation.PxPy, definition, null),
                 new NestedDesignItem.FunctionalCompositeReference(new(24, 0, 0), VoxelOrientation.PxPy, new("test:missing"), null)])
        };
        using var session = EditingFixtures.Open(draft, content: content);
        var comparison = new FullComparison(session, content);
        var initialFrame = comparison.Check();
        var initialState = session.Read();
        var closed = initialState.Elements.Single(element => element.Kind == EditElementKind.FunctionalComposite && element.At.X == 16);
        Assert.IsFalse(initialFrame.Diagnostics.IsEmpty);
        CompositeHits(initialFrame, initialState.Snapshot.Stamp);
        Assert.AreEqual(new AssetRevision("geometry-model"), initialFrame.Objects.SelectMany(item => item.Visuals)
            .OfType<SceneVisual.Model>().Single().Asset);
        var model = initialState.Elements.Single(element => element.Kind == EditElementKind.Cell && element.At.X == -64);
        EditingHit(initialFrame, EditingFixtures.Ray(-63.5f), initialState.Snapshot.Stamp, model.Id, new(-64, 0, 0));
        comparison.Check(0);
        comparison.Check();
        var assembly = session.Read().Elements.First(element => element.Kind == EditElementKind.Assembly);
        EditingFixtures.Value(session.Enter(assembly.Id));
        comparison.Check();
        EditingFixtures.Apply(session, EditingFixtures.Add(new(-32, 0, 0)));
        comparison.Check();
        comparison.Check(0);
        EditingFixtures.Value(session.Undo());
        comparison.Check();
        EditingFixtures.Value(session.Redo());
        comparison.Check();
        var nested = session.Read().Elements.Single(element => element.Parent == assembly.Id && element.Kind == EditElementKind.Assembly);
        EditingFixtures.Value(session.Enter(nested.Id));
        comparison.Check();
        EditingFixtures.Apply(session, new EditOperation.Cells(new EditFootprint.Cell(new(-1, 0, 0)), new CellEdit.Paint(new("#ff0000"))));
        comparison.Check();
        EditingFixtures.Value(session.Enter(null));
        comparison.Check();
        EditingFixtures.Value(session.Select(new EditSelection.Elements([closed.Id])));
        EditingFixtures.Apply(session, new EditOperation.PaintSelection(new("#00ff00")));
        var painted = comparison.Check();
        var paintedStamp = session.Read().Snapshot.Stamp;
        CompositeHits(painted, paintedStamp);
        EditingFixtures.Value(session.Undo());
        var undone = comparison.Check();
        var undoStamp = session.Read().Snapshot.Stamp;
        CompositeHits(undone, undoStamp);
        EditingFixtures.Value(session.Redo());
        comparison.Check();
        CompositeHits(comparison.Frame, session.Read().Snapshot.Stamp);
        CompositeHits(initialFrame, initialState.Snapshot.Stamp);
        CompositeHits(painted, paintedStamp);
        CompositeHits(undone, undoStamp);

        void CompositeHits(ISceneFrame frame, EditStamp stamp)
        {
            for (var x = 0; x < 2; x++)
                EditingHit(frame, EditingFixtures.Ray(16.5f + x), stamp, closed.Id, new(x, 0, 0));
        }
    }

    private static void EditingHit(ISceneFrame frame, SceneRay ray, EditStamp stamp, EditElementId element, GridPosition cell)
    {
        var picked = frame.Pick(ray);
        Assert.IsInstanceOfType<ScenePick.Hit>(picked);
        var hit = ((ScenePick.Hit)picked).Value;
        Assert.AreEqual(frame.Update.Frame, hit.Frame);
        Assert.AreEqual(new PickSubject.Editing(stamp.Session, stamp.Revision, element), hit.Subject);
        Assert.AreEqual(cell, hit.Cell);
    }

    private static IEnumerable<SceneMesh> Meshes(SceneFrame frame) => frame.Objects
        .SelectMany(item => item.Visuals).OfType<SceneVisual.Geometry>().Select(visual => visual.Mesh);

    private sealed class FullComparison(IDesignEditSession session, IContentView content)
    {
        private readonly Dictionary<EditElementId, SceneObjectId> objectIds = [];
        private readonly Dictionary<(SceneObjectId, GridPosition), SceneObjectId> modelIds = [];
        private readonly Dictionary<SceneObjectId, SceneObject> displayed = [];
        private ulong nextObject;
        private SceneFrame? first, firstExpected;
        private SceneRay[] firstRays = [];
        internal SceneFrame Frame { get; private set; } = null!;

        internal SceneFrame Check(int? highestY = null)
        {
            var state = session.Read();
            var next = 0;
            var root = EditLayout.Import(state.Snapshot.Draft, () => state.Elements[next++].Id);
            var path = state.EnteredObject is { } entered
                ? state.Elements.Single(element => element.Id == entered).ObjectPath : ImmutableArray<int>.Empty;
            var cold = new EditGeometry().Build(EditLayout.At(root, path), new(content), session.Id,
                state.Snapshot.Stamp.Revision, highestY, ObjectId, ModelId, path, state.EnteredObject ?? default);
            // 全量网格入口不经过编辑器的区块缓存和区块拼接。
            var grids = cold.Picking.ToDictionary(value => value.Object, value => value.Grid);
            var objects = cold.Objects.Select(item => grids.TryGetValue(item.Id, out var grid)
                ? item with { Visuals = DesignGeometry.Meshes(grid, true) } : item).ToImmutableArray();
            Frame = (SceneFrame)EditingFixtures.Value(session.Present(EditingFixtures.View(highestY), Frame));
            var expected = new SceneFrame(Frame.Update.Frame, Frame.Origin, 0, SceneReadiness.Ready,
                objects, cold.Picking, [], cold.Diagnostics);
            if (Frame.Update.Base is null) displayed.Clear();
            foreach (var id in Frame.Update.Removed) displayed.Remove(id);
            foreach (var item in Frame.Update.Upserts) displayed[item.Id] = item;
            ObjectsEqual(expected.Objects, displayed.Values.OrderBy(item => item.Id.Value).ToImmutableArray());
            var rays = cold.Picking.SelectMany(value => value.Grid.Cells.Select(cell =>
            {
                var center = value.Pose.Position + Vector3.Transform(new Vector3(cell.At.X + .5f, cell.At.Y + .25f, cell.At.Z + .5f), value.Pose.Rotation);
                return new SceneRay(center - Vector3.UnitZ * 4, Vector3.UnitZ, 8);
            })).Append(new(new(1000, 1000, 1000), Vector3.UnitZ, 8)).ToArray();
            FramesEqual(expected, Frame, rays, state.Snapshot.Stamp.Revision);
            if (first is null) { first = Frame; firstExpected = expected; firstRays = rays; }
            FramesEqual(firstExpected!, first, firstRays, null);
            return Frame;
        }

        private SceneObjectId ObjectId(EditElementId element)
        {
            if (!objectIds.TryGetValue(element, out var id)) objectIds.Add(element, id = new(++nextObject));
            return id;
        }

        private SceneObjectId ModelId(SceneObjectId parent, GridPosition cell)
        {
            if (!modelIds.TryGetValue((parent, cell), out var id)) modelIds.Add((parent, cell), id = new(++nextObject));
            return id;
        }
    }

    private static void FramesEqual(SceneFrame expected, SceneFrame actual, SceneRay[] rays, EditRevision? revision)
    {
        ObjectsEqual(expected.Objects, actual.Objects);
        Assert.AreEqual(expected.Diagnostics.Length, actual.Diagnostics.Length);
        foreach (var (a, b) in expected.Diagnostics.Zip(actual.Diagnostics))
        {
            Assert.AreEqual((a.Code, a.Stage, a.Subject, a.Content, a.Package, a.Detail),
                (b.Code, b.Stage, b.Subject, b.Content, b.Package, b.Detail));
            Assert.IsTrue(EditLayout.Same(a.Element, b.Element));
        }
        foreach (var ray in rays)
        {
            var a = expected.Pick(ray);
            var b = actual.Pick(ray);
            Assert.AreEqual(a.GetType(), b.GetType());
            if (a is not ScenePick.Hit hit) continue;
            var picked = ((ScenePick.Hit)b).Value;
            CollectionAssert.AreEqual(hit.Value.NestedObjectPath.ToArray(), picked.NestedObjectPath.ToArray());
            Assert.AreEqual(hit.Value with { NestedObjectPath = picked.NestedObjectPath }, picked);
            if (revision is { } current) Assert.AreEqual(current, ((PickSubject.Editing)picked.Subject).Revision);
        }
    }

    private static void ObjectsEqual(ImmutableArray<SceneObject> expected, ImmutableArray<SceneObject> actual)
    {
        Assert.AreEqual(expected.Length, actual.Length);
        foreach (var (a, b) in expected.OrderBy(item => item.Id.Value).Zip(actual.OrderBy(item => item.Id.Value)))
        {
            Assert.AreEqual((a.Id, a.Parent, a.Pose, a.Subject), (b.Id, b.Parent, b.Pose, b.Subject));
            CollectionAssert.AreEqual(a.Lights.ToArray(), b.Lights.ToArray());
            CollectionAssert.AreEqual(a.Effects.ToArray(), b.Effects.ToArray());
            Assert.AreEqual(a.Visuals.Length, b.Visuals.Length);
            foreach (var (x, y) in a.Visuals.Zip(b.Visuals))
            {
                if (x is SceneVisual.Geometry geometry)
                {
                    Assert.IsInstanceOfType<SceneVisual.Geometry>(y);
                    var other = (SceneVisual.Geometry)y;
                    Assert.AreEqual(geometry.Surface, other.Surface);
                    Assert.AreEqual(geometry.Mesh.Material, other.Mesh.Material);
                    CollectionAssert.AreEqual(geometry.Mesh.Vertices.ToArray(), other.Mesh.Vertices.ToArray());
                    CollectionAssert.AreEqual(geometry.Mesh.TriangleIndices.ToArray(), other.Mesh.TriangleIndices.ToArray());
                }
                else Assert.AreEqual(x, y);
            }
        }
    }
}
