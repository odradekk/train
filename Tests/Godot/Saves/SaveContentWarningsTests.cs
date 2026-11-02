using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.App.Saves;
using Train.Content.Contracts;
using Train.Core.Contracts;

namespace Train.Godot.Tests.Saves;

[TestClass]
public sealed class SaveContentWarningsTests
{
    private const string Package = "b63cc410-ccdd-4efe-991a-42e6172fced9";
    private static readonly CoreDefinitionReference Voxel = new CoreDefinitionReference.Voxel(new(Package + ":wheel"));

    [TestMethod]
    public void No_runtime_missing_content_requires_no_confirmation()
    {
        Assert.IsEmpty(SaveContentWarnings.Describe(new([], [], [], []), []));
        Assert.IsEmpty(SaveContentWarnings.Describe(new([], [], [Missing(Voxel)], []), []));
    }

    [TestMethod]
    public void Missing_packages_use_saved_names_once_and_exclude_unaffected_packages()
    {
        var other = new MissingContentReference(new ContentReference.Core(new CoreDefinitionReference.Rule(new("other:rule"))),
            new("other"), "Rules.Population", []);
        var warnings = SaveContentWarnings.Describe(new([Status("当前包名")], [Missing(Voxel), Missing(Voxel), other], [], []),
            [new(Voxel, "World.Inventory")], [Metadata("存档包名"), new(new("other"), "另一缺失包", "1", [], []),
                new(new("unaffected"), "未缺失的包", "1", [], [])]);
        var text = string.Join("\n", warnings);
        StringAssert.Contains(text, "存档包名");
        Assert.AreEqual(1, text.Split("存档包名", StringSplitOptions.None).Length - 1);
        StringAssert.Contains(text, "另一缺失包");
        Assert.IsFalse(text.Contains("当前包名", StringComparison.Ordinal));
        Assert.IsFalse(text.Contains("未缺失的包", StringComparison.Ordinal));
        AssertPlayerText(text);
    }

    [TestMethod]
    public void Missing_package_names_fall_back_to_the_preparation_report()
    {
        var text = Describe(new([Status("山间铁路") with { State = PackageState.Available }], [Missing(Voxel)], [], []), []);
        StringAssert.Contains(text.Split('\n')[0], "山间铁路");
        Assert.IsFalse(text.Split('\n')[0].Contains("名称未知", StringComparison.Ordinal));
        AssertPlayerText(text);
    }

    [TestMethod]
    public void Core_only_missing_content_uses_its_saved_package_name()
    {
        var text = string.Join("\n", SaveContentWarnings.Describe(new([], [], [], []),
            [new(Voxel, "World.Inventory")], [Metadata("山间铁路")]));
        StringAssert.Contains(text, "山间铁路");
        AssertPlayerText(text);
    }

    [TestMethod]
    public void Unknown_packages_and_diagnostics_do_not_expose_internal_identifiers()
    {
        var missing = Missing(Voxel) with
        {
            Diagnostics = [new("reference_missing", ContentDiagnosticSeverity.Error, "restore", null,
                null, "World.Inventory", null, "internal diagnostic")]
        };
        var text = Describe(new([], [missing], [], []), []);
        StringAssert.Contains(text, "名称未知");
        AssertPlayerText(text);
        Assert.IsFalse(text.Contains("internal diagnostic", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Missing_parts_explain_only_the_affected_parts_and_goods(bool composite)
    {
        CoreDefinitionReference definition = composite ? new CoreDefinitionReference.Composite(new(Package + ":motor")) : Voxel;
        var warnings = SaveContentWarnings.Describe(new([], [Missing(definition)], [], []), []);
        var text = string.Join("\n", warnings);
        StringAssert.Contains(warnings[1], composite ? "功能复合体" : "体素");
        StringAssert.Contains(text, "质量");
        StringAssert.Contains(text, "结构");
        StringAssert.Contains(text, "伤害");
        StringAssert.Contains(text, "修理");
        StringAssert.Contains(text, "交易");
        Assert.IsFalse(text.Contains("所有车辆", StringComparison.Ordinal));
        Assert.IsFalse(text.Contains("所有建筑", StringComparison.Ordinal));
        AssertPlayerText(text);
    }

    [TestMethod]
    [DynamicData(nameof(OtherReferences))]
    public void Other_missing_kinds_have_chinese_labels_without_unrelated_part_restrictions(ContentReference reference)
    {
        var text = Describe(new([], [new(reference, null, "World.Inventory", [])], [], []), []);
        StringAssert.Contains(text, "失效");
        Assert.IsFalse(text.Contains("其他", StringComparison.Ordinal));
        Assert.IsFalse(text.Contains("修理", StringComparison.Ordinal));
        Assert.IsFalse(text.Contains("交易", StringComparison.Ordinal));
        AssertPlayerText(text);
    }

    [TestMethod]
    public void Continuing_preserves_state_and_explains_how_to_restore_content()
    {
        var text = Describe(new([], [], [], []), [new(Voxel, "World.Inventory")]);
        StringAssert.Contains(text, "占位");
        StringAssert.Contains(text, "重新读档");
    }

    public static IEnumerable<object[]> OtherReferences()
    {
        yield return [new ContentReference.Core(new CoreDefinitionReference.Process(new ProcessRef.Production(new(Package + ":process"))))];
        yield return [new ContentReference.Core(new CoreDefinitionReference.Process(new ProcessRef.Recovery(new(Package + ":recovery"))))];
        yield return [new ContentReference.Core(new CoreDefinitionReference.Rule(new(Package + ":rule")))];
        yield return [new ContentReference.Core(new CoreDefinitionReference.SettlementType(new(Package + ":town")))];
        yield return [new ContentReference.Core(new CoreDefinitionReference.Template(new(Package + ":template")))];
        yield return [new ContentReference.Core(new CoreDefinitionReference.Terrain(new(Package + ":terrain")))];
        yield return [new ContentReference.Core(new CoreDefinitionReference.Creature(new(Package + ":creature")))];
        yield return [new ContentReference.Core(new CoreDefinitionReference.Lair(new(Package + ":lair")))];
        yield return [new ContentReference.Blueprint(new(BlueprintLibrary.Packages, new(Package + ":blueprint")))];
        yield return [new ContentReference.Design(new("opaque-design-hash"))];
        yield return [new ContentReference.Draft(new(Guid.Parse(Package)))];
        yield return [new ContentReference.Generation(new(Package + ":generation"))];
    }

    private static MissingContentReference Missing(CoreDefinitionReference reference) => new(new ContentReference.Core(reference), new(Package), "World.Inventory", []);
    private static PackageMetadata Metadata(string name) => new(new(Package), name, "1", [], []);
    private static PackageStatus Status(string name) => new(Metadata(name), new("fixture"), PackageState.Disabled, []);
    private static string Describe(ContentPreparationReport report, ImmutableArray<MissingCoreReference> core) => string.Join("\n", SaveContentWarnings.Describe(report, core));

    private static void AssertPlayerText(string text)
    {
        foreach (var line in text.Split('\n'))
            Assert.IsTrue(line[(line.IndexOf('：') + 1)..].Any(character => character is >= '\u4e00' and <= '\u9fff'), line);
        foreach (var identifier in new[] { Package, "World.Inventory", "opaque-design-hash", "Value =", "CoreDefinitionReference", "reference_missing" })
            Assert.IsFalse(text.Contains(identifier, StringComparison.Ordinal), text);
    }
}
