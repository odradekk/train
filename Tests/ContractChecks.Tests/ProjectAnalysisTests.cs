using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.ContractChecks;

namespace Train.ContractChecks.Tests;

[TestClass]
public sealed class ProjectAnalysisTests
{
    [TestMethod]
    [DataRow("Tools/Cli/CliContentFiles.cs", true)]
    [DataRow("Tools/Cli/AssetInspector.cs", true)]
    [DataRow("Tools/Cli/GlbAssetInspector.cs", true)]
    [DataRow("App/Assets/DiskContentFiles.cs", true)]
    [DataRow("App/Assets/GlbInput.cs", true)]
    [DataRow("App/Saves/SaveContainer.cs", true)]
    [DataRow("App/Saves/FileSaveStore.cs", true)]
    [DataRow("Tools/Cli/Composition.cs", false)]
    [DataRow("Tools/Cli/Nested/CliContentFiles.cs", false)]
    [DataRow("Tools/Cli/../Foreign.cs", false)]
    [DataRow("App/Assets/GodotAssetInspector.cs", false)]
    [DataRow("App/Saves/SaveService.cs", false)]
    [DataRow("App/Measurement/WorldCounts.cs", false)]
    public void Scene_scale_links_only_its_registered_shared_source(string file, bool allowed)
    {
        var root = Path.GetFullPath("workspace");
        var owner = Path.Combine(root, "Tools", "SceneScale");
        Assert.AreEqual(allowed, ProjectAnalysis.OwnsCompileSource(root, "Train.SceneScale", owner, Path.Combine(root, file)));
    }

    [TestMethod]
    public void Other_projects_cannot_link_scene_scale_shared_source()
    {
        var root = Path.GetFullPath("workspace");
        Assert.IsFalse(ProjectAnalysis.OwnsCompileSource(root, "Train.Content", Path.Combine(root, "Content"),
            Path.Combine(root, "Tools", "Cli", "CliContentFiles.cs")));
    }

    [TestMethod]
    public void Cli_links_only_its_registered_host_source()
    {
        var root = Path.GetFullPath("workspace");
        var owner = Path.Combine(root, "Tools", "Cli");
        bool Allowed(string file) => ProjectAnalysis.OwnsCompileSource(root, "Train.Cli", owner, Path.Combine(root, "App", file));
        Assert.IsTrue(Allowed("Assets/DiskContentFiles.cs"));
        Assert.IsTrue(Allowed("Assets/GlbInput.cs"));
        Assert.IsTrue(Allowed("Saves/SaveContainer.cs"));
        Assert.IsTrue(Allowed("Saves/FileSaveStore.cs"));
        Assert.IsTrue(Allowed("RuntimeEnvironment.cs"));
        Assert.IsTrue(Allowed("Measurement/ProcessMemory.cs"));
        Assert.IsFalse(Allowed("Measurement/MeasurementRun.cs"));
        Assert.IsFalse(Allowed("Saves/SaveCapture.cs"));
        Assert.IsFalse(Allowed("Saves/SaveService.cs"));
        Assert.IsFalse(Allowed("Assets/ContentFiles.cs"));
        Assert.IsFalse(Allowed("Assets/GodotAssetInspector.cs"));
        Assert.IsFalse(Allowed("Assets/../HostCoordinator.cs"));
    }

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
    public void Benchmark_linked_sources_are_limited_to_the_world_and_train_fixtures()
    {
        var root = Path.GetFullPath("workspace");
        var owner = Path.Combine(root, "Tests", "Benchmarks");
        bool Allowed(string name, string file) => ProjectAnalysis.OwnsCompileSource(root, name, owner,
            Path.Combine(root, "Tests", "Core", file));
        Assert.IsTrue(Allowed("Train.Benchmarks", "World/WorldFixture.cs"));
        Assert.IsTrue(Allowed("Train.Benchmarks", "Railway/TrainRecoveryFixture.cs"));
        Assert.IsFalse(Allowed("Train.Benchmarks", "World/WorldStateFixture.cs"));
        Assert.IsFalse(Allowed("Train.Benchmarks", "World/OtherFixture.cs"));
        Assert.IsFalse(Allowed("Train.Content", "World/WorldFixture.cs"));
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
