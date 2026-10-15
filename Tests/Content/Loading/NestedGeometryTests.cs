using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.Designs;
using Train.Content.Loading;
using static Train.Content.Tests.Loading.NestedDesignTests;
using C = Train.Content.Contracts;
using K = Train.Core.Contracts;

namespace Train.Content.Tests.Loading;

[TestClass]
public sealed class NestedGeometryTests
{
    [TestMethod]
    public void Nested_rotation_reports_host_collision_once_without_a_fictitious_element()
    {
        using var content = DesignFixtures.Content().Load();
        var result = DesignFixtures.Evaluate(content.View, RotatingGrandchildHost());
        var sweep = result.Scene.RotationSweeps.Single(s => s.NestedItemIndices.SequenceEqual(new[] { 0, 0 }));
        // Positive Y rotation carries the child's +X cell to -Z, occupied by A's base.
        CollectionAssert.Contains(Known(sweep.SweptCells).ToArray(), new C.GridPosition(0, 0, -1));
        CollectionAssert.Contains(Known(sweep.ConflictCells).ToArray(), new C.GridPosition(0, 0, -1));
        AssertInvalid(result);
        var diagnostic = result.Diagnostics.Single(d => d.Code == "design_invalid" && d.Field == GrandchildField);
        Assert.IsNull(diagnostic.Element);
    }

    [TestMethod]
    [DataRow(false, 3)]
    [DataRow(true, 23)]
    public void Rotation_can_leave_the_envelope_while_all_initial_cells_fit(bool building, int edge)
    {
        using var content = DesignFixtures.Content().Load();
        var rotating = Assembly(new(C.RotationAxis.Y, new(0, 0, 0), 0, 90, 20)) with
        { Layout = new([DesignFixtures.Entry()], [new(new(0, 0, 1), 0)], []) };
        var host = Host(new C.NestedDesignItem.AssemblyCopy(new(edge, 0, 0), C.VoxelOrientation.PxPy, rotating));
        host = host with { Layout = host.Layout with
        { Cells = Enumerable.Range(0, edge + 1).Select(x => new C.LayoutCell(new(x, 0, 0), 0)).ToImmutableArray() } };
        if (building) host = host with { Specification = new C.DesignSpecification.Building(K.PlotSize.Small, K.BuildingKind.Production, []) };
        var result = DesignFixtures.Evaluate(content.View, host);
        Assert.IsTrue(result.Scene.Voxels.All(v => v.At.X <= edge));
        // +Z rotates to +X, one cell beyond the small vehicle / small plot's right edge.
        var outside = new C.GridPosition(edge + 1, 0, 0);
        var sweep = result.Scene.RotationSweeps.Single();
        CollectionAssert.Contains(Known(sweep.SweptCells).ToArray(), outside);
        CollectionAssert.Contains(Known(sweep.ConflictCells).ToArray(), outside);
        AssertInvalid(result);
        Assert.IsTrue(result.Diagnostics.Any(d => d.Code == "design_invalid" && d.Field == "/layout/composites/0"));
    }

    [TestMethod]
    public void Nested_occupancy_outside_vehicle_envelope_is_invalid_even_without_rotation()
    {
        using var content = DesignFixtures.Content().Load();
        var host = Host(new C.NestedDesignItem.AssemblyCopy(new(4, 0, 0), C.VoxelOrientation.PxPy, Assembly()));
        host = host with { Layout = host.Layout with
        { Cells = Enumerable.Range(0, 4).Select(x => new C.LayoutCell(new(x, 0, 0), 0)).ToImmutableArray() } };
        var result = DesignFixtures.Evaluate(content.View, host);
        Assert.AreEqual(new C.GridPosition(4, 0, 0), result.Scene.Voxels.Single(v => !v.ObjectPath.IsEmpty).At);
        Assert.IsTrue(result.Scene.RotationSweeps.IsEmpty);
        AssertInvalid(result);
        var diagnostic = result.Diagnostics.Single(d => d.Code == "design_invalid");
        Assert.AreEqual("/layout/composites/0/design/layout/cells/0", diagnostic.Field);
        Assert.IsInstanceOfType<C.DesignElementPath.Cell>(diagnostic.Element);
    }

    [TestMethod]
    public void Placement_rotates_a_nondefault_local_orientation_in_the_correct_order()
    {
        using var content = DesignFixtures.Content().Load();
        var child = Assembly() with { Layout = new(
            [DesignFixtures.Entry() with { Orientation = C.VoxelOrientation.PzPy }], [new(new(1, 2, 3), 0)], []) };
        var parent = Assembly() with { Layout = new([], [],
            [new C.NestedDesignItem.AssemblyCopy(new(7, 8, 9), C.VoxelOrientation.PyPx, child)]) };
        var result = DesignFixtures.Evaluate(content.View, parent);
        var voxel = result.Scene.Voxels.Single();
        // Placement maps (x,y,z) to (y,x,-z). Local right +Z becomes -Z; local up +Y becomes +X.
        Assert.AreEqual(new C.GridPosition(9, 9, 6), voxel.At);
        Assert.AreEqual(C.VoxelOrientation.NzPx, voxel.Orientation);
        Assert.AreEqual(new C.GridPosition(1, 2, 3), voxel.LocalAt);
        Assert.AreEqual(C.VoxelOrientation.PzPy, voxel.LocalOrientation);
        Assert.IsTrue(result.IsValid);
    }

    [TestMethod]
    public void Nonidentity_placement_detects_overlap_with_host_occupancy()
    {
        using var content = DesignFixtures.Content().Load();
        var child = Assembly() with { Layout = new([DesignFixtures.Entry()], [new(new(1, 0, 0), 0)], []) };
        // Placement maps local +X to +Z; translation -Z places the cell on the host origin.
        var result = DesignFixtures.Evaluate(content.View, Host(
            new C.NestedDesignItem.AssemblyCopy(new(0, 0, -1), C.VoxelOrientation.PzPy, child)));
        Assert.AreEqual(2, result.Scene.Voxels.Count(v => v.At == new C.GridPosition(0, 0, 0)));
        AssertInvalid(result);
        Assert.IsTrue(result.Diagnostics.Any(d => d.Code == "design_invalid" && d.Field == "/layout/composites/0"));
    }

    [TestMethod]
    public void Missing_occupancy_elsewhere_makes_nested_conflicts_unknown_without_a_definite_diagnostic()
    {
        using var content = DesignFixtures.Content().Load();
        var host = RotatingGrandchildHost();
        host = host with { Layout = host.Layout with { NestedItems = host.Layout.NestedItems.Add(
            new C.NestedDesignItem.FunctionalCompositeReference(new(0, 0, 10), C.VoxelOrientation.PxPy, new("core:absent"), null)) } };
        var evaluation = DesignEvaluator.Evaluate((ContentView)content.View, host, allowMissing: true);
        Assert.IsInstanceOfType<C.DesignEvaluation.Partial>(evaluation);
        var result = (C.DesignEvaluation.Partial)evaluation;
        var sweep = result.Scene.RotationSweeps.Single(s => s.NestedItemIndices.SequenceEqual(new[] { 0, 0 }));
        Assert.IsInstanceOfType<C.ComputedValue<ImmutableArray<C.GridPosition>>.Known>(sweep.SweptCells);
        Assert.IsInstanceOfType<C.ComputedValue<ImmutableArray<C.GridPosition>>.Unknown>(sweep.ConflictCells);
        Assert.IsFalse(result.Diagnostics.Any(d => d.Code == "design_invalid" && d.Field == GrandchildField));
        Assert.IsTrue(result.Diagnostics.Any(d => d.Code == "reference_missing" && d.Field == "/layout/composites/1/blueprint"));
    }

    private const string GrandchildField = "/layout/composites/0/design/layout/composites/0";

    private static C.DesignDraft RotatingGrandchildHost()
    {
        var rotating = Assembly(new(C.RotationAxis.Y, new(0, 0, 0), 0, 90, 20)) with
        { Layout = new([DesignFixtures.Entry()], [new(new(1, 0, 0), 0)], []) };
        var parent = Assembly() with { Layout = new([DesignFixtures.Entry()],
            [new(new(0, 0, 0), 0), new(new(0, 0, -1), 0)],
            [new C.NestedDesignItem.AssemblyCopy(new(0, 0, 0), C.VoxelOrientation.PxPy, rotating)]) };
        var host = Host(new C.NestedDesignItem.AssemblyCopy(new(0, 0, 0), C.VoxelOrientation.PxPy, parent));
        return host with { Layout = host.Layout with { Cells = [new(new(0, 0, 1), 0)] } };
    }

    private static ImmutableArray<C.GridPosition> Known(C.ComputedValue<ImmutableArray<C.GridPosition>> value)
    {
        Assert.IsInstanceOfType<C.ComputedValue<ImmutableArray<C.GridPosition>>.Known>(value);
        return ((C.ComputedValue<ImmutableArray<C.GridPosition>>.Known)value).Value;
    }

    private static void AssertInvalid(C.DesignEvaluation.Complete result)
    {
        Assert.IsFalse(result.IsValid);
        Assert.IsFalse(result.Core!.Design.CanConstruct);
    }
}
