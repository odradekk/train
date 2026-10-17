using Microsoft.VisualStudio.TestTools.UnitTesting;
using C = Train.Content.Contracts;
using K = Train.Core.Contracts;

namespace Train.Content.Tests.Loading;

[TestClass]
public sealed class PartialDraftTests
{
    [TestMethod]
    public void Missing_voxel_keeps_known_subtotals_and_occupancy_without_a_runtime_placeholder()
    {
        using var candidate = DesignFixtures.Content().Load();
        var draft = DesignFixtures.Vehicle() with
        {
            Layout = new([DesignFixtures.Entry(), DesignFixtures.Entry("absent")],
                [new(new(0, 0, 0), 0), new(new(0, 0, 1), 1)], [])
        };
        var evaluation = Partial(candidate.View, draft);
        Assert.IsNull(evaluation.CorePlaceholder);
        Assert.IsNotNull(evaluation.Scene.Design);
        Assert.AreEqual(3d, evaluation.KnownSubtotal.MassTons);
        Assert.AreEqual(new K.Money(7), evaluation.KnownSubtotal.StructuralBaseValue);
        Assert.AreEqual(5L, evaluation.KnownSubtotal.MaxStructureDurability);
        Assert.IsTrue(evaluation.KnownSubtotal.Parts.IsEmpty);
        Assert.AreEqual(new K.GoodsQuantity(new K.GoodId.Voxel(new("core:block")), 1),
            evaluation.KnownSubtotal.BillOfMaterials.Single());
        Assert.IsInstanceOfType<C.ComputedValue<double>.Unknown>(evaluation.Metrics.MassTons);
        Assert.AreEqual(2, ((C.ComputedValue<int>.Known)evaluation.Metrics.VehicleLengthCells).Value);
        Assert.IsTrue(evaluation.Scene.OccupancyComplete);
        Assert.IsTrue(evaluation.Scene.Voxels.Single(v => v.Missing).At == new C.GridPosition(0, 0, 1));
        var editLock = evaluation.Scene.EditLocks.Single();
        CollectionAssert.AreEquivalent(Enum.GetValues<C.LockedDesignOperation>(), editLock.BlockedOperations.ToArray());
        Assert.IsTrue(evaluation.Diagnostics.Any(d => d.Code == "reference_missing"));
    }

    [TestMethod]
    public void Missing_composite_preserves_known_cells_but_leaves_length_unknown()
    {
        using var candidate = DesignFixtures.Content().Load();
        var draft = DesignFixtures.Vehicle() with
        {
            Layout = new([DesignFixtures.Entry()], [new(new(0, 0, 0), 0)],
                [new C.NestedDesignItem.FunctionalCompositeReference(new(0, 0, 1),
                    C.VoxelOrientation.PxPy, new("core:absent"), null)])
        };
        var evaluation = Partial(candidate.View, draft);
        Assert.IsNull(evaluation.CorePlaceholder);
        Assert.AreEqual(3d, evaluation.KnownSubtotal.MassTons);
        Assert.IsFalse(evaluation.Scene.OccupancyComplete);
        Assert.IsInstanceOfType<C.ComputedValue<int>.Unknown>(evaluation.Metrics.VehicleLengthCells);
        Assert.AreEqual(1, evaluation.Scene.EditLocks.Length);
        Assert.AreEqual(1, evaluation.Scene.Voxels.Length);
    }

    [TestMethod]
    public void Missing_required_vehicle_size_preserves_all_computable_metrics_without_identity()
    {
        using var candidate = DesignFixtures.Content().Load();
        var draft = DesignFixtures.Vehicle() with { Specification = new C.DesignSpecification.Vehicle(null) };
        var evaluation = Partial(candidate.View, draft);
        var complete = DesignFixtures.Evaluate(candidate.View, DesignFixtures.Vehicle()).Core!.Design;
        Assert.IsNull(evaluation.CorePlaceholder);
        Assert.IsNull(evaluation.Scene.Design);
        Assert.IsNull(evaluation.Scene.AllowedEnvelope);
        Assert.AreEqual(complete.MassTons, ((C.ComputedValue<double>.Known)evaluation.Metrics.MassTons).Value);
        Assert.AreEqual(complete.MaxStructureDurability, ((C.ComputedValue<long>.Known)evaluation.Metrics.MaxStructureDurability).Value);
        CollectionAssert.AreEqual(complete.Parts.ToArray(), ((C.ComputedValue<System.Collections.Immutable.ImmutableArray<K.PartProjection>>.Known)evaluation.Metrics.Parts).Value.ToArray());
        Assert.AreEqual(3, ((C.ComputedValue<int>.Known)evaluation.Metrics.VehicleLengthCells).Value);
        Assert.IsTrue(evaluation.Diagnostics.Any(d => d.Field == "/simulation/size_class"));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void Missing_building_specification_has_no_identity_but_keeps_known_mass(bool missingPlot)
    {
        using var candidate = DesignFixtures.Content().Load();
        var draft = DesignFixtures.Vehicle() with
        {
            Specification = new C.DesignSpecification.Building(missingPlot ? null : K.PlotSize.Small,
                missingPlot ? K.BuildingKind.Station : null, [])
        };
        var evaluation = Partial(candidate.View, draft);
        Assert.IsNull(evaluation.CorePlaceholder);
        Assert.IsNull(evaluation.Scene.Design);
        Assert.AreEqual(9d, ((C.ComputedValue<double>.Known)evaluation.Metrics.MassTons).Value);
        Assert.IsTrue(evaluation.Diagnostics.Any(d => d.Field == (missingPlot ? "/simulation/plot_size" : "/simulation/building_type")));
    }

    [TestMethod]
    public void Missing_definition_in_a_nested_assembly_keeps_its_known_mass_and_locks()
    {
        using var candidate = DesignFixtures.Content().Load();
        var assembly = DesignFixtures.Vehicle() with
        {
            Specification = new C.DesignSpecification.Assembly(null),
            Layout = new([DesignFixtures.Entry(), DesignFixtures.Entry("absent")],
                [new(new(0, 0, 0), 0), new(new(0, 0, 1), 1)], [])
        };
        var draft = DesignFixtures.Vehicle() with
        {
            Layout = new([], [], [new C.NestedDesignItem.AssemblyCopy(new(0, 0, 0), C.VoxelOrientation.PxPy, assembly)])
        };
        var evaluation = Partial(candidate.View, draft);
        Assert.IsNull(evaluation.CorePlaceholder);
        Assert.AreEqual(3d, evaluation.KnownSubtotal.MassTons);
        Assert.AreEqual(1,
            ((C.DesignElementPath.Cell)evaluation.Scene.EditLocks.Single().Element).CellIndex);
        CollectionAssert.AreEqual(new[] { 0 }, ((C.DesignElementPath.Cell)evaluation.Scene.EditLocks.Single().Element).NestedItemIndices.ToArray());
    }

    [TestMethod]
    public void Incomplete_specification_inside_a_copy_cannot_create_a_placeholder_or_identity()
    {
        using var candidate = DesignFixtures.Content().Load();
        var incomplete = DesignFixtures.Vehicle() with { Specification = new C.DesignSpecification.Vehicle(null) };
        var assembly = DesignFixtures.Vehicle() with
        {
            Specification = new C.DesignSpecification.Assembly(null),
            Layout = new([DesignFixtures.Entry()], [new(new(0, 0, 0), 0)],
                [new C.NestedDesignItem.AssemblyCopy(new(0, 0, 1), C.VoxelOrientation.PxPy, incomplete)])
        };
        var draft = DesignFixtures.Vehicle() with
        {
            Layout = new([], [], [new C.NestedDesignItem.AssemblyCopy(new(0, 0, 0), C.VoxelOrientation.PxPy, assembly)])
        };
        var evaluation = Partial(candidate.View, draft);
        Assert.IsNull(evaluation.CorePlaceholder);
        Assert.IsNull(evaluation.Scene.Design);
        Assert.AreEqual(3d, ((C.ComputedValue<double>.Known)evaluation.Metrics.MassTons).Value);
        Assert.IsTrue(evaluation.Diagnostics.Any(d => d.Field == "/layout/composites/0/design/layout/composites/0/design/simulation/size_class"));
    }

    [TestMethod]
    public void Missing_definition_in_a_nested_scene_model_keeps_the_host_partial()
    {
        using var candidate = DesignFixtures.Content().Load();
        var child = DesignFixtures.Vehicle() with
        {
            Specification = new C.DesignSpecification.SceneModel(null),
            Layout = new([DesignFixtures.Entry("absent")], [new(new(0, 0, 0), 0)], [])
        };
        var draft = child with
        {
            Layout = new([], [], [new C.NestedDesignItem.SceneModelCopy(new(0, 0, 0), C.VoxelOrientation.PxPy, child)])
        };
        var evaluation = Partial(candidate.View, draft);
        Assert.IsNull(evaluation.CorePlaceholder);
        Assert.AreEqual(1, evaluation.Scene.EditLocks.Length);
        Assert.IsTrue(evaluation.Diagnostics.Any(d => d.Code == "reference_missing" && d.Field == "/layout/composites/0/design/layout/palette/0/voxel"));
    }

    private static C.DesignEvaluation.Partial Partial(C.IContentView view, C.DesignDraft draft)
    {
        var result = ContentFactory.Create().EvaluateDesign(view, draft);
        Assert.IsInstanceOfType<C.ContentResult<C.DesignEvaluation>.Success>(result, result.ToString());
        var evaluation = ((C.ContentResult<C.DesignEvaluation>.Success)result).Value;
        Assert.IsInstanceOfType<C.DesignEvaluation.Partial>(evaluation, evaluation.ToString());
        return (C.DesignEvaluation.Partial)evaluation;
    }
}
