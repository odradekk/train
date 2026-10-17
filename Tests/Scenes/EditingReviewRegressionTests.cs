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
        var preview = EditingFixtures.Preview(session, new EditOperation.Cells(new EditFootprint.Stroke([new(0, 0, 0), new(1, 0, 0)], 1), new CellEdit.Add(value)));
        Assert.IsTrue(preview.CanApply);
        Assert.IsTrue(preview.Conflicts.IsEmpty);
        Assert.AreEqual(2, preview.CoveredCells);
        Assert.AreEqual(1, preview.AffectedCells);
        Assert.AreEqual(1, preview.SkippedCells);
        EditingFixtures.Value(session.Apply(preview, false));
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
    public void NonCellOperationsOver512CellsNeverRequireConfirmation()
    {
        var child = EditingFixtures.Draft(Enumerable.Range(0, 513).Select(x => new GridPosition(x, 0, 0)).ToArray()) with { Specification = new DesignSpecification.Assembly(null) };
        var item = new NestedDesignItem.AssemblyCopy(new(0, 0, 0), VoxelOrientation.PxPy, child);
        using var session = EditingFixtures.Open(EditingFixtures.Draft());
        ApplyWithoutConfirmation(new EditOperation.PlaceNested(item));
        var id = session.Read().Elements.Single(e => e.Kind == EditElementKind.Assembly).Id;
        ApplyWithoutConfirmation(new EditOperation.ReplaceNested(id, item with { At = new(0, 1, 0) }));
        id = session.Read().Elements.Single(e => e.Kind == EditElementKind.Assembly).Id;
        EditingFixtures.Value(session.Select(new EditSelection.Elements([id])));
        ApplyWithoutConfirmation(new EditOperation.RemoveSelection());
        Assert.IsTrue(session.Capture().Draft.Layout.NestedItems.IsEmpty);

        void ApplyWithoutConfirmation(EditOperation operation)
        {
            var preview = EditingFixtures.Preview(session, operation);
            Assert.IsTrue(preview.AffectedCells > 512);
            Assert.IsFalse(preview.RequiresConfirmation);
            EditingFixtures.Value(session.Apply(preview, false));
        }
    }

    [TestMethod]
    public void BrushWidthAndCoverageHaveExact16And4096Boundaries()
    {
        using var session = EditingFixtures.Open(EditingFixtures.Draft());
        var atLimit = EditingFixtures.Preview(session, new EditOperation.Cells(new EditFootprint.Stroke([new(0, 0, 0)], 16), new CellEdit.Add(EditingFixtures.Cube)));
        Assert.AreEqual(4096, atLimit.CoveredCells);
        Assert.AreEqual(4096, atLimit.AffectedCells);
        Assert.IsTrue(atLimit.CanApply);
        Assert.AreEqual("region_limit", EditingFixtures.Code(session.Preview(new EditOperation.Cells(new EditFootprint.Stroke([new(0, 0, 0)], 17), new CellEdit.Add(EditingFixtures.Cube)))));
        var samples = Enumerable.Range(0, 4097).Select(x => new GridPosition(x, 0, 0)).ToImmutableArray();
        var exact = EditingFixtures.Preview(session, new EditOperation.Cells(new EditFootprint.Stroke(samples.RemoveAt(4096), 1), new CellEdit.Add(EditingFixtures.Cube)));
        Assert.AreEqual(4096, exact.CoveredCells);
        Assert.AreEqual("region_limit", EditingFixtures.Code(session.Preview(new EditOperation.Cells(new EditFootprint.Stroke(samples, 1), new CellEdit.Add(EditingFixtures.Cube)))));
        Assert.IsFalse(session.Read().CanUndo);
    }

    [TestMethod]
    [DataRow(512, false)]
    [DataRow(513, true)]
    public void CellConfirmationStartsAt513AffectedCells(int count, bool requiresConfirmation)
    {
        using var session = EditingFixtures.Open(EditingFixtures.Draft());
        var samples = Enumerable.Range(0, count).Select(x => new GridPosition(x, 0, 0)).ToImmutableArray();
        var preview = EditingFixtures.Preview(session, new EditOperation.Cells(new EditFootprint.Stroke(samples, 1), new CellEdit.Add(EditingFixtures.Cube)));
        Assert.AreEqual(count, preview.AffectedCells);
        Assert.AreEqual(requiresConfirmation, preview.RequiresConfirmation);
        if (requiresConfirmation)
            Assert.AreEqual("confirmation_required", EditingFixtures.Code(session.Apply(preview, false)));
        EditingFixtures.Value(session.Apply(preview, requiresConfirmation));
        Assert.AreEqual(count, session.Capture().Draft.Layout.Cells.Length);
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
        var preview = EditingFixtures.Preview(session, new EditOperation.Cells(new EditFootprint.Stroke([new(0, 0, 0), new(1, 0, 0)], 1), new CellEdit.Add(EditingFixtures.Cube)));
        Assert.IsFalse(preview.CanApply);
        var conflict = preview.Conflicts.Single(c => c.Problem.Code == "edit_locked");
        Assert.AreEqual(blocker, conflict.Element);
        Assert.AreEqual("edit_locked", EditingFixtures.Code(session.Apply(preview, true)));
        Assert.AreEqual(before.Stamp, session.Capture().Stamp);
        Assert.AreEqual(1, session.Capture().Draft.Layout.Cells.Length);
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
            var preview = EditingFixtures.Preview(session, EditingFixtures.Add(new(1, 0, 0)));
            Assert.ThrowsExactly<ArgumentException>(() => session.AcceptEvaluation(before.Snapshot.Stamp,
                EditingFixtures.Evaluation(draft) with { Scene = malformed }));
            var after = session.Read();
            Assert.AreSame(before.CurrentEvaluation, after.CurrentEvaluation);
            Assert.AreEqual(before.Snapshot.Stamp, after.Snapshot.Stamp);
            Assert.AreEqual(before.IsDirty, after.IsDirty);
            Assert.AreEqual(before.CanUndo, after.CanUndo);
            CollectionAssert.AreEqual(before.Elements.ToArray(), after.Elements.ToArray());
            CollectionAssert.AreEqual(frame.DebugLines.ToArray(), EditingFixtures.Frame(session).DebugLines.ToArray());
            var applied = EditingFixtures.Value(session.Apply(preview, false));
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
