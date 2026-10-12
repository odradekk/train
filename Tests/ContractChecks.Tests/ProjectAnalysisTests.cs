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
}
