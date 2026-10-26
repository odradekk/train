using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes.Tests;

[TestClass]
public sealed class EditingDerivedStateTests
{
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public void CellDeletionAndHistoryInsertionMatchColdDerivationWithUnchangedNestedCopy(int index)
    {
        var child = EditingFixtures.Draft(new GridPosition(0, 0, 0)) with
        { Specification = new DesignSpecification.Assembly(null) };
        var draft = EditingFixtures.Draft(new(0, 0, 0), new(2, 0, 0), new(4, 0, 0));
        draft = draft with { Layout = draft.Layout with { NestedItems =
            [new NestedDesignItem.AssemblyCopy(new(10, 0, 0), VoxelOrientation.PxPy, child)] } };
        using var session = EditingFixtures.Open(draft);
        var remaining = draft with { Layout = draft.Layout with { Cells = draft.Layout.Cells.RemoveAt(index) } };
        EditingFixtures.Apply(session, new EditOperation.Cells(new EditFootprint.Cell(draft.Layout.Cells[index].At), new CellEdit.Remove()));
        AssertMatchesCold(session, remaining);
        EditingFixtures.Value(session.Undo());
        AssertMatchesCold(session, draft);
        EditingFixtures.Value(session.Redo());
        AssertMatchesCold(session, remaining);
        EditingFixtures.Apply(session, EditingFixtures.Add(new(6, 0, 0)));
        var appended = remaining with { Layout = remaining.Layout with { Cells = remaining.Layout.Cells.Add(new(new(6, 0, 0), 0)) } };
        AssertMatchesCold(session, appended);

        var replacement = child with { Layout = child.Layout with { Cells = [new(new(1, 0, 0), 0), new(new(2, 0, 0), 0)] } };
        var assembly = session.Read().Elements.Single(e => e.Kind == EditElementKind.Assembly);
        var item = new NestedDesignItem.AssemblyCopy(new(12, 0, 0), VoxelOrientation.PxPy, replacement);
        EditingFixtures.Apply(session, new EditOperation.ReplaceNested(assembly.Id, item));
        AssertMatchesCold(session, appended with { Layout = appended.Layout with { NestedItems = [item] } });
        EditingFixtures.Value(session.Undo());
        AssertMatchesCold(session, appended);
    }

    [TestMethod]
    public void SavedCapturesRequireMatchingValueAndRemainUsableAfterHistoryBranches()
    {
        using var session = EditingFixtures.Open(EditingFixtures.Draft(new GridPosition(0, 0, 0)));
        EditingFixtures.Apply(session, EditingFixtures.Add(new(1, 0, 0)));
        var captured = session.Capture();
        var clone = captured with { Draft = EditingFixtures.Clone(captured.Draft) };
        Assert.IsTrue(session.MarkSaved(clone));
        Assert.IsFalse(session.Read().IsDirty);
        Assert.IsFalse(session.MarkSaved(captured with { Draft = captured.Draft with { Display = new("tampered", "") } }));
        Assert.IsFalse(session.MarkSaved(captured with { Stamp = captured.Stamp with { Revision = new(ulong.MaxValue) } }));
        Assert.IsFalse(session.MarkSaved(captured with { Stamp = captured.Stamp with { Content = new(session.Content.Value + 1) } }));
        Assert.IsFalse(session.Read().IsDirty);
        EditingFixtures.Value(session.Undo());
        Assert.IsTrue(session.Read().IsDirty);
        EditingFixtures.Apply(session, EditingFixtures.Add(new(2, 0, 0)));
        Assert.IsFalse(session.Read().CanRedo);
        Assert.IsTrue(session.MarkSaved(clone));
        Assert.IsTrue(session.Read().IsDirty);
        var current = session.Capture();
        Assert.IsTrue(session.MarkSaved(current with { Draft = EditingFixtures.Clone(current.Draft) }));
        Assert.IsFalse(session.Read().IsDirty);
    }

    [TestMethod]
    public void EvaluationAfterIncrementalEditUsesCurrentExportWithoutChangingSavedState()
    {
        using var session = EditingFixtures.Open(EditingFixtures.Draft(new(0, 0, 0), new(2, 0, 0)));
        EditingFixtures.Apply(session, new EditOperation.Cells(new EditFootprint.Cell(new(0, 0, 0)), new CellEdit.Remove()));
        var current = session.Capture();
        var expected = EditingFixtures.Draft(new GridPosition(2, 0, 0));
        var evaluation = EditingFixtures.Evaluation(EditingFixtures.Clone(expected));
        Assert.IsTrue(session.AcceptEvaluation(current.Stamp, evaluation));
        Assert.IsTrue(session.Read().IsDirty);
        Assert.IsFalse(session.AcceptEvaluation(current.Stamp, EditingFixtures.Evaluation(EditingFixtures.Draft(new GridPosition(0, 0, 0)))));
        Assert.AreSame(evaluation, session.Read().CurrentEvaluation);
        Assert.IsTrue(session.MarkSaved(current));
        Assert.IsFalse(session.Read().IsDirty);
        Assert.IsTrue(session.AcceptEvaluation(current.Stamp, evaluation));
        Assert.IsFalse(session.Read().IsDirty);
    }

    private static void AssertMatchesCold(IDesignEditSession session, DesignDraft expected)
    {
        using var cold = EditingFixtures.Open(expected);
        AssertDraft(expected, session.Capture().Draft);
        var actual = session.Read().Elements;
        var elements = cold.Read().Elements;
        Assert.AreEqual(elements.Length, actual.Length);
        for (var i = 0; i < elements.Length; i++)
        {
            var a = actual[i];
            var b = elements[i];
            Assert.AreEqual((b.Kind, b.At, b.Locked), (a.Kind, a.At, a.Locked));
            CollectionAssert.AreEqual(b.ObjectPath.ToArray(), a.ObjectPath.ToArray());
            Assert.AreEqual(b.Parent is null ? -1 : Array.FindIndex(elements.ToArray(), e => e.Id == b.Parent),
                a.Parent is null ? -1 : Array.FindIndex(actual.ToArray(), e => e.Id == a.Parent));
            Assert.AreEqual(b.Path?.GetType(), a.Path?.GetType());
            if (b.Path is DesignElementPath.Cell path)
            {
                var actualPath = (DesignElementPath.Cell)a.Path!;
                Assert.AreEqual(path.CellIndex, actualPath.CellIndex);
                CollectionAssert.AreEqual(path.NestedItemIndices.ToArray(), actualPath.NestedItemIndices.ToArray());
            }
        }
    }

    private static void AssertDraft(DesignDraft expected, DesignDraft actual)
    {
        Assert.AreEqual(expected.Display, actual.Display);
        Assert.AreEqual(expected.Specification, actual.Specification);
        CollectionAssert.AreEqual(expected.Layout.Palette, actual.Layout.Palette);
        CollectionAssert.AreEqual(expected.Layout.Cells, actual.Layout.Cells);
        Assert.AreEqual(expected.Layout.NestedItems.Length, actual.Layout.NestedItems.Length);
        for (var i = 0; i < expected.Layout.NestedItems.Length; i++)
        {
            var a = (NestedDesignItem.AssemblyCopy)actual.Layout.NestedItems[i];
            var b = (NestedDesignItem.AssemblyCopy)expected.Layout.NestedItems[i];
            Assert.AreEqual((b.At, b.Orientation), (a.At, a.Orientation));
            AssertDraft(b.Design, a.Design);
        }
    }
}
