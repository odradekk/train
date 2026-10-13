using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.ContractChecks;

namespace Train.ContractChecks.Tests;

[TestClass]
public sealed class ProjectAnalysisTests
{
    [TestMethod]
    public void Child_path_requires_a_directory_boundary()
    {
        var parent = Path.GetFullPath("isolated");
        Assert.IsTrue(ProjectAnalysis.IsChildPath(Path.Combine(parent, "Core", "Train.Core.dll"), parent));
        Assert.IsFalse(ProjectAnalysis.IsChildPath(parent + "-other" + Path.DirectorySeparatorChar + "Train.Core.dll", parent));
    }

    [TestMethod]
    public void Asset_export_linked_sources_are_limited_to_assets_and_the_glb_fixture()
    {
        var root = Path.GetFullPath("workspace");
        var owner = Path.Combine(root, "Tests", "AssetExport");
        bool Allowed(string name, string path) => ProjectAnalysis.OwnsCompileSource(root, name, owner, Path.Combine(root, path));
        Assert.IsTrue(Allowed("Train.AssetExport", "App/Assets/Nested/Asset.cs"));
        Assert.IsTrue(Allowed("Train.AssetExport", "Tests/Engine/Assets/GlbFixture.cs"));
        Assert.IsFalse(Allowed("Train.AssetExport", "App/Assets-other/Asset.cs"));
        Assert.IsFalse(Allowed("Train.AssetExport", "App/Assets/../Foreign.cs"));
        Assert.IsFalse(Allowed("Train.AssetExport", "Tests/Engine/Assets/OtherFixture.cs"));
        Assert.IsFalse(Allowed("Train.Core", "App/Assets/Asset.cs"));
        Assert.IsFalse(Allowed("Train.Core", "Tests/Engine/Assets/GlbFixture.cs"));
    }

    [TestMethod]
    [DataRow("texture.png", "[remap]\nimporter=\"keep\"\n", null)]
    [DataRow("model.glb", null, "Keep File import missing:")]
    [DataRow("texture.png", "[remap]\nimporter=\"texture\"\n", "Keep File importer required:")]
    [DataRow("texture.png", "[remap]\n[params]\nimporter=\"keep\"\n", "Keep File importer required:")]
    [DataRow("texture.png", "[remap]\nimporter=\"keep\"\nimporter=\"keep\"\n", "Keep File importer required:")]
    [DataRow("texture.PNG", "[remap]\nimporter=\"texture\"\n", "Keep File importer required:")]
    public void Keep_file_rule_checks_builtin_import_metadata(string filename, string? import, string? expectedFailure)
    {
        var root = Path.Combine(Path.GetTempPath(), "keep-rule-" + Guid.NewGuid().ToString("N"));
        var builtin = Directory.CreateDirectory(Path.Combine(root, "builtin")).FullName;
        try
        {
            var asset = Path.Combine(builtin, filename);
            File.WriteAllText(asset, "fixture");
            if (import is not null) File.WriteAllText(asset + ".import", import);
            var failures = ProjectAnalysis.BuiltInImportFailures(root).ToArray();
            if (expectedFailure is null) Assert.AreEqual(0, failures.Length);
            else
            {
                Assert.AreEqual(1, failures.Length);
                StringAssert.Contains(failures[0], expectedFailure);
                StringAssert.Contains(failures[0], asset);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
