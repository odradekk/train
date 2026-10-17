using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Train.Content.Tests.Loading.WorldContentFixtures;
using C = Train.Content.Contracts;

namespace Train.Content.Tests.Loading;

[TestClass]
public sealed class AuthoringReloadTests
{
    [TestMethod]
    [DataRow("scene")]
    [DataRow("wagon")]
    public void Edited_package_blueprint_reloads_definition_and_scene_without_mutating_old_view(string id)
    {
        var fixture = WorldContentFixtures.Content();
        using var initial = fixture.Load();
        var package = Open(fixture);
        var draft = (C.DefinitionDraft.Blueprint)Found(package.Read(new(C.DefinitionKind.Blueprint, id)));
        var before = Blueprint(initial.View, id);
        var cell = draft.Value.Layout.Cells.Single();
        var edited = draft with
        {
            Value = draft.Value with
            {
                Display = new("重载设计", "已编辑布局"),
                Layout = draft.Value.Layout with { Cells = [cell with { At = new(1, 0, 0) }] }
            }
        };
        Apply(fixture, package, new C.PackageEdit.WriteDefinition(edited));
        using var reloaded = Reload(fixture, initial.View);
        var after = Blueprint(reloaded.View, id);
        Assert.AreEqual(edited.Value.Display, after.Definition.Display);
        Assert.AreEqual(edited.Value.Specification, after.Definition.Specification);
        CollectionAssert.AreEqual(edited.Value.Layout.Cells.ToArray(), after.Definition.Layout.Cells.ToArray());
        CollectionAssert.AreEqual(edited.Value.Layout.Palette.ToArray(), after.Definition.Layout.Palette.ToArray());
        Assert.AreNotEqual(before.Design, after.Design);
        Assert.AreEqual(new C.GridPosition(1, 0, 0), after.Scene.Voxels.Single().At);
        Assert.AreEqual(draft.Value.Display, Blueprint(initial.View, id).Definition.Display);
        Assert.AreEqual(new C.GridPosition(0, 0, 0), Blueprint(initial.View, id).Definition.Layout.Cells.Single().At);
        Assert.AreEqual(new C.GridPosition(0, 0, 0), Blueprint(initial.View, id).Scene.Voxels.Single().At);
        Assert.AreEqual(before.Design, Blueprint(initial.View, id).Design);
    }

    [TestMethod]
    public void Edited_settlement_type_and_existing_template_reload_into_projections_without_mutating_old_view()
    {
        var fixture = WorldContentFixtures.Content();
        using var initial = fixture.Load();
        var package = Open(fixture);
        var type = (C.DefinitionDraft.SettlementType)Found(package.Read(new(C.DefinitionKind.SettlementType, "farm")));
        var template = (C.DefinitionDraft.SettlementTemplate)Found(package.Read(new(C.DefinitionKind.SettlementTemplate, "farm-start")));
        var editedType = type with { Value = type.Value with { MinimumPopulation = 120, MaximumPopulation = 240 } };
        var editedTemplate = template with
        {
            Value = template.Value with
            {
                Weight = 9,
                Plots = template.Value.Plots.SetItem(1, template.Value.Plots[1] with { At = new(240, 0, 0) })
            }
        };
        Apply(fixture, package, new C.PackageEdit.WriteDefinition(editedType), new C.PackageEdit.WriteDefinition(editedTemplate));
        using var reloaded = Reload(fixture, initial.View);
        var actualType = reloaded.View.Core.SettlementTypes.Single(t => t.Id.Value == "core:farm");
        Assert.AreEqual(120L, actualType.MinimumInitialPopulation);
        Assert.AreEqual(240L, actualType.MaximumInitialPopulation);
        Assert.AreEqual(9, reloaded.View.Core.SettlementTemplates.Single().Weight);
        var factory = ContentFactory.Create();
        var oldLayout = Found(factory.WithSettlementCopies(initial.View, [Reference(41)]).FindSettlementLayout(new(41)));
        var newLayout = Found(factory.WithSettlementCopies(reloaded.View, [Reference(41)]).FindSettlementLayout(new(41)));
        Assert.AreEqual(new C.GridPosition(240, 0, 0), newLayout.Plots.Single(p => p.Number == 11).At);
        Assert.AreEqual(new C.GridPosition(160, 0, 0), oldLayout.Plots.Single(p => p.Number == 11).At);
        var oldType = initial.View.Core.SettlementTypes.Single(t => t.Id.Value == "core:farm");
        Assert.AreEqual(100L, oldType.MinimumInitialPopulation);
        Assert.AreEqual(200L, oldType.MaximumInitialPopulation);
        Assert.AreEqual(2, initial.View.Core.SettlementTemplates.Single().Weight);
    }

    private static C.DesignEvaluation.Complete Blueprint(C.IContentView view, string id) =>
        (C.DesignEvaluation.Complete)Found(view.FindBlueprint(new(C.BlueprintLibrary.Packages, new("core:" + id)))).Evaluation;

    private static C.IAuthoringPackage Open(ContentFixture fixture) => Success(ContentFactory.Create().OpenAuthoringPackage(
        ContentFixture.BuiltIn, new("core"), new("snapshot"), fixture));

    private static void Apply(ContentFixture fixture, C.IAuthoringPackage package, params C.PackageEdit[] edits)
    {
        var patch = Success(ContentFactory.Create().PreparePackageWrite(package, [.. edits]));
        Assert.IsFalse(patch.Diagnostics.Any(d => d.Severity == C.ContentDiagnosticSeverity.Error));
        Assert.IsTrue(patch.Changes.Length > 0);
        foreach (var change in patch.Changes)
            fixture.AddFile(ContentFixture.BuiltIn, change.RelativePath, change.Bytes!.Value.ToArray());
    }

    private static C.IContentCandidate Reload(ContentFixture fixture, C.IContentView basis) =>
        Success(ContentFactory.Create().PrepareLoad(new(fixture.Installation,
            new C.ContentLoadPurpose.Reload(basis, new([], [], [], [basis.Core.Identity]))), fixture, fixture));

    private static T Success<T>(C.ContentResult<T> result) where T : notnull
    {
        Assert.IsInstanceOfType<C.ContentResult<T>.Success>(result, result.ToString());
        return ((C.ContentResult<T>.Success)result).Value;
    }
}
