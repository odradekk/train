using Microsoft.VisualStudio.TestTools.UnitTesting;
using C = Train.Content.Contracts;
using K = Train.Core.Contracts;

namespace Train.Content.Tests.Loading;

[TestClass]
public sealed class AuthoringPackageDiagnosticTests
{
    [TestMethod]
    [DataRow(C.DefinitionKind.Voxel, "resources/voxels/block.voxel.json", "/appearance/presentation/shapes", "invalid_field")]
    [DataRow(C.DefinitionKind.Blueprint, "resources/blueprints/broken.blueprint.json", "/layout/composites", "design_invalid")]
    [DataRow(C.DefinitionKind.SettlementType, "resources/settlement-types/broken.settlement-type.json", "/simulation/preferred_terrain", "invalid_field")]
    [DataRow(C.DefinitionKind.SettlementTemplate, "resources/settlement-templates/broken.settlement-template.json", "", "invalid_field")]
    [DataRow(C.DefinitionKind.LairType, "resources/lair-types/broken.lair-type.json", "/generation_weight", "invalid_field")]
    public void Failed_definition_after_valid_edit_reports_its_own_source_and_identity_without_a_patch(
        C.DefinitionKind kind, string path, string field, string code)
    {
        var fixture = DesignFixtures.Content();
        var factory = ContentFactory.Create();
        var opened = factory.OpenAuthoringPackage(ContentFixture.BuiltIn, new("core"), new("snapshot"), fixture);
        Assert.IsInstanceOfType<C.ContentResult<C.IAuthoringPackage>.Success>(opened);
        var package = ((C.ContentResult<C.IAuthoringPackage>.Success)opened).Value;
        var voxel = (C.DefinitionDraft.Voxel)((C.ContentLookup<C.DefinitionDraft>.Found)package.Read(new(C.DefinitionKind.Voxel, "block"))).Value;
        var valid = (C.DefinitionDraft.Voxel)((C.ContentLookup<C.DefinitionDraft>.Found)package.Read(new(C.DefinitionKind.Voxel, "machine"))).Value;
        var vehicle = DesignFixtures.Vehicle();
        C.DefinitionDraft invalid = kind switch
        {
            C.DefinitionKind.Voxel => voxel with { Value = voxel.Value with
                { Presentation = ((C.AuthoringVoxelPresentation.Cube)voxel.Value.Presentation!) with { Shapes = default } } },
            C.DefinitionKind.Blueprint => new C.DefinitionDraft.Blueprint("broken", vehicle with
                { Specification = new C.DesignSpecification.Creature(null, null), Layout = vehicle.Layout with
                    { NestedItems = [new C.NestedDesignItem.AssemblyCopy(new(0, 0, 0), C.VoxelOrientation.PxPy, vehicle)] } }),
            C.DefinitionKind.SettlementType => new C.DefinitionDraft.SettlementType(new("broken", new("", ""), null, false, null, default, null, null)),
            C.DefinitionKind.SettlementTemplate => new C.DefinitionDraft.SettlementTemplate(new("broken", new("", ""), null, null, null, default, [], [], [])),
            C.DefinitionKind.LairType => new C.DefinitionDraft.LairType(new("broken", new("巢穴", ""), null, null, [], null, null, null, null, null, null, null)),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        C.ContentReference subject = kind switch
        {
            C.DefinitionKind.Voxel => new C.ContentReference.Core(new K.CoreDefinitionReference.Voxel(new("core:block"))),
            C.DefinitionKind.Blueprint => new C.ContentReference.Blueprint(new(C.BlueprintLibrary.Packages, new("core:broken"))),
            C.DefinitionKind.SettlementType => new C.ContentReference.Core(new K.CoreDefinitionReference.SettlementType(new("core:broken"))),
            C.DefinitionKind.SettlementTemplate => new C.ContentReference.Core(new K.CoreDefinitionReference.Template(new("core:broken"))),
            C.DefinitionKind.LairType => new C.ContentReference.Core(new K.CoreDefinitionReference.Lair(new("core:broken"))),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

        var result = factory.PreparePackageWrite(package,
            [new C.PackageEdit.WriteDefinition(valid with { Value = valid.Value with { Display = new("有效修改", "") } }),
                new C.PackageEdit.WriteDefinition(invalid)]);

        Assert.IsInstanceOfType<C.ContentResult<C.PackageWritePlan>.Failure>(result);
        var diagnostic = ((C.ContentResult<C.PackageWritePlan>.Failure)result).Diagnostics.Single();
        Assert.AreEqual(code, diagnostic.Code);
        Assert.AreEqual(field, diagnostic.Field);
        Assert.AreEqual(new C.ContentSourceLocation(new("core"), path), diagnostic.Source);
        Assert.AreEqual(subject, diagnostic.Subject);
    }
}
