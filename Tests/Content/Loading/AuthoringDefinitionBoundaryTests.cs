using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using C = Train.Content.Contracts;
using K = Train.Core.Contracts;

namespace Train.Content.Tests.Loading;

[TestClass]
public sealed class AuthoringDefinitionBoundaryTests
{
    [TestMethod]
    [DataRow(double.NaN)]
    [DataRow(double.PositiveInfinity)]
    [DataRow(double.NegativeInfinity)]
    public void Nonfinite_mass_returns_a_field_diagnostic(double mass)
    {
        var fixture = DesignFixtures.Content();
        using var content = fixture.Load();
        var factory = ContentFactory.Create();
        var source = AuthoringReferenceTests.Open(factory, fixture);
        var voxel = (C.DefinitionDraft.Voxel)WorldContentFixtures.Found(source.Read(new(C.DefinitionKind.Voxel, "block")));
        var changed = voxel with { Value = voxel.Value with { Simulation = voxel.Value.Simulation! with { MassTons = mass } } };
        AssertDiagnostic(factory.ValidateAuthoringDefinition(source, content.View,
            [new C.PackageEdit.WriteDefinition(changed)], new(C.DefinitionKind.Voxel, "block")), "invalid_field", "/simulation/mass");
    }

    [TestMethod]
    [DataRow("shared", false)]
    [DataRow("-leading", false)]
    [DataRow("has-dash", true)]
    [DataRow("7", true)]
    [DataRow("Upper", false)]
    [DataRow("64", true)]
    [DataRow("65", false)]
    public void Definition_ids_obey_the_shared_content_identity_rules(string example, bool valid)
    {
        var id = example is "64" or "65" ? new string('a', int.Parse(example)) : example;
        var fixture = DesignFixtures.Content();
        using var content = fixture.Load();
        var factory = ContentFactory.Create();
        var source = AuthoringReferenceTests.Open(factory, fixture);
        var draft = new C.DefinitionDraft.Blueprint(id, Creature());
        var result = factory.ValidateAuthoringDefinition(source, content.View,
            [new C.PackageEdit.WriteDefinition(draft)], new(C.DefinitionKind.Blueprint, id));
        if (valid) AuthoringReferenceTests.Success(result);
        else AssertDiagnostic(result, "invalid_field", "/id");
    }

    [TestMethod]
    public void Creature_and_recovery_choices_preserve_their_reference_kinds()
    {
        var fixture = DesignFixtures.Content();
        ProcessLoadingTests.Add(fixture, ProcessLoadingTests.Process("recovery", "recover"));
        using var content = fixture.Load();
        var factory = ContentFactory.Create();
        var source = AuthoringReferenceTests.Open(factory, fixture);
        var choices = AuthoringReferenceTests.Success(factory.QueryAuthoringReferences(source, content.View,
            [new C.PackageEdit.WriteDefinition(new C.DefinitionDraft.Blueprint("scout", Creature()))]));
        var creature = choices.Single(choice => choice.Kind == C.AuthoringReferenceKind.Creature);
        Assert.AreEqual(new C.ContentReference.Core(new K.CoreDefinitionReference.Creature(new("core:scout"))), creature.Reference);
        var recovery = choices.Single(choice => choice.Kind == C.AuthoringReferenceKind.RecoveryProcess);
        Assert.AreEqual(new C.ContentReference.Core(new K.CoreDefinitionReference.Process(new K.ProcessRef.Recovery(new("core:recover")))), recovery.Reference);
        Assert.IsFalse(choices.Any(choice => choice.Kind == C.AuthoringReferenceKind.ProductionProcess && choice.Reference == recovery.Reference));
    }

    [TestMethod]
    [DataRow("weight", "invalid_field", "/generation_weight")]
    [DataRow("members", "invalid_field", "/members")]
    [DataRow("interval", "invalid_field", "/spawn_interval_seconds")]
    [DataRow("scene-kind", "reference_wrong_kind", "/scene_model")]
    [DataRow("creature-kind", "reference_wrong_kind", "/members/0/creature")]
    [DataRow("creature-missing", "reference_missing", "/members/0/creature")]
    public void Lair_validation_reports_fields_and_reference_categories(string change, string code, string field)
    {
        var fixture = DesignFixtures.Content();
        WorldContentFixtures.AddBlueprint(fixture, WorldContentFixtures.Scene());
        using var content = fixture.Load();
        var factory = ContentFactory.Create();
        var source = AuthoringReferenceTests.Open(factory, fixture);
        var lair = new C.LairTypeDraft("den", new("巢穴", "测试"), new("core:scene"), 2,
            [new(new("core:scout"), 1)], 4, 30, 60, 1, 2, 15, 10);
        lair = change switch
        {
            "weight" => lair with { GenerationWeight = 0 },
            "members" => lair with { Members = [] },
            "interval" => lair with { SpawnIntervalSeconds = 0 },
            "scene-kind" => lair with { SceneModel = new("core:scout") },
            "creature-kind" => lair with { Members = [new(new("core:scene"), 1)] },
            "creature-missing" => lair with { Members = [new(new("core:absent"), 1)] },
            _ => throw new AssertFailedException()
        };
        AssertDiagnostic(factory.ValidateAuthoringDefinition(source, content.View,
            [new C.PackageEdit.WriteDefinition(new C.DefinitionDraft.Blueprint("scout", Creature())),
             new C.PackageEdit.WriteDefinition(new C.DefinitionDraft.LairType(lair))],
            new(C.DefinitionKind.LairType, "den")), code, field);
    }

    [TestMethod]
    public void Blueprint_weapon_parameters_are_validated_at_the_behavior_field()
    {
        var fixture = DesignFixtures.Content();
        using var content = fixture.Load();
        var factory = ContentFactory.Create();
        var source = AuthoringReferenceTests.Open(factory, fixture);
        var draft = Creature() with { Specification = new C.DesignSpecification.Creature(new(8, 1.5, 0.25, 12), new(2, 0)) };
        AssertDiagnostic(factory.ValidateAuthoringDefinition(source, content.View,
            [new C.PackageEdit.WriteDefinition(new C.DefinitionDraft.Blueprint("scout", draft))],
            new(C.DefinitionKind.Blueprint, "scout")), "invalid_field", "/simulation/behaviors/weapon/interval");
    }

    [TestMethod]
    public void Blueprint_missing_voxel_reports_the_palette_reference()
    {
        var fixture = DesignFixtures.Content();
        using var content = fixture.Load();
        var factory = ContentFactory.Create();
        var source = AuthoringReferenceTests.Open(factory, fixture);
        var draft = Creature() with { Layout = new([DesignFixtures.Entry("absent")], [new(new(0, 0, 0), 0)], []) };
        AssertDiagnostic(factory.ValidateAuthoringDefinition(source, content.View,
            [new C.PackageEdit.WriteDefinition(new C.DefinitionDraft.Blueprint("scout", draft))],
            new(C.DefinitionKind.Blueprint, "scout")), "reference_missing", "/layout/palette/0/voxel");
    }

    private static C.DesignDraft Creature() => new(new("侦察", "测试生物"),
        new C.DesignSpecification.Creature(new(8, 1.5, 0.25, 12), null),
        new([DesignFixtures.Entry("scenery")], [new(new(0, 0, 0), 0)], []));

    private static void AssertDiagnostic(C.ContentResult<ImmutableArray<C.ContentDiagnostic>> result, string code, string field)
    {
        Assert.IsInstanceOfType<C.ContentResult<ImmutableArray<C.ContentDiagnostic>>.Failure>(result, result.ToString());
        var diagnostics = ((C.ContentResult<ImmutableArray<C.ContentDiagnostic>>.Failure)result).Diagnostics;
        Assert.IsTrue(diagnostics.Any(diagnostic => diagnostic.Code == code && diagnostic.Field == field),
            string.Join("; ", diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Field}")));
    }
}
