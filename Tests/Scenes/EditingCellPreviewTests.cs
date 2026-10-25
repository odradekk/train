using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes.Tests;

[TestClass]
public sealed class EditingCellPreviewTests
{
    private static CellEdit Action(string action) => action switch
    {
        "remove" => new CellEdit.Remove(),
        "paint" => new CellEdit.Paint(new("#123456")),
        "shape" => new CellEdit.Shape(new("cube")),
        _ => new CellEdit.Replace(EditingFixtures.Cube with { Paint = new("#abcdef") })
    };

    private static VoxelPaletteEntry ExpectedValue(string action) => action switch
    {
        "paint" => EditingFixtures.Cube with { Paint = new("#123456") },
        "shape" => EditingFixtures.Cube with { Shape = new("cube") },
        _ => EditingFixtures.Cube with { Paint = new("#abcdef") }
    };

    private static void AssertCells(DesignDraft draft, params (GridPosition At, VoxelPaletteEntry Value)[] expected) =>
        CollectionAssert.AreEquivalent(expected, draft.Layout.Cells.Select(cell =>
            (cell.At, draft.Layout.Palette[cell.PaletteIndex])).ToArray());

    [TestMethod]
    [DataRow("remove")]
    [DataRow("paint")]
    [DataRow("shape")]
    [DataRow("replace")]
    public void CellBoxAndStrokeKeepCountsHistoryAndOldSnapshots(string action)
    {
        EditFootprint[] footprints = [new EditFootprint.Cell(new(0, 0, 0)),
            new EditFootprint.Box(new(new(0, 0, 0), new(0, 0, 0))),
            new EditFootprint.Stroke([new(0, 0, 0), new(0, 0, 0)], 1)];
        foreach (var footprint in footprints)
        {
            using var session = EditingFixtures.Open(EditingFixtures.Draft(new(0, 0, 0), new(5, 0, 0)));
            var old = session.Capture();
            var preview = EditingFixtures.Preview(session, new EditOperation.Cells(footprint, Action(action)));
            Assert.IsTrue(preview.CanApply);
            Assert.IsTrue(preview.OccupancyComplete);
            Assert.AreEqual(1, preview.CoveredCells);
            Assert.AreEqual(1, preview.AffectedCells);
            Assert.AreEqual(0, preview.SkippedCells);
            Assert.IsFalse(preview.RequiresConfirmation);
            AssertCells(session.Capture().Draft, (new(0, 0, 0), EditingFixtures.Cube), (new(5, 0, 0), EditingFixtures.Cube));
            var expected = action == "remove"
                ? new[] { (new GridPosition(5, 0, 0), EditingFixtures.Cube) }
                : new[] { (new GridPosition(0, 0, 0), ExpectedValue(action)), (new GridPosition(5, 0, 0), EditingFixtures.Cube) };
            AssertCells(EditingFixtures.Value(session.Apply(preview, false)).Snapshot.Draft, expected);
            AssertCells(EditingFixtures.Value(session.Undo()).Snapshot.Draft, (new(0, 0, 0), EditingFixtures.Cube), (new(5, 0, 0), EditingFixtures.Cube));
            AssertCells(EditingFixtures.Value(session.Redo()).Snapshot.Draft, expected);
            AssertCells(old.Draft, (new(0, 0, 0), EditingFixtures.Cube), (new(5, 0, 0), EditingFixtures.Cube));
        }
    }

    [TestMethod]
    [DataRow("remove")]
    [DataRow("paint")]
    [DataRow("shape")]
    [DataRow("replace")]
    public void EmptyFootprintTargetsAreAllSkippedWithoutHistory(string action)
    {
        using var session = EditingFixtures.Open(EditingFixtures.Draft(new GridPosition(5, 0, 0)));
        var before = session.Capture();
        var preview = EditingFixtures.Preview(session, new EditOperation.Cells(
            new EditFootprint.Box(new(new(0, 0, 0), new(2, 0, 0))), Action(action)));
        Assert.AreEqual(3, preview.CoveredCells);
        Assert.AreEqual(0, preview.AffectedCells);
        Assert.AreEqual(3, preview.SkippedCells);
        Assert.IsTrue(preview.CanApply);
        Assert.AreEqual(before.Stamp, EditingFixtures.Value(session.Apply(preview, false)).Snapshot.Stamp);
        Assert.IsFalse(session.Read().CanUndo);
        AssertCells(session.Capture().Draft, (new(5, 0, 0), EditingFixtures.Cube));
    }

    [TestMethod]
    [DataRow("remove")]
    [DataRow("paint")]
    [DataRow("shape")]
    [DataRow("replace")]
    public void LockedAndFreeCellsRejectTheWholeStroke(string action)
    {
        var scene = SceneFixtures.Projection([]) with
        {
            EditLocks = [new(new DesignElementPath.Cell([], 0),
                new ContentReference.Core(new CoreDefinitionReference.Voxel(SceneFixtures.Voxel)), new("source"),
                [LockedDesignOperation.Remove, LockedDesignOperation.Paint, LockedDesignOperation.ChangeShape, LockedDesignOperation.Replace], [])]
        };
        using var session = EditingFixtures.Open(EditingFixtures.Draft(new(0, 0, 0), new(1, 0, 0)), scene);
        var before = session.Capture();
        var preview = EditingFixtures.Preview(session, new EditOperation.Cells(
            new EditFootprint.Stroke([new(0, 0, 0), new(1, 0, 0)], 1), Action(action)));
        Assert.IsFalse(preview.CanApply);
        Assert.AreEqual(1, preview.AffectedCells);
        Assert.AreEqual(new GridPosition(0, 0, 0), preview.Conflicts.Single().Cell);
        Assert.AreEqual("edit_locked", EditingFixtures.Code(session.Apply(preview, true)));
        Assert.AreEqual(before.Stamp, session.Capture().Stamp);
        Assert.IsFalse(session.Read().CanUndo);
        AssertCells(session.Capture().Draft, (new(0, 0, 0), EditingFixtures.Cube), (new(1, 0, 0), EditingFixtures.Cube));
    }

    [TestMethod]
    [DataRow("remove")]
    [DataRow("paint")]
    [DataRow("shape")]
    [DataRow("replace")]
    public void LargeCellEditsRequireConfirmationBeforeApplying(string action)
    {
        var positions = Enumerable.Range(0, 513).Select(x => new GridPosition(x, 0, 0)).ToArray();
        using var session = EditingFixtures.Open(EditingFixtures.Draft(positions));
        var before = session.Capture();
        var preview = EditingFixtures.Preview(session, new EditOperation.Cells(
            new EditFootprint.Box(new(new(0, 0, 0), new(512, 0, 0))), Action(action)));
        Assert.AreEqual(513, preview.AffectedCells);
        Assert.IsTrue(preview.RequiresConfirmation);
        Assert.AreEqual("confirmation_required", EditingFixtures.Code(session.Apply(preview, false)));
        Assert.AreEqual(before.Stamp, session.Capture().Stamp);
        (GridPosition At, VoxelPaletteEntry Value)[] expected = action == "remove" ? [] : positions.Select(at => (at, ExpectedValue(action))).ToArray();
        AssertCells(EditingFixtures.Value(session.Apply(preview, true)).Snapshot.Draft, expected);
        AssertCells(before.Draft, positions.Select(at => (at, EditingFixtures.Cube)).ToArray());
    }

    [TestMethod]
    [DataRow("remove")]
    [DataRow("paint")]
    [DataRow("shape")]
    [DataRow("replace")]
    public void EnteredEditsIgnoreUnrelatedOverlapButRejectHostOccupancy(string action)
    {
        var child = EditingFixtures.Draft(new GridPosition(0, 0, 0)) with { Specification = new DesignSpecification.Assembly(null) };
        var draft = EditingFixtures.Draft() with
        {
            Layout = new([EditingFixtures.Cube], [new(new(5, 0, 0), 0), new(new(20, 0, 0), 0), new(new(20, 0, 0), 0)],
                [new NestedDesignItem.AssemblyCopy(new(4, 0, 0), VoxelOrientation.PxPy, child)])
        };
        using var session = EditingFixtures.Open(draft);
        EditingFixtures.Value(session.Enter(session.Read().Elements.Single(e => e.Kind == EditElementKind.Assembly).Id));
        var before = session.Capture();
        var conflict = EditingFixtures.Preview(session, new EditOperation.Cells(
            new EditFootprint.Box(new(new(0, 0, 0), new(1, 0, 0))), Action(action)));
        Assert.AreEqual(1, conflict.AffectedCells);
        Assert.AreEqual(new GridPosition(1, 0, 0), conflict.Conflicts.Single().Cell);
        Assert.AreEqual("occupancy_conflict", EditingFixtures.Code(session.Apply(conflict, true)));
        Assert.AreEqual(before.Stamp, session.Capture().Stamp);
        var preview = EditingFixtures.Preview(session, new EditOperation.Cells(new EditFootprint.Cell(new(0, 0, 0)), Action(action)));
        Assert.IsTrue(preview.CanApply);
        Assert.IsTrue(preview.Conflicts.IsEmpty);
        var result = EditingFixtures.Value(session.Apply(preview, false)).Snapshot.Draft;
        AssertCells(result, (new(5, 0, 0), EditingFixtures.Cube), (new(20, 0, 0), EditingFixtures.Cube), (new(20, 0, 0), EditingFixtures.Cube));
        var nested = (NestedDesignItem.AssemblyCopy)result.Layout.NestedItems.Single();
        Assert.AreEqual(new GridPosition(4, 0, 0), nested.At);
        AssertCells(nested.Design, action == "remove" ? [] : [(new(0, 0, 0), ExpectedValue(action))]);
        AssertCells(((NestedDesignItem.AssemblyCopy)before.Draft.Layout.NestedItems.Single()).Design, (new(0, 0, 0), EditingFixtures.Cube));
    }

    [TestMethod]
    [DataRow("remove")]
    [DataRow("paint")]
    [DataRow("shape")]
    [DataRow("replace")]
    public void CompositeCompletenessSurvivesCellEdits(string action)
    {
        var definition = new CompositeDefinitionId("test:preview");
        var good = new GoodId.Composite(definition);
        var hash = new DesignHash("preview-design");
        var composite = EditingFixtures.Draft(new GridPosition(0, 0, 0)) with { Specification = new DesignSpecification.FunctionalComposite([], null) };
        var content = new FixedContentView
        {
            Generation = new(1), Core = SceneFixtures.Content().Core,
            VoxelAppearances = new Dictionary<VoxelDefinitionId, ContentLookup<VoxelAppearanceProjection>> { [SceneFixtures.Voxel] = new ContentLookup<VoxelAppearanceProjection>.Found(SceneFixtures.Cube) },
            Goods = new Dictionary<GoodId, ContentLookup<GoodPresentation>> { [good] = new ContentLookup<GoodPresentation>.Found(new(good, new(new("preview", ""), null, null, false), hash, false)) },
            Designs = new Dictionary<DesignHash, ContentLookup<DesignEvaluation>> { [hash] = new ContentLookup<DesignEvaluation>.Found(EditingFixtures.Evaluation(composite) with { Scene = SceneFixtures.Projection([SceneFixtures.Cell(new(0, 0, 0))]) }) }
        };
        var reference = new ContentReference.Core(new CoreDefinitionReference.Composite(definition));
        var diagnostic = new ContentDiagnostic("missing", ContentDiagnosticSeverity.Error, "test", reference,
            new(new("source"), "part.json"), "", null, "Unavailable definition");
        var missingContent = new FixedContentView
        {
            Generation = content.Generation, Core = content.Core,
            VoxelAppearances = new Dictionary<VoxelDefinitionId, ContentLookup<VoxelAppearanceProjection>> { [SceneFixtures.Voxel] = new ContentLookup<VoxelAppearanceProjection>.Found(SceneFixtures.Cube) },
            Goods = new Dictionary<GoodId, ContentLookup<GoodPresentation>> { [good] = new ContentLookup<GoodPresentation>.Missing([diagnostic]) }
        };
        foreach (var complete in new[] { false, true })
        {
            var draft = EditingFixtures.Draft() with
            {
                Layout = new([EditingFixtures.Cube], [new(new(0, 0, 0), 0)],
                    [new NestedDesignItem.FunctionalCompositeReference(new(8, 0, 0), VoxelOrientation.PxPy, definition, null)])
            };
            using var session = EditingFixtures.Open(draft, content: complete ? content : missingContent);
            var preview = EditingFixtures.Preview(session, new EditOperation.Cells(new EditFootprint.Cell(new(0, 0, 0)), Action(action)));
            Assert.AreEqual(complete, preview.OccupancyComplete);
            Assert.IsTrue(preview.CanApply);
            var result = EditingFixtures.Value(session.Apply(preview, false)).Snapshot.Draft;
            AssertCells(result, action == "remove" ? [] : [(new(0, 0, 0), ExpectedValue(action))]);
            CollectionAssert.AreEqual(draft.Layout.NestedItems, result.Layout.NestedItems);
        }
    }
}
