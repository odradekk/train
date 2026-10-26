using System.Buffers.Binary;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using C = Train.Content.Contracts;
using K = Train.Core.Contracts;

namespace Train.Content.Tests.Loading;

[TestClass]
[TestCategory("integration")]
public sealed class DocumentationBlueprintTests
{
    private const string Package = "5e1f0c3a-7b2d-4c8e-9a61-0d4b7f2e8c15";
    private const string Addon = "9c4d2b7e-1a3f-4e60-8b25-6f0e3d9a7c41";

    [TestMethod]
    public void Documentation_nested_blueprints_load_with_stable_parts_whole_equipment_materials_and_three_levels()
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository != null && !File.Exists(Path.Combine(repository.FullName, "global.json"))) repository = repository.Parent;
        Assert.IsNotNull(repository);
        var fixture = new ContentFixture
        {
            Installation = new(ContentFixture.BuiltIn, ContentFixture.Mods, null, "0.0.56", [new(Package), new(Addon)])
        };
        ReadFiles(Path.Combine(repository.FullName, "builtin/core"), ContentFixture.BuiltIn, "", fixture);
        foreach (var name in new[] { "example-pack", "example-addon" })
            ReadFiles(Path.Combine(repository.FullName, "docs/content/examples", name), ContentFixture.Mods, name + "/", fixture);
        using var candidate = fixture.Load();
        Assert.AreEqual(0, candidate.Report.Diagnostics.Length, string.Join("\n", candidate.Report.Diagnostics));
        var turret = Complete(candidate.View, "mg-turret");
        var sweep = turret.Scene.RotationSweeps.Single();
        Assert.AreEqual(0, sweep.NestedItemIndices.Length);
        Assert.IsInstanceOfType<C.ComputedValue<System.Collections.Immutable.ImmutableArray<C.GridPosition>>.Known>(sweep.SweptCells);
        Assert.IsTrue(((C.ComputedValue<System.Collections.Immutable.ImmutableArray<C.GridPosition>>.Known)sweep.SweptCells).Value.Length > 18);
        Assert.IsInstanceOfType<C.ComputedValue<System.Collections.Immutable.ImmutableArray<C.GridPosition>>.Known>(sweep.ConflictCells);
        Assert.AreEqual(0, ((C.ComputedValue<System.Collections.Immutable.ImmutableArray<C.GridPosition>>.Known)sweep.ConflictCells).Value.Length);

        var locomotive = Complete(candidate.View, "armed-locomotive");
        var core = locomotive.Core!.Design;
        foreach (var composite in locomotive.Scene.NestedObjects.Select(item => item.Details).OfType<C.NestedObjectDetails.FunctionalComposite>())
        {
            var part = core.Parts.Single(part => part.Id == composite.Number);
            Assert.AreEqual(new K.CoreDefinitionReference.Composite(composite.Definition), part.Definition);
            Assert.AreEqual(1, locomotive.Scene.Elements.Count(element => element.Number == composite.Number));
        }
        Assert.AreEqual(1L, core.BillOfMaterials.Single(item => item.Good == new K.GoodId.Composite(new(Package + ":large-diesel-engine"))).Quantity);
        Assert.AreEqual(1L, core.BillOfMaterials.Single(item => item.Good == new K.GoodId.Assembly(turret.Composition)).Quantity);
        Assert.IsFalse(core.BillOfMaterials.Any(item => item.Good == new K.GoodId.Voxel(new(Package + ":machine-gun"))));

        foreach (var (id, plot, vehicle) in new[]
        {
            ("workshop-crane", K.PlotSize.Large, (K.SizeClass?)K.SizeClass.Large),
            ("gantry-crane", K.PlotSize.Giant, (K.SizeClass?)K.SizeClass.Titan)
        })
        {
            var crane = Complete(candidate.View, id);
            var specification = (C.DesignSpecification.FunctionalComposite)crane.Definition.Specification;
            var capability = specification.Behaviors.OfType<K.PartCapability.Construction>().Single();
            Assert.AreEqual(plot, capability.MaximumBuildingPlotSize);
            Assert.AreEqual(vehicle, capability.MaximumVehicleSize);
        }

        var park = Complete(candidate.View, "park");
        Assert.AreEqual(2, park.Scene.NestedObjects.Max(item => item.NestedItemIndices.Length));
        Assert.IsTrue(park.Scene.NestedObjects.Any(item => item.NestedItemIndices.Length == 1));
        Assert.IsNull(park.Core);
        var addon = candidate.View.FindBlueprint(new(C.BlueprintLibrary.Packages, new(Addon + ":armored-wagon")));
        Assert.IsInstanceOfType<C.ContentLookup<C.BlueprintRecord>.Found>(addon);

        fixture.ReverseEnumeration = true;
        using var reordered = fixture.Load();
        var repeated = Complete(reordered.View, "armed-locomotive");
        Assert.AreEqual(locomotive.Design, repeated.Design);
        Assert.AreEqual(locomotive.Composition, repeated.Composition);
        CollectionAssert.AreEqual(core.Parts.Select(part => (part.Id, part.Definition)).ToArray(),
            repeated.Core!.Design.Parts.Select(part => (part.Id, part.Definition)).ToArray());
    }

    private static C.DesignEvaluation.Complete Complete(C.IContentView view, string id)
    {
        var lookup = view.FindBlueprint(new(C.BlueprintLibrary.Packages, new(Package + ":" + id)));
        Assert.IsInstanceOfType<C.ContentLookup<C.BlueprintRecord>.Found>(lookup);
        var evaluation = ((C.ContentLookup<C.BlueprintRecord>.Found)lookup).Value.Evaluation;
        Assert.IsInstanceOfType<C.DesignEvaluation.Complete>(evaluation);
        var complete = (C.DesignEvaluation.Complete)evaluation;
        Assert.IsTrue(complete.IsValid, string.Join("\n", complete.Diagnostics));
        return complete;
    }

    private static void ReadFiles(string directory, C.ContentRoot root, string prefix, ContentFixture fixture)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(directory, file).Replace('\\', '/');
            var bytes = File.ReadAllBytes(file);
            if (relative.EndsWith(".png", StringComparison.Ordinal))
            {
                // 测试检查器仅提供 PNG 头部尺寸，不证明引擎可解码资产。
                fixture.AddAsset(root, prefix + relative, bytes, new C.AssetMetadata.Png(
                    BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4)), BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4))));
            }
            else if (relative.EndsWith(".glb", StringComparison.Ordinal))
            {
                // 只读取表面数，其余固定格内元数据不证明完整模型事实；真实事实由 CLI 和 Engine 资产测试验证。
                using var glb = JsonDocument.Parse(bytes.AsSpan(20,
                    BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(12, 4))).ToArray());
                var surfaces = glb.RootElement.GetProperty("meshes").EnumerateArray()
                    .Sum(mesh => mesh.GetProperty("primitives").GetArrayLength());
                fixture.AddAsset(root, prefix + relative, bytes, new C.AssetMetadata.Glb(1,
                    [.. Enumerable.Repeat(C.AssetAlphaMode.Opaque, surfaces)],
                    new(new(-0.25, 0, -0.25), new(0.25, 0.5, 0.25)), false, false, false, false, false, false));
            }
            else fixture.AddFile(root, prefix + relative, bytes);
        }
    }
}
