using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using C = Train.Content.Contracts;

namespace Train.Content.Tests.Loading;

[TestClass]
public sealed class AuthoringValidationTests
{
    [TestMethod]
    public void Template_plan_checks_capabilities_from_the_staged_voxel_instead_of_published_design()
    {
        var fixture = WorldContentFixtures.InitializedTemplate();
        using var content = fixture.Load();
        var factory = ContentFactory.Create();
        var source = AuthoringReferenceTests.Open(factory, fixture);
        var machine = (C.DefinitionDraft.Voxel)WorldContentFixtures.Found(source.Read(new(C.DefinitionKind.Voxel, "machine")));
        var changed = machine with { Value = machine.Value with { Simulation = machine.Value.Simulation! with
        {
            Behaviors = machine.Value.Simulation.Behaviors.Where(behavior => behavior is not Train.Core.Contracts.PartCapability.Production).ToImmutableArray()
        } } };
        var result = factory.ValidateAuthoringDefinition(source, content.View,
            [new C.PackageEdit.WriteDefinition(changed)], new(C.DefinitionKind.SettlementTemplate, "farm-start"));
        Assert.IsInstanceOfType<C.ContentResult<System.Collections.Immutable.ImmutableArray<C.ContentDiagnostic>>.Failure>(result);
        Assert.IsTrue(((C.ContentResult<System.Collections.Immutable.ImmutableArray<C.ContentDiagnostic>>.Failure)result).Diagnostics.Any(diagnostic =>
            diagnostic.Field == "/production_plans/0/capability" && diagnostic.Code == "invalid_field"));
    }

    [TestMethod]
    public void Template_uses_current_building_edits_and_reports_the_incompatible_plot()
    {
        var fixture = WorldContentFixtures.InitializedTemplate();
        using var content = fixture.Load();
        var factory = ContentFactory.Create();
        var source = AuthoringReferenceTests.Open(factory, fixture);
        var address = new C.AuthoringDefinitionAddress(C.DefinitionKind.SettlementTemplate, "farm-start");
        var template = WorldContentFixtures.Found(source.Read(address));
        Assert.IsInstanceOfType<C.ContentResult<System.Collections.Immutable.ImmutableArray<C.ContentDiagnostic>>.Success>(
            factory.ValidateAuthoringDefinition(source, content.View, [new C.PackageEdit.WriteDefinition(template)], address));
        var building = (C.DefinitionDraft.Blueprint)WorldContentFixtures.Found(source.Read(new(C.DefinitionKind.Blueprint, "workshop")));
        var changed = building with { Value = building.Value with { Specification = ((C.DesignSpecification.Building)building.Value.Specification) with { PlotSize = Train.Core.Contracts.PlotSize.Giant } } };
        var result = factory.ValidateAuthoringDefinition(source, content.View, [new C.PackageEdit.WriteDefinition(template), new C.PackageEdit.WriteDefinition(changed)], address);
        Assert.IsInstanceOfType<C.ContentResult<System.Collections.Immutable.ImmutableArray<C.ContentDiagnostic>>.Failure>(result);
        Assert.IsTrue(((C.ContentResult<System.Collections.Immutable.ImmutableArray<C.ContentDiagnostic>>.Failure)result).Diagnostics.Any(diagnostic => diagnostic.Field == "/plots" && diagnostic.Message.Contains("尺寸")));
    }

    [TestMethod]
    public void Removing_a_referenced_building_is_reported_without_reloading_the_package()
    {
        var fixture = WorldContentFixtures.Content();
        using var content = fixture.Load();
        var factory = ContentFactory.Create();
        var source = AuthoringReferenceTests.Open(factory, fixture);
        var result = factory.ValidateAuthoringDefinition(source, content.View,
            [new C.PackageEdit.DeleteDefinition(new(C.DefinitionKind.Blueprint, "station"))], new(C.DefinitionKind.SettlementTemplate, "farm-start"));
        Assert.IsInstanceOfType<C.ContentResult<System.Collections.Immutable.ImmutableArray<C.ContentDiagnostic>>.Failure>(result);
        Assert.IsTrue(((C.ContentResult<System.Collections.Immutable.ImmutableArray<C.ContentDiagnostic>>.Failure)result).Diagnostics.Any(diagnostic => diagnostic.Code == "reference_missing" && diagnostic.Field == "/plots"));
    }

    [TestMethod]
    public void Invalid_mass_is_reported_at_its_field_without_changing_the_source_or_view()
    {
        var fixture = DesignFixtures.Content();
        using var content = fixture.Load();
        var factory = ContentFactory.Create();
        var source = AuthoringReferenceTests.Open(factory, fixture);
        var original = (C.DefinitionDraft.Voxel)WorldContentFixtures.Found(source.Read(new(C.DefinitionKind.Voxel, "block")));
        var invalid = original with { Value = original.Value with { Simulation = original.Value.Simulation! with { MassTons = -1 } } };
        var result = factory.ValidateAuthoringDefinition(source, content.View, [new C.PackageEdit.WriteDefinition(invalid)], new(C.DefinitionKind.Voxel, "block"));
        Assert.IsInstanceOfType<C.ContentResult<System.Collections.Immutable.ImmutableArray<C.ContentDiagnostic>>.Failure>(result);
        var diagnostic = ((C.ContentResult<System.Collections.Immutable.ImmutableArray<C.ContentDiagnostic>>.Failure)result).Diagnostics.Single();
        Assert.AreEqual("/simulation/mass", diagnostic.Field);
        Assert.AreEqual("invalid_field", diagnostic.Code);
        Assert.AreEqual(original.Value.Simulation, ((C.DefinitionDraft.Voxel)WorldContentFixtures.Found(source.Read(new(C.DefinitionKind.Voxel, "block")))).Value.Simulation);
        Assert.AreEqual(3d, content.View.Core.Goods.Single(good => good.Good == new Train.Core.Contracts.GoodId.Voxel(new("core:block"))).MassTons);
    }
}
