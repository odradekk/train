using Microsoft.VisualStudio.TestTools.UnitTesting;
using C = Train.Content.Contracts;
using K = Train.Core.Contracts;

namespace Train.Content.Tests.Loading;

[TestClass]
public sealed class DesignEvaluationIdentityTests
{
    [TestMethod]
    [DataRow("core:engine")]
    [DataRow("12345678-1234-1234-1234-123456789abc:engine")]
    public void Functional_root_uses_the_supplied_definition_as_its_good_identity(string definition)
    {
        using var candidate = DesignFixtures.Content().Load();
        var identity = new K.CompositeDefinitionId(definition);
        var evaluation = Evaluate(candidate.View, FunctionalDraft(), identity);

        Assert.IsTrue(evaluation.IsValid);
        Assert.AreEqual(new K.DesignKind.LargeEquipment(new K.GoodId.Composite(identity)), evaluation.Core!.Design.Kind);
    }

    [TestMethod]
    public void Layout_changes_preserve_the_functional_roots_good_identity()
    {
        using var candidate = DesignFixtures.Content().Load();
        var identity = new K.CompositeDefinitionId("core:engine");
        var draft = FunctionalDraft();
        var before = Evaluate(candidate.View, draft, identity);
        var after = Evaluate(candidate.View, draft with
        {
            Layout = draft.Layout with { Cells = [new(new(0, 0, 0), 0), new(new(0, 0, 1), 0)] }
        }, identity);

        Assert.IsTrue(after.IsValid);
        Assert.AreNotEqual(before.Design, after.Design);
        Assert.AreNotEqual(before.Composition, after.Composition);
        Assert.AreEqual(new K.DesignKind.LargeEquipment(new K.GoodId.Composite(identity)), before.Core!.Design.Kind);
        Assert.AreEqual(before.Core.Design.Kind, after.Core!.Design.Kind);
    }

    [TestMethod]
    [DataRow("vehicle")]
    [DataRow("building")]
    [DataRow("assembly")]
    [DataRow("scene_model")]
    [DataRow("creature")]
    public void Other_root_kinds_ignore_the_composite_identity(string kind)
    {
        using var candidate = DesignFixtures.Content().Load();
        C.DesignSpecification specification = kind switch
        {
            "vehicle" => new C.DesignSpecification.Vehicle(K.SizeClass.Small),
            "building" => new C.DesignSpecification.Building(K.PlotSize.Small, K.BuildingKind.Production, []),
            "assembly" => new C.DesignSpecification.Assembly(null),
            "scene_model" => new C.DesignSpecification.SceneModel(null),
            "creature" => new C.DesignSpecification.Creature(new(10, 1, 0.5, 10), null),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        var draft = FunctionalDraft() with
        {
            Specification = specification,
            Layout = new([DesignFixtures.Entry(kind is "scene_model" or "creature" ? "scenery" : "block")],
                [new(new(0, 0, 0), 0)], [])
        };
        var before = Evaluate(candidate.View, draft);
        var after = Evaluate(candidate.View, draft, new("core:unrelated"));

        Assert.IsTrue(before.IsValid);
        Assert.IsTrue(after.IsValid);
        Assert.AreEqual(before.Design, after.Design);
        Assert.AreEqual(before.Composition, after.Composition);
        Assert.AreEqual(before.Core?.Design.Kind, after.Core?.Design.Kind);
    }

    private static C.DesignDraft FunctionalDraft() => new(new("设备", "测试设备"),
        new C.DesignSpecification.FunctionalComposite([new K.PartCapability.Power(5)], null),
        new([DesignFixtures.Entry()], [new(new(0, 0, 0), 0)], []));

    private static C.DesignEvaluation.Complete Evaluate(C.IContentView content, C.DesignDraft draft,
        K.CompositeDefinitionId? identity = null)
    {
        var result = ContentFactory.Create().EvaluateDesign(content, draft, identity);
        Assert.IsInstanceOfType<C.ContentResult<C.DesignEvaluation>.Success>(result, result.ToString());
        var value = ((C.ContentResult<C.DesignEvaluation>.Success)result).Value;
        Assert.IsInstanceOfType<C.DesignEvaluation.Complete>(value, value.ToString());
        return (C.DesignEvaluation.Complete)value;
    }
}
