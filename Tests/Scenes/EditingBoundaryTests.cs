using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes.Tests;

[TestClass]
public sealed class EditingBoundaryTests
{
    [TestMethod]
    public void PartialDraftOpensAndFailedEvaluationIsRejected()
    {
        var draft = EditingFixtures.Draft(new GridPosition(0, 0, 0));
        var partial = new DesignEvaluation.Partial(draft,
            new(new ComputedValue<double>.Known(0), new ComputedValue<Money>.Known(new(0)),
                new ComputedValue<long>.Known(0), new ComputedValue<ImmutableArray<PartProjection>>.Known([]),
                new ComputedValue<ImmutableArray<GoodsQuantity>>.Known([]), new ComputedValue<int>.Known(0)),
            new(0, new(0), 0, [], []), null, SceneFixtures.Projection([]), []);
        using var session = EditingFixtures.Value(ScenesFactory.Create().OpenEditor(SceneFixtures.Content(), partial));
        Assert.AreEqual(1, session.Capture().Draft.Layout.Cells.Length);
        Assert.IsFalse(session.Read().IsDirty);
        var failed = ScenesFactory.Create().OpenEditor(SceneFixtures.Content(), new DesignEvaluation.Failed([]));
        Assert.IsInstanceOfType<SceneResult<IDesignEditSession>.Failure>(failed);
    }

    [TestMethod]
    public void UnknownCompositeHasMarkerWithoutInventedOccupancyAndNewMissingReferenceIsRejected()
    {
        var definition = new CompositeDefinitionId("test:unknown");
        var good = new GoodId.Composite(definition);
        var reference = new ContentReference.Core(new CoreDefinitionReference.Composite(definition));
        var package = new PackageId("source");
        var diagnostic = new ContentDiagnostic("missing", ContentDiagnosticSeverity.Error, "test", reference,
            new(package, "part.json"), "", null, "Unavailable definition");
        var content = new FixedContentView
        {
            Generation = new(1), Core = SceneFixtures.Content().Core,
            VoxelAppearances = new Dictionary<VoxelDefinitionId, ContentLookup<VoxelAppearanceProjection>> { [SceneFixtures.Voxel] = new ContentLookup<VoxelAppearanceProjection>.Found(SceneFixtures.Cube) },
            Goods = new Dictionary<GoodId, ContentLookup<GoodPresentation>> { [good] = new ContentLookup<GoodPresentation>.Missing([diagnostic]) }
        };
        var nested = new NestedDesignItem.FunctionalCompositeReference(new(0, 0, 0), VoxelOrientation.PxPy, definition, null);
        var draft = EditingFixtures.Draft() with { Layout = new([], [], [nested]) };
        using var session = EditingFixtures.Open(draft, content: content);
        var frame = EditingFixtures.Frame(session);
        Assert.IsInstanceOfType<ScenePick.Miss>(frame.Pick(EditingFixtures.Ray()));
        Assert.IsTrue(frame.Update.Upserts.SelectMany(o => o.Visuals).OfType<SceneVisual.Missing>().Any(m => !m.KnownCell));
        Assert.IsFalse(EditingFixtures.Preview(session, EditingFixtures.Add(new(5, 0, 0))).OccupancyComplete);
        var before = session.Capture();
        var preview = session.Preview(new EditOperation.PlaceNested(nested with { At = new(7, 0, 0) }));
        Assert.AreEqual("content_missing", EditingFixtures.Code(preview));
        var problem = ((SceneResult<IEditPreview>.Failure)preview).Problems.Single();
        Assert.AreEqual(reference, problem.Content);
        Assert.AreEqual(package, problem.Package);
        Assert.AreEqual(before.Stamp, session.Capture().Stamp);
    }

    [TestMethod]
    public void NestedCopyCategoryCannotHideInsideLegalWrapperOrBeEnteredFromExistingDraft()
    {
        var sealedDraft = EditingFixtures.Draft(new GridPosition(0, 0, 0)) with
        { Specification = new DesignSpecification.FunctionalComposite([], null) };
        var invalidInner = new NestedDesignItem.AssemblyCopy(new(0, 0, 0), VoxelOrientation.PxPy, sealedDraft);
        var wrapper = EditingFixtures.Draft() with
        {
            Specification = new DesignSpecification.Assembly(null),
            Layout = new([], [], [invalidInner])
        };
        var invalidOuter = new NestedDesignItem.AssemblyCopy(new(4, 0, 0), VoxelOrientation.PxPy, wrapper);
        var validOuter = invalidOuter with { Design = wrapper with { Layout = new([], [], []) } };
        var draft = EditingFixtures.Draft() with { Layout = new([], [], [validOuter]) };
        using var session = EditingFixtures.Open(draft);
        var before = session.Read();
        var parent = before.Elements.Single(e => e.Kind == EditElementKind.Assembly);
        Assert.AreEqual("invalid_operation", EditingFixtures.Code(session.Preview(new EditOperation.PlaceNested(invalidOuter with { At = new(8, 0, 0) }))));
        Assert.AreEqual("invalid_operation", EditingFixtures.Code(session.Preview(new EditOperation.ReplaceNested(parent.Id, invalidOuter))));
        Assert.AreEqual(before.Snapshot.Stamp, session.Capture().Stamp);
        Assert.IsFalse(session.Read().CanUndo);
        Assert.IsFalse(session.Read().IsDirty);

        Assert.IsTrue(((NestedDesignItem.AssemblyCopy)session.Capture().Draft.Layout.NestedItems.Single()).Design.Layout.NestedItems.IsEmpty);
        using var repair = EditingFixtures.Open(draft with { Layout = new([], [], [invalidOuter]) });
        var inner = repair.Read().Elements.Single(e => e.Kind == EditElementKind.Assembly && e.Parent is not null);
        Assert.AreEqual("invalid_operation", EditingFixtures.Code(repair.Enter(inner.Id)));
        Assert.AreEqual("invalid_operation", EditingFixtures.Code(repair.CaptureObject(inner.Id)));
        Assert.IsFalse(repair.Read().CanUndo);
    }

    [TestMethod]
    [DataRow(0, 0, 2)]
    [DataRow(5, 1, 3)]
    [DataRow(1, 1, 2)]
    public void NestedReplacementCountsUnionOfOldAndNewOccupancy(int replacementX, int newCells, int affected)
    {
        var oldDraft = EditingFixtures.Draft(new(0, 0, 0), new(1, 0, 0)) with
        { Specification = new DesignSpecification.Assembly(null) };
        var oldItem = new NestedDesignItem.AssemblyCopy(new(0, 0, 0), VoxelOrientation.PxPy, oldDraft);
        using var session = EditingFixtures.Open(EditingFixtures.Draft() with { Layout = new([], [], [oldItem]) });
        var parent = session.Read().Elements.Single(e => e.Kind == EditElementKind.Assembly);
        var replacement = oldItem with
        {
            At = new(replacementX, 0, 0),
            Design = oldDraft with { Layout = new([EditingFixtures.Cube], newCells == 0 ? [] : [new(new(0, 0, 0), 0)], []) }
        };
        var preview = EditingFixtures.Preview(session, new EditOperation.ReplaceNested(parent.Id, replacement));
        Assert.IsTrue(preview.CanApply);
        Assert.AreEqual(affected, preview.AffectedCells);
        Assert.AreEqual(affected, preview.CoveredCells);
        CollectionAssert.AreEquivalent(newCells == 0 ? Array.Empty<GridPosition>() : new[] { new GridPosition(replacementX, 0, 0) }, preview.HighlightCells.ToArray());
        Assert.IsFalse(session.Read().CanUndo);
        var applied = EditingFixtures.Value(session.Apply(preview, false));
        Assert.AreEqual(newCells, ((NestedDesignItem.AssemblyCopy)applied.Snapshot.Draft.Layout.NestedItems.Single()).Design.Layout.Cells.Length);
    }

    [TestMethod]
    [DataRow(double.NaN)]
    [DataRow(double.PositiveInfinity)]
    [DataRow(double.NegativeInfinity)]
    public void CreatureWeaponIntervalMustBeFiniteBeforeOpenOrPropertiesPreview(double interval)
    {
        var draft = EditingFixtures.Draft() with
        { Specification = new DesignSpecification.Creature(null, new PartCapability.Weapon(1, 1)) };
        using var session = EditingFixtures.Open(draft);
        var before = session.Read();
        var invalid = new DesignSpecification.Creature(null, new PartCapability.Weapon(1, interval));
        Assert.ThrowsExactly<ArgumentException>(() => session.Preview(new EditOperation.Properties(draft.Display, invalid)));
        Assert.AreEqual(before.Snapshot.Stamp, session.Capture().Stamp);
        Assert.IsFalse(session.Read().CanUndo);
        Assert.IsFalse(session.Read().IsDirty);
        Assert.AreSame(before.CurrentEvaluation, session.Read().CurrentEvaluation);
        Assert.AreEqual(1d, ((DesignSpecification.Creature)session.Capture().Draft.Specification).Weapon!.IntervalSeconds);
        Assert.ThrowsExactly<ArgumentException>(() => ScenesFactory.Create().OpenEditor(SceneFixtures.Content(),
            EditingFixtures.Evaluation(draft with { Specification = invalid })));
    }
    [TestMethod]
    public void DeferredBatchOperationsRemainExplicitlyUnimplemented()
    {
        using var session = EditingFixtures.Open(EditingFixtures.Draft());
        EditOperation[] deferred = [
            new EditOperation.Cells(new EditFootprint.Box(new(new(0, 0, 0), new(1, 1, 1))), new CellEdit.Add(EditingFixtures.Cube)),
            new EditOperation.Cells(new EditFootprint.Cell(new(0, 0, 0)), new CellEdit.Paint(null)),
            new EditOperation.Cells(new EditFootprint.Cell(new(0, 0, 0)), new CellEdit.Shape(new("cube"))),
            new EditOperation.MoveSelection(new(1, 0, 0)), new EditOperation.RotateSelection(GridAxis.Y, new(0, 0, 0), 1),
            new EditOperation.CopySelection(new(1, 0, 0)), new EditOperation.PaintSelection(null), new EditOperation.ShapeSelection(new("cube"))];
        foreach (var operation in deferred)
            StringAssert.Contains(Assert.ThrowsExactly<NotImplementedException>(() => session.Preview(operation)).Message, "#334");
    }
}
