using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes.Tests;

[TestClass]
public sealed class EditingRegionTests
{
    [TestMethod]
    public void BoxIncludesBothEndsAndEachActionIsOneHistoryEntry()
    {
        using var session = EditingFixtures.Open(EditingFixtures.Draft());
        var box = new EditFootprint.Box(new(new(-1, 0, 0), new(1, 1, 0)));
        var added = EditingFixtures.Apply(session, new EditOperation.Cells(box, new CellEdit.Add(EditingFixtures.Cube)));
        CollectionAssert.AreEquivalent(new[] { new GridPosition(-1, 0, 0), new(-1, 1, 0), new(0, 0, 0), new(0, 1, 0), new(1, 0, 0), new(1, 1, 0) }, added.Snapshot.Draft.Layout.Cells.Select(c => c.At).ToArray());
        var ids = added.Elements.Select(e => e.Id).ToArray();
        var color = new PaintColor("#123456");
        var replaced = EditingFixtures.Apply(session, new EditOperation.Cells(box, new CellEdit.Replace(EditingFixtures.Cube with { Paint = color })));
        Assert.IsTrue(replaced.Snapshot.Draft.Layout.Cells.All(c => replaced.Snapshot.Draft.Layout.Palette[c.PaletteIndex].Paint == color));
        CollectionAssert.AreEqual(ids, replaced.Elements.Select(e => e.Id).ToArray());
        EditingFixtures.Apply(session, new EditOperation.Cells(box, new CellEdit.Remove()));
        Assert.IsTrue(session.Capture().Draft.Layout.Cells.IsEmpty);
        Assert.AreEqual(6, EditingFixtures.Value(session.Undo()).Snapshot.Draft.Layout.Cells.Length);
        var unpainted = EditingFixtures.Value(session.Undo()).Snapshot.Draft;
        Assert.IsTrue(unpainted.Layout.Cells.All(c => unpainted.Layout.Palette[c.PaletteIndex].Paint is null));
        Assert.IsTrue(EditingFixtures.Value(session.Undo()).Snapshot.Draft.Layout.Cells.IsEmpty);
        Assert.IsFalse(session.Read().CanUndo);
    }

    [TestMethod]
    public void BoxVolumeIsLimitedBeforeEnumerationAndUsesConfirmationFromActualChanges()
    {
        using var session = EditingFixtures.Open(EditingFixtures.Draft());
        var exact = EditingFixtures.Preview(session, new EditOperation.Cells(new EditFootprint.Box(new(new(0, 0, 0), new(49, 9, 9))), new CellEdit.Add(EditingFixtures.Cube)));
        Assert.AreEqual(5000, exact.CoveredCells);
        Assert.AreEqual(5000, exact.AffectedCells);
        Assert.IsTrue(exact.RequiresConfirmation);
        Assert.AreEqual("confirmation_required", EditingFixtures.Code(session.Apply(exact, false)));
        var applied = EditingFixtures.Value(session.Apply(exact, true));
        Assert.AreEqual(5000, applied.Snapshot.Draft.Layout.Cells.Length);
        Assert.IsTrue(EditingFixtures.Value(session.Undo()).Snapshot.Draft.Layout.Cells.IsEmpty);
        Assert.IsFalse(session.Read().CanUndo);
        foreach (var bounds in new[] { new GridBounds(new(0, 0, 0), new(5000, 0, 0)), new GridBounds(new(int.MinValue, int.MinValue, int.MinValue), new(int.MaxValue, int.MaxValue, int.MaxValue)) })
            Assert.AreEqual("region_limit", EditingFixtures.Code(session.Preview(new EditOperation.Cells(new EditFootprint.Box(bounds), new CellEdit.Remove()))));
        Assert.IsFalse(session.Read().CanUndo);
    }

    [TestMethod]
    public void PaintCountsEmptyAndUnchangedCellsAndShapeSkipsUnsupportedValues()
    {
        var color = new PaintColor("#123456");
        var draft = EditingFixtures.Draft(new(0, 0, 0), new(1, 0, 0));
        draft = draft with { Layout = draft.Layout with { Palette = [EditingFixtures.Cube, EditingFixtures.Cube with { Paint = color }], Cells = [new(new(0, 0, 0), 0), new(new(1, 0, 0), 1)] } };
        using var session = EditingFixtures.Open(draft);
        var paint = EditingFixtures.Preview(session, new EditOperation.Cells(new EditFootprint.Box(new(new(0, 0, 0), new(2, 0, 0))), new CellEdit.Paint(color)));
        Assert.AreEqual(3, paint.CoveredCells);
        Assert.AreEqual(1, paint.AffectedCells);
        Assert.AreEqual(2, paint.SkippedCells);
        EditingFixtures.Value(session.Apply(paint, false));
        var before = session.Capture();
        var shape = EditingFixtures.Preview(session, new EditOperation.Cells(new EditFootprint.Stroke([new(0, 0, 0), new(1, 0, 0)], 1), new CellEdit.Shape(new("unsupported"))));
        Assert.IsTrue(shape.CanApply);
        Assert.AreEqual(0, shape.AffectedCells);
        Assert.AreEqual(2, shape.SkippedCells);
        Assert.AreEqual(before.Stamp, EditingFixtures.Value(session.Apply(shape, false)).Snapshot.Stamp);
        var cleared = EditingFixtures.Apply(session, new EditOperation.Cells(new EditFootprint.Cell(new(0, 0, 0)), new CellEdit.Paint(null)));
        Assert.IsNull(cleared.Snapshot.Draft.Layout.Palette[cleared.Snapshot.Draft.Layout.Cells[0].PaletteIndex].Paint);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void AppearanceFootprintCannotEditNestedCellsOrPartiallyCommit(bool shape)
    {
        var child = EditingFixtures.Draft(new GridPosition(0, 0, 0)) with { Specification = new DesignSpecification.Assembly(null) };
        var draft = EditingFixtures.Draft(new GridPosition(0, 0, 0));
        draft = draft with { Layout = draft.Layout with { NestedItems = [new NestedDesignItem.AssemblyCopy(new(1, 0, 0), VoxelOrientation.PxPy, child)] } };
        using var session = EditingFixtures.Open(draft);
        var before = session.Capture();
        CellEdit action = shape ? new CellEdit.Shape(new("cube")) : new CellEdit.Paint(new("#123456"));
        var preview = EditingFixtures.Preview(session, new EditOperation.Cells(new EditFootprint.Box(new(new(0, 0, 0), new(1, 0, 0))), action));
        Assert.IsFalse(preview.CanApply);
        Assert.AreEqual(new GridPosition(1, 0, 0), preview.Conflicts.Single().Cell);
        Assert.AreEqual("occupancy_conflict", EditingFixtures.Code(session.Apply(preview, true)));
        Assert.AreEqual(before.Stamp, session.Capture().Stamp);
        Assert.IsFalse(session.Read().CanUndo);
    }
}
