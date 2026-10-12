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
}
