using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.ContractChecks;

namespace Train.ContractChecks.Tests;

[TestClass]
public sealed class ProjectAnalysisTests
{
    [TestMethod]
    public void Cli_links_only_its_registered_host_source()
    {
        var root = Path.GetFullPath("workspace");
        var owner = Path.Combine(root, "Tools", "Cli");
        bool Allowed(string file) => ProjectAnalysis.OwnsCompileSource(root, "Train.Cli", owner, Path.Combine(root, "App", file));
        Assert.IsTrue(Allowed("Assets/DiskContentFiles.cs"));
        Assert.IsTrue(Allowed("Saves/SaveContainer.cs"));
        Assert.IsTrue(Allowed("Saves/FileSaveStore.cs"));
        Assert.IsFalse(Allowed("Saves/SaveCapture.cs"));
        Assert.IsFalse(Allowed("Saves/SaveService.cs"));
        Assert.IsFalse(Allowed("Assets/ContentFiles.cs"));
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
    public void Benchmark_linked_sources_are_limited_to_the_resource_fixtures()
    {
        var root = Path.GetFullPath("workspace");
        var owner = Path.Combine(root, "Tests", "Benchmarks");
        bool Allowed(string name, string file) => ProjectAnalysis.OwnsCompileSource(root, name, owner,
            Path.Combine(root, "Tests", "Core", "Simulation", file));
        Assert.IsTrue(Allowed("Train.Benchmarks", "SimulationExamples.cs"));
        Assert.IsTrue(Allowed("Train.Benchmarks", "ResourceCommandCodec.cs"));
        Assert.IsFalse(Allowed("Train.Benchmarks", "ResourceStateCodec.cs"));
        Assert.IsFalse(Allowed("Train.Benchmarks", "../World/Fixture.cs"));
        Assert.IsFalse(Allowed("Train.Content", "SimulationExamples.cs"));
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
