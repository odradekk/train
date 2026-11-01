using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes.Tests;

[TestClass]
public sealed class EditingReviewRegressionTests
{
    [TestMethod]
    public void PropertiesTrimDisplayBeforeCaptureAndNormalizedEvaluationCanBeAccepted()
    {
        using var session = EditingFixtures.Open(EditingFixtures.Draft());
        EditingFixtures.Apply(session, new EditOperation.Properties(new("  Wagon \t", "\n Description \r\n"), new DesignSpecification.SceneModel(null)));
        var capture = session.Capture();
        Assert.AreEqual(new DisplayText("Wagon", "Description"), capture.Draft.Display);
        Assert.IsTrue(session.AcceptEvaluation(capture.Stamp, EditingFixtures.Evaluation(capture.Draft with { Display = new("Wagon", "Description") })));
        Assert.IsNotNull(session.Read().CurrentEvaluation);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void NestedCopyDisplayIsTrimmedAtEveryLevelBeforeEvaluation(bool replace)
    {
        var leaf = EditingFixtures.Draft() with { Display = new(" Leaf ", " leaf description\n"), Specification = new DesignSpecification.Assembly(null) };
        var wrapper = EditingFixtures.Draft() with
        {
            Display = new(" Wrapper ", "\t wrapper description "),
            Specification = new DesignSpecification.SceneModel(null),
            Layout = new([], [], [new NestedDesignItem.AssemblyCopy(new(0, 0, 0), VoxelOrientation.PxPy, leaf)])
        };
        var item = new NestedDesignItem.SceneModelCopy(new(3, 0, 0), VoxelOrientation.PxPy, wrapper);
        using var session = EditingFixtures.Open(EditingFixtures.Draft());
        if (replace)
        {
            EditingFixtures.Apply(session, new EditOperation.PlaceNested(item with { Design = wrapper with { Display = new("Old", ""), Layout = new([], [], []) } }));
            var id = session.Read().Elements.Single(e => e.Parent is null).Id;
            EditingFixtures.Apply(session, new EditOperation.ReplaceNested(id, item));
        }
        else EditingFixtures.Apply(session, new EditOperation.PlaceNested(item));
        var capture = session.Capture();
        var actualWrapper = ((NestedDesignItem.SceneModelCopy)capture.Draft.Layout.NestedItems.Single()).Design;
        var actualLeaf = ((NestedDesignItem.AssemblyCopy)actualWrapper.Layout.NestedItems.Single()).Design;
        Assert.AreEqual(new DisplayText("Wrapper", "wrapper description"), actualWrapper.Display);
        Assert.AreEqual(new DisplayText("Leaf", "leaf description"), actualLeaf.Display);
        var normalizedWrapper = wrapper with
        {
            Display = new("Wrapper", "wrapper description"),
            Layout = wrapper.Layout with { NestedItems = [new NestedDesignItem.AssemblyCopy(new(0, 0, 0), VoxelOrientation.PxPy, leaf with { Display = new("Leaf", "leaf description") })] }
        };
        var normalized = capture.Draft with { Layout = capture.Draft.Layout with { NestedItems = [item with { Design = normalizedWrapper }] } };
        Assert.IsTrue(session.AcceptEvaluation(capture.Stamp, EditingFixtures.Evaluation(normalized)));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void AddSkipsExistingOwnCellsRegardlessOfValueAndOnlyFillsEmptyCells(bool different)
    {
        using var session = EditingFixtures.Open(EditingFixtures.Draft(new GridPosition(0, 0, 0)));
        var id = session.Read().Elements.Single().Id;
        var value = different ? EditingFixtures.Cube with { Paint = new("#ff0000") } : EditingFixtures.Cube;
        var previewOperation = new EditOperation.Cells(new EditFootprint.Stroke([new(0, 0, 0), new(1, 0, 0)], 1), new CellEdit.Add(value));
        var preview = EditingFixtures.Preview(session, previewOperation);
        Assert.IsTrue(preview.CanApply);
        Assert.IsTrue(preview.Conflicts.IsEmpty);
        Assert.AreEqual(2, preview.CoveredCells);
        Assert.AreEqual(1, preview.AffectedCells);
        Assert.AreEqual(1, preview.SkippedCells);
        EditingFixtures.Value(session.Apply(previewOperation));
        var capture = session.Capture();
        Assert.AreEqual(2, capture.Draft.Layout.Cells.Length);
        var original = capture.Draft.Layout.Cells.Single(c => c.At == new GridPosition(0, 0, 0));
        Assert.AreEqual(EditingFixtures.Cube, capture.Draft.Layout.Palette[original.PaletteIndex]);
        Assert.AreEqual(id, session.Read().Elements.Single(e => e.At == original.At).Id);
        var added = capture.Draft.Layout.Cells.Single(c => c.At == new GridPosition(1, 0, 0));
        Assert.AreEqual(value, capture.Draft.Layout.Palette[added.PaletteIndex]);
    }

    [TestMethod]
    public void ElementsSelectionRequiresEnteringParentBeforeSelectingDescendant()
    {
        var child = EditingFixtures.Draft(new GridPosition(0, 0, 0)) with { Specification = new DesignSpecification.Assembly(null) };
        using var session = EditingFixtures.Open(EditingFixtures.Draft() with { Layout = new([], [], [new NestedDesignItem.AssemblyCopy(new(4, 0, 0), VoxelOrientation.PxPy, child)]) });
        var parent = session.Read().Elements.Single(e => e.Kind == EditElementKind.Assembly);
        var cell = session.Read().Elements.Single(e => e.Kind == EditElementKind.Cell);
        Assert.AreEqual("target_missing", EditingFixtures.Code(session.Select(new EditSelection.Elements([cell.Id]))));
        Assert.IsTrue(session.Read().Selection.IsEmpty);
        EditingFixtures.Value(session.Enter(parent.Id));
        EditingFixtures.Value(session.Select(new EditSelection.Elements([cell.Id])));
        CollectionAssert.AreEqual(new[] { cell.Id }, session.Read().Selection.ToArray());
        EditingFixtures.Apply(session, new EditOperation.RemoveSelection());
        Assert.IsTrue(((NestedDesignItem.AssemblyCopy)session.Capture().Draft.Layout.NestedItems.Single()).Design.Layout.Cells.IsEmpty);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void RootNestedPlacementReportsOneConflictWithExistingBlocker(bool anchor)
    {
        var child = EditingFixtures.Draft(anchor ? [] : [new GridPosition(0, 0, 0)]) with { Specification = new DesignSpecification.Assembly(null) };
        var existing = new NestedDesignItem.AssemblyCopy(new(4, 0, 0), VoxelOrientation.PxPy, child);
        var draft = anchor ? EditingFixtures.Draft() with { Layout = new([], [], [existing]) } : EditingFixtures.Draft(new GridPosition(4, 0, 0));
        using var session = EditingFixtures.Open(draft);
        var blocker = session.Read().Elements.Single(e => e.Parent is null).Id;
        var preview = EditingFixtures.Preview(session, new EditOperation.PlaceNested(new NestedDesignItem.AssemblyCopy(new(4, 0, 0), VoxelOrientation.PxPy, child)));
        Assert.IsFalse(preview.CanApply);
        var conflict = preview.Conflicts.Single(c => c.Problem.Code == "occupancy_conflict");
        Assert.AreEqual(new GridPosition(4, 0, 0), conflict.Cell);
        Assert.AreEqual(blocker, conflict.Element);
    }

    [TestMethod]
    public void NestedSelfOverlapHasNoExistingBlockerElement()
    {
        using var session = EditingFixtures.Open(EditingFixtures.Draft());
        var child = EditingFixtures.Draft(new(0, 0, 0), new(0, 0, 0)) with { Specification = new DesignSpecification.Assembly(null) };
        var preview = EditingFixtures.Preview(session, new EditOperation.PlaceNested(new NestedDesignItem.AssemblyCopy(new(4, 0, 0), VoxelOrientation.PxPy, child)));
        Assert.IsFalse(preview.CanApply);
        var conflict = preview.Conflicts.Single();
        Assert.AreEqual(new GridPosition(4, 0, 0), conflict.Cell);
        Assert.IsNull(conflict.Element);
    }

    [TestMethod]
    public void EvaluationAndSavedCaptureRejectOtherSessionAndContentGeneration()
    {
        using var session = EditingFixtures.Open(EditingFixtures.Draft());
        using var other = EditingFixtures.Open(EditingFixtures.Draft());
        var capture = session.Capture();
        var evaluation = EditingFixtures.Evaluation(capture.Draft);
        var initialEvaluation = session.Read().CurrentEvaluation;
        Assert.IsFalse(session.AcceptEvaluation(capture.Stamp with { Session = other.Id }, evaluation));
        Assert.IsFalse(session.AcceptEvaluation(capture.Stamp with { Content = new(session.Content.Value + 1) }, evaluation));
        Assert.AreSame(initialEvaluation, session.Read().CurrentEvaluation);
        EditingFixtures.Apply(session, EditingFixtures.Add(new(1, 0, 0)));
        Assert.IsFalse(session.MarkSaved(other.Capture()));
        Assert.IsTrue(session.Read().IsDirty);
    }
    [TestMethod]
    [DataRow(VoxelOrientation.PxPy, 5, 0, 1, 0)]
    [DataRow(VoxelOrientation.PxNy, 4, -1, 0, 1)]
    public void EnteredConflictUsesDeduplicatedLocalCellAndExistingBlocker(VoxelOrientation orientation,
        int rootX, int rootY, int localX, int localY)
    {
        var child = EditingFixtures.Draft(new GridPosition(0, 0, 0)) with { Specification = new DesignSpecification.Assembly(null) };
        var rootAt = new GridPosition(rootX, rootY, 0);
        var localAt = new GridPosition(localX, localY, 0);
        var draft = EditingFixtures.Draft(rootAt) with
        {
            Layout = new([EditingFixtures.Cube], [new(rootAt, 0)],
                [new NestedDesignItem.AssemblyCopy(new(4, 0, 0), orientation, child)])
        };
        using var session = EditingFixtures.Open(draft);
        var blocker = session.Read().Elements.Single(e => e.Parent is null && e.Kind == EditElementKind.Cell).Id;
        var parent = session.Read().Elements.Single(e => e.Kind == EditElementKind.Assembly).Id;
        EditingFixtures.Value(session.Enter(parent));
        var preview = EditingFixtures.Preview(session, EditingFixtures.Add(localAt));
        var conflict = preview.Conflicts.Single(c => c.Problem.Code == "occupancy_conflict");
        Assert.AreEqual(localAt, conflict.Cell);
        Assert.AreEqual(blocker, conflict.Element);
        CollectionAssert.AreEqual(new[] { localAt }, preview.HighlightCells.ToArray());
        Assert.IsFalse(preview.CanApply);
    }

    [TestMethod]
    public void EnteredBrushFindsHostConflictThroughMultipleAsymmetricRotations()
    {
        var inner = EditingFixtures.Draft(new GridPosition(0, 0, 0)) with
        { Specification = new DesignSpecification.Assembly(null) };
        var outer = EditingFixtures.Draft() with
        {
            Specification = new DesignSpecification.Assembly(null),
            Layout = new([], [], [new NestedDesignItem.AssemblyCopy(new(-4, 5, 2), VoxelOrientation.NxPz, inner)])
        };
        var draft = EditingFixtures.Draft(new GridPosition(18, -2, 1)) with
        {
            Layout = new([EditingFixtures.Cube], [new(new(18, -2, 1), 0)],
                [new NestedDesignItem.AssemblyCopy(new(10, -3, 7), VoxelOrientation.PzPx, outer)])
        };
        using var session = EditingFixtures.Open(draft);
        var initial = session.Read();
        var blocker = initial.Elements.Single(element => element.Kind == EditElementKind.Cell && element.Parent is null).Id;
        var entered = initial.Elements.Single(element => element.Kind == EditElementKind.Assembly && element.Parent is not null).Id;
        EditingFixtures.Value(session.Enter(entered));
        var before = session.Capture();
        var previewOperation = new EditOperation.Cells(
            new EditFootprint.Stroke([new(2, -1, 3), new(3, -1, 3)], 1), new CellEdit.Add(EditingFixtures.Cube));
        var preview = EditingFixtures.Preview(session, previewOperation);
        var conflict = preview.Conflicts.Single();
        Assert.AreEqual(new GridPosition(2, -1, 3), conflict.Cell);
        Assert.AreEqual(blocker, conflict.Element);
        Assert.AreEqual("occupancy_conflict", EditingFixtures.Code(session.Apply(previewOperation)));
        Assert.AreEqual(before.Stamp, session.Capture().Stamp);

        var applied = EditingFixtures.Apply(session, EditingFixtures.Add(new(3, -1, 3)));
        Assert.IsTrue(applied.Elements.Any(element => element.Parent == entered && element.At == new GridPosition(3, -1, 3)));
        CollectionAssert.AreEqual(before.Draft.Layout.Cells, applied.Snapshot.Draft.Layout.Cells);
    }

    [TestMethod]
    [DataRow(int.MaxValue, 1)]
    [DataRow(int.MinValue, -1)]
    public void EnteredBrushOutsideRootIntegerRangeCanRemoveEmptySpaceButCannotAdd(int placement, int localX)
    {
        var child = EditingFixtures.Draft(new GridPosition(0, 0, 0)) with
        { Specification = new DesignSpecification.Assembly(null) };
        var draft = EditingFixtures.Draft() with { Layout = new([], [],
            [new NestedDesignItem.AssemblyCopy(new(placement, 0, 0), VoxelOrientation.PxPy, child)]) };
        using var session = EditingFixtures.Open(draft);
        EditingFixtures.Value(session.Enter(session.Read().Elements.Single(element => element.Kind == EditElementKind.Assembly).Id));
        var before = session.Capture();
        var previewOperation = new EditOperation.Cells(
            new EditFootprint.Cell(new(localX, 0, 0)), new CellEdit.Remove());
        var preview = EditingFixtures.Preview(session, previewOperation);
        Assert.IsTrue(preview.CanApply);
        Assert.AreEqual(0, preview.AffectedCells);
        Assert.AreEqual(1, preview.SkippedCells);
        var unchanged = EditingFixtures.Value(session.Apply(previewOperation));
        Assert.AreEqual(before.Stamp, unchanged.Snapshot.Stamp);
        Assert.IsFalse(unchanged.CanUndo);
        Assert.IsFalse(unchanged.IsDirty);
        Assert.AreEqual("invalid_operation", EditingFixtures.Code(session.Preview(EditingFixtures.Add(new(localX, 0, 0)))));
        Assert.AreEqual(before.Stamp, session.Capture().Stamp);
        Assert.IsFalse(session.Read().CanUndo);
    }

    [TestMethod]
    [DataRow(int.MinValue, int.MaxValue)]
    [DataRow(int.MaxValue, int.MinValue)]
    public void EnteredBrushCanDeleteLocalCellWithoutMappingDistantRootCell(int rootX, int placement)
    {
        var child = EditingFixtures.Draft(new GridPosition(0, 0, 0)) with
        { Specification = new DesignSpecification.Assembly(null) };
        var draft = EditingFixtures.Draft(new GridPosition(rootX, 0, 0)) with
        { Layout = new([EditingFixtures.Cube], [new(new(rootX, 0, 0), 0)],
            [new NestedDesignItem.AssemblyCopy(new(placement, 0, 0), VoxelOrientation.PxPy, child)]) };
        using var session = EditingFixtures.Open(draft);
        EditingFixtures.Value(session.Enter(session.Read().Elements.Single(element => element.Kind == EditElementKind.Assembly).Id));
        var before = session.Capture();
        var previewOperation = new EditOperation.Cells(
            new EditFootprint.Cell(new(0, 0, 0)), new CellEdit.Remove());
        var preview = EditingFixtures.Preview(session, previewOperation);
        Assert.IsTrue(preview.CanApply);
        Assert.AreEqual(1, preview.AffectedCells);
        var applied = EditingFixtures.Value(session.Apply(previewOperation));
        CollectionAssert.AreEqual(before.Draft.Layout.Cells, applied.Snapshot.Draft.Layout.Cells);
        Assert.IsTrue(((NestedDesignItem.AssemblyCopy)applied.Snapshot.Draft.Layout.NestedItems.Single()).Design.Layout.Cells.IsEmpty);
        Assert.IsTrue(applied.Snapshot.Stamp.Revision.Value > before.Stamp.Revision.Value);
        Assert.IsTrue(applied.CanUndo);
        Assert.IsTrue(applied.IsDirty);
    }

    [TestMethod]
    public void NewOverlapConflictsKeepResultCellOrderAndExistingBlockersWithoutReportingUntouchedOverlap()
    {
        var existing = EditingFixtures.Draft(new GridPosition(0, 0, 0)) with
        { Specification = new DesignSpecification.Assembly(null) };
        var draft = EditingFixtures.Draft(new(4, 0, 0), new(2, 0, 0), new(8, 0, 0));
        draft = draft with { Layout = draft.Layout with { NestedItems =
            [new NestedDesignItem.AssemblyCopy(new(8, 0, 0), VoxelOrientation.PxPy, existing)] } };
        using var session = EditingFixtures.Value(ScenesFactory.Create().OpenEditor(SceneFixtures.Content(),
            EditingFixtures.Evaluation(draft) with { IsValid = false }));
        var before = session.Capture();
        var blockers = session.Read().Elements.Where(element => element.Kind == EditElementKind.Cell && element.Parent is null)
            .ToDictionary(element => element.At, element => element.Id);
        var unchangedOccupancy = EditingFixtures.Preview(session, new EditOperation.Properties(new("Renamed", ""), draft.Specification));
        Assert.IsTrue(unchangedOccupancy.CanApply);
        Assert.IsTrue(unchangedOccupancy.Conflicts.IsEmpty);

        var added = EditingFixtures.Draft(new(2, 0, 0), new(4, 0, 0)) with
        { Specification = new DesignSpecification.Assembly(null) };
        var previewOperation = new EditOperation.PlaceNested(
            new NestedDesignItem.AssemblyCopy(new(0, 0, 0), VoxelOrientation.PxPy, added));
        var preview = EditingFixtures.Preview(session, previewOperation);
        CollectionAssert.AreEqual(new[] { new GridPosition(4, 0, 0), new(2, 0, 0) },
            preview.Conflicts.Select(conflict => conflict.Cell!.Value).ToArray());
        CollectionAssert.AreEqual(new EditElementId?[] { blockers[new(4, 0, 0)], blockers[new(2, 0, 0)] },
            preview.Conflicts.Select(conflict => conflict.Element).ToArray());
        Assert.IsTrue(preview.Conflicts.All(conflict => conflict.Problem.Code == "occupancy_conflict"));
        Assert.AreEqual("occupancy_conflict", EditingFixtures.Code(session.Apply(previewOperation)));
        Assert.AreEqual(before.Stamp, session.Capture().Stamp);
        Assert.IsFalse(session.Read().CanUndo);
    }

    [TestMethod]
    public void AddConflictIdentifiesNestedBlockerEvenWhenOwnCellAlsoOccupiesTarget()
    {
        var child = EditingFixtures.Draft(new GridPosition(0, 0, 0)) with { Specification = new DesignSpecification.Assembly(null) };
        var draft = EditingFixtures.Draft(new GridPosition(0, 0, 0)) with
        {
            Layout = new([EditingFixtures.Cube], [new(new(0, 0, 0), 0)],
                [new NestedDesignItem.AssemblyCopy(new(0, 0, 0), VoxelOrientation.PxPy, child)])
        };
        using var session = EditingFixtures.Open(draft);
        var blocker = session.Read().Elements.Single(e => e.Kind == EditElementKind.Cell && e.Parent is not null).Id;
        var preview = EditingFixtures.Preview(session, EditingFixtures.Add(new(0, 0, 0)));
        var conflict = preview.Conflicts.Single(c => c.Problem.Code == "occupancy_conflict");
        Assert.AreEqual(blocker, conflict.Element);
        Assert.AreEqual(new GridPosition(0, 0, 0), conflict.Cell);
        Assert.IsFalse(preview.CanApply);
    }

    [TestMethod]
    public void AddCannotSkipPastLockedOwnCell()
    {
        var scene = SceneFixtures.Projection([]) with
        {
            EditLocks = [new(new DesignElementPath.Cell([], 0),
                new ContentReference.Core(new CoreDefinitionReference.Voxel(SceneFixtures.Voxel)), new("source"), [LockedDesignOperation.Replace], [])]
        };
        using var session = EditingFixtures.Open(EditingFixtures.Draft(new GridPosition(0, 0, 0)), scene);
        var before = session.Capture();
        var blocker = session.Read().Elements.Single().Id;
        var previewOperation = new EditOperation.Cells(new EditFootprint.Stroke([new(0, 0, 0), new(1, 0, 0)], 1), new CellEdit.Add(EditingFixtures.Cube));
        var preview = EditingFixtures.Preview(session, previewOperation);
        Assert.IsFalse(preview.CanApply);
        var conflict = preview.Conflicts.Single(c => c.Problem.Code == "edit_locked");
        Assert.AreEqual(blocker, conflict.Element);
        Assert.AreEqual("edit_locked", EditingFixtures.Code(session.Apply(previewOperation)));
        Assert.AreEqual(before.Stamp, session.Capture().Stamp);
        Assert.AreEqual(1, session.Capture().Draft.Layout.Cells.Length);
    }

    [TestMethod]
    public void AddCannotSkipOwnCellThatAlsoOverlapsHost()
    {
        var child = EditingFixtures.Draft(new GridPosition(0, 0, 0)) with { Specification = new DesignSpecification.Assembly(null) };
        var draft = EditingFixtures.Draft(new GridPosition(4, 0, 0)) with
        {
            Layout = new([EditingFixtures.Cube], [new(new(4, 0, 0), 0)],
                [new NestedDesignItem.AssemblyCopy(new(4, 0, 0), VoxelOrientation.PxPy, child)])
        };
        using var session = EditingFixtures.Value(ScenesFactory.Create().OpenEditor(SceneFixtures.Content(),
            EditingFixtures.Evaluation(draft) with { IsValid = false }));
        var blocker = session.Read().Elements.Single(e => e.Parent is null && e.Kind == EditElementKind.Cell).Id;
        EditingFixtures.Value(session.Enter(session.Read().Elements.Single(e => e.Kind == EditElementKind.Assembly).Id));
        var before = session.Capture();
        var previewOperation = new EditOperation.Cells(
            new EditFootprint.Stroke([new(0, 0, 0), new(1, 0, 0)], 1), new CellEdit.Add(EditingFixtures.Cube));
        var preview = EditingFixtures.Preview(session, previewOperation);
        Assert.IsFalse(preview.CanApply);
        var conflict = preview.Conflicts.Single();
        Assert.AreEqual(new GridPosition(0, 0, 0), conflict.Cell);
        Assert.AreEqual(blocker, conflict.Element);
        Assert.AreEqual(0, preview.SkippedCells);
        Assert.AreEqual("occupancy_conflict", EditingFixtures.Code(session.Apply(previewOperation)));
        Assert.AreEqual(before.Stamp, session.Capture().Stamp);
        Assert.IsFalse(session.Read().CanUndo);
        Assert.IsFalse(session.Read().IsDirty);
    }

    private static IEnumerable<DesignSceneProjection> MalformedScenes()
    {
        var scene = SceneFixtures.Projection([]);
        var reference = new ContentReference.Core(new CoreDefinitionReference.Voxel(SceneFixtures.Voxel));
        var validLock = new DesignEditLock(new DesignElementPath.Cell([], 0), reference, new("source"), [LockedDesignOperation.Remove], []);
        var known = new ComputedValue<ImmutableArray<GridPosition>>.Known([]);
        yield return scene with { EditLocks = default };
        yield return scene with { RotationSweeps = default };
        yield return scene with { RotationSweeps = [new([], new ComputedValue<ImmutableArray<GridPosition>>.Known(default), known)] };
        yield return scene with { RotationSweeps = [new([], known, new ComputedValue<ImmutableArray<GridPosition>>.Known(default))] };
        yield return scene with { RotationSweeps = [new(default, known, known)] };
        yield return scene with { EditLocks = [validLock with { BlockedOperations = default }] };
        yield return scene with { EditLocks = [validLock with { Element = new DesignElementPath.Cell(default, 0) }] };
    }

    [TestMethod]
    public void MalformedEvaluationCollectionsThrowBeforeOpeningOrChangingSessionState()
    {
        var draft = EditingFixtures.Draft(new(0, 0, 0), new(2, 0, 0));
        foreach (var invalid in MalformedScenes())
        {
            var malformed = invalid.EditLocks.IsDefault ? invalid : invalid with
            {
                EditLocks = invalid.EditLocks.Insert(0, new(new DesignElementPath.Cell([], 1),
                    new ContentReference.Core(new CoreDefinitionReference.Voxel(SceneFixtures.Voxel)), new("new-lock"), [LockedDesignOperation.Remove], []))
            };
            Assert.ThrowsExactly<ArgumentException>(() => ScenesFactory.Create().OpenEditor(SceneFixtures.Content(),
                EditingFixtures.Evaluation(draft) with { Scene = malformed }));
            var validScene = SceneFixtures.Projection([]) with
            {
                EditLocks = [new(new DesignElementPath.Cell([], 0),
                    new ContentReference.Core(new CoreDefinitionReference.Voxel(SceneFixtures.Voxel)), new("source"), [LockedDesignOperation.Remove], [])],
                RotationSweeps = [new([], new ComputedValue<ImmutableArray<GridPosition>>.Known([new(2, 0, 0)]), new ComputedValue<ImmutableArray<GridPosition>>.Known([]))]
            };
            using var session = EditingFixtures.Open(draft, validScene);
            var before = session.Read();
            var frame = EditingFixtures.Frame(session);
            var previewOperation = EditingFixtures.Add(new(1, 0, 0));
            Assert.ThrowsExactly<ArgumentException>(() => session.AcceptEvaluation(before.Snapshot.Stamp,
                EditingFixtures.Evaluation(draft) with { Scene = malformed }));
            var after = session.Read();
            Assert.AreSame(before.CurrentEvaluation, after.CurrentEvaluation);
            Assert.AreEqual(before.Snapshot.Stamp, after.Snapshot.Stamp);
            Assert.AreEqual(before.IsDirty, after.IsDirty);
            Assert.AreEqual(before.CanUndo, after.CanUndo);
            CollectionAssert.AreEqual(before.Elements.ToArray(), after.Elements.ToArray());
            CollectionAssert.AreEqual(frame.DebugLines.ToArray(), EditingFixtures.Frame(session).DebugLines.ToArray());
            var applied = EditingFixtures.Value(session.Apply(previewOperation));
            Assert.AreEqual(3, applied.Snapshot.Draft.Layout.Cells.Length);
            Assert.IsTrue(applied.Elements.Single(e => e.At == new GridPosition(0, 0, 0)).Locked);
        }
    }

    [TestMethod]
    public void ClosedCompositeDefaultVoxelsThrowBeforeOpenOrPlacementChangesSession()
    {
        var definition = new CompositeDefinitionId("test:invalid-voxels");
        var good = new GoodId.Composite(definition);
        var hash = new DesignHash("invalid-voxels");
        var definitionDraft = EditingFixtures.Draft() with { Specification = new DesignSpecification.FunctionalComposite([], null) };
        var content = new FixedContentView
        {
            Generation = new(1), Core = SceneFixtures.Content().Core,
            Goods = new Dictionary<GoodId, ContentLookup<GoodPresentation>>
            { [good] = new ContentLookup<GoodPresentation>.Found(new(good, new(new("closed", ""), null, null, false), hash, false)) },
            Designs = new Dictionary<DesignHash, ContentLookup<DesignEvaluation>>
            { [hash] = new ContentLookup<DesignEvaluation>.Found(EditingFixtures.Evaluation(definitionDraft) with { Scene = SceneFixtures.Projection([]) with { Voxels = default } }) }
        };
        var item = new NestedDesignItem.FunctionalCompositeReference(new(3, 0, 0), VoxelOrientation.PxPy, definition, null);
        var invalidDraft = EditingFixtures.Draft() with { Layout = new([], [], [item]) };
        Assert.ThrowsExactly<ArgumentException>(() => ScenesFactory.Create().OpenEditor(content, EditingFixtures.Evaluation(invalidDraft)));
        using var session = EditingFixtures.Open(EditingFixtures.Draft(), content: content);
        var before = session.Read();
        Assert.ThrowsExactly<ArgumentException>(() => session.Preview(new EditOperation.PlaceNested(item)));
        Assert.AreSame(before.CurrentEvaluation, session.Read().CurrentEvaluation);
        Assert.AreEqual(before.Snapshot.Stamp, session.Capture().Stamp);
        Assert.IsFalse(session.Read().CanUndo);
        Assert.IsFalse(session.Read().IsDirty);
        Assert.IsTrue(session.Read().Elements.IsEmpty);
    }

}
