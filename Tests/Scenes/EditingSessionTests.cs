using System.Collections.Immutable;
using System.Numerics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes.Tests;

internal static class EditingFixtures
{
    internal static VoxelPaletteEntry Cube => new(SceneFixtures.Voxel, VoxelOrientation.PxPy, null, null);
    internal static DesignDraft Draft(params GridPosition[] cells) => SceneFixtures.Draft with
    { Layout = new([Cube], cells.Select(at => new LayoutCell(at, 0)).ToImmutableArray(), []) };
    internal static DesignEvaluation.Complete Evaluation(DesignDraft draft) =>
        SceneFixtures.Evaluation(SceneFixtures.Projection([])) with { Definition = draft };
    internal static IDesignEditSession Open(DesignDraft draft, DesignSceneProjection? scene = null,
        IContentView? content = null) => Value(ScenesFactory.Create().OpenEditor(content ?? SceneFixtures.Content(),
            Evaluation(draft) with { Scene = scene ?? SceneFixtures.Projection([]) }));
    internal static T Value<T>(SceneResult<T> result) where T : notnull
    {
        Assert.IsInstanceOfType<SceneResult<T>.Success>(result);
        return ((SceneResult<T>.Success)result).Value;
    }
    internal static string Code<T>(SceneResult<T> result) where T : notnull
    {
        Assert.IsInstanceOfType<SceneResult<T>.Failure>(result);
        return ((SceneResult<T>.Failure)result).Problems[0].Code;
    }
    internal static IEditPreview Preview(IDesignEditSession session, EditOperation operation) => Value(session.Preview(operation));
    internal static EditState Apply(IDesignEditSession session, EditOperation operation) => Value(session.Apply(Preview(session, operation), true));
    internal static EditOperation.Cells Add(GridPosition at) => new(new EditFootprint.Cell(at), new CellEdit.Add(Cube));
    internal static EditViewOptions View(int? top = null) => new(new(GridAxis.Y, 0), top, SceneDebugLayer.None);
    internal static ISceneFrame Frame(IDesignEditSession session, int? top = null) => Value(session.Present(View(top), null));
    internal static SceneRay Ray(float x = .5f, float y = .5f) => new(new(x, y, -3), Vector3.UnitZ, 100);
    internal static DesignDraft Clone(DesignDraft draft) => draft with
    {
        Layout = new(draft.Layout.Palette.Select(p => p with { }).ToImmutableArray(),
            draft.Layout.Cells.Select(c => c with { }).ToImmutableArray(),
            draft.Layout.NestedItems.Select<NestedDesignItem, NestedDesignItem>(item => item switch
            {
                NestedDesignItem.AssemblyCopy a => a with { Design = Clone(a.Design) },
                NestedDesignItem.SceneModelCopy m => m with { Design = Clone(m.Design) },
                NestedDesignItem.FunctionalCompositeReference c => c with { },
                _ => throw new InvalidOperationException()
            }).ToImmutableArray())
    };
}

[TestClass]
public sealed class EditingSessionTests
{
    [TestMethod]
    public void SnapshotsHistoryAndSavedVersionKeepIdentityAndRevisionContracts()
    {
        using var session = EditingFixtures.Open(EditingFixtures.Draft(new GridPosition(0, 0, 0)));
        var initial = session.Read();
        var id = initial.Elements.Single().Id;
        var added = EditingFixtures.Apply(session, EditingFixtures.Add(new(1, 0, 0)));
        var captured = session.Capture();
        var addedId = added.Elements.Single(e => e.At == new GridPosition(1, 0, 0)).Id;
        EditingFixtures.Apply(session, EditingFixtures.Add(new(2, 0, 0)));
        Assert.IsTrue(session.MarkSaved(captured));
        Assert.IsTrue(session.Read().IsDirty);
        var undo = EditingFixtures.Value(session.Undo());
        Assert.IsFalse(undo.IsDirty);
        Assert.IsTrue(undo.Snapshot.Stamp.Revision.Value > added.Snapshot.Stamp.Revision.Value);
        Assert.AreEqual(addedId, undo.Elements.Single(e => e.At == new GridPosition(1, 0, 0)).Id);
        Assert.AreEqual(id, undo.Elements.Single(e => e.At == new GridPosition(0, 0, 0)).Id);
        Assert.AreEqual(1, initial.Snapshot.Draft.Layout.Cells.Length);
        Assert.AreEqual(2, captured.Draft.Layout.Cells.Length);
        EditingFixtures.Value(session.Redo());
        EditingFixtures.Value(session.Undo());
        EditingFixtures.Apply(session, EditingFixtures.Add(new(3, 0, 0)));
        Assert.IsFalse(session.Read().CanRedo);
    }

    [TestMethod]
    public void EvaluationRequiresDeeplyEqualCurrentDraftAndMatchingStamp()
    {
        var child = EditingFixtures.Draft(new GridPosition(0, 0, 0)) with { Specification = new DesignSpecification.Assembly(null) };
        var draft = EditingFixtures.Draft() with { Layout = new([], [], [new NestedDesignItem.AssemblyCopy(new(2, 0, 0), VoxelOrientation.PxPy, child)]) };
        using var session = EditingFixtures.Open(draft);
        var capture = session.Capture();
        var equal = EditingFixtures.Evaluation(EditingFixtures.Clone(capture.Draft));
        Assert.IsTrue(session.AcceptEvaluation(capture.Stamp, equal));
        var changedChild = child with { Layout = child.Layout with { Cells = [new(new(1, 0, 0), 0)] } };
        var different = draft with { Layout = draft.Layout with { NestedItems = [new NestedDesignItem.AssemblyCopy(new(2, 0, 0), VoxelOrientation.PxPy, changedChild)] } };
        Assert.IsFalse(session.AcceptEvaluation(capture.Stamp, EditingFixtures.Evaluation(different)));
        Assert.AreSame(equal, session.Read().CurrentEvaluation);
        EditingFixtures.Apply(session, EditingFixtures.Add(new(5, 0, 0)));
        Assert.IsNull(session.Read().CurrentEvaluation);
        Assert.IsFalse(session.AcceptEvaluation(capture.Stamp, equal));
        EditingFixtures.Value(session.Undo());
        Assert.IsFalse(session.AcceptEvaluation(capture.Stamp, equal));
        session.Dispose();
        Assert.IsFalse(session.AcceptEvaluation(capture.Stamp, equal));
        Assert.IsFalse(session.MarkSaved(capture));
        Assert.ThrowsExactly<ObjectDisposedException>(() => session.Read());
    }

    [TestMethod]
    public void PreviewFreezesSelectionAndExpiresOnNewPreviewOrEdit()
    {
        using var session = EditingFixtures.Open(EditingFixtures.Draft(new(0, 0, 0), new(1, 0, 0)));
        var cells = session.Read().Elements;
        EditingFixtures.Value(session.Select(new EditSelection.Elements([cells[0].Id])));
        var removal = EditingFixtures.Preview(session, new EditOperation.RemoveSelection());
        EditingFixtures.Value(session.Select(new EditSelection.Elements([cells[1].Id])));
        EditingFixtures.Value(session.Apply(removal, false));
        Assert.AreEqual(cells[1].Id, session.Read().Elements.Single().Id);
        var old = EditingFixtures.Preview(session, EditingFixtures.Add(new(2, 0, 0)));
        var current = EditingFixtures.Preview(session, EditingFixtures.Add(new(3, 0, 0)));
        Assert.AreEqual("stale_preview", EditingFixtures.Code(session.Apply(old, true)));
        EditingFixtures.Value(session.Apply(current, true));
        Assert.AreEqual("stale_preview", EditingFixtures.Code(session.Apply(current, true)));
    }

    [TestMethod]
    public void StrokeDeduplicatesWithoutConnectingSamplesAndConflictRejectsWholeEdit()
    {
        using var session = EditingFixtures.Open(EditingFixtures.Draft(new GridPosition(0, 0, 0)));
        var before = session.Capture();
        var collision = EditingFixtures.Preview(session, new EditOperation.Cells(new EditFootprint.Stroke([new(0, 0, 0), new(5, 0, 0)], 1), new CellEdit.Add(EditingFixtures.Cube)));
        Assert.IsFalse(collision.CanApply);
        Assert.IsTrue(collision.Conflicts.Any(c => c.Problem.Code == "occupancy_conflict"));
        Assert.AreEqual("occupancy_conflict", EditingFixtures.Code(session.Apply(collision, true)));
        Assert.AreEqual(before.Stamp, session.Capture().Stamp);
        var preview = EditingFixtures.Preview(session, new EditOperation.Cells(new EditFootprint.Stroke([new(2, 0, 0), new(2, 0, 0), new(5, 0, 0)], 1), new CellEdit.Add(EditingFixtures.Cube)));
        Assert.AreEqual(2, preview.CoveredCells);
        Assert.AreEqual(2, preview.AffectedCells);
        EditingFixtures.Value(session.Apply(preview, false));
        CollectionAssert.AreEquivalent(new[] { new GridPosition(0, 0, 0), new(2, 0, 0), new(5, 0, 0) }, session.Capture().Draft.Layout.Cells.Select(c => c.At).ToArray());
    }

    [TestMethod]
    public void ReplaceRemoveAndNoChangeReportActualCountsAndKeepStableIds()
    {
        using var session = EditingFixtures.Open(EditingFixtures.Draft(new GridPosition(0, 0, 0)));
        var initial = session.Read();
        var noChange = EditingFixtures.Preview(session, new EditOperation.Cells(new EditFootprint.Cell(new(0, 0, 0)), new CellEdit.Replace(EditingFixtures.Cube)));
        Assert.AreEqual(0, noChange.AffectedCells);
        Assert.AreEqual(1, noChange.SkippedCells);
        EditingFixtures.Value(session.Apply(noChange, false));
        Assert.AreEqual(initial.Snapshot.Stamp, session.Capture().Stamp);
        var replaced = EditingFixtures.Apply(session, new EditOperation.Cells(new EditFootprint.Cell(new(0, 0, 0)), new CellEdit.Replace(EditingFixtures.Cube with { Paint = new("#ff0000") })));
        Assert.AreEqual(initial.Elements.Single().Id, replaced.Elements.Single().Id);
        var remove = EditingFixtures.Preview(session, new EditOperation.Cells(new EditFootprint.Stroke([new(0, 0, 0), new(9, 0, 0)], 1), new CellEdit.Remove()));
        Assert.AreEqual(1, remove.AffectedCells);
        Assert.AreEqual(1, remove.SkippedCells);
        EditingFixtures.Value(session.Apply(remove, false));
        var restored = EditingFixtures.Value(session.Undo());
        Assert.AreEqual(initial.Elements.Single().Id, restored.Elements.Single().Id);
    }

    [TestMethod]
    public void DeletedIdsAreNotReusedAndSuccessiveEditsCanBeFullyUndone()
    {
        using var session = EditingFixtures.Open(EditingFixtures.Draft(new GridPosition(0, 0, 0)));
        var original = session.Read().Elements.Single().Id;
        EditingFixtures.Apply(session, new EditOperation.Cells(new EditFootprint.Cell(new(0, 0, 0)), new CellEdit.Remove()));
        EditingFixtures.Apply(session, EditingFixtures.Add(new(0, 0, 0)));
        Assert.AreNotEqual(original, session.Read().Elements.Single().Id);
        for (var i = 1; i <= 12; i++) EditingFixtures.Apply(session, EditingFixtures.Add(new(i, 0, 0)));
        for (var i = 0; i < 14; i++) EditingFixtures.Value(session.Undo());
        Assert.IsFalse(session.Read().CanUndo);
        Assert.AreEqual(original, session.Read().Elements.Single().Id);
        Assert.IsFalse(session.Read().IsDirty);
    }
    [TestMethod]
    public void RegionLimitAndConfirmationAreCheckedBeforeMutation()
    {
        using var session = EditingFixtures.Open(EditingFixtures.Draft());
        var before = session.Capture();
        var preview = EditingFixtures.Preview(session, new EditOperation.Cells(new EditFootprint.Stroke([new(0, 0, 0)], 9), new CellEdit.Add(EditingFixtures.Cube)));
        Assert.AreEqual(729, preview.CoveredCells);
        Assert.IsTrue(preview.RequiresConfirmation);
        Assert.AreEqual("confirmation_required", EditingFixtures.Code(session.Apply(preview, false)));
        Assert.AreEqual(before.Stamp, session.Capture().Stamp);
        Assert.AreEqual(729, EditingFixtures.Value(session.Apply(preview, true)).Snapshot.Draft.Layout.Cells.Length);
        var tooLarge = session.Preview(new EditOperation.Cells(new EditFootprint.Stroke([new(30, 0, 0), new(60, 0, 0)], 16), new CellEdit.Add(EditingFixtures.Cube)));
        Assert.AreEqual("region_limit", EditingFixtures.Code(tooLarge));
    }
}
