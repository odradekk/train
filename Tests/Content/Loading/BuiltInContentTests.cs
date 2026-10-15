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
            new DiskFiles(), new PngHeaderInspector());
        Assert.IsInstanceOfType<C.ContentResult<C.IContentCandidate>.Success>(result, result.ToString());
        using var candidate = ((C.ContentResult<C.IContentCandidate>.Success)result).Value;
        Assert.AreEqual(0, candidate.Report.Diagnostics.Length);
        var view = candidate.View;
        var vehicles = new List<K.DesignHash>();
        foreach (var (id, size) in new[] { ("small-locomotive", K.SizeClass.Small),
                     ("medium-locomotive", K.SizeClass.Medium), ("extra-large-locomotive", K.SizeClass.ExtraLarge) })
        {
            var design = Complete(view, id);
            Assert.IsNotNull(design.Core);
            Assert.IsInstanceOfType<K.DesignKind.Vehicle>(design.Core.Design.Kind);
            Assert.AreEqual(size, ((K.DesignKind.Vehicle)design.Core.Design.Kind).Size);
            var capabilities = design.Core.Design.Parts.SelectMany(part => part.Capabilities).ToArray();
            Assert.IsTrue(capabilities.OfType<K.PartCapability.Control>().Any(), id);
            Assert.IsTrue(capabilities.OfType<K.PartCapability.Propulsion>().Any(), id);
            Assert.IsTrue(capabilities.OfType<K.PartCapability.Storage>().Any(), id);
            vehicles.Add(design.Design);
        }
        var station = Complete(view, "small-station");
        Assert.IsNotNull(station.Core);
        Assert.IsTrue(station.Core.Design.Parts.Any(part => part.Definition is K.CoreDefinitionReference.Voxel &&
            part.Capabilities.OfType<K.PartCapability.Platform>().Any()));
        Assert.AreEqual(2, station.Core.Design.Parts.Count(part => part.Capabilities.OfType<K.PartCapability.Platform>().Any()));
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
            Assert.IsTrue(template.SpecialInitialInventory.IsEmpty, id);
            Assert.IsTrue(template.ProductionPlans.IsEmpty, id);
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
        foreach (var vehicle in vehicles) Assert.IsTrue(world.Start.Trains.Any(train => train.Vehicles.Contains(vehicle)));
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
            var files = new DiskFiles();
            var inspector = new PngHeaderInspector();
            using var initial = ContentCaptureTests.Success(factory.PrepareLoad(new(installation, new C.ContentLoadPurpose.Initial()), files, inspector));
            var original = initial.View.Core.Processes.Single();
            Assert.AreEqual(new K.GoodsQuantity(new K.GoodId.Voxel(new("core:staple-food")), 2),
                ((K.ProcessDetails.Production)original.Details).Outputs.Single());
            process["simulation"]!["work_required"] = 31;
            File.WriteAllText(file, process.ToJsonString());
            var references = new K.CoreReferenceManifest([], [],
                [new(new K.CoreDefinitionReference.Process(new K.ProcessRef.Production(new(package + ":test"))), original.Revision, "facility.process", false)],
                [initial.View.Core.Identity]);
            using var reloaded = ContentCaptureTests.Success(factory.PrepareLoad(new(installation,
                new C.ContentLoadPurpose.Reload(initial.View, references)), files, inspector));
            Assert.AreEqual(31L, reloaded.View.Core.Processes.Single().WorkRequired);
            Assert.AreNotEqual(original.Revision, reloaded.View.Core.Processes.Single().Revision);
            Assert.AreEqual(17L, original.WorkRequired);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static C.DesignEvaluation.Complete Complete(C.IContentView view, string id)
    {
        var evaluation = Found(view.FindBlueprint(new(C.BlueprintLibrary.Packages, new("core:" + id)))).Evaluation;
        Assert.IsInstanceOfType<C.DesignEvaluation.Complete>(evaluation, evaluation.ToString());
        var complete = (C.DesignEvaluation.Complete)evaluation;
        Assert.IsTrue(complete.IsValid, id);
        Assert.AreEqual(0, complete.Diagnostics.Length);
        return complete;
    }

    private sealed class DiskFiles : C.IContentFiles
    {
        public C.ContentIoResult<ImmutableArray<C.ContentFileEntry>> Enumerate(C.ContentPath directory)
        {
            var path = Path.Combine(directory.Root.Key, directory.RelativePath);
            if (!Directory.Exists(path))
                return new C.ContentIoResult<ImmutableArray<C.ContentFileEntry>>.Failure(C.ContentIoFailure.Missing, path);
            return new C.ContentIoResult<ImmutableArray<C.ContentFileEntry>>.Success(Directory.EnumerateFileSystemEntries(path)
                .Select(entry => new C.ContentFileEntry(Path.GetFileName(entry),
                    (File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0 ? C.ContentEntryKind.Link :
                    Directory.Exists(entry) ? C.ContentEntryKind.Directory : C.ContentEntryKind.File)).ToImmutableArray());
        }

        public C.ContentIoResult<ImmutableArray<byte>> Read(C.ContentPath file, long maximumBytes)
        {
            var path = Path.Combine(file.Root.Key, file.RelativePath);
            if (!File.Exists(path))
                return new C.ContentIoResult<ImmutableArray<byte>>.Failure(C.ContentIoFailure.Missing, path);
            if (new FileInfo(path).Length > maximumBytes)
                return new C.ContentIoResult<ImmutableArray<byte>>.Failure(C.ContentIoFailure.LimitExceeded, path);
            return new C.ContentIoResult<ImmutableArray<byte>>.Success(File.ReadAllBytes(path).ToImmutableArray());
        }
    }

    // 只提供 PNG 头中的尺寸事实；本测试不证明 Godot 能解码这些资产。
    private sealed class PngHeaderInspector : C.IAssetInspector
    {
        public C.AssetInspection Inspect(C.AssetKind kind, C.AssetRevision revision, ImmutableArray<byte> bytes)
        {
            Assert.AreEqual(C.AssetKind.Png, kind);
            Assert.IsTrue(bytes.Length >= 24);
            Assert.IsTrue(bytes.AsSpan()[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }));
            return new C.AssetInspection.Success(new C.AssetMetadata.Png(
                BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan().Slice(16, 4)),
                BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan().Slice(20, 4))));
        }
    }
}
