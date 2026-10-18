using System.Buffers.Binary;
using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Train.Content.Tests.Loading.WorldContentFixtures;
using C = Train.Content.Contracts;
using K = Train.Core.Contracts;

namespace Train.Content.Tests.Loading;

[TestClass]
[TestCategory("integration")]
public sealed class BuiltInContentTests
{
    [TestMethod]
    public void Builtin_core_loads_running_vehicles_and_economic_templates_with_real_facilities()
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository != null && !File.Exists(Path.Combine(repository.FullName, "global.json"))) repository = repository.Parent;
        Assert.IsNotNull(repository);
        var builtIn = new C.ContentRoot(Path.Combine(repository.FullName, "builtin", "core"));
        var mods = new C.ContentRoot(Path.Combine(repository.FullName, "TestResults", "builtin-test-mods-" + Guid.NewGuid().ToString("N")));
        var factory = ContentFactory.Create();
        var result = factory.PrepareLoad(new(new(builtIn, mods, null, "0.0.56", []), new C.ContentLoadPurpose.Initial()),
            new ContentDiskFiles(), new FixtureAssetInspector());
        Assert.IsInstanceOfType<C.ContentResult<C.IContentCandidate>.Success>(result, result.ToString());
        using var candidate = ((C.ContentResult<C.IContentCandidate>.Success)result).Value;
        Assert.AreEqual(0, candidate.Report.Diagnostics.Length);
        var view = candidate.View;
        CollectionAssert.AllItemsAreUnique(new[] { "plains", "forest", "desert", "mountains" }
            .Select(id => Found(view.FindTerrain(new("core:" + id))).SurfaceVoxel).ToArray());
        var vehicleDesigns = new List<K.DesignProjection>();
        foreach (var summary in view.Blueprints)
        {
            var blueprint = Found(view.FindBlueprint(summary.Address));
            AssertPaintedPalette(blueprint.Definition);
            C.DesignEvaluation evaluation;
            if (blueprint.Definition.Specification is C.DesignSpecification.FunctionalComposite)
                evaluation = blueprint.Evaluation;
            else
            {
                var evaluated = factory.EvaluateDesign(view, blueprint.Definition);
                Assert.IsInstanceOfType<C.ContentResult<C.DesignEvaluation>.Success>(evaluated, summary.Address.ToString());
                evaluation = ((C.ContentResult<C.DesignEvaluation>.Success)evaluated).Value;
            }
            Assert.IsInstanceOfType<C.DesignEvaluation.Complete>(evaluation, summary.Address.ToString());
            var complete = (C.DesignEvaluation.Complete)evaluation;
            Assert.IsTrue(complete.IsValid, summary.Address.ToString());
            Assert.AreEqual(0, complete.Diagnostics.Length, summary.Address.ToString());
            if (complete.Core is not { } core) continue;
            if (core.Design.Kind is K.DesignKind.Vehicle) vehicleDesigns.Add(core.Design);
            Assert.IsFalse(core.Design.BillOfMaterials.IsEmpty, summary.Address.ToString());
            foreach (var item in core.Design.BillOfMaterials)
            {
                Assert.IsTrue(item.Quantity > 0, summary.Address.ToString());
                Assert.IsFalse(Found(view.FindGood(item.Good)).Missing, item.Good.ToString());
            }
        }
        void AssertPaintedPalette(C.DesignDraft design)
        {
            foreach (var entry in design.Layout.Palette)
            {
                var appearance = Found(view.FindVoxelAppearance(entry.Voxel));
                if (appearance.Presentation is C.VoxelPresentation.Cube cube &&
                    cube.Material is C.MaterialCategory.PaintedMetal or C.MaterialCategory.Concrete)
                    Assert.IsNotNull(entry.Paint, entry.Voxel.ToString());
            }
            foreach (var nested in design.Layout.NestedItems)
                if (nested is C.NestedDesignItem.AssemblyCopy assembly) AssertPaintedPalette(assembly.Design);
                else if (nested is C.NestedDesignItem.SceneModelCopy scene) AssertPaintedPalette(scene.Design);
        }
        var processes = view.Core.Processes.ToDictionary(process => ProcessReference(process.Details));
        foreach (var sizes in new K.SizeClass[][]
                 {
                     [K.SizeClass.Small],
                     [K.SizeClass.Medium, K.SizeClass.Large],
                     [K.SizeClass.ExtraLarge, K.SizeClass.Titan]
                 })
        {
            var designs = vehicleDesigns.Where(design => sizes.Contains(((K.DesignKind.Vehicle)design.Kind).Size)).ToArray();
            Assert.IsTrue(designs.Any(design =>
                design.Parts.SelectMany(part => part.Capabilities).OfType<K.PartCapability.Control>().Any() &&
                design.Parts.SelectMany(part => part.Capabilities).OfType<K.PartCapability.Propulsion>().Any()), string.Join(",", sizes));
            Assert.IsTrue(designs.Any(design => design.Parts.SelectMany(part => part.Capabilities)
                .OfType<K.PartCapability.Storage>().Any(storage => storage.Capacity > 0)), string.Join(",", sizes));
        }
        var station = Complete(view, "small-station");
        Assert.IsNotNull(station.Core);
        Assert.IsTrue(station.Core.Design.Parts.Any(part => part.Definition is K.CoreDefinitionReference.Voxel &&
            part.Capabilities.OfType<K.PartCapability.Platform>().Any()));
        Assert.AreEqual(2, station.Core.Design.Parts.Count(part => part.Capabilities.OfType<K.PartCapability.Platform>().Any()));
        var largeStation = Complete(view, "large-station");
        Assert.IsNotNull(largeStation.Core);
        var stationKind = (K.DesignKind.Building)largeStation.Core.Design.Kind;
        Assert.AreEqual(K.BuildingKind.Station, stationKind.Type);
        Assert.AreEqual(K.PlotSize.LargeStation, stationKind.PlotSize);
        var platforms = largeStation.Core.Design.Parts.Where(part => part.Capabilities.OfType<K.PartCapability.Platform>().Any()).ToArray();
        Assert.AreEqual(4, platforms.Length);
        // 槽位与最近规则见 docs/contracts/scenes.md：large_station 为 −80/−40/40/80，等距取较小 LocalZ。
        var slots = new[] { -80, -40, 40, 80 };
        var assigned = platforms.Select(part =>
        {
            var localZ = largeStation.Scene.Voxels.Where(voxel => voxel.Number == part.Id).Average(voxel => (double)voxel.At.Z);
            var ordered = slots.OrderBy(slot => Math.Abs(slot - localZ)).ThenBy(slot => slot).ToArray();
            Assert.IsTrue(Math.Abs(ordered[0] - localZ) < Math.Abs(ordered[1] - localZ), part.Id.ToString());
            return ordered[0];
        }).ToArray();
        CollectionAssert.AreEquivalent(slots, assigned);
        var largeWarehouse = Complete(view, "large-warehouse");
        Assert.IsNotNull(largeWarehouse.Core);
        var warehouseKind = (K.DesignKind.Building)largeWarehouse.Core.Design.Kind;
        Assert.AreEqual(K.BuildingKind.Warehouse, warehouseKind.Type);
        Assert.AreEqual(K.PlotSize.Medium, warehouseKind.PlotSize);
        long Storage(C.DesignEvaluation.Complete design) => design.Core!.Design.Parts.SelectMany(part => part.Capabilities)
            .OfType<K.PartCapability.Storage>().Sum(item => item.Capacity);
        Assert.IsTrue(Storage(largeWarehouse) > Storage(Complete(view, "small-warehouse")));
        foreach (var (id, productionBuildings) in new (string, string[])[]
                 {
                     ("agricultural-town", ["farm", "food-factory"]),
                     ("mining-town", ["extraction-site", "metallurgy-factory", "building-materials-factory"]),
                     ("forestry-town", ["extraction-site", "light-industry-factory"]),
                     ("oilfield-town", ["extraction-site", "chemical-factory"]),
                     ("industrial-city", ["assembly-factory", "electronics-factory", "recovery-factory", "heavy-vehicle-factory"]),
                     ("core-city", ["assembly-factory", "electronics-factory", "recovery-factory", "heavy-vehicle-factory"])
                 })
        {
            var template = view.Core.SettlementTemplates.Single(item => item.Id.Value == "core:" + id);
            Assert.IsTrue(view.Core.SettlementTypes.Any(type => type.Id == template.Type), id);
            Assert.IsTrue(template.Plots.Length >= 8, id);
            Assert.IsTrue(template.Plots.Count(plot => plot.Size == K.PlotSize.SmallStation) >= 2, id);
            Complete(view, id + "-ground");
            foreach (var building in productionBuildings.Concat(["small-station", "vehicle-workshop", "small-warehouse", "power-station"]))
            {
                var design = Complete(view, building);
                Assert.IsNotNull(design.Core);
                Assert.IsTrue(template.Plots.Any(plot => plot.PrebuiltDesign == design.Design), id + ":" + building);
                var capabilities = design.Core.Design.Parts.SelectMany(part => part.Capabilities).ToArray();
                Assert.IsTrue(building switch
                {
                    "small-station" => capabilities.OfType<K.PartCapability.Platform>().Any(),
                    "vehicle-workshop" or "heavy-vehicle-factory" => capabilities.OfType<K.PartCapability.Construction>().Any(),
                    "small-warehouse" => capabilities.OfType<K.PartCapability.Storage>().Any(storage => storage.Capacity > 0),
                    "power-station" => capabilities.OfType<K.PartCapability.Power>().Any(power => power.EnergyPerSecond > 0),
                    "extraction-site" => capabilities.OfType<K.PartCapability.Extraction>().Any(extraction => extraction.WorkPerSecond > 0),
                    _ => capabilities.OfType<K.PartCapability.Production>().Any(production => production.WorkPerSecond > 0)
                }, building);
            }
            CollectionAssert.AreEquivalent(new[] { station.Design, Complete(view, "basic-shed").Design },
                template.FoundingBuildings.Select(building => building.Design).ToArray(), id);
            foreach (var founding in template.FoundingBuildings)
                Assert.IsTrue(template.Plots.Any(plot => plot.Number == founding.PlotNumber), id);
            Assert.IsFalse(template.ProductionPlans.IsEmpty, id);
            CollectionAssert.AllItemsAreUnique(template.ProductionPlans
                .Select(plan => (plan.PlotNumber, plan.Capability)).ToArray(), id);
            foreach (var plan in template.ProductionPlans)
            {
                Assert.IsTrue(processes.TryGetValue(plan.Process, out var process), id);
                Assert.IsNotNull(process, id);
                var plot = template.Plots.Single(plot => plot.Number == plan.PlotNumber);
                Assert.IsNotNull(plot.PrebuiltDesign, id);
                var evaluation = Found(view.FindDesign(plot.PrebuiltDesign.Value));
                Assert.IsInstanceOfType<C.DesignEvaluation.Complete>(evaluation, id);
                var building = ((C.DesignEvaluation.Complete)evaluation).Core!.Design;
                Assert.IsInstanceOfType<K.DesignKind.Building>(building.Kind, id);
                Assert.IsTrue(((K.DesignKind.Building)building.Kind).FacilityTags.Contains(process.RequiredFacilityTag), id);
                var capabilities = building.Parts.SelectMany(part => part.Capabilities);
                Assert.IsTrue(plan.Capability switch
                {
                    K.CapabilityKind.Production => capabilities.OfType<K.PartCapability.Production>().Any(),
                    K.CapabilityKind.Extraction => capabilities.OfType<K.PartCapability.Extraction>().Any(),
                    _ => false
                }, id);
                Assert.AreEqual(plan.Capability == K.CapabilityKind.Extraction,
                    process.Details is K.ProcessDetails.Extraction, id);
            }
        }
        var city = view.Core.SettlementTemplates.Single(item => item.Id.Value == "core:core-city");
        Assert.AreEqual(view.Core.SettlementTemplates.Single(item => item.Id.Value == "core:industrial-city").Type, city.Type);
        foreach (var (id, kind) in new[] { ("core-city-hub", K.BuildingKind.Warehouse), ("bastion-fortress", K.BuildingKind.Defense) })
        {
            var design = Complete(view, id);
            Assert.IsNotNull(design.Core);
            var building = (K.DesignKind.Building)design.Core.Design.Kind;
            Assert.AreEqual(K.PlotSize.Giant, building.PlotSize, id);
            Assert.AreEqual(kind, building.Type, id);
            var capabilities = design.Core.Design.Parts.SelectMany(part => part.Capabilities).ToArray();
            Assert.IsTrue(capabilities.OfType<K.PartCapability.Storage>().Any(storage => storage.Capacity > 0), id);
            Assert.IsTrue(capabilities.All(capability => capability is K.PartCapability.Storage), id);
            Assert.IsTrue(city.Plots.Any(plot => plot.Size == K.PlotSize.Giant && plot.PrebuiltDesign == design.Design), id);
        }
        foreach (var (id, plotSize, maximumPlot, maximumVehicle, component) in new[]
                 {
                     ("basic-shed", K.PlotSize.Small, K.PlotSize.Medium, (K.SizeClass?)null, "construction-equipment"),
                     ("vehicle-workshop", K.PlotSize.Medium, K.PlotSize.Large, (K.SizeClass?)K.SizeClass.Large, "workshop-crane"),
                     ("heavy-vehicle-factory", K.PlotSize.Large, K.PlotSize.Giant, (K.SizeClass?)K.SizeClass.Titan, "gantry-crane")
                 })
        {
            var building = Complete(view, id).Core!.Design;
            Assert.AreEqual(plotSize, ((K.DesignKind.Building)building.Kind).PlotSize, id);
            var part = building.Parts.Single(part => part.Capabilities.OfType<K.PartCapability.Construction>().Any());
            if (maximumVehicle == null)
                Assert.AreEqual(new K.CoreDefinitionReference.Voxel(new("core:" + component)), part.Definition, id);
            else
                Assert.AreEqual(new K.CoreDefinitionReference.Composite(new("core:" + component)), part.Definition, id);
            var construction = part.Capabilities.OfType<K.PartCapability.Construction>().Single();
            Assert.IsTrue(construction.WorkPerSecond > 0, id);
            Assert.AreEqual(maximumPlot, construction.MaximumBuildingPlotSize, id);
            Assert.AreEqual(maximumVehicle, construction.MaximumVehicleSize, id);
        }
        var worldResult = factory.CreateWorldInput(view, new("core:basic-through-world"), 1, "stage1-world-v1");
        Assert.IsInstanceOfType<C.ContentResult<K.WorldGenerationInput>.Success>(worldResult, worldResult.ToString());
        var world = ((C.ContentResult<K.WorldGenerationInput>.Success)worldResult).Value;
        var commission = view.Core.Designs.Single(design =>
            design.Design == world.Configuration.StartConstraints.RequiredAffordableCommissionDesign);
        var enabledProcesses = view.Core.SettlementTemplates.SelectMany(template => template.ProductionPlans)
            .Where(plan => plan.Enabled).Select(plan => plan.Process).ToHashSet();
        var suppliedGoods = view.Core.Processes.SelectMany(process => process.Details switch
        {
            K.ProcessDetails.Production production when enabledProcesses.Contains(new K.ProcessRef.Production(production.Id))
                => production.Outputs,
            K.ProcessDetails.Extraction extraction when enabledProcesses.Contains(new K.ProcessRef.Production(extraction.Id))
                => ImmutableArray.Create(extraction.Extracts),
            K.ProcessDetails.Recovery recovery when enabledProcesses.Contains(new K.ProcessRef.Recovery(recovery.Id))
                => recovery.Outputs,
            _ => ImmutableArray<K.GoodsQuantity>.Empty
        }).Select(item => item.Good).ToHashSet();
        var constructionRule = view.Core.ConstructionRules.Single(rule => rule.Id == world.Configuration.Rules.Construction);
        foreach (var template in view.Core.SettlementTemplates)
        {
            var hasVehicleWorkshop = template.Plots.Where(plot => plot.PrebuiltDesign != null)
                .Select(plot => view.Core.Designs.Single(design => design.Design == plot.PrebuiltDesign))
                .SelectMany(design => design.Parts).SelectMany(part => part.Capabilities)
                .OfType<K.PartCapability.Construction>()
                .Any(construction => construction.MaximumVehicleSize >= K.SizeClass.Small);
            var commissionMaterials = hasVehicleWorkshop ? commission.BillOfMaterials : [];
            foreach (var item in commissionMaterials)
                Assert.IsTrue(template.SpecialInitialInventory.Where(stock => stock.Good == item.Good)
                    .Sum(stock => stock.Quantity) >= item.Quantity, template.Id + ":" + item.Good);
            var founding = template.FoundingBuildings.Select(building =>
                view.Core.Designs.Single(design => design.Design == building.Design)).ToArray();
            Assert.IsTrue(founding.SelectMany(design => design.Parts).SelectMany(part => part.Capabilities)
                .OfType<K.PartCapability.Power>().Any() || constructionRule.PowerPerWork == 0, template.Id.ToString());
            var maximumPlot = founding.SelectMany(design => design.Parts).SelectMany(part => part.Capabilities)
                .OfType<K.PartCapability.Construction>().Max(capability => capability.MaximumBuildingPlotSize);
            foreach (var path in new string[][] { ["small-warehouse", "farm"],
                         ["small-warehouse", "power-station", "extraction-site"] })
            {
                var buildings = path.Select(id => Complete(view, id).Core!.Design).ToArray();
                foreach (var building in buildings)
                    Assert.IsTrue(maximumPlot >= ((K.DesignKind.Building)building.Kind).PlotSize,
                        template.Id + ":" + building.Design);
                var required = founding.Concat(buildings).SelectMany(design => design.BillOfMaterials)
                    .GroupBy(item => item.Good);
                foreach (var items in required)
                    Assert.IsTrue(suppliedGoods.Contains(items.Key) || template.SpecialInitialInventory.Where(stock => stock.Good == items.Key)
                        .Sum(stock => stock.Quantity) >= items.Sum(item => item.Quantity)
                        + commissionMaterials.Where(item => item.Good == items.Key).Sum(item => item.Quantity),
                        template.Id + ":" + items.Key);
            }
        }
        Assert.AreEqual(2, world.Start.Trains.Length);
        CollectionAssert.AreEqual(new[] { "small-locomotive", "small-freight-wagon", "small-freight-wagon", "small-freight-wagon" }
            .Select(id => Complete(view, id).Design).ToArray(), world.Start.Trains[0].Vehicles.ToArray());
        CollectionAssert.AreEqual(new[] { "small-locomotive", "small-freight-wagon", "small-freight-wagon", "small-armed-wagon" }
            .Select(id => Complete(view, id).Design).ToArray(), world.Start.Trains[1].Vehicles.ToArray());
    }

    [TestMethod]
    public void Builtin_titan_expands_to_about_twenty_five_thousand_voxels_with_large_equipment_parts()
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository != null && !File.Exists(Path.Combine(repository.FullName, "global.json"))) repository = repository.Parent;
        Assert.IsNotNull(repository);
        var builtIn = new C.ContentRoot(Path.Combine(repository.FullName, "builtin", "core"));
        var mods = new C.ContentRoot(Path.Combine(repository.FullName, "TestResults", "builtin-test-mods-" + Guid.NewGuid().ToString("N")));
        using var candidate = ContentCaptureTests.Success(ContentFactory.Create().PrepareLoad(
            new(new(builtIn, mods, null, "0.0.56", []), new C.ContentLoadPurpose.Initial()),
            new ContentDiskFiles(), new FixtureAssetInspector()));
        var titan = Complete(candidate.View, "titan-armed-vehicle");
        Assert.IsTrue(titan.Scene.Voxels.Length is >= 24_500 and <= 25_500, titan.Scene.Voxels.Length.ToString());
        var parts = titan.Core!.Design.Parts;
        Assert.IsTrue(parts.Length >= 60, parts.Length.ToString());
        Assert.AreEqual(2, parts.Count(part => part.Definition == new K.CoreDefinitionReference.Composite(new("core:large-diesel-engine"))));
        Assert.AreEqual(4, parts.Count(part => part.Definition == new K.CoreDefinitionReference.Composite(new("core:heavy-turret"))));
    }

    [TestMethod]
    public void Process_mod_load_and_reload_use_current_disk_bytes()
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository != null && !File.Exists(Path.Combine(repository.FullName, "global.json"))) repository = repository.Parent;
        Assert.IsNotNull(repository);
        var directory = Path.Combine(Path.GetTempPath(), "process-mod-" + Guid.NewGuid().ToString("N"));
        const string package = "11111111-1111-4111-8111-111111111111";
        try
        {
            var processDirectory = Path.Combine(directory, "farm", "resources", "processes");
            Directory.CreateDirectory(processDirectory);
            File.WriteAllText(Path.Combine(directory, "farm", "mod.json"), $$$"""
                {"schema_version":2,"id":"{{{package}}}","name":"农场流程","version":"1.0.0","authors":["测试"]}
                """);
            var process = ProcessLoadingTests.Process("production");
            process["simulation"]!.AsObject().Remove("inputs");
            process["simulation"]!["outputs"]![0]!["voxel"] = "core:staple-food";
            var file = Path.Combine(processDirectory, "test.process.json");
            File.WriteAllText(file, process.ToJsonString());
            var factory = ContentFactory.Create();
            var installation = new C.ContentInstallation(new(Path.Combine(repository.FullName, "builtin", "core")), new(directory), null, "0.0.56", [new(package)]);
            var files = new ContentDiskFiles();
            var inspector = new FixtureAssetInspector();
            using var initial = ContentCaptureTests.Success(factory.PrepareLoad(new(installation, new C.ContentLoadPurpose.Initial()), files, inspector));
            var original = initial.View.Core.Processes.Single(process => ProcessReference(process.Details) == new K.ProcessRef.Production(new(package + ":test")));
            Assert.AreEqual(new K.GoodsQuantity(new K.GoodId.Voxel(new("core:staple-food")), 2),
                ((K.ProcessDetails.Production)original.Details).Outputs.Single());
            process["simulation"]!["work_required"] = 31;
            File.WriteAllText(file, process.ToJsonString());
            var references = new K.CoreReferenceManifest([], [],
                [new(new K.CoreDefinitionReference.Process(new K.ProcessRef.Production(new(package + ":test"))), original.Revision, "facility.process", false)],
                [initial.View.Core.Identity]);
            using var reloaded = ContentCaptureTests.Success(factory.PrepareLoad(new(installation,
                new C.ContentLoadPurpose.Reload(initial.View, references)), files, inspector));
            var updated = reloaded.View.Core.Processes.Single(process => ProcessReference(process.Details) == new K.ProcessRef.Production(new(package + ":test")));
            Assert.AreEqual(31L, updated.WorkRequired);
            Assert.AreNotEqual(original.Revision, updated.Revision);
            Assert.AreEqual(17L, original.WorkRequired);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static K.ProcessRef ProcessReference(K.ProcessDetails details) => details switch
    {
        K.ProcessDetails.Production production => new K.ProcessRef.Production(production.Id),
        K.ProcessDetails.Extraction extraction => new K.ProcessRef.Production(extraction.Id),
        K.ProcessDetails.Recovery recovery => new K.ProcessRef.Recovery(recovery.Id),
        _ => throw new ArgumentOutOfRangeException(nameof(details))
    };

    private static C.DesignEvaluation.Complete Complete(C.IContentView view, string id)
    {
        var evaluation = Found(view.FindBlueprint(new(C.BlueprintLibrary.Packages, new("core:" + id)))).Evaluation;
        Assert.IsInstanceOfType<C.DesignEvaluation.Complete>(evaluation, evaluation.ToString());
        var complete = (C.DesignEvaluation.Complete)evaluation;
        Assert.IsTrue(complete.IsValid, id);
        Assert.AreEqual(0, complete.Diagnostics.Length);
        return complete;
    }

    // PNG 读取头尺寸，GLB 返回固定格内元数据以测试内容加载；不证明模型事实。
    // 真实 GLB 元数据由 CLI 检查器测试和 Engine 资产测试验证。
    private sealed class FixtureAssetInspector : C.IAssetInspector
    {
        public C.AssetInspection Inspect(C.AssetKind kind, C.AssetRevision revision, ImmutableArray<byte> bytes)
        {
            if (kind == C.AssetKind.Glb)
                return new C.AssetInspection.Success(new C.AssetMetadata.Glb(1, [C.AssetAlphaMode.Opaque, C.AssetAlphaMode.Opaque],
                    new(new(-0.25, 0, -0.25), new(0.25, 0.5, 0.25)), false, false, false, false, false, false));
            Assert.AreEqual(C.AssetKind.Png, kind);
            Assert.IsTrue(bytes.Length >= 24);
            Assert.IsTrue(bytes.AsSpan()[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }));
            return new C.AssetInspection.Success(new C.AssetMetadata.Png(
                BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan().Slice(16, 4)),
                BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan().Slice(20, 4))));
        }
    }
}
