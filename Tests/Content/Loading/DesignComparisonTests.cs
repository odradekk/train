using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.Designs;
using Train.Content.Loading;
using Train.Content.Persistence;
using C = Train.Content.Contracts;
using K = Train.Core.Contracts;

namespace Train.Content.Tests.Loading;

[TestClass]
public sealed class DesignComparisonTests
{
    [TestMethod]
    public void Name_changes_only_the_design_identity() => Appearance(d => d with { Display = d.Display with { Name = "新名称" } });

    [TestMethod]
    public void Description_changes_only_the_design_identity() => Appearance(d => d with { Display = d.Display with { Description = "新说明" } });

    [TestMethod]
    public void Shape_changes_only_the_design_identity() => Appearance(d => Palette(d, d.Layout.Palette[0] with { Shape = new("wedge") }));

    [TestMethod]
    public void Paint_changes_only_the_design_identity() => Appearance(d => Palette(d, d.Layout.Palette[0] with { Paint = new("#abcdef") }));

    [TestMethod]
    public void Ordinary_part_orientation_preserves_damage_correspondence_but_requires_refit()
    {
        var draft = DesignFixtures.Vehicle();
        var result = Compare(draft, Palette(draft, draft.Layout.Palette[0] with { Orientation = C.VoxelOrientation.PzPy }));
        Assert.AreEqual(0L, result.Core.ChangedItemCount);
        Assert.AreEqual(1, result.Core.RetainedParts.Length);
        Assert.IsFalse(result.CanApplyImmediately);
    }

    [TestMethod]
    public void Assembly_rotation_retains_parts_movement_costs_two_and_replacement_costs_one()
    {
        var equipment = DesignFixtures.Vehicle() with { Specification = new C.DesignSpecification.Assembly(null) };
        var item = new C.NestedDesignItem.AssemblyCopy(new(0, 0, 0), C.VoxelOrientation.PxPy, equipment);
        var draft = DesignFixtures.Vehicle() with { Layout = new([], [], [item]) };
        var rotation = Compare(draft, draft with { Layout = draft.Layout with { NestedItems = [item with { Orientation = C.VoxelOrientation.PzPy }] } });
        Assert.AreEqual(0L, rotation.Core.ChangedItemCount);
        Assert.AreEqual(1, rotation.Core.RetainedParts.Length);
        Assert.IsFalse(rotation.CanApplyImmediately);
        var moved = Compare(draft, draft with { Layout = draft.Layout with { NestedItems = [item with { At = new(1, 0, 0) }] } });
        Assert.AreEqual(2L, moved.Core.ChangedItemCount);
        Assert.AreEqual(0, moved.Core.RetainedParts.Length);
        Assert.AreEqual(1L, moved.Core.AddedGoods.Single().Quantity);
        Assert.AreEqual(1L, moved.Core.RemovedItems.Single().Quantity);
        var replacement = Compare(draft, draft with { Layout = draft.Layout with { NestedItems = [item with
        { Design = equipment with { Layout = equipment.Layout with { Cells = equipment.Layout.Cells.RemoveAt(2) } } }] } });
        Assert.AreEqual(1L, replacement.Core.ChangedItemCount);
        Assert.AreEqual(0, replacement.Core.RetainedParts.Length);
        Assert.AreEqual(1, replacement.Core.RemovedItems.Single().Parts.Length);
    }

    [TestMethod]
    public void Composite_repaint_retains_the_whole_equipment_and_applies_immediately()
    {
        using var candidate = NestedDesignTests.CompositeContent().Load();
        var item = new C.NestedDesignItem.FunctionalCompositeReference(new(0, 0, 0), C.VoxelOrientation.PxPy, new("core:engine"), null);
        var draft = DesignFixtures.Vehicle() with { Layout = new([], [], [item]) };
        var target = draft with { Layout = draft.Layout with { NestedItems = [item with { Paint = new("#abcdef") }] } };
        var result = DesignComparer.Compare(Store((ContentView)candidate.View, draft), DesignEvaluator.Identity(draft), target);
        Assert.IsInstanceOfType<C.ContentResult<C.DesignChangeEvaluation>.Success>(result,
            result is C.ContentResult<C.DesignChangeEvaluation>.Failure failure
                ? string.Join("; ", failure.Diagnostics.Select(d => d.Field + ": " + d.Message)) : result.ToString());
        var difference = ((C.ContentResult<C.DesignChangeEvaluation>.Success)result).Value;
        Assert.AreNotEqual(difference.Core.Before, difference.Core.Target.Design.Design);
        Assert.AreEqual(0L, difference.Core.ChangedItemCount);
        Assert.AreEqual(1, difference.Core.RetainedParts.Length);
        Assert.IsTrue(difference.CanApplyImmediately);
    }

    [TestMethod]
    public void Equipment_anchor_may_equal_a_host_cell_when_occupied_cells_do_not_overlap()
    {
        var equipment = NestedDesignTests.Assembly() with
        { Layout = new([DesignFixtures.Entry("machine")], [new(new(0, 0, 1), 0)], []) };
        var draft = new C.DesignDraft(new("宿主", "测试"), new C.DesignSpecification.Vehicle(K.SizeClass.Small),
            new([DesignFixtures.Entry()], [new(new(0, 0, 0), 0)],
                [new C.NestedDesignItem.AssemblyCopy(new(0, 0, 0), C.VoxelOrientation.PxPy, equipment)]));
        var result = Compare(draft, draft with { Display = new("新名称", "测试") });
        Assert.AreEqual(0L, result.Core.ChangedItemCount);
        Assert.AreEqual(1, result.Core.RetainedParts.Length);
        Assert.IsTrue(result.CanApplyImmediately);
    }

    [TestMethod]
    public void Missing_current_missing_target_incomplete_target_and_incompatible_target_are_failures()
    {
        using var candidate = DesignFixtures.Content().Load();
        var view = (ContentView)candidate.View;
        var draft = DesignFixtures.Vehicle();
        AssertFailure(DesignComparer.Compare(view, new("absent"), draft), "reference_missing");
        var hash = DesignEvaluator.Identity(draft);
        view = Store(view, draft);
        AssertFailure(DesignComparer.Compare(view, hash, Palette(draft, DesignFixtures.Entry("absent"))), "reference_missing");
        AssertFailure(DesignComparer.Compare(view, hash, draft with { Specification = new C.DesignSpecification.Vehicle(null) }), "design_invalid");
        AssertFailure(DesignComparer.Compare(view, hash, draft with { Layout = draft.Layout with { Cells = [] } }), "design_invalid");
        AssertFailure(DesignComparer.Compare(view, hash, draft with { Specification = new C.DesignSpecification.Building(K.PlotSize.Small, K.BuildingKind.Warehouse, []) }), "design_invalid");
    }

    [TestMethod]
    public void Missing_current_is_rejected_but_complete_invalid_current_can_be_repaired()
    {
        using var candidate = DesignFixtures.Content().Load();
        var draft = DesignFixtures.Vehicle();
        var missing = Palette(draft, DesignFixtures.Entry("absent"));
        AssertFailure(DesignComparer.Compare(Store((ContentView)candidate.View, missing), DesignEvaluator.Identity(missing), draft), "reference_missing");
        var invalid = draft with { Display = draft.Display with { Name = "" } };
        var result = DesignComparer.Compare(Store((ContentView)candidate.View, invalid), DesignEvaluator.Identity(invalid), draft);
        Assert.IsInstanceOfType<C.ContentResult<C.DesignChangeEvaluation>.Success>(result);
    }

    [TestMethod]
    public void Inserting_a_cell_moves_retained_root_and_assembly_part_numbers_and_paths()
    {
        var equipment = DesignFixtures.Vehicle() with { Specification = new C.DesignSpecification.Assembly(null) };
        var draft = DesignFixtures.Vehicle() with
        {
            Layout = DesignFixtures.Vehicle().Layout with
            { NestedItems = [new C.NestedDesignItem.AssemblyCopy(new(1, 0, 0), C.VoxelOrientation.PxPy, equipment)] }
        };
        var target = draft with { Layout = draft.Layout with
        { Cells = draft.Layout.Cells.Insert(0, new(new(0, 0, -2), 1)) } };
        var result = Compare(draft, target);
        CollectionAssert.AreEqual(new[] { new K.PartCorrespondence(new(2), new(3)), new K.PartCorrespondence(new(5), new(6)) },
            result.Core.RetainedParts.ToArray());
        Assert.AreEqual(2, result.Elements.Length);
        AssertCell(result.Elements[0].Before, [], 0);
        AssertCell(result.Elements[0].After, [], 1);
        AssertCell(result.Elements[1].Before, [0], 0);
        AssertCell(result.Elements[1].After, [0], 0);
        Assert.AreEqual(1L, result.Core.ChangedItemCount);
        CollectionAssert.AreEqual(new[] { new K.GoodsQuantity(new K.GoodId.Voxel(new("core:block")), 1) }, result.Core.AddedGoods.ToArray());
        Assert.IsTrue(result.Core.RemovedItems.IsEmpty);
    }

    [TestMethod]
    public void Replacing_structure_and_removing_equipment_reports_exact_goods_and_return_units()
    {
        var equipment = DesignFixtures.Vehicle() with { Specification = new C.DesignSpecification.Assembly(null) };
        var draft = DesignFixtures.Vehicle() with { Layout = DesignFixtures.Vehicle().Layout with
        { NestedItems = [new C.NestedDesignItem.AssemblyCopy(new(1, 0, 0), C.VoxelOrientation.PxPy, equipment)] } };
        var target = draft with { Layout = draft.Layout with
        { Cells = [new(new(0, 0, 1), 0), new(new(0, 0, 0), 0), new(new(0, 0, -1), 0)], NestedItems = [] } };
        var result = Compare(draft, target);
        var block = new K.GoodId.Voxel(new("core:block"));
        var machine = new K.GoodId.Voxel(new("core:machine"));
        CollectionAssert.AreEqual(new[] { new K.GoodsQuantity(machine, 2) }, result.Core.AddedGoods.ToArray());
        Assert.AreEqual(3L, result.Core.ChangedItemCount);
        Assert.AreEqual(2, result.Core.RemovedItems.Length);
        var assembly = result.Core.RemovedItems[0];
        Assert.AreEqual(new K.GoodId.Assembly(new("E20AA88E1FE47778C85AA0C5E9EA7EB733AE39E15A4C2D4627263866FA64CDF6")), assembly.Good);
        Assert.AreEqual(0, result.Core.Target.AssemblyGoods.Length);
        Assert.AreEqual(1L, assembly.Quantity);
        CollectionAssert.AreEqual(new[] { new K.PartId(5) }, assembly.Parts.ToArray());
        CollectionAssert.AreEqual(new[] { new K.GoodsQuantity(block, 2) }, assembly.StructuralGoods.ToArray());
        Assert.IsTrue(assembly.MissingReferences.IsEmpty);
        var structure = result.Core.RemovedItems[1];
        Assert.AreEqual(block, structure.Good);
        Assert.AreEqual(2L, structure.Quantity);
        Assert.IsTrue(structure.Parts.IsEmpty);
        CollectionAssert.AreEqual(new[] { new K.GoodsQuantity(block, 2) }, structure.StructuralGoods.ToArray());
        Assert.IsTrue(structure.MissingReferences.IsEmpty);
    }

    [TestMethod]
    public void Removing_functional_voxels_returns_one_unit_per_part()
    {
        var draft = DesignFixtures.Vehicle();
        draft = draft with { Layout = draft.Layout with { Cells = draft.Layout.Cells.Add(new(new(1, 0, 0), 0)) } };
        var target = draft with { Layout = draft.Layout with { Cells = draft.Layout.Cells.RemoveAt(3).RemoveAt(0) } };
        var result = Compare(draft, target);
        Assert.AreEqual(2L, result.Core.ChangedItemCount);
        Assert.IsTrue(result.Core.AddedGoods.IsEmpty);
        Assert.AreEqual(2, result.Core.RemovedItems.Length);
        for (var i = 0; i < 2; i++)
        {
            var unit = result.Core.RemovedItems[i];
            Assert.AreEqual(new K.GoodId.Voxel(new("core:machine")), unit.Good);
            Assert.AreEqual(1L, unit.Quantity);
            CollectionAssert.AreEqual(new[] { new K.PartId(2 + i) }, unit.Parts.ToArray());
            Assert.IsTrue(unit.StructuralGoods.IsEmpty);
            Assert.IsTrue(unit.MissingReferences.IsEmpty);
        }
    }

    [TestMethod]
    public void Incomplete_and_unsupported_targets_keep_specific_diagnostic_fields()
    {
        using var candidate = DesignFixtures.Content().Load();
        var draft = DesignFixtures.Vehicle();
        var view = Store((ContentView)candidate.View, draft);
        var hash = DesignEvaluator.Identity(draft);
        var missingSize = DesignComparer.Compare(view, hash, draft with { Specification = new C.DesignSpecification.Vehicle(null) });
        AssertFailure(missingSize, "design_invalid");
        Assert.IsTrue(((C.ContentResult<C.DesignChangeEvaluation>.Failure)missingSize).Diagnostics.Any(
            d => d.Code == "design_invalid" && d.Field == "/simulation/size_class"));
        var missingVoxel = DesignComparer.Compare(view, hash, Palette(draft, DesignFixtures.Entry("absent")));
        AssertFailure(missingVoxel, "reference_missing");
        Assert.IsTrue(((C.ContentResult<C.DesignChangeEvaluation>.Failure)missingVoxel).Diagnostics.Any(
            d => d.Code == "reference_missing" && d.Field == "/layout/palette/0/voxel"));
        var creature = DesignComparer.Compare(view, hash, draft with { Specification = new C.DesignSpecification.Creature(null, null) });
        AssertFailure(creature, "unsupported_format");
        Assert.IsTrue(((C.ContentResult<C.DesignChangeEvaluation>.Failure)creature).Diagnostics.Any(
            d => d.Code == "unsupported_format" && d.Field == "/category"));
    }

    private static void AssertCell(C.DesignElementPath path, int[] nested, int cell)
    {
        Assert.IsInstanceOfType<C.DesignElementPath.Cell>(path);
        var actual = (C.DesignElementPath.Cell)path;
        CollectionAssert.AreEqual(nested, actual.NestedItemIndices.ToArray());
        Assert.AreEqual(cell, actual.CellIndex);
    }

    private static void Appearance(Func<C.DesignDraft, C.DesignDraft> change)
    {
        var draft = DesignFixtures.Vehicle();
        var result = Compare(draft, change(draft));
        Assert.AreNotEqual(result.Core.Before, result.Core.Target.Design.Design);
        Assert.AreEqual(result.Core.BeforeComposition, result.Core.Target.Design.Composition);
        Assert.IsTrue(result.CanApplyImmediately);
        Assert.AreEqual(0L, result.Core.ChangedItemCount);
        Assert.IsTrue(result.Core.AddedGoods.IsEmpty);
        Assert.IsTrue(result.Core.RemovedItems.IsEmpty);
    }

    private static C.DesignDraft Palette(C.DesignDraft draft, C.VoxelPaletteEntry entry) =>
        draft with { Layout = draft.Layout with { Palette = draft.Layout.Palette.SetItem(0, entry) } };

    private static C.DesignChangeEvaluation Compare(C.DesignDraft before, C.DesignDraft after)
    {
        using var candidate = DesignFixtures.Content().Load();
        var result = DesignComparer.Compare(Store((ContentView)candidate.View, before), DesignEvaluator.Identity(before), after);
        Assert.IsInstanceOfType<C.ContentResult<C.DesignChangeEvaluation>.Success>(result,
            result is C.ContentResult<C.DesignChangeEvaluation>.Failure failure
                ? string.Join("; ", failure.Diagnostics.Select(d => d.Field + ": " + d.Message)) : result.ToString());
        return ((C.ContentResult<C.DesignChangeEvaluation>.Success)result).Value;
    }

    private static ContentView Store(ContentView view, C.DesignDraft draft) => view.WithCapture(
        new ContentCapture(new(0), [new(DesignEvaluator.Identity(draft), draft, null)], [], [], []), new([], [], [], []));

    private static void AssertFailure(C.ContentResult<C.DesignChangeEvaluation> result, string code)
    {
        Assert.IsInstanceOfType<C.ContentResult<C.DesignChangeEvaluation>.Failure>(result, result.ToString());
        Assert.IsTrue(((C.ContentResult<C.DesignChangeEvaluation>.Failure)result).Diagnostics.Any(d => d.Code == code));
    }
}
