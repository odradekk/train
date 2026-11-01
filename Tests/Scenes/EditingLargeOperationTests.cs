using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes.Tests;

[TestClass]
public sealed class EditingLargeOperationTests
{
    [TestMethod]
    public void LargeNestedOperationsApplyDirectly()
    {
        var child = EditingFixtures.Draft(Enumerable.Range(0, 513).Select(x => new GridPosition(x, 0, 0)).ToArray()) with { Specification = new DesignSpecification.Assembly(null) };
        var item = new NestedDesignItem.AssemblyCopy(new(0, 0, 0), VoxelOrientation.PxPy, child);
        using var session = EditingFixtures.Open(EditingFixtures.Draft());
        ApplyLargeOperation(new EditOperation.PlaceNested(item));
        var id = session.Read().Elements.Single(e => e.Kind == EditElementKind.Assembly).Id;
        ApplyLargeOperation(new EditOperation.ReplaceNested(id, item with { At = new(0, 1, 0) }));
        id = session.Read().Elements.Single(e => e.Kind == EditElementKind.Assembly).Id;
        EditingFixtures.Value(session.Select(new EditSelection.Elements([id])));
        ApplyLargeOperation(new EditOperation.RemoveSelection());
        Assert.IsTrue(session.Capture().Draft.Layout.NestedItems.IsEmpty);

        void ApplyLargeOperation(EditOperation operation)
        {
            var preview = EditingFixtures.Preview(session, operation);
            Assert.IsTrue(preview.AffectedCells > 512);
            EditingFixtures.Value(session.Apply(operation));
        }
    }

    [TestMethod]
    public void BrushWidthAndCoverageHaveExact16And5000Boundaries()
    {
        using var session = EditingFixtures.Open(EditingFixtures.Draft());
        var atLimit = EditingFixtures.Preview(session, new EditOperation.Cells(new EditFootprint.Stroke([new(0, 0, 0)], 16), new CellEdit.Add(EditingFixtures.Cube)));
        Assert.AreEqual(4096, atLimit.CoveredCells);
        Assert.AreEqual(4096, atLimit.AffectedCells);
        Assert.IsTrue(atLimit.CanApply);
        Assert.AreEqual("region_limit", EditingFixtures.Code(session.Preview(new EditOperation.Cells(new EditFootprint.Stroke([new(0, 0, 0)], 17), new CellEdit.Add(EditingFixtures.Cube)))));
        var samples = Enumerable.Range(0, 5001).Select(x => new GridPosition(x, 0, 0)).ToImmutableArray();
        var exact = EditingFixtures.Preview(session, new EditOperation.Cells(new EditFootprint.Stroke(samples.RemoveAt(5000), 1), new CellEdit.Add(EditingFixtures.Cube)));
        Assert.AreEqual(5000, exact.CoveredCells);
        var tooLarge = new EditOperation.Cells(new EditFootprint.Stroke(samples, 1), new CellEdit.Add(EditingFixtures.Cube));
        var unchanged = session.Capture().Stamp;
        Assert.AreEqual("region_limit", EditingFixtures.Code(session.Preview(tooLarge)));
        Assert.AreEqual("region_limit", EditingFixtures.Code(session.Apply(tooLarge)));
        Assert.AreEqual(unchanged, session.Capture().Stamp);
        Assert.IsFalse(session.Read().CanUndo);
    }

    [TestMethod]
    [DataRow(512)]
    [DataRow(513)]
    [DataRow(5000)]
    public void CellEditsApplyDirectlyAsOneHistoryEntry(int count)
    {
        using var session = EditingFixtures.Open(EditingFixtures.Draft());
        var samples = Enumerable.Range(0, count).Select(x => new GridPosition(x, 0, 0)).ToImmutableArray();
        var previewOperation = new EditOperation.Cells(new EditFootprint.Stroke(samples, 1), new CellEdit.Add(EditingFixtures.Cube));
        var preview = EditingFixtures.Preview(session, previewOperation);
        Assert.AreEqual(count, preview.AffectedCells);
        EditingFixtures.Value(session.Apply(previewOperation));
        Assert.AreEqual(count, session.Capture().Draft.Layout.Cells.Length);
        Assert.IsTrue(EditingFixtures.Value(session.Undo()).Snapshot.Draft.Layout.Cells.IsEmpty);
        Assert.IsFalse(session.Read().CanUndo);
    }

    [TestMethod]
    public void LargeSelectionAppliesAsOneHistoryEntry()
    {
        using var session = EditingFixtures.Open(EditingFixtures.Draft(Enumerable.Range(0, 513).Select(x => new GridPosition(x, 0, 0)).ToArray()));
        EditingFixtures.Value(session.Select(new EditSelection.Elements(session.Read().Elements.Select(element => element.Id).ToImmutableArray())));
        var previewOperation = new EditOperation.PaintSelection(new("#123456"));
        var preview = EditingFixtures.Preview(session, previewOperation);
        Assert.AreEqual(513, preview.AffectedCells);
        EditingFixtures.Value(session.Apply(previewOperation));
        Assert.IsFalse(EditingFixtures.Value(session.Undo()).CanUndo);
    }

}
