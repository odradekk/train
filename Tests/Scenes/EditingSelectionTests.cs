using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes.Tests;

[TestClass]
public sealed class EditingSelectionTests
{
    private static void SelectRoot(IDesignEditSession session) => EditingFixtures.Value(session.Select(new EditSelection.Elements(session.Read().Elements.Where(e => e.Parent is null).Select(e => e.Id).ToImmutableArray())));

    [TestMethod]
    [DataRow(GridAxis.X, VoxelOrientation.PxPz)]
    [DataRow(GridAxis.Y, VoxelOrientation.NzPy)]
    [DataRow(GridAxis.Z, VoxelOrientation.PyNx)]
    public void RotationUsesRightHandRuleAndPreservesIdentity(GridAxis axis, VoxelOrientation orientation)
    {
        using var session = EditingFixtures.Open(EditingFixtures.Draft(new GridPosition(1, 0, 0)));
        SelectRoot(session);
        var id = session.Read().Selection.Single();
        var state = EditingFixtures.Apply(session, new EditOperation.RotateSelection(axis, new(0, 0, 0), 1));
        // X rotation leaves the point on the X axis; the other cases turn it toward +Y or -Z.
        var expected = axis switch { GridAxis.X => new GridPosition(1, 0, 0), GridAxis.Y => new GridPosition(0, 0, -1), _ => new GridPosition(0, 1, 0) };
        Assert.AreEqual(expected, state.Snapshot.Draft.Layout.Cells.Single().At);
        Assert.AreEqual(orientation, state.Snapshot.Draft.Layout.Palette[state.Snapshot.Draft.Layout.Cells.Single().PaletteIndex].Orientation);
        Assert.AreEqual(id, state.Elements.Single().Id);
    }

    [TestMethod]
    public void RotationHighlightsDiscretePosesAndOnlyFinalPoseBlocksApply()
    {
        using var session = EditingFixtures.Open(EditingFixtures.Draft(new(1, 0, 0), new(0, 0, -1)));
        var selected = session.Read().Elements.Single(e => e.At == new GridPosition(1, 0, 0)).Id;
        EditingFixtures.Value(session.Select(new EditSelection.Elements([selected])));
        var operation = new EditOperation.RotateSelection(GridAxis.Y, new(0, 0, 0), 2);
        var preview = EditingFixtures.Preview(session, operation);
        CollectionAssert.AreEquivalent(new[] { new GridPosition(0, 0, -1), new(-1, 0, 0) }, preview.HighlightCells.ToArray());
        Assert.AreEqual(1, preview.CoveredCells);
        Assert.AreEqual(0, preview.SkippedCells);
        Assert.IsTrue(preview.CanApply);
        EditingFixtures.Value(session.Apply(operation));
        Assert.AreEqual(new GridPosition(-1, 0, 0), session.Read().Elements.Single(e => e.Id == selected).At);
        var blockedOperation = new EditOperation.RotateSelection(GridAxis.Y, new(0, 0, 0), 3);
        var blocked = EditingFixtures.Preview(session, blockedOperation);
        Assert.IsFalse(blocked.CanApply);
        Assert.AreEqual("occupancy_conflict", EditingFixtures.Code(session.Apply(blockedOperation)));
    }

    [TestMethod]
    public void RotationRejectsUndefinedAxisAsAnArgumentError()
    {
        using var session = EditingFixtures.Open(EditingFixtures.Draft(new GridPosition(1, 0, 0)));
        SelectRoot(session);
        Assert.ThrowsExactly<ArgumentException>(() => session.Preview(new EditOperation.RotateSelection((GridAxis)99, new(0, 0, 0), 1)));
        Assert.IsFalse(session.Read().CanUndo);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ShapeRejectsNullNameAsAnArgumentError(bool selection)
    {
        using var session = EditingFixtures.Open(EditingFixtures.Draft(new GridPosition(0, 0, 0)));
        SelectRoot(session);
        EditOperation operation = selection ? new EditOperation.ShapeSelection(default)
            : new EditOperation.Cells(new EditFootprint.Cell(new(0, 0, 0)), new CellEdit.Shape(default));
        Assert.ThrowsExactly<ArgumentNullException>(() => session.Preview(operation));
        Assert.IsFalse(session.Read().CanUndo);
    }

    [TestMethod]
    public void PivotAndThreeQuarterTurnsUseCellCentersAndInvalidTurnsAreRejected()
    {
        using var session = EditingFixtures.Open(EditingFixtures.Draft(new GridPosition(3, 2, 0)));
        SelectRoot(session);
        foreach (var turns in new[] { -1, 0, 4 })
            Assert.AreEqual("invalid_operation", EditingFixtures.Code(session.Preview(new EditOperation.RotateSelection(GridAxis.Z, new(2, 2, 0), turns))));
        var state = EditingFixtures.Apply(session, new EditOperation.RotateSelection(GridAxis.Z, new(2, 2, 0), 3));
        Assert.AreEqual(new GridPosition(2, 1, 0), state.Snapshot.Draft.Layout.Cells.Single().At);
    }

    [TestMethod]
    [DataRow(AllowedVoxelOrientations.None, true)]
    [DataRow(AllowedVoxelOrientations.FourWay, false)]
    public void RotationHonorsOrientationPolicy(AllowedVoxelOrientations allowed, bool canApply)
    {
        var content = new FixedContentView { Generation = new(1), Core = SceneFixtures.Content().Core,
            VoxelAppearances = new Dictionary<VoxelDefinitionId, ContentLookup<VoxelAppearanceProjection>> { [SceneFixtures.Voxel] = new ContentLookup<VoxelAppearanceProjection>.Found(SceneFixtures.Cube with { AllowedOrientations = allowed }) } };
        using var session = EditingFixtures.Open(EditingFixtures.Draft(new GridPosition(0, 1, 0)), content: content);
        SelectRoot(session);
        var operation = new EditOperation.RotateSelection(GridAxis.X, new(0, 0, 0), 1);
        var preview = EditingFixtures.Preview(session, operation);
        Assert.AreEqual(canApply, preview.CanApply);
        if (canApply)
        {
            var state = EditingFixtures.Value(session.Apply(operation));
            Assert.AreEqual(new GridPosition(0, 0, 1), state.Snapshot.Draft.Layout.Cells.Single().At);
            Assert.AreEqual(VoxelOrientation.PxPy, state.Snapshot.Draft.Layout.Palette.Single().Orientation);
        }
        else
        {
            Assert.AreEqual("invalid_operation", EditingFixtures.Code(session.Apply(operation)));
            Assert.IsFalse(session.Read().CanUndo);
        }
    }

    [TestMethod]
    public void ShapeSelectionChangesSupportedCellsAndSkipsOtherVoxelDefinitions()
    {
        var halfOnly = new VoxelDefinitionId("test:half-capable");
        var half = new VoxelShape("half");
        var appearance = SceneFixtures.Cube with { Definition = halfOnly,
            Presentation = ((VoxelPresentation.Cube)SceneFixtures.Cube.Presentation) with { Shapes = [new("cube"), half] } };
        var content = new FixedContentView { Generation = new(1), Core = SceneFixtures.Content().Core,
            VoxelAppearances = new Dictionary<VoxelDefinitionId, ContentLookup<VoxelAppearanceProjection>> {
                [SceneFixtures.Voxel] = new ContentLookup<VoxelAppearanceProjection>.Found(SceneFixtures.Cube),
                [halfOnly] = new ContentLookup<VoxelAppearanceProjection>.Found(appearance) } };
        var draft = EditingFixtures.Draft() with { Layout = new(
            [EditingFixtures.Cube with { Voxel = halfOnly }, EditingFixtures.Cube],
            [new(new(0, 0, 0), 0), new(new(1, 0, 0), 1)], []) };
        using var session = EditingFixtures.Open(draft, content: content);
        SelectRoot(session);
        var operation = new EditOperation.ShapeSelection(half);
        var preview = EditingFixtures.Preview(session, operation);
        Assert.IsTrue(preview.CanApply);
        Assert.AreEqual(2, preview.CoveredCells);
        Assert.AreEqual(1, preview.AffectedCells);
        Assert.AreEqual(1, preview.SkippedCells);
        var changed = EditingFixtures.Value(session.Apply(operation)).Snapshot.Draft;
        Assert.AreEqual(half, changed.Layout.Palette[changed.Layout.Cells.Single(c => c.At == new GridPosition(0, 0, 0)).PaletteIndex].Shape);
        Assert.IsNull(changed.Layout.Palette[changed.Layout.Cells.Single(c => c.At == new GridPosition(1, 0, 0)).PaletteIndex].Shape);
        var undone = EditingFixtures.Value(session.Undo());
        Assert.IsFalse(undone.CanUndo);
        Assert.IsTrue(undone.Snapshot.Draft.Layout.Cells.All(c => undone.Snapshot.Draft.Layout.Palette[c.PaletteIndex].Shape is null));
    }

    [TestMethod]
    public void RotationComposesExistingVoxelAndNestedOrientations()
    {
        var child = EditingFixtures.Draft(new GridPosition(1, 0, 0)) with { Specification = new DesignSpecification.Assembly(null) };
        var draft = EditingFixtures.Draft() with { Layout = new(
            [EditingFixtures.Cube with { Orientation = VoxelOrientation.PzPy }],
            [new(new(0, 2, 0), 0)],
            [new NestedDesignItem.AssemblyCopy(new(3, 0, 0), VoxelOrientation.NzPy, child)]) };
        using var session = EditingFixtures.Open(draft);
        SelectRoot(session);
        var originalIds = session.Read().Elements.Select(e => e.Id).ToArray();
        var operation = new EditOperation.RotateSelection(GridAxis.Y, new(0, 0, 0), 1);
        var preview = EditingFixtures.Preview(session, operation);
        CollectionAssert.AreEquivalent(new[] { new GridPosition(0, 2, 0), new(-1, 0, -3) }, preview.HighlightCells.ToArray());
        EditingFixtures.Value(session.Apply(operation));
        var captured = session.Capture().Draft;
        Assert.AreEqual(VoxelOrientation.PxPy, captured.Layout.Palette[captured.Layout.Cells.Single().PaletteIndex].Orientation);
        var nested = (NestedDesignItem.AssemblyCopy)captured.Layout.NestedItems.Single();
        Assert.AreEqual(new GridPosition(0, 0, -3), nested.At);
        Assert.AreEqual(VoxelOrientation.NxPy, nested.Orientation);
        Assert.AreEqual(new GridPosition(1, 0, 0), nested.Design.Layout.Cells.Single().At);
        CollectionAssert.AreEqual(originalIds, session.Read().Elements.Select(e => e.Id).ToArray());
        var frame = EditingFixtures.Frame(session);
        Assert.IsInstanceOfType<ScenePick.Hit>(frame.Pick(new SceneRay(new(-.5f, .5f, -6), System.Numerics.Vector3.UnitZ, 100)));
        Assert.IsFalse(EditingFixtures.Value(session.Undo()).CanUndo);
        Assert.AreEqual(VoxelOrientation.PzPy, session.Capture().Draft.Layout.Palette.Single().Orientation);
        Assert.AreEqual(VoxelOrientation.NzPy, ((NestedDesignItem.AssemblyCopy)session.Capture().Draft.Layout.NestedItems.Single()).Orientation);
    }

    [TestMethod]
    public void SelectionLargerThanRegionLimitCanMoveAndUndoAsOneEdit()
    {
        var coordinates = Enumerable.Range(0, 5001).Select(x => new GridPosition(x, 0, 0)).ToArray();
        using var session = EditingFixtures.Open(EditingFixtures.Draft(coordinates));
        SelectRoot(session);
        var original = session.Read();
        var operation = new EditOperation.MoveSelection(new(0, 1, 0));
        var preview = EditingFixtures.Preview(session, operation);
        Assert.IsTrue(preview.CanApply);

        var moved = EditingFixtures.Value(session.Apply(operation));
        CollectionAssert.AreEquivalent(coordinates.Select(p => new GridPosition(p.X, 1, 0)).ToArray(), moved.Snapshot.Draft.Layout.Cells.Select(c => c.At).ToArray());
        CollectionAssert.AreEqual(original.Elements.Select(e => e.Id).ToArray(), moved.Elements.Select(e => e.Id).ToArray());
        var undone = EditingFixtures.Value(session.Undo());
        CollectionAssert.AreEquivalent(coordinates, undone.Snapshot.Draft.Layout.Cells.Select(c => c.At).ToArray());
        Assert.IsFalse(undone.CanUndo);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ActionHighlightsStaySeparateFromEvaluatedSweepsAndOldRangesExpire(bool selection)
    {
        var child = EditingFixtures.Draft(new GridPosition(1, 0, 0)) with { Specification = new DesignSpecification.Assembly(null) };
        var draft = EditingFixtures.Draft(new GridPosition(0, 0, 0));
        draft = draft with { Layout = draft.Layout with { NestedItems = [new NestedDesignItem.AssemblyCopy(new(10, 0, 0), VoxelOrientation.PxPy, child)] } };
        var scene = SceneFixtures.Projection([]) with { RotationSweeps = [
            new([], new ComputedValue<ImmutableArray<GridPosition>>.Known([new(20, 0, 0)]), new ComputedValue<ImmutableArray<GridPosition>>.Known([])),
            new([0], new ComputedValue<ImmutableArray<GridPosition>>.Known([new(20, 0, 0)]), new ComputedValue<ImmutableArray<GridPosition>>.Known([]))] };
        using var session = EditingFixtures.Open(draft, scene);
        if (selection)
            EditingFixtures.Value(session.Select(new EditSelection.Elements([session.Read().Elements.Single(e => e.Kind == EditElementKind.Assembly).Id])));
        var original = session.Capture();
        var initialLines = EditingFixtures.Frame(session).DebugLines;
        Assert.IsFalse(initialLines.IsEmpty);
        Assert.IsTrue(initialLines.All(line => line.From.X >= 20 && line.To.X >= 20 && line.From.X <= 21 && line.To.X <= 21));
        EditOperation operation = selection ? new EditOperation.MoveSelection(new(1, 0, 0))
            : new EditOperation.Cells(new EditFootprint.Box(new(new(2, 0, 0), new(3, 0, 0))), new CellEdit.Add(EditingFixtures.Cube));
        var preview = EditingFixtures.Preview(session, operation);
        var expected = selection ? new[] { new GridPosition(12, 0, 0) } : new[] { new GridPosition(2, 0, 0), new GridPosition(3, 0, 0) };
        CollectionAssert.AreEquivalent(expected, preview.HighlightCells.ToArray());
        CollectionAssert.AreEqual(initialLines.ToArray(), EditingFixtures.Frame(session).DebugLines.ToArray());
        EditingFixtures.Value(session.Apply(operation));
        Assert.IsNull(session.Read().CurrentEvaluation);
        Assert.IsTrue(EditingFixtures.Frame(session).DebugLines.IsEmpty);
        Assert.IsFalse(session.AcceptEvaluation(original.Stamp, EditingFixtures.Evaluation(original.Draft) with { Scene = scene }));
        Assert.IsTrue(EditingFixtures.Frame(session).DebugLines.IsEmpty);
        var current = session.Capture();
        var newScene = scene with { RotationSweeps = [
            new([], new ComputedValue<ImmutableArray<GridPosition>>.Known([new(30, 0, 0)]), new ComputedValue<ImmutableArray<GridPosition>>.Known([])),
            new([0], new ComputedValue<ImmutableArray<GridPosition>>.Known([new(30, 0, 0)]), new ComputedValue<ImmutableArray<GridPosition>>.Known([]))] };
        Assert.IsTrue(session.AcceptEvaluation(current.Stamp, EditingFixtures.Evaluation(current.Draft) with { Scene = newScene }));
        var newLines = EditingFixtures.Frame(session).DebugLines;
        Assert.IsFalse(newLines.IsEmpty);
        Assert.IsTrue(newLines.All(line => line.From.X >= 30 && line.To.X >= 30 && line.From.X <= 31 && line.To.X <= 31));
    }

    [TestMethod]
    public void CopyCreatesIndependentDescendantIdentitiesAndKeepsOriginalSelection()
    {
        var inner = EditingFixtures.Draft(new GridPosition(1, 0, 0)) with { Specification = new DesignSpecification.SceneModel(null) };
        var child = EditingFixtures.Draft(new GridPosition(0, 0, 0)) with { Specification = new DesignSpecification.SceneModel(null) };
        child = child with { Layout = child.Layout with { NestedItems = [new NestedDesignItem.SceneModelCopy(new(2, 0, 0), VoxelOrientation.PxPy, inner)] } };
        var draft = EditingFixtures.Draft() with { Specification = new DesignSpecification.SceneModel(null), Layout = new([], [], [new NestedDesignItem.SceneModelCopy(new(0, 0, 0), VoxelOrientation.PxPy, child)]) };
        using var session = EditingFixtures.Open(draft);
        SelectRoot(session);
        var original = session.Read();
        var copied = EditingFixtures.Apply(session, new EditOperation.CopySelection(new(10, 0, 0)));
        CollectionAssert.AreEqual(original.Selection.ToArray(), copied.Selection.ToArray());
        var oldIds = original.Elements.Select(e => e.Id).ToHashSet();
        Assert.AreEqual(original.Elements.Length, copied.Elements.Count(e => !oldIds.Contains(e.Id)));
        Assert.IsTrue(oldIds.IsSubsetOf(copied.Elements.Select(e => e.Id)));
        var originalItem = (NestedDesignItem.SceneModelCopy)copied.Snapshot.Draft.Layout.NestedItems[0];
        var copyItem = (NestedDesignItem.SceneModelCopy)copied.Snapshot.Draft.Layout.NestedItems[1];
        Assert.AreEqual(new GridPosition(10, 0, 0), copyItem.At);
        Assert.AreEqual(originalItem.Design.Layout.Cells.Single().At, copyItem.Design.Layout.Cells.Single().At);
        var moved = EditingFixtures.Apply(session, new EditOperation.MoveSelection(new(0, 2, 0)));
        Assert.AreEqual(new GridPosition(0, 2, 0), ((NestedDesignItem.SceneModelCopy)moved.Snapshot.Draft.Layout.NestedItems[0]).At);
        Assert.AreEqual(new GridPosition(10, 0, 0), ((NestedDesignItem.SceneModelCopy)moved.Snapshot.Draft.Layout.NestedItems[1]).At);
        EditingFixtures.Value(session.Undo());
        Assert.AreEqual(1, EditingFixtures.Value(session.Undo()).Snapshot.Draft.Layout.NestedItems.Length);
        Assert.AreEqual(2, EditingFixtures.Value(session.Redo()).Snapshot.Draft.Layout.NestedItems.Length);
    }

    [TestMethod]
    public void SelectionAppearanceRecursesIntoCopiesAndUnsupportedShapeDoesNotCreateHistory()
    {
        var child = EditingFixtures.Draft(new GridPosition(1, 0, 0)) with { Specification = new DesignSpecification.Assembly(null) };
        var draft = EditingFixtures.Draft(new GridPosition(0, 0, 0));
        draft = draft with { Layout = draft.Layout with { NestedItems = [new NestedDesignItem.AssemblyCopy(new(4, 0, 0), VoxelOrientation.PxPy, child)] } };
        using var session = EditingFixtures.Open(draft);
        SelectRoot(session);
        var color = new PaintColor("#123456");
        var paintOperation = new EditOperation.PaintSelection(color);
        var paint = EditingFixtures.Preview(session, paintOperation);
        Assert.AreEqual(2, paint.CoveredCells);
        Assert.AreEqual(2, paint.AffectedCells);
        var painted = EditingFixtures.Value(session.Apply(paintOperation));
        var nested = ((NestedDesignItem.AssemblyCopy)painted.Snapshot.Draft.Layout.NestedItems.Single()).Design;
        Assert.AreEqual(color, nested.Layout.Palette[nested.Layout.Cells.Single().PaletteIndex].Paint);
        Assert.AreEqual(color, painted.Snapshot.Draft.Layout.Palette[painted.Snapshot.Draft.Layout.Cells.Single().PaletteIndex].Paint);
        var shapeOperation = new EditOperation.ShapeSelection(new("cube"));
        var shape = EditingFixtures.Preview(session, shapeOperation);
        Assert.AreEqual(2, shape.AffectedCells);
        var shaped = EditingFixtures.Value(session.Apply(shapeOperation));
        nested = ((NestedDesignItem.AssemblyCopy)shaped.Snapshot.Draft.Layout.NestedItems.Single()).Design;
        Assert.AreEqual(new VoxelShape("cube"), nested.Layout.Palette[nested.Layout.Cells.Single().PaletteIndex].Shape);
        var noChangeOperation = new EditOperation.ShapeSelection(new("unsupported"));
        var noChange = EditingFixtures.Preview(session, noChangeOperation);
        Assert.AreEqual(2, noChange.SkippedCells);
        Assert.AreEqual(shaped.Snapshot.Stamp, EditingFixtures.Value(session.Apply(noChangeOperation)).Snapshot.Stamp);
        EditingFixtures.Value(session.Undo());
        var restored = EditingFixtures.Value(session.Undo());
        Assert.IsFalse(restored.CanUndo);
        Assert.IsNull(((NestedDesignItem.AssemblyCopy)restored.Snapshot.Draft.Layout.NestedItems.Single()).Design.Layout.Palette.Single().Paint);
    }

    [TestMethod]
    public void SealedCompositePaintIsWholeObjectAndShapeSkipsItsKnownOccupancy()
    {
        var definition = new CompositeDefinitionId("test:closed");
        var good = new GoodId.Composite(definition);
        var hash = new DesignHash("closed-design");
        var definitionDraft = EditingFixtures.Draft(new(0, 0, 0), new(1, 0, 0)) with { Specification = new DesignSpecification.FunctionalComposite([], null) };
        var content = new FixedContentView
        {
            Generation = new(1), Core = SceneFixtures.Content().Core,
            VoxelAppearances = new Dictionary<VoxelDefinitionId, ContentLookup<VoxelAppearanceProjection>> { [SceneFixtures.Voxel] = new ContentLookup<VoxelAppearanceProjection>.Found(SceneFixtures.Cube) },
            Goods = new Dictionary<GoodId, ContentLookup<GoodPresentation>> { [good] = new ContentLookup<GoodPresentation>.Found(new(good, new(new("closed", ""), null, null, false), hash, false)) },
            Designs = new Dictionary<DesignHash, ContentLookup<DesignEvaluation>> { [hash] = new ContentLookup<DesignEvaluation>.Found(EditingFixtures.Evaluation(definitionDraft) with { Scene = SceneFixtures.Projection([SceneFixtures.Cell(new(0, 0, 0)), SceneFixtures.Cell(new(1, 0, 0), 2)]) }) }
        };
        var draft = EditingFixtures.Draft() with { Layout = new([], [], [new NestedDesignItem.FunctionalCompositeReference(new(4, 0, 0), VoxelOrientation.PxPy, definition, null)]) };
        using var session = EditingFixtures.Open(draft, content: content);
        SelectRoot(session);
        var color = new PaintColor("#123456");
        var paintOperation = new EditOperation.PaintSelection(color);
        var paint = EditingFixtures.Preview(session, paintOperation);
        Assert.AreEqual(2, paint.CoveredCells);
        Assert.AreEqual(2, paint.AffectedCells);
        var painted = EditingFixtures.Value(session.Apply(paintOperation));
        Assert.AreEqual(color, ((NestedDesignItem.FunctionalCompositeReference)painted.Snapshot.Draft.Layout.NestedItems.Single()).Paint);
        var shapeOperation = new EditOperation.ShapeSelection(new("cube"));
        var shape = EditingFixtures.Preview(session, shapeOperation);
        Assert.AreEqual(0, shape.AffectedCells);
        Assert.AreEqual(2, shape.SkippedCells);
        Assert.AreEqual(painted.Snapshot.Stamp, EditingFixtures.Value(session.Apply(shapeOperation)).Snapshot.Stamp);
        Assert.IsFalse(EditingFixtures.Value(session.Undo()).CanUndo);
    }

    [TestMethod]
    public void MissingDescendantLocksEveryNewWholeSelectionOperation()
    {
        var child = EditingFixtures.Draft(new GridPosition(0, 0, 0)) with { Specification = new DesignSpecification.Assembly(null) };
        var draft = EditingFixtures.Draft() with { Layout = new([], [], [new NestedDesignItem.AssemblyCopy(new(4, 0, 0), VoxelOrientation.PxPy, child)]) };
        var scene = SceneFixtures.Projection([]) with { EditLocks = [new(new DesignElementPath.Cell([0], 0), new ContentReference.Core(new CoreDefinitionReference.Voxel(SceneFixtures.Voxel)), new("source"), [LockedDesignOperation.Move, LockedDesignOperation.Rotate, LockedDesignOperation.Copy, LockedDesignOperation.Paint, LockedDesignOperation.ChangeShape], [])] };
        using var session = EditingFixtures.Open(draft, scene);
        SelectRoot(session);
        var before = session.Capture();
        foreach (var op in new EditOperation[] { new EditOperation.MoveSelection(new(1, 0, 0)), new EditOperation.CopySelection(new(1, 0, 0)), new EditOperation.RotateSelection(GridAxis.Y, new(0, 0, 0), 1), new EditOperation.PaintSelection(new("#123456")), new EditOperation.ShapeSelection(new("cube")) })
        {
            var preview = EditingFixtures.Preview(session, op);
            Assert.IsFalse(preview.CanApply);
            Assert.IsTrue(preview.Conflicts.Any(c => c.Problem.Code == "edit_locked"));
            Assert.AreEqual("edit_locked", EditingFixtures.Code(session.Apply(op)));
            Assert.AreEqual(before.Stamp, session.Capture().Stamp);
        }
        Assert.IsFalse(session.Read().CanUndo);
    }

    [TestMethod]
    public void EnteredSelectionUsesLocalCoordinatesAndCannotMoveThroughHostCells()
    {
        var child = EditingFixtures.Draft(new GridPosition(1, 0, 0)) with { Specification = new DesignSpecification.Assembly(null) };
        var draft = EditingFixtures.Draft(new GridPosition(4, 0, -2));
        draft = draft with { Layout = draft.Layout with { NestedItems = [new NestedDesignItem.AssemblyCopy(new(4, 0, 0), VoxelOrientation.NzPy, child)] } };
        using var session = EditingFixtures.Open(draft);
        var parent = session.Read().Elements.Single(e => e.Kind == EditElementKind.Assembly).Id;
        EditingFixtures.Value(session.Enter(parent));
        var cell = session.Read().Elements.Single(e => e.Parent == parent && e.Kind == EditElementKind.Cell).Id;
        EditingFixtures.Value(session.Select(new EditSelection.Elements([cell])));
        var before = session.Capture();
        var blockedOperation = new EditOperation.MoveSelection(new(1, 0, 0));
        var blocked = EditingFixtures.Preview(session, blockedOperation);
        Assert.IsFalse(blocked.CanApply);
        Assert.AreEqual(new GridPosition(2, 0, 0), blocked.Conflicts.Single().Cell);
        Assert.AreEqual("occupancy_conflict", EditingFixtures.Code(session.Apply(blockedOperation)));
        Assert.AreEqual(before.Stamp, session.Capture().Stamp);
        var moved = EditingFixtures.Apply(session, new EditOperation.MoveSelection(new(0, 1, 0)));
        var nested = ((NestedDesignItem.AssemblyCopy)moved.Snapshot.Draft.Layout.NestedItems.Single()).Design;
        Assert.AreEqual(new GridPosition(1, 1, 0), nested.Layout.Cells.Single().At);
        Assert.AreEqual(cell, moved.Elements.Single(e => e.Parent == parent && e.Kind == EditElementKind.Cell).Id);
    }

    [TestMethod]
    public void EmptySelectionAndZeroMoveDoNotChangeRevisionAndOverflowDoesNotWrap()
    {
        using var session = EditingFixtures.Open(EditingFixtures.Draft(new GridPosition(int.MaxValue, 0, 0)));
        var before = session.Capture();
        foreach (var op in new EditOperation[] { new EditOperation.MoveSelection(new(1, 0, 0)), new EditOperation.CopySelection(new(1, 0, 0)), new EditOperation.RotateSelection(GridAxis.Y, new(0, 0, 0), 1), new EditOperation.PaintSelection(null), new EditOperation.ShapeSelection(new("cube")) })
        {
            var preview = EditingFixtures.Preview(session, op);
            Assert.AreEqual(0, preview.AffectedCells);
            Assert.AreEqual(before.Stamp, EditingFixtures.Value(session.Apply(op)).Snapshot.Stamp);
        }
        SelectRoot(session);
        EditingFixtures.Apply(session, new EditOperation.MoveSelection(new(0, 0, 0)));
        foreach (var op in new EditOperation[] { new EditOperation.MoveSelection(new(1, 0, 0)), new EditOperation.CopySelection(new(1, 0, 0)), new EditOperation.RotateSelection(GridAxis.Z, new(int.MinValue, 0, 0), 2) })
            Assert.IsInstanceOfType<SceneResult<IEditPreview>.Failure>(session.Preview(op));
        Assert.AreEqual(before.Stamp, session.Capture().Stamp);
        Assert.IsFalse(session.Read().CanUndo);
    }

    [TestMethod]
    public void ZeroMovePreservesDuplicatePaletteIndices()
    {
        var draft = EditingFixtures.Draft(new GridPosition(0, 0, 0));
        draft = draft with { Layout = draft.Layout with
        { Palette = [EditingFixtures.Cube, EditingFixtures.Cube], Cells = [new(new(0, 0, 0), 1)] } };
        using var session = EditingFixtures.Open(draft);
        SelectRoot(session);
        var before = session.Capture();
        var operation = new EditOperation.MoveSelection(new(0, 0, 0));
        var preview = EditingFixtures.Preview(session, operation);
        Assert.AreEqual(0, preview.AffectedCells);
        var result = EditingFixtures.Value(session.Apply(operation));
        Assert.AreEqual(before.Stamp, result.Snapshot.Stamp);
        Assert.AreEqual(1, result.Snapshot.Draft.Layout.Cells.Single().PaletteIndex);
        Assert.IsFalse(result.CanUndo);
        Assert.IsFalse(result.IsDirty);
    }

    [TestMethod]
    public void NestedRotationChecksFinalCoordinatesWithoutIntermediateIntOverflow()
    {
        var child = EditingFixtures.Draft(new GridPosition(int.MinValue, 0, 0));
        var draft = EditingFixtures.Draft() with { Layout = new([], [],
            [new NestedDesignItem.SceneModelCopy(new(int.MaxValue, 0, 0), VoxelOrientation.PxPy, child)]) };
        using var session = EditingFixtures.Open(draft);
        SelectRoot(session);
        var operation = new EditOperation.RotateSelection(GridAxis.Y, new(0, 0, 0), 1);
        var preview = EditingFixtures.Preview(session, operation);
        Assert.IsTrue(preview.CanApply);
        CollectionAssert.AreEqual(new[] { new GridPosition(0, 0, 1) }, preview.HighlightCells.ToArray());
        var result = EditingFixtures.Value(session.Apply(operation));
        var nested = (NestedDesignItem.SceneModelCopy)result.Snapshot.Draft.Layout.NestedItems.Single();
        Assert.AreEqual(new GridPosition(0, 0, -int.MaxValue), nested.At);
        Assert.AreEqual(VoxelOrientation.NzPy, nested.Orientation);
        Assert.AreEqual(new GridPosition(int.MinValue, 0, 0), nested.Design.Layout.Cells.Single().At);
    }

    [TestMethod]
    public void LateLocksAreRecheckedAtApplyAndUndoCannotReviveOldEvaluation()
    {
        using var session = EditingFixtures.Open(EditingFixtures.Draft(new GridPosition(0, 0, 0)));
        SelectRoot(session);
        var before = session.Capture();
        var operation = new EditOperation.MoveSelection(new(1, 0, 0));
        EditingFixtures.Preview(session, operation);
        var scene = SceneFixtures.Projection([]) with { EditLocks = [new(new DesignElementPath.Cell([], 0), new ContentReference.Core(new CoreDefinitionReference.Voxel(SceneFixtures.Voxel)), new("source"), [LockedDesignOperation.Move], [])] };
        Assert.IsTrue(session.AcceptEvaluation(before.Stamp, EditingFixtures.Evaluation(before.Draft) with { Scene = scene }));
        Assert.AreEqual("edit_locked", EditingFixtures.Code(session.Apply(operation)));
        Assert.AreEqual(before.Stamp, session.Capture().Stamp);
        EditingFixtures.Apply(session, new EditOperation.PaintSelection(new("#123456")));
        Assert.IsNull(session.Read().CurrentEvaluation);
        Assert.IsFalse(session.AcceptEvaluation(before.Stamp, EditingFixtures.Evaluation(before.Draft)));
        var undone = EditingFixtures.Value(session.Undo());
        Assert.IsNull(undone.CurrentEvaluation);
        Assert.AreNotEqual(before.Stamp, undone.Snapshot.Stamp);
        Assert.IsFalse(session.AcceptEvaluation(before.Stamp, EditingFixtures.Evaluation(before.Draft)));
        Assert.IsNull(EditingFixtures.Value(session.Redo()).CurrentEvaluation);
    }
}
