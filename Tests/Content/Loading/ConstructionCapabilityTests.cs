using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Train.Content.Tests.Loading.ContentCaptureTests;
using static Train.Content.Tests.Loading.ContentRestoreTests;
using C = Train.Content.Contracts;
using K = Train.Core.Contracts;

namespace Train.Content.Tests.Loading;

[TestClass]
public sealed class ConstructionCapabilityTests
{
    [TestMethod]
    [DataRow("medium", null, K.PlotSize.Medium, null)]
    [DataRow("large", "large", K.PlotSize.Large, K.SizeClass.Large)]
    [DataRow("giant", "titan", K.PlotSize.Giant, K.SizeClass.Titan)]
    public void Construction_limits_are_projected(string plot, string? vehicle, K.PlotSize expectedPlot, K.SizeClass? expectedVehicle)
    {
        using var content = Content(plot, vehicle).Load();
        var result = DesignFixtures.Evaluate(content.View, DesignFixtures.Vehicle());
        Assert.AreEqual(new K.PartCapability.Construction(4, expectedPlot, expectedVehicle),
            result.Core!.Design.Parts.Single().Capabilities.OfType<K.PartCapability.Construction>().Single());
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("small_station")]
    [DataRow("large_station")]
    public void Construction_requires_an_ordinary_building_plot_limit(string? plot)
    {
        var result = Content(plot, null).Prepare();
        Assert.IsInstanceOfType<C.ContentResult<C.IContentCandidate>.Failure>(result);
        Assert.IsTrue(((C.ContentResult<C.IContentCandidate>.Failure)result).Diagnostics.Any(d =>
            d.Code == "invalid_field" && d.Field == "/simulation/behaviors/construction/max_plot_size"));
    }

    [TestMethod]
    [DataRow(K.PlotSize.Small, "small", false)]
    [DataRow(K.PlotSize.Small, "medium", false)]
    [DataRow(K.PlotSize.Small, "large", false)]
    [DataRow(K.PlotSize.Small, "extra_large", false)]
    [DataRow(K.PlotSize.Small, "titan", false)]
    [DataRow(K.PlotSize.Medium, "large", true)]
    [DataRow(K.PlotSize.Medium, "extra_large", false)]
    [DataRow(K.PlotSize.Large, "titan", true)]
    [DataRow(K.PlotSize.SmallStation, "titan", true)]
    public void Building_vehicle_limit_must_fit_declared_plot_in_either_horizontal_direction(K.PlotSize plot, string vehicle, bool valid)
    {
        using var content = Content("large", vehicle).Load();
        var draft = DesignFixtures.Vehicle() with
        {
            Specification = new C.DesignSpecification.Building(plot,
                plot == K.PlotSize.SmallStation ? K.BuildingKind.Station : K.BuildingKind.Production, [])
        };
        var result = DesignFixtures.Evaluate(content.View, draft);
        Assert.AreEqual(valid, result.IsValid);
        Assert.AreEqual(valid, result.Core!.Design.CanConstruct);
        if (!valid) Assert.IsTrue(result.Diagnostics.Any(d => d.Code == "design_invalid" && d.Field == "/simulation/plot_size"));
    }

    [TestMethod]
    public void Nested_composite_construction_limit_also_restricts_building_plot()
    {
        var fixture = NestedDesignTests.CompositeContent();
        WorldContentFixtures.Change(fixture, "resources/voxels/machine.voxel.json", json =>
            json["simulation"]!["behaviors"]!["construction"]!["max_size_class"] = "small");
        WorldContentFixtures.Change(fixture, "resources/blueprints/engine.blueprint.json", json =>
            json["simulation"]!["behaviors"] = JsonNode.Parse("""{"construction":{"work_rate":4,"max_plot_size":"large","max_size_class":"extra_large"}}"""));
        using var content = fixture.Load();
        var child = NestedDesignTests.Assembly() with
        {
            Layout = new([], [], [new C.NestedDesignItem.FunctionalCompositeReference(new(0, 0, 0), C.VoxelOrientation.PxPy, new("core:engine"), null)])
        };
        var draft = NestedDesignTests.Host(new C.NestedDesignItem.AssemblyCopy(new(0, 0, 1), C.VoxelOrientation.PxPy, child)) with
        {
            Specification = new C.DesignSpecification.Building(K.PlotSize.Medium, K.BuildingKind.Production, []),
            Layout = new([DesignFixtures.Entry("machine")], [new(new(0, 0, 0), 0)],
                [new C.NestedDesignItem.AssemblyCopy(new(0, 0, 1), C.VoxelOrientation.PxPy, child)])
        };
        var result = DesignFixtures.Evaluate(content.View, draft);
        Assert.IsFalse(result.IsValid);
        Assert.IsFalse(result.Core!.Design.CanConstruct);
        Assert.IsTrue(result.Core.Design.Parts.Any(p => p.Definition is K.CoreDefinitionReference.Composite));
        Assert.IsTrue(result.Diagnostics.Any(d => d.Code == "design_invalid" && d.Field == "/simulation/plot_size"));
    }

    [TestMethod]
    [DataRow("max_plot_size", "giant")]
    [DataRow("max_size_class", "titan")]
    public void Changed_construction_limit_changes_semantic_input_and_survives_restore_and_replay(string field, string value)
    {
        var fixture = Content("medium", "large");
        WorldContentFixtures.AddBlueprint(fixture, DesignFixtures.Blueprint("vehicle"));
        WorldContentFixtures.Change(fixture, "resources/blueprints/vehicle.blueprint.json", json =>
            json["layout"]!["palette"]![0]!["voxel"] = "machine");
        using var initial = fixture.Load();
        var factory = ContentFactory.Create();
        var references = References(initial.View, "vehicle");
        var saved = Success(factory.Capture(initial.View, references));
        WorldContentFixtures.Change(fixture, "resources/voxels/machine.voxel.json", json =>
            json["simulation"]!["behaviors"]!["construction"]![field] = value);
        using var changed = fixture.Load();
        Assert.AreNotEqual(initial.View.Core.Identity, changed.View.Core.Identity);
        Assert.AreNotEqual(initial.View.Core.Goods.Single(g => g.Good == new K.GoodId.Voxel(new("core:machine"))).Revision,
            changed.View.Core.Goods.Single(g => g.Good == new K.GoodId.Voxel(new("core:machine"))).Revision);
        using var restored = Restore(fixture, Success(factory.DecodeCapture(saved.Encode())), references);
        var expected = DesignFixtures.Evaluate(changed.View, DesignFixtures.Vehicle()).Core!.Design.Parts.Single().Capabilities;
        CollectionAssert.AreEqual(expected.ToArray(), DesignFixtures.Evaluate(restored.View, DesignFixtures.Vehicle()).Core!.Design.Parts.Single().Capabilities.ToArray());
        var replayReferences = references with { Inputs = [initial.View.Core.Identity, changed.View.Core.Identity] };
        var replay = Success(factory.CaptureReplay([initial.View, changed.View], replayReferences));
        var decoded = Success(factory.DecodeReplay(replay.Encode()));
        for (var index = 0; index < 2; index++)
        {
            var original = index == 0 ? initial.View : changed.View;
            Assert.AreEqual(original.Core.Identity, decoded.History[index].Core.Identity);
            CollectionAssert.AreEqual(DesignFixtures.Evaluate(original, DesignFixtures.Vehicle()).Core!.Design.Parts.Single().Capabilities.ToArray(),
                DesignFixtures.Evaluate(decoded.History[index], DesignFixtures.Vehicle()).Core!.Design.Parts.Single().Capabilities.ToArray());
        }
    }

    [TestMethod]
    public void Initial_load_rejects_a_building_whose_vehicle_limit_does_not_fit()
    {
        var fixture = Content("medium", "small");
        var blueprint = DesignFixtures.Blueprint("workshop");
        blueprint["category"] = "building";
        blueprint["simulation"] = JsonNode.Parse("""{"plot_size":"small","building_type":"production","facility_tags":[]}""");
        blueprint["layout"]!["palette"]![0]!["voxel"] = "machine";
        WorldContentFixtures.AddBlueprint(fixture, blueprint);
        var result = fixture.Prepare();
        Assert.IsInstanceOfType<C.ContentResult<C.IContentCandidate>.Failure>(result);
        Assert.IsTrue(((C.ContentResult<C.IContentCandidate>.Failure)result).Diagnostics.Any(d =>
            d.Code == "design_invalid" && d.Field == "/simulation/plot_size"));
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow(K.SizeClass.Titan)]
    public void Functional_composite_limits_survive_design_capture_and_strict_replay(K.SizeClass? vehicle)
    {
        var fixture = NestedDesignTests.CompositeContent();
        WorldContentFixtures.Change(fixture, "resources/blueprints/engine.blueprint.json", json =>
        {
            json["simulation"]!["behaviors"] = JsonNode.Parse("""{"construction":{"work_rate":4,"max_plot_size":"giant","max_size_class":"titan"}}""");
            if (vehicle is null) json["simulation"]!["behaviors"]!["construction"]!.AsObject().Remove("max_size_class");
        });
        using var content = fixture.Load();
        var factory = ContentFactory.Create();
        var references = References(content.View, "engine");
        var capture = Success(factory.Capture(content.View, references));
        var decoded = Success(factory.DecodeCapture(capture.Encode()));
        Assert.AreEqual(capture.Encode(), decoded.Encode());
        using var restored = Restore(fixture, decoded, references);
        var replay = Success(factory.CaptureReplay([content.View], references));
        var history = Success(factory.DecodeReplay(replay.Encode())).History.Single();
        foreach (var view in new[] { restored.View, history })
        {
            var blueprint = ((C.ContentLookup<C.BlueprintRecord>.Found)view.FindBlueprint(new(C.BlueprintLibrary.Packages, new("core:engine")))).Value;
            var specification = (C.DesignSpecification.FunctionalComposite)((C.DesignEvaluation.Complete)blueprint.Evaluation).Definition.Specification;
            Assert.AreEqual(new K.PartCapability.Construction(4, K.PlotSize.Giant, vehicle), specification.Behaviors.Single());
        }
        Assert.AreEqual(content.View.Core.Identity, history.Core.Identity);
    }

    private static ContentFixture Content(string? plot, string? vehicle)
    {
        var fixture = DesignFixtures.Content();
        WorldContentFixtures.Change(fixture, "resources/voxels/machine.voxel.json", json =>
        {
            var capability = json["simulation"]!["behaviors"]!["construction"]!.AsObject();
            if (plot is null) capability.Remove("max_plot_size");
            else capability["max_plot_size"] = plot;
            if (vehicle is not null) capability["max_size_class"] = vehicle;
        });
        return fixture;
    }
}
