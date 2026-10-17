using System.Collections.Immutable;
using System.Numerics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes.Tests;

[TestClass]
public sealed class EditingHierarchyTests
{
    private static DesignDraft Assembly => EditingFixtures.Draft(new GridPosition(0, 0, 0)) with { Specification = new DesignSpecification.Assembly(null) };

    [TestMethod]
    public void EnterEditsLocalCopyAndCaptureObjectExportsIndependentDraft()
    {
        using var session = EditingFixtures.Open(EditingFixtures.Draft());
        var state = EditingFixtures.Apply(session, new EditOperation.PlaceNested(new NestedDesignItem.AssemblyCopy(new(4, 0, 0), VoxelOrientation.PxPy, Assembly)));
        var assembly = state.Elements.Single(e => e.Kind == EditElementKind.Assembly);
        var preview = EditingFixtures.Preview(session, EditingFixtures.Add(new(9, 0, 0)));
        var entered = EditingFixtures.Value(session.Enter(assembly.Id));
        Assert.AreEqual(state.Snapshot.Stamp, entered.Snapshot.Stamp);
        Assert.IsTrue(entered.Selection.IsEmpty);
        Assert.AreEqual("stale_preview", EditingFixtures.Code(session.Apply(preview, true)));
        EditingFixtures.Apply(session, EditingFixtures.Add(new(1, 0, 0)));
        EditingFixtures.Apply(session, new EditOperation.Properties(new("Edited", "local"), Assembly.Specification));
        var exported = EditingFixtures.Value(session.CaptureObject(assembly.Id));
        Assert.AreEqual("Edited", exported.Display.Name);
        CollectionAssert.AreEquivalent(new[] { new GridPosition(0, 0, 0), new(1, 0, 0) }, exported.Layout.Cells.Select(c => c.At).ToArray());
        Assert.IsTrue(session.Capture().Draft.Layout.Cells.IsEmpty);
        EditingFixtures.Value(session.Enter(null));
        EditingFixtures.Apply(session, new EditOperation.ReplaceNested(assembly.Id, new NestedDesignItem.AssemblyCopy(new(8, 0, 0), VoxelOrientation.PxPy, Assembly)));
        Assert.AreEqual(2, exported.Layout.Cells.Length);
        Assert.AreEqual(1, ((NestedDesignItem.AssemblyCopy)session.Capture().Draft.Layout.NestedItems.Single()).Design.Layout.Cells.Length);
    }

    [TestMethod]
    public void LocateUsesCurrentPathAndRejectsRevisionAfterIndexChanges()
    {
        using var session = EditingFixtures.Open(EditingFixtures.Draft(new(0, 0, 0), new(1, 0, 0)));
        var initial = session.Read();
        var second = initial.Elements.Single(e => e.At == new GridPosition(1, 0, 0));
        Assert.AreEqual(second.Id, EditingFixtures.Value(session.Locate(initial.Snapshot.Stamp, new DesignElementPath.Cell([], 1))));
        EditingFixtures.Apply(session, new EditOperation.Cells(new EditFootprint.Cell(new(0, 0, 0)), new CellEdit.Remove()));
        Assert.AreEqual("stale_request", EditingFixtures.Code(session.Locate(initial.Snapshot.Stamp, new DesignElementPath.Cell([], 1))));
        Assert.AreEqual(second.Id, EditingFixtures.Value(session.Locate(session.Capture().Stamp, new DesignElementPath.Cell([], 0))));
    }

    [TestMethod]
    public void CompositeOccupancyIsResolvedThroughGoodDesignAndRemainsClosed()
    {
        var definition = new CompositeDefinitionId("test:closed");
        var good = new GoodId.Composite(definition);
        var hash = new DesignHash("closed-design");
        var definitionDraft = EditingFixtures.Draft(new(0, 0, 0), new(1, 0, 0)) with
        { Specification = new DesignSpecification.FunctionalComposite([], null) };
        var projection = SceneFixtures.Projection([SceneFixtures.Cell(new(0, 0, 0)), SceneFixtures.Cell(new(1, 0, 0), 2)]);
        var content = new FixedContentView
        {
            Generation = new(1), Core = SceneFixtures.Content().Core,
            VoxelAppearances = new Dictionary<VoxelDefinitionId, ContentLookup<VoxelAppearanceProjection>> { [SceneFixtures.Voxel] = new ContentLookup<VoxelAppearanceProjection>.Found(SceneFixtures.Cube) },
            Goods = new Dictionary<GoodId, ContentLookup<GoodPresentation>> { [good] = new ContentLookup<GoodPresentation>.Found(new(good, new(new("closed", ""), null, null, false), hash, false)) },
            Designs = new Dictionary<DesignHash, ContentLookup<DesignEvaluation>> { [hash] = new ContentLookup<DesignEvaluation>.Found(EditingFixtures.Evaluation(definitionDraft) with { Scene = projection }) }
        };
        using var session = EditingFixtures.Open(EditingFixtures.Draft(), content: content);
        var state = EditingFixtures.Apply(session, new EditOperation.PlaceNested(new NestedDesignItem.FunctionalCompositeReference(new(4, 0, 0), VoxelOrientation.PxPy, definition, null)));
        var composite = state.Elements.Single(e => e.Kind == EditElementKind.FunctionalComposite);
        Assert.AreEqual("invalid_operation", EditingFixtures.Code(session.Enter(composite.Id)));
        Assert.AreEqual("invalid_operation", EditingFixtures.Code(session.CaptureObject(composite.Id)));
        var frame = EditingFixtures.Frame(session);
        var left = ((ScenePick.Hit)frame.Pick(EditingFixtures.Ray(4.5f))).Value;
        var right = ((ScenePick.Hit)frame.Pick(EditingFixtures.Ray(5.5f))).Value;
        Assert.AreEqual(composite.Id, ((PickSubject.Editing)left.Subject).Element);
        Assert.AreEqual(composite.Id, ((PickSubject.Editing)right.Subject).Element);
        var conflict = EditingFixtures.Preview(session, EditingFixtures.Add(new(5, 0, 0)));
        Assert.IsTrue(conflict.OccupancyComplete);
        Assert.IsFalse(conflict.CanApply);
        EditingFixtures.Value(session.Select(new EditSelection.Box(new(new(5, 0, 0), new(5, 0, 0)))));
        CollectionAssert.AreEqual(new[] { composite.Id }, session.Read().Selection.ToArray());
        EditingFixtures.Apply(session, new EditOperation.RemoveSelection());
        Assert.IsTrue(session.Capture().Draft.Layout.NestedItems.IsEmpty);
    }

    [TestMethod]
    public void MissingDescendantLocksParentRemovalReplacementAndExport()
    {
        var path = new DesignElementPath.Cell([0], 0);
        var reference = new ContentReference.Core(new CoreDefinitionReference.Voxel(SceneFixtures.Voxel));
        var package = new PackageId("missing-source");
        var scene = SceneFixtures.Projection([]) with
        {
            EditLocks = [new(path, reference, package,
                [LockedDesignOperation.Remove, LockedDesignOperation.Replace, LockedDesignOperation.Copy], [])]
        };
        var draft = EditingFixtures.Draft() with { Layout = new([], [], [new NestedDesignItem.AssemblyCopy(new(4, 0, 0), VoxelOrientation.PxPy, Assembly)]) };
        using var session = EditingFixtures.Open(draft, scene);
        var parent = session.Read().Elements.Single(e => e.Kind == EditElementKind.Assembly);
        Assert.AreEqual("edit_locked", EditingFixtures.Code(session.CaptureObject(parent.Id)));
        EditingFixtures.Value(session.Select(new EditSelection.Elements([parent.Id])));
        var removal = EditingFixtures.Preview(session, new EditOperation.RemoveSelection());
        Assert.IsFalse(removal.CanApply);
        var problem = removal.Conflicts.Single(c => c.Problem.Code == "edit_locked").Problem;
        Assert.AreEqual(reference, problem.Content);
        Assert.AreEqual(package, problem.Package);
        var replacement = EditingFixtures.Preview(session, new EditOperation.ReplaceNested(parent.Id, new NestedDesignItem.AssemblyCopy(new(4, 0, 0), VoxelOrientation.PxPy, Assembly)));
        Assert.IsFalse(replacement.CanApply);
        EditingFixtures.Apply(session, EditingFixtures.Add(new(9, 0, 0)));
        Assert.AreEqual(1, session.Capture().Draft.Layout.NestedItems.Length);
    }

    [TestMethod]
    public void LocalNestedEditConflictsWithCellsOutsideEnteredObject()
    {
        var draft = EditingFixtures.Draft(new GridPosition(5, 0, 0)) with
        { Layout = new([EditingFixtures.Cube], [new(new(5, 0, 0), 0)], [new NestedDesignItem.AssemblyCopy(new(4, 0, 0), VoxelOrientation.PxPy, Assembly)]) };
        using var session = EditingFixtures.Open(draft);
        var parent = session.Read().Elements.Single(e => e.Kind == EditElementKind.Assembly);
        EditingFixtures.Value(session.Enter(parent.Id));
        var before = session.Capture();
        var preview = EditingFixtures.Preview(session, EditingFixtures.Add(new(1, 0, 0)));
        Assert.IsFalse(preview.CanApply);
        Assert.IsTrue(preview.Conflicts.Any(c => c.Problem.Code == "occupancy_conflict"));
        Assert.AreEqual("occupancy_conflict", EditingFixtures.Code(session.Apply(preview, true)));
        Assert.AreEqual(before.Stamp, session.Capture().Stamp);
        Assert.AreEqual(1, ((NestedDesignItem.AssemblyCopy)session.Capture().Draft.Layout.NestedItems.Single()).Design.Layout.Cells.Length);
    }

    [TestMethod]
    public void MissingChildDoesNotPreventEnteringParentAndEditingKnownSpace()
    {
        var scene = SceneFixtures.Projection([]) with
        {
            EditLocks = [new(new DesignElementPath.Cell([0], 0),
                new ContentReference.Core(new CoreDefinitionReference.Voxel(SceneFixtures.Voxel)), new("source"),
                [LockedDesignOperation.Enter, LockedDesignOperation.Remove, LockedDesignOperation.Replace, LockedDesignOperation.Copy], [])]
        };
        var draft = EditingFixtures.Draft() with
        { Layout = new([], [], [new NestedDesignItem.AssemblyCopy(new(4, 0, 0), VoxelOrientation.PxPy, Assembly)]) };
        using var session = EditingFixtures.Open(draft, scene);
        var parent = session.Read().Elements.Single(e => e.Kind == EditElementKind.Assembly);
        var child = session.Read().Elements.Single(e => e.Kind == EditElementKind.Cell);
        Assert.IsTrue(child.Locked);
        EditingFixtures.Value(session.Enter(parent.Id));
        EditingFixtures.Apply(session, EditingFixtures.Add(new(2, 0, 0)));
        var updated = (NestedDesignItem.AssemblyCopy)session.Capture().Draft.Layout.NestedItems.Single();
        CollectionAssert.AreEquivalent(new[] { new GridPosition(0, 0, 0), new(2, 0, 0) }, updated.Design.Layout.Cells.Select(c => c.At).ToArray());
        Assert.IsTrue(session.Read().Elements.Single(e => e.Id == child.Id).Locked);
    }
    [TestMethod]
    public void MissingLockRemainsOnSameElementAfterEarlierCellIsDeleted()
    {
        var scene = SceneFixtures.Projection([]) with
        {
            EditLocks = [new(new DesignElementPath.Cell([], 1),
                new ContentReference.Core(new CoreDefinitionReference.Voxel(SceneFixtures.Voxel)), new("source"),
                [LockedDesignOperation.Remove, LockedDesignOperation.Replace, LockedDesignOperation.Copy], [])]
        };
        using var session = EditingFixtures.Open(EditingFixtures.Draft(new(0, 0, 0), new(1, 0, 0)), scene);
        var locked = session.Read().Elements.Single(e => e.At == new GridPosition(1, 0, 0));
        EditingFixtures.Apply(session, new EditOperation.Cells(new EditFootprint.Cell(new(0, 0, 0)), new CellEdit.Remove()));
        var current = session.Read();
        Assert.AreEqual(locked.Id, current.Elements.Single().Id);
        Assert.IsTrue(current.Elements.Single().Locked);
        Assert.AreEqual(locked.Id, EditingFixtures.Value(session.Locate(current.Snapshot.Stamp, new DesignElementPath.Cell([], 0))));
        var removal = EditingFixtures.Preview(session, new EditOperation.Cells(new EditFootprint.Cell(new(1, 0, 0)), new CellEdit.Remove()));
        Assert.IsFalse(removal.CanApply);
        Assert.IsTrue(removal.Conflicts.Any(c => c.Cell == new GridPosition(1, 0, 0) && c.Problem.Code == "edit_locked"));
    }
    [TestMethod]
    public void WorkingDraftDrivesMeshesPickingAndCutawayAfterEvaluationExpires()
    {
        using var session = EditingFixtures.Open(EditingFixtures.Draft(new(0, 0, 0), new(0, 2, 0)));
        var initial = EditingFixtures.Frame(session);
        Assert.IsInstanceOfType<ScenePick.Hit>(initial.Pick(EditingFixtures.Ray()));
        EditingFixtures.Apply(session, new EditOperation.Cells(new EditFootprint.Cell(new(0, 0, 0)), new CellEdit.Remove()));
        EditingFixtures.Apply(session, EditingFixtures.Add(new(3, 0, 0)));
        Assert.IsNull(session.Read().CurrentEvaluation);
        var current = EditingFixtures.Frame(session);
        Assert.IsInstanceOfType<ScenePick.Miss>(current.Pick(EditingFixtures.Ray()));
        var hit = ((ScenePick.Hit)current.Pick(EditingFixtures.Ray(3.5f))).Value;
        Assert.AreEqual(session.Capture().Stamp.Revision, ((PickSubject.Editing)hit.Subject).Revision);
        Assert.IsTrue(current.Update.Upserts.SelectMany(o => o.Visuals).OfType<SceneVisual.Geometry>().Any());
        var cutaway = EditingFixtures.Frame(session, 0);
        Assert.IsInstanceOfType<ScenePick.Miss>(cutaway.Pick(EditingFixtures.Ray(.5f, 2.5f)));
        Assert.IsInstanceOfType<ScenePick.Hit>(current.Pick(EditingFixtures.Ray(.5f, 2.5f)));
        var vertices = cutaway.Update.Upserts.SelectMany(o => o.Visuals).OfType<SceneVisual.Geometry>().SelectMany(g => g.Mesh.Vertices);
        Assert.IsTrue(vertices.All(v => v.Position.Y <= 1));
        Assert.IsInstanceOfType<ScenePick.Hit>(initial.Pick(EditingFixtures.Ray()));
    }

    [TestMethod]
    public void RotationSweepsFollowSelectionAndEnteredObjectAndExpireWithEvaluation()
    {
        var draft = EditingFixtures.Draft() with { Layout = new([], [], [new NestedDesignItem.AssemblyCopy(new(10, 0, 0), VoxelOrientation.PxPy, Assembly)]) };
        var scene = SceneFixtures.Projection([]) with
        {
            RotationSweeps = [
                new([], new ComputedValue<ImmutableArray<GridPosition>>.Known([new(1, 0, 0)]), new ComputedValue<ImmutableArray<GridPosition>>.Unknown([])),
                new([0], new ComputedValue<ImmutableArray<GridPosition>>.Known([new(12, 0, 0)]), new ComputedValue<ImmutableArray<GridPosition>>.Known([new(13, 0, 0)]))]
        };
        using var session = EditingFixtures.Open(draft, scene);
        var root = EditingFixtures.Frame(session);
        Assert.IsTrue(root.DebugLines.Length > 0);
        Assert.IsTrue(root.DebugLines.All(line => line.From.X <= 2 && line.To.X <= 2));
        var assembly = session.Read().Elements.Single(e => e.Kind == EditElementKind.Assembly);
        EditingFixtures.Value(session.Select(new EditSelection.Elements([assembly.Id])));
        var selected = EditingFixtures.Frame(session);
        Assert.IsTrue(selected.DebugLines.All(line => line.From.X >= 12 && line.To.X >= 12));
        Assert.AreEqual(2, selected.DebugLines.Select(line => line.Color).Distinct().Count());
        EditingFixtures.Value(session.Enter(assembly.Id));
        var entered = EditingFixtures.Frame(session);
        Assert.AreEqual(selected.DebugLines.Length, entered.DebugLines.Length);
        Assert.IsTrue(entered.DebugLines.All(line => line.From.X >= 2 && line.To.X >= 2 && line.From.X <= 4 && line.To.X <= 4));
        var stamp = session.Capture().Stamp;
        EditingFixtures.Apply(session, EditingFixtures.Add(new(5, 0, 0)));
        Assert.IsTrue(EditingFixtures.Frame(session).DebugLines.IsEmpty);
        Assert.IsFalse(session.AcceptEvaluation(stamp, EditingFixtures.Evaluation(draft) with { Scene = scene }));
        Assert.IsTrue(EditingFixtures.Frame(session).DebugLines.IsEmpty);
    }
}
