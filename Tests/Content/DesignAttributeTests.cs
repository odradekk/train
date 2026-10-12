// Content 内容模块的设计属性函数示例。
// 用手算结果检验结构、组合部件、BOM、额定速度与不可表示的输入。

using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.Behaviors;
using Train.Content.Composites;
using Train.Content.Voxels;

namespace Train.Content.Tests;

[TestClass]
[TestCategory("family:unit")]
[TestCategory("scope:content")]
public sealed class DesignAttributeTests
{
    [TestMethod]
    public void Empty_input_has_no_capabilities_or_materials()
    {
        var attributes = DesignAttributeCalculator.Calculate(DesignAttributeExamples.Empty);
        Assert.AreEqual(0d, attributes.Mass);
        Assert.AreEqual(0d, attributes.Traction);
        Assert.IsNull(attributes.RatedSpeed);
        Assert.AreEqual(0L, attributes.CargoCapacity);
        Assert.AreEqual(0L, attributes.StructuralDurabilityMax);
        Assert.IsFalse(attributes.HasControl);
        Assert.AreEqual(0, attributes.Bom.Count);
        Assert.AreEqual(0L, attributes.BomValue);
    }

    [TestMethod]
    public void Structure_without_behaviors_contributes_durability_but_no_capabilities()
    {
        var attributes = DesignAttributeCalculator.Calculate(DesignAttributeExamples.StructureOnly);
        Assert.AreEqual(0.01, attributes.Mass, 1e-12);
        Assert.AreEqual(100L, attributes.StructuralDurabilityMax);
        Assert.AreEqual(1L, attributes.BomValue);
        Assert.AreEqual(1, attributes.Bom[DesignAttributeExamples.Structure.Id]);
        Assert.AreEqual(0d, attributes.Traction);
        Assert.IsNull(attributes.RatedSpeed);
        Assert.AreEqual(0L, attributes.CargoCapacity);
        Assert.IsFalse(attributes.HasControl);
    }

    [TestMethod]
    public void Structure_and_three_components_produce_the_hand_calculated_design_attributes()
    {
        var attributes = DesignAttributeCalculator.Calculate(DesignAttributeExamples.Mixed);
        Assert.AreEqual(7.12, attributes.Mass, 1e-12);
        Assert.AreEqual(962L, attributes.BomValue);
        Assert.AreEqual(12.5, attributes.Traction);
        Assert.AreEqual(20d, attributes.RatedSpeed);
        Assert.AreEqual(4L, attributes.CargoCapacity);
        Assert.AreEqual(100L, attributes.StructuralDurabilityMax);
        Assert.IsTrue(attributes.HasControl);
        Assert.AreEqual(4, attributes.Bom.Count);
        foreach (var definition in DesignAttributeExamples.Mixed)
            Assert.AreEqual(1, attributes.Bom[definition.Id]);
    }

    [TestMethod]
    public void Repeated_voxel_identities_count_materials_and_capabilities_per_cell()
    {
        var attributes = DesignAttributeCalculator.Calculate(new[]
        {
            DesignAttributeExamples.Structure, DesignAttributeExamples.Structure,
            DesignAttributeExamples.Propulsion, DesignAttributeExamples.Cargo, DesignAttributeExamples.Cargo
        });
        Assert.AreEqual(8.76, attributes.Mass, 1e-12);
        Assert.AreEqual(726L, attributes.BomValue);
        Assert.AreEqual(200L, attributes.StructuralDurabilityMax);
        Assert.AreEqual(12.5, attributes.Traction);
        Assert.AreEqual(8L, attributes.CargoCapacity);
        Assert.IsFalse(attributes.HasControl);
        Assert.AreEqual(3, attributes.Bom.Count);
        Assert.AreEqual(2, attributes.Bom[DesignAttributeExamples.Structure.Id]);
        Assert.AreEqual(1, attributes.Bom[DesignAttributeExamples.Propulsion.Id]);
        Assert.AreEqual(2, attributes.Bom[DesignAttributeExamples.Cargo.Id]);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Composed_behaviors_share_one_material_cell_and_speed_uses_the_slowest_propulsion(bool slowFirst)
    {
        var voxels = slowFirst
            ? new[] { DesignAttributeExamples.Combined, DesignAttributeExamples.Propulsion }
            : new[] { DesignAttributeExamples.Propulsion, DesignAttributeExamples.Combined };
        var attributes = DesignAttributeCalculator.Calculate(voxels);
        Assert.AreEqual(9d, attributes.Mass);
        Assert.AreEqual(550L, attributes.BomValue);
        Assert.AreEqual(17.5, attributes.Traction);
        Assert.AreEqual(10d, attributes.RatedSpeed);
        Assert.AreEqual(1L, attributes.CargoCapacity);
        Assert.AreEqual(0L, attributes.StructuralDurabilityMax);
        Assert.IsTrue(attributes.HasControl);
        Assert.AreEqual(2, attributes.Bom.Count);
        Assert.AreEqual(1, attributes.Bom[DesignAttributeExamples.Combined.Id]);
    }

    [TestMethod]
    public void Null_input_or_cell_is_rejected()
    {
        Assert.ThrowsException<ArgumentNullException>(() => DesignAttributeCalculator.Calculate(null!));
        Assert.ThrowsException<ArgumentNullException>(() => DesignAttributeCalculator.Calculate(new ICoreVoxelDefinition[] { null! }));
    }

    [TestMethod]
    public void Non_finite_aggregate_is_rejected_instead_of_publishing_infinite_traction()
    {
        var definition = new VoxelDefinition(
            new VoxelDefinitionId("core", "large-propulsion"), "动力", "大牵引力", 1, 1, 100,
            Array.Empty<string>(), new VoxelBehavior[] { new PropulsionBehavior(double.MaxValue, 20) },
            VoxelOrientationConstraint.None, new VoxelPresentation(VoxelPresentationKind.Cube, "a.png", "a.png", "a.png", "a.png"),
            new VoxelCollision(VoxelCollisionKind.FullBox));
        Assert.ThrowsException<OverflowException>(() => DesignAttributeCalculator.Calculate(new[] { definition, definition }));
    }
}
