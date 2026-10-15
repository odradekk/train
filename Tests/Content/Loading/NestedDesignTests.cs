using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Train.Content.Tests.Loading.ContentCaptureTests;
using C = Train.Content.Contracts;
using K = Train.Core.Contracts;

namespace Train.Content.Tests.Loading;

[TestClass]
public sealed class NestedDesignTests
{
    [TestMethod]
    public void Reordering_children_keeps_hashes_and_numbers_but_preserves_source_paths()
    {
        using var content = DesignFixtures.Content().Load();
        var child = Assembly();
        var first = Host(new C.NestedDesignItem.AssemblyCopy(new(0, 0, 1), C.VoxelOrientation.PxPy, child),
            new C.NestedDesignItem.AssemblyCopy(new(0, 0, -1), C.VoxelOrientation.PxPy, child));
        var before = DesignFixtures.Evaluate(content.View, first);
        var after = DesignFixtures.Evaluate(content.View, first with { Layout = first.Layout with { NestedItems = first.Layout.NestedItems.Reverse().ToImmutableArray() } });
        Assert.AreEqual(before.Design, after.Design);
        Assert.AreEqual(before.Composition, after.Composition);
        foreach (var voxel in before.Scene.Voxels)
            Assert.AreEqual(voxel.Number, after.Scene.Voxels.Single(v => v.At == voxel.At).Number);
        Assert.AreEqual(1, ((C.DesignElementPath.Cell)before.Scene.Voxels.Single(v => v.At.Z == -1).Element).NestedItemIndices.Single());
        Assert.AreEqual(0, ((C.DesignElementPath.Cell)after.Scene.Voxels.Single(v => v.At.Z == -1).Element).NestedItemIndices.Single());
    }

    [TestMethod]
    public void Every_orientation_transforms_root_coordinates_and_keeps_local_coordinates()
    {
        using var content = DesignFixtures.Content().Load();
        var images = new HashSet<(C.GridPosition, C.VoxelOrientation)>();
        foreach (var orientation in Enum.GetValues<C.VoxelOrientation>())
        {
            var child = Assembly() with { Layout = new([DesignFixtures.Entry()], [new(new(1, 2, 3), 0)], []) };
            var result = DesignFixtures.Evaluate(content.View, Host(new C.NestedDesignItem.AssemblyCopy(new(7, 8, 9), orientation, child)));
            var voxel = result.Scene.Voxels.Single(v => !v.ObjectPath.IsEmpty);
            Assert.AreEqual(new C.GridPosition(1, 2, 3), voxel.LocalAt);
            Assert.AreEqual(C.VoxelOrientation.PxPy, voxel.LocalOrientation);
            var axes = orientation.ToString();
            var x = Axis(axes[..2]);
            var y = Axis(axes[2..]);
            var z = new C.GridPosition(x.Y * y.Z - x.Z * y.Y, x.Z * y.X - x.X * y.Z, x.X * y.Y - x.Y * y.X);
            Assert.AreEqual(new C.GridPosition(7 + x.X + 2 * y.X + 3 * z.X, 8 + x.Y + 2 * y.Y + 3 * z.Y, 9 + x.Z + 2 * y.Z + 3 * z.Z), voxel.At);
            Assert.AreEqual(orientation, voxel.Orientation);
            images.Add((voxel.At, voxel.Orientation));
        }
        Assert.AreEqual(24, images.Count);
    }

    [TestMethod]
    public void Composite_is_one_part_and_assembly_is_one_bill_item_with_whole_return_unit()
    {
        var fixture = CompositeContent();
        using var content = fixture.Load();
        var child = Assembly() with { Layout = new([DesignFixtures.Entry("machine")], [new(new(0, 0, 0), 0)], []) };
        var childEvaluation = DesignFixtures.Evaluate(content.View, child);
        var result = DesignFixtures.Evaluate(content.View, Host(
            new C.NestedDesignItem.FunctionalCompositeReference(new(0, 0, 1), C.VoxelOrientation.PxPy, new("core:engine"), null),
            new C.NestedDesignItem.AssemblyCopy(new(0, 0, -1), C.VoxelOrientation.PxPy, child)));
        Assert.IsTrue(result.IsValid);
        var core = result.Core!.Design;
        Assert.AreEqual(2, core.Parts.Length);
        var composite = core.Parts.Single(p => p.Definition is K.CoreDefinitionReference.Composite);
        Assert.AreEqual(10L, composite.MaxDurability);
        Assert.AreEqual(2L, composite.HitWeight);
        Assert.AreEqual(13L, core.MaxStructureDurability); // Own block 5, composite 5, machine 3.
        Assert.AreEqual(1L, core.StructureHitWeight);
        Assert.AreEqual(12d, core.MassTons);
        var assemblyGood = new K.GoodId.Assembly(childEvaluation.Composition);
        CollectionAssert.AreEquivalent(new[] { new K.GoodsQuantity(new K.GoodId.Voxel(new("core:block")), 1),
            new K.GoodsQuantity(new K.GoodId.Composite(new("core:engine")), 1), new K.GoodsQuantity(assemblyGood, 1) }, core.BillOfMaterials.ToArray());
        var returned = core.ReturnUnits.Single(r => r.Good == assemblyGood);
        Assert.AreEqual(1L, returned.Quantity);
        CollectionAssert.AreEqual(new[] { core.Parts.Single(p => p.Definition is K.CoreDefinitionReference.Voxel).Id }, returned.Parts.ToArray());
        Assert.AreEqual(0, returned.StructuralGoods.Length);
        Assert.AreEqual(1, result.Scene.Voxels.Where(v => v.Voxel.Value == "core:block" && v.ObjectPath.SequenceEqual(new[] { 0 })).Select(v => v.Number).Distinct().Count());
    }

    [TestMethod]
    public void Public_copy_cannot_smuggle_functional_composite_specification()
    {
        using var content = DesignFixtures.Content().Load();
        var child = Assembly() with { Specification = new C.DesignSpecification.FunctionalComposite([new K.PartCapability.Control()], null) };
        var result = Success(ContentFactory.Create().EvaluateDesign(content.View, Host(new C.NestedDesignItem.AssemblyCopy(new(0, 0, 1), C.VoxelOrientation.PxPy, child))));
        Assert.IsFalse(result is C.DesignEvaluation.Complete { IsValid: true });
    }

    [TestMethod]
    public void Child_sweep_does_not_include_ancestor_rotation_and_paths_keep_nested_prefix()
    {
        using var content = DesignFixtures.Content().Load();
        var rotating = Assembly(new(C.RotationAxis.Y, new(0, 0, 0), 0, 90, 20)) with
        { Layout = new([DesignFixtures.Entry()], [new(new(2, 0, 0), 0)], []) };
        var parent = Assembly(new(C.RotationAxis.Y, new(0, 0, 0), -90, 0, 20)) with
        { Layout = new([], [], [new C.NestedDesignItem.AssemblyCopy(new(0, 0, 0), C.VoxelOrientation.PxPy, rotating)]) };
        var result = DesignFixtures.Evaluate(content.View, parent);
        var root = result.Scene.RotationSweeps.Single(s => s.NestedItemIndices.IsEmpty);
        var child = result.Scene.RotationSweeps.Single(s => s.NestedItemIndices.SequenceEqual(new[] { 0 }));
        var rootCells = ((C.ComputedValue<ImmutableArray<C.GridPosition>>.Known)root.SweptCells).Value;
        var childCells = ((C.ComputedValue<ImmutableArray<C.GridPosition>>.Known)child.SweptCells).Value;
        Assert.IsTrue(rootCells.Length > childCells.Length);
        Assert.IsFalse(childCells.Contains(new(0, 0, 2)) && childCells.Contains(new(0, 0, -2)));
        Assert.AreEqual(0, ((C.ComputedValue<ImmutableArray<C.GridPosition>>.Known)root.ConflictCells).Value.Length);
    }

    internal static C.DesignDraft Assembly(C.RotationAppearance? rotation = null) => new(new("组装体", "测试"), new C.DesignSpecification.Assembly(rotation),
        new([DesignFixtures.Entry()], [new(new(0, 0, 0), 0)], []));
    internal static C.DesignDraft Host(params C.NestedDesignItem[] children) => new(new("宿主", "测试"), new C.DesignSpecification.Vehicle(K.SizeClass.Small),
        new([DesignFixtures.Entry()], [new(new(0, 0, 0), 0)], children.ToImmutableArray()));
    internal static ContentFixture CompositeContent()
    {
        var fixture = DesignFixtures.Content();
        var definition = DesignFixtures.Blueprint("engine");
        definition["category"] = "large_equipment";
        definition["simulation"] = JsonNode.Parse("""{"behaviors":{"control":{}}}""");
        definition["layout"]!["cells"] = JsonNode.Parse("""[[0,0,0,0],[0,0,1,0]]""");
        WorldContentFixtures.AddBlueprint(fixture, definition);
        return fixture;
    }
    private static C.GridPosition Axis(string axis) => axis switch
    {
        "Px" => new(1, 0, 0), "Nx" => new(-1, 0, 0), "Py" => new(0, 1, 0),
        "Ny" => new(0, -1, 0), "Pz" => new(0, 0, 1), "Nz" => new(0, 0, -1), _ => throw new AssertFailedException(axis)
    };
}
