using Microsoft.VisualStudio.TestTools.UnitTesting;
using C = Train.Content.Contracts;
using K = Train.Core.Contracts;

namespace Train.Content.Tests.Loading;

[TestClass]
public sealed class DesignEvaluationTests
{
    [TestMethod]
    [DataRow(0, 0)]
    [DataRow(int.MinValue, 0)]
    [DataRow(-int.MaxValue, 0)]
    [DataRow(int.MinValue, int.MaxValue)]
    public void Unrepresentable_length_or_invalid_palette_index_fails_as_a_design_error(int minimum, int maximum)
    {
        using var candidate = DesignFixtures.Content().Load();
        var draft = DesignFixtures.Vehicle() with
        {
            Layout = new([DesignFixtures.Entry()], [new(new(0, 0, minimum), minimum == maximum ? 1 : 0), new(new(0, 0, maximum), 0)], [])
        };
        var result = ContentFactory.Create().EvaluateDesign(candidate.View, draft);
        Assert.IsInstanceOfType<C.ContentResult<C.DesignEvaluation>.Success>(result, result.ToString());
        var evaluation = ((C.ContentResult<C.DesignEvaluation>.Success)result).Value;
        Assert.IsInstanceOfType<C.DesignEvaluation.Failed>(evaluation);
        Assert.IsTrue(((C.DesignEvaluation.Failed)evaluation).Diagnostics.Any(d => d.Code == "design_invalid"));
    }

    [TestMethod]
    public void Representable_but_illegal_vehicle_length_keeps_a_complete_projection()
    {
        using var candidate = DesignFixtures.Content().Load();
        var draft = DesignFixtures.Vehicle() with
        {
            Layout = new([DesignFixtures.Entry()], [new(new(0, 0, -int.MaxValue + 1), 0), new(new(0, 0, 0), 0)], [])
        };
        var evaluation = DesignFixtures.Evaluate(candidate.View, draft);
        Assert.IsFalse(evaluation.IsValid);
        Assert.AreEqual(int.MaxValue, ((K.DesignKind.Vehicle)evaluation.Core!.Design.Kind).LengthCells);
    }

    [TestMethod]
    public void Illegal_scene_cells_do_not_count_as_bill_of_materials_items()
    {
        using var candidate = DesignFixtures.Content().Load();
        var draft = DesignFixtures.Vehicle() with
        {
            Layout = new([DesignFixtures.Entry(), DesignFixtures.Entry("scenery")],
                [new(new(0, 0, 0), 0), new(new(0, 0, 1), 1)], [])
        };
        var evaluation = DesignFixtures.Evaluate(candidate.View, draft);
        Assert.IsFalse(evaluation.IsValid);
        var design = evaluation.Core!.Design;
        Assert.IsFalse(design.CanConstruct);
        CollectionAssert.AreEqual(new[] { new K.GoodsQuantity(new K.GoodId.Voxel(new("core:block")), 1) }, design.BillOfMaterials.ToArray());
        Assert.AreEqual(1L, design.BillOfMaterialsItemCount);
    }

    [TestMethod]
    public void Hashes_encode_the_design_fields_and_empty_composites_without_external_revisions()
    {
        using var candidate = DesignFixtures.Content().Load();
        var draft = new C.DesignDraft(new("Test", "Design"), new C.DesignSpecification.Vehicle(K.SizeClass.Small),
            new([DesignFixtures.Entry()], [new(new(0, 0, 0), 0)], []));
        const string complete = """
            {"category":"vehicle","display":{"description":"Design","name":"Test"},"layout":{"cells":[{"at":[0,0,0],"orientation":"px_py","paint":null,"shape":null,"voxel":"core:block"}],"composites":[]},"simulation":{"size_class":"small"}}
            """;
        const string composition = """
            {"category":"vehicle","display":{},"layout":{"cells":[{"at":[0,0,0],"orientation":"px_py","voxel":"core:block"}],"composites":[]},"simulation":{"size_class":"small"}}
            """;
        var result = DesignFixtures.Evaluate(candidate.View, draft);
        Assert.AreEqual(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(complete))), result.Design.Value);
        Assert.AreEqual(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(composition))), result.Composition.Value);
    }

    [TestMethod]
    [DataRow(-3, 0, -26, true)]
    [DataRow(3, 8, 25, true)]
    [DataRow(-4, 0, 0, false)]
    [DataRow(4, 0, 0, false)]
    [DataRow(0, -1, 0, false)]
    [DataRow(0, 9, 0, false)]
    [DataRow(0, 0, -27, false)]
    [DataRow(0, 0, 26, false)]
    public void Vehicle_envelope_uses_centered_horizontal_bounds_and_ground_based_height(int x, int y, int z, bool valid)
    {
        using var candidate = DesignFixtures.Content().Load();
        var draft = DesignFixtures.Vehicle() with { Layout = new([DesignFixtures.Entry()], [new(new(x, y, z), 0)], []) };
        var result = DesignFixtures.Evaluate(candidate.View, draft);
        Assert.AreEqual(valid, result.IsValid);
        Assert.AreEqual(valid, result.Core!.Design.CanConstruct);
    }

    [TestMethod]
    [DataRow(K.PlotSize.SmallStation, 95, 47, 55)]
    [DataRow(K.PlotSize.LargeStation, 127, 63, 103)]
    public void Station_has_its_own_plot_envelope_and_requires_matching_building_type(K.PlotSize size, int x, int y, int z)
    {
        using var candidate = DesignFixtures.Content().Load();
        var draft = DesignFixtures.Vehicle() with
        {
            Specification = new C.DesignSpecification.Building(size, K.BuildingKind.Station, []),
            Layout = new([DesignFixtures.Entry()], [new(new(x, y, z), 0)], [])
        };
        Assert.IsTrue(DesignFixtures.Evaluate(candidate.View, draft).IsValid);
        Assert.IsFalse(DesignFixtures.Evaluate(candidate.View, draft with { Layout = draft.Layout with { Cells = [new(new(x + 1, y, z), 0)] } }).IsValid);
        Assert.IsFalse(DesignFixtures.Evaluate(candidate.View, draft with { Specification = new C.DesignSpecification.Building(size, K.BuildingKind.Warehouse, []) }).IsValid);
        Assert.IsFalse(DesignFixtures.Evaluate(candidate.View, draft with { Specification = new C.DesignSpecification.Building(K.PlotSize.Small, K.BuildingKind.Station, []) }).IsValid);
    }

    [TestMethod]
    public void Minimum_durability_part_still_contributes_one_structure_point_and_keeps_orientation()
    {
        var fixture = DesignFixtures.Content();
        DesignFixtures.AddVoxel(fixture, "machine", behaviors: """{"control":{}}""", durability: 1);
        using var candidate = fixture.Load();
        var draft = DesignFixtures.Vehicle() with { Layout = new([DesignFixtures.Entry("machine") with { Orientation = C.VoxelOrientation.PzPy }], [new(new(0, 0, 0), 0)], []) };
        var result = DesignFixtures.Evaluate(candidate.View, draft);
        Assert.IsTrue(result.IsValid);
        Assert.AreEqual(1L, result.Core!.Design.MaxStructureDurability);
        Assert.AreEqual(0L, result.Core.Design.StructureHitWeight);
        Assert.AreEqual(0L, result.Core.Design.StructuralBaseValue.Value);
        Assert.AreEqual(0, result.Core.Design.StructuralGoods.Length);
        Assert.AreEqual(C.VoxelOrientation.PzPy, result.Scene.Voxels.Single().Orientation);
        Assert.AreEqual(C.VoxelOrientation.PzPy, result.Scene.Voxels.Single().LocalOrientation);
    }

    [TestMethod]
    public void Connected_vehicle_projects_materials_parts_and_scene_from_each_occupied_cell()
    {
        using var candidate = DesignFixtures.Content().Load();
        var draft = DesignFixtures.Vehicle();
        var result = DesignFixtures.Evaluate(candidate.View, draft);
        Assert.IsTrue(result.IsValid);
        Assert.AreEqual(0, result.Diagnostics.Length);
        Assert.AreEqual(draft.Display, result.Definition.Display);
        var input = result.Core!;
        Assert.AreEqual(candidate.View.Core.Identity, input.InputIdentity);
        Assert.AreEqual(0, input.AssemblyGoods.Length);
        var core = input.Design;
        Assert.AreEqual(result.Design, core.Design);
        Assert.AreEqual(result.Composition, core.Composition);
        Assert.AreEqual(new K.DesignKind.Vehicle(K.SizeClass.Small, 3), core.Kind);
        Assert.AreEqual(13L, core.MaxStructureDurability);
        Assert.AreEqual(2L, core.StructureHitWeight);
        Assert.AreEqual(9d, core.MassTons);
        Assert.AreEqual(new K.Money(14), core.StructuralBaseValue);
        CollectionAssert.AreEqual(new[] { new K.GoodsQuantity(new K.GoodId.Voxel(new("core:block")), 2) }, core.StructuralGoods.ToArray());
        CollectionAssert.AreEqual(new[] { new K.GoodsQuantity(new K.GoodId.Voxel(new("core:block")), 2), new K.GoodsQuantity(new K.GoodId.Voxel(new("core:machine")), 1) }, core.BillOfMaterials.ToArray());
        Assert.AreEqual(3L, core.BillOfMaterialsItemCount);
        Assert.AreEqual(0, core.ReturnUnits.Length);
        Assert.AreEqual(0, core.MissingReferences.Length);
        Assert.IsTrue(core.CanConstruct);
        var part = core.Parts.Single();
        Assert.AreEqual(new K.CoreDefinitionReference.Voxel(new("core:machine")), part.Definition);
        Assert.AreEqual(new K.GoodId.Voxel(new("core:machine")), part.RepairGood);
        Assert.AreEqual(5L, part.MaxDurability);
        Assert.AreEqual(1L, part.HitWeight);
        Assert.AreEqual(new K.Money(7), part.BaseValue);
        Assert.IsFalse(part.Missing);
        CollectionAssert.AreEquivalent(new K.PartCapability[] { new K.PartCapability.Propulsion(25, 20), new K.PartCapability.Control(),
            new K.PartCapability.Storage(100), new K.PartCapability.Production(2), new K.PartCapability.Extraction(3),
            new K.PartCapability.Construction(4, K.SizeClass.Titan), new K.PartCapability.Power(5), new K.PartCapability.Weapon(6, 0.5), new K.PartCapability.Platform() }, part.Capabilities.ToArray());
        var scene = result.Scene;
        Assert.AreEqual(result.Design, scene.Design);
        Assert.AreEqual(new C.GridBounds(new(0, 0, -1), new(0, 0, 1)), scene.OccupiedBounds);
        Assert.IsNotNull(scene.AllowedEnvelope);
        Assert.IsTrue(scene.OccupancyComplete);
        Assert.IsNull(scene.RootRotation);
        Assert.IsNull(scene.RootFlatten);
        Assert.AreEqual(0, scene.NestedObjects.Length);
        Assert.AreEqual(0, scene.RotationSweeps.Length);
        Assert.AreEqual(0, scene.EditLocks.Length);
        Assert.AreEqual(3, scene.Elements.Length);
        var machine = scene.Voxels.Single(v => v.Voxel.Value == "core:machine");
        Assert.AreEqual(part.Id, machine.Number);
        Assert.AreEqual(0, ((C.DesignElementPath.Cell)machine.Element).CellIndex);
        Assert.AreEqual(0, machine.ObjectPath.Length);
        Assert.AreEqual(machine.At, machine.LocalAt);
        Assert.AreEqual(machine.Orientation, machine.LocalOrientation);
        Assert.AreEqual(new C.VoxelShape("cube"), machine.Shape);
        Assert.IsNull(machine.Paint);
        Assert.IsFalse(machine.Missing);
        var element = scene.Elements.Single(e => e.Number == part.Id);
        Assert.IsTrue(element.FunctionalPart);
        Assert.IsFalse(element.Missing);
        CollectionAssert.AreEqual(new[] { machine.At }, ((C.ComputedValue<System.Collections.Immutable.ImmutableArray<C.GridPosition>>.Known)element.OccupiedCells).Value.ToArray());
    }

    [TestMethod]
    public void Palette_and_cell_order_do_not_change_hashes_or_numbers_but_paths_keep_original_indices()
    {
        using var candidate = DesignFixtures.Content().Load();
        var draft = DesignFixtures.Vehicle();
        var before = DesignFixtures.Evaluate(candidate.View, draft);
        var reordered = draft with { Layout = new([DesignFixtures.Entry(), DesignFixtures.Entry("scenery"), DesignFixtures.Entry("machine")],
            [new(new(0, 0, -1), 0), new(new(0, 0, 1), 2), new(new(0, 0, 0), 0)], []) };
        var after = DesignFixtures.Evaluate(candidate.View, reordered);
        Assert.AreEqual(before.Design, after.Design);
        Assert.AreEqual(before.Composition, after.Composition);
        foreach (var voxel in before.Scene.Voxels)
            Assert.AreEqual(voxel.Number, after.Scene.Voxels.Single(v => v.At == voxel.At).Number);
        Assert.AreEqual(1, ((C.DesignElementPath.Cell)after.Scene.Voxels.Single(v => v.Voxel.Value == "core:machine").Element).CellIndex);
        Assert.AreEqual(0, ((C.DesignElementPath.Cell)after.Scene.Voxels[0].Element).NestedItemIndices.Length);
    }

    [TestMethod]
    public void Presentation_changes_only_design_hash_while_specification_changes_both_hashes()
    {
        using var candidate = DesignFixtures.Content().Load();
        var draft = DesignFixtures.Vehicle();
        var original = DesignFixtures.Evaluate(candidate.View, draft);
        var appearance = DesignFixtures.Evaluate(candidate.View, draft with { Display = new("改名", "改说明"),
            Layout = draft.Layout with { Palette = [draft.Layout.Palette[0] with { Shape = new("wedge"), Paint = new("#123abc") }, draft.Layout.Palette[1]] } });
        Assert.AreNotEqual(original.Design, appearance.Design);
        Assert.AreEqual(original.Composition, appearance.Composition);
        var size = DesignFixtures.Evaluate(candidate.View, draft with { Specification = new C.DesignSpecification.Vehicle(K.SizeClass.Medium) });
        Assert.AreNotEqual(original.Design, size.Design);
        Assert.AreNotEqual(original.Composition, size.Composition);
        var explicitShape = DesignFixtures.Evaluate(candidate.View, draft with { Layout = draft.Layout with { Palette = [draft.Layout.Palette[0] with { Shape = new("cube") }, draft.Layout.Palette[1]] } });
        Assert.AreNotEqual(original.Design, explicitShape.Design);
        Assert.AreEqual(original.Composition, explicitShape.Composition);
    }

    [TestMethod]
    public void Building_and_scene_model_preserve_their_specific_projection_and_flatten_hash()
    {
        using var candidate = DesignFixtures.Content().Load();
        var buildingDraft = new C.DesignDraft(new("Test", "Design"),
            new C.DesignSpecification.Building(K.PlotSize.Small, K.BuildingKind.Production, [K.FacilityTag.FoodProcessing, K.FacilityTag.Assembly]),
            new([DesignFixtures.Entry()], [new(new(0, 0, 0), 0)], []));
        var building = DesignFixtures.Evaluate(candidate.View, buildingDraft);
        const string buildingJson = """
            {"category":"building","display":{"description":"Design","name":"Test"},"layout":{"cells":[{"at":[0,0,0],"orientation":"px_py","paint":null,"shape":null,"voxel":"core:block"}],"composites":[]},"simulation":{"building_type":"production","facility_tags":["assembly","food_processing"],"plot_size":"small"}}
            """;
        Assert.AreEqual(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(buildingJson))), building.Design.Value);
        var kind = (K.DesignKind.Building)building.Core!.Design.Kind;
        Assert.AreEqual(K.PlotSize.Small, kind.PlotSize);
        Assert.AreEqual(K.BuildingKind.Production, kind.Type);
        CollectionAssert.AreEqual(new[] { K.FacilityTag.Assembly, K.FacilityTag.FoodProcessing }, kind.FacilityTags.ToArray());
        Assert.AreEqual(building.Design, DesignFixtures.Evaluate(candidate.View, buildingDraft with
        { Specification = new C.DesignSpecification.Building(K.PlotSize.Small, K.BuildingKind.Production, [K.FacilityTag.Assembly, K.FacilityTag.FoodProcessing]) }).Design);
        var flatten = new C.FlattenRegion(new(-1, -2), new(1, 2), 3);
        var draft = new C.DesignDraft(new("Scene", "Model"), new C.DesignSpecification.SceneModel(flatten),
            new([DesignFixtures.Entry("scenery")], [new(new(0, 0, 0), 0), new(new(5, 0, 5), 0)], []));
        var scene = DesignFixtures.Evaluate(candidate.View, draft);
        const string sceneJson = """
            {"appearance":{"flatten":{"from":[-1,-2],"height":3,"to":[1,2]}},"category":"scene_model","display":{"description":"Model","name":"Scene"},"layout":{"cells":[{"at":[0,0,0],"orientation":"px_py","paint":null,"shape":null,"voxel":"core:scenery"},{"at":[5,0,5],"orientation":"px_py","paint":null,"shape":null,"voxel":"core:scenery"}],"composites":[]}}
            """;
        Assert.AreEqual(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sceneJson))), scene.Design.Value);
        Assert.IsTrue(scene.IsValid);
        Assert.IsFalse(DesignFixtures.Evaluate(candidate.View, draft with { Layout = new([DesignFixtures.Entry()], [new(new(0, 0, 0), 0)], []) }).IsValid);
        Assert.IsNull(scene.Core);
        Assert.IsNull(scene.Scene.AllowedEnvelope);
        Assert.AreEqual(flatten, scene.Scene.RootFlatten);
        var changed = DesignFixtures.Evaluate(candidate.View, draft with { Specification = new C.DesignSpecification.SceneModel(flatten with { Height = 4 }) });
        Assert.AreNotEqual(scene.Design, changed.Design);
        Assert.AreNotEqual(scene.Composition, changed.Composition);
    }

    [TestMethod]
    [DataRow("disconnected")]
    [DataRow("envelope")]
    [DataRow("scene")]
    [DataRow("orientation")]
    [DataRow("shape")]
    [DataRow("paint")]
    [DataRow("duplicate_tags")]
    [DataRow("unknown_tag")]
    [DataRow("unknown_building")]
    public void Complete_but_illegal_design_retains_core_and_cell_diagnostics(string problem)
    {
        using var candidate = DesignFixtures.Content().Load();
        var draft = DesignFixtures.Vehicle();
        var entry = DesignFixtures.Entry();
        var at = problem == "envelope" ? new C.GridPosition(4, 0, 0) : new C.GridPosition(0, 0, 1);
        if (problem == "scene") entry = DesignFixtures.Entry("scenery");
        if (problem == "orientation") entry = entry with { Orientation = C.VoxelOrientation.PxNy };
        if (problem == "shape") entry = entry with { Shape = new("unlisted") };
        if (problem == "paint") entry = entry with { Paint = new("red") };
        if (problem == "duplicate_tags") draft = draft with { Specification = new C.DesignSpecification.Building(K.PlotSize.Small, K.BuildingKind.Production, [K.FacilityTag.Assembly, K.FacilityTag.Assembly]) };
        if (problem == "unknown_tag") draft = draft with { Specification = new C.DesignSpecification.Building(K.PlotSize.Small, K.BuildingKind.Production, [(K.FacilityTag)999]) };
        if (problem == "unknown_building") draft = draft with { Specification = new C.DesignSpecification.Building(K.PlotSize.Small, (K.BuildingKind)999, []) };
        draft = draft with { Layout = new([entry, DesignFixtures.Entry()], [new(at, 0), new(problem == "envelope" ? new(3, 0, 0) : new(0, 0, problem == "disconnected" ? -1 : 0), 1)], []) };
        var result = DesignFixtures.Evaluate(candidate.View, draft);
        Assert.IsFalse(result.IsValid);
        Assert.IsFalse(result.Core!.Design.CanConstruct);
        if (problem is "duplicate_tags" or "unknown_tag")
            Assert.IsTrue(result.Diagnostics.Any(d => d.Code == "design_invalid" && d.Field == "/simulation/facility_tags"));
        else if (problem == "unknown_building")
            Assert.IsTrue(result.Diagnostics.Any(d => d.Code == "design_invalid" && d.Field == "/simulation/building_type"));
        else if (problem == "paint")
            Assert.IsTrue(result.Diagnostics.Any(d => d.Code == "design_invalid" && d.Field == "/layout/palette/0/paint"));
        else Assert.IsTrue(result.Diagnostics.Any(d => d.Severity == C.ContentDiagnosticSeverity.Error && d.Element is C.DesignElementPath.Cell { CellIndex: 0 } cell && cell.NestedItemIndices.IsEmpty));
    }

    [TestMethod]
    public void Unsupported_nested_categories_and_partial_inputs_throw_directly()
    {
        using var candidate = DesignFixtures.Content().Load();
        var draft = DesignFixtures.Vehicle();
        var factory = ContentFactory.Create();
        foreach (var specification in new C.DesignSpecification[] { new C.DesignSpecification.Assembly(null),
            new C.DesignSpecification.FunctionalComposite([], null), new C.DesignSpecification.Creature(null, null), new C.DesignSpecification.Vehicle(null),
            new C.DesignSpecification.Building(null, K.BuildingKind.Warehouse, []) })
            Assert.ThrowsExactly<NotImplementedException>(() => factory.EvaluateDesign(candidate.View, draft with { Specification = specification }));
        Assert.ThrowsExactly<NotImplementedException>(() => factory.EvaluateDesign(candidate.View, draft with { Layout = draft.Layout with { NestedItems = [new C.NestedDesignItem.AssemblyCopy(new(0, 0, 0), C.VoxelOrientation.PxPy, draft)] } }));
        Assert.ThrowsExactly<NotImplementedException>(() => factory.EvaluateDesign(candidate.View, draft with { Layout = draft.Layout with { Palette = [DesignFixtures.Entry("absent"), DesignFixtures.Entry()] } }));
        Assert.ThrowsExactly<NotImplementedException>(() => factory.EvaluateDesign(candidate.View, draft with { Layout = draft.Layout with { Palette = [draft.Layout.Palette[0], draft.Layout.Palette[1], DesignFixtures.Entry("absent")] } }));
    }

    [TestMethod]
    public void Each_fixed_view_recalculates_semantics_and_keeps_old_results_and_presentation_only_changes_stable()
    {
        var fixture = DesignFixtures.Content();
        using var original = fixture.Load();
        var draft = DesignFixtures.Vehicle();
        var before = DesignFixtures.Evaluate(original.View, draft);
        DesignFixtures.AddVoxel(fixture, "block", durability: 9, mass: 4, value: 11);
        using var updated = fixture.Load();
        var after = DesignFixtures.Evaluate(updated.View, draft);
        Assert.AreEqual(before.Design, after.Design);
        Assert.AreEqual(before.Composition, after.Composition);
        Assert.AreNotEqual(before.Core!.InputIdentity, after.Core!.InputIdentity);
        Assert.AreEqual(21L, after.Core.Design.MaxStructureDurability);
        Assert.AreEqual(11d, after.Core.Design.MassTons);
        Assert.AreEqual(new K.Money(22), after.Core.Design.StructuralBaseValue);
        CollectionAssert.AreEqual(before.Core.Design.BillOfMaterials.ToArray(), after.Core.Design.BillOfMaterials.ToArray());
        Assert.AreEqual(13L, DesignFixtures.Evaluate(original.View, draft).Core!.Design.MaxStructureDurability);
        fixture.AddAsset(ContentFixture.BuiltIn, "resources/voxels/block/tile.png", [7, 8, 9], new C.AssetMetadata.Png(32, 32));
        using var presentation = fixture.Load();
        var visual = DesignFixtures.Evaluate(presentation.View, draft);
        Assert.AreEqual(after.Core.InputIdentity, visual.Core!.InputIdentity);
        Assert.AreEqual(after.Core.Design.MassTons, visual.Core.Design.MassTons);
        Assert.AreEqual(after.Core.Design.MaxStructureDurability, visual.Core.Design.MaxStructureDurability);
        CollectionAssert.AreEqual(after.Core.Design.BillOfMaterials.ToArray(), visual.Core.Design.BillOfMaterials.ToArray());
    }
}
