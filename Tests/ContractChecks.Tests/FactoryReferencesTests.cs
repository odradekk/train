using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Train.ContractChecks.Tests;

[TestClass]
[TestCategory("fast")]
public sealed class FactoryReferencesTests
{
    [TestMethod]
    [DataRow("using Factory = Train.Core.CoreFactory;", "Factory.Create()")]
    [DataRow("", "global::Train.Core.CoreFactory.Create()")]
    [DataRow("using static Train.Core.CoreFactory;", "Create")]
    public void Semantic_references_include_aliases_full_names_and_static_method_groups(string imports, string reference)
    {
        var failures = Check("Train", "App/Flow.cs", imports, reference);
        Assert.AreEqual(1, failures.Count);
        StringAssert.Contains(failures[0], "Train.Core.CoreFactory.Create");
    }

    [TestMethod]
    [DataRow("Train", "App/Composition/Nested/Factory.cs", 0)]
    [DataRow("Train", "App/Composition-other/Factory.cs", 1)]
    [DataRow("Train.Cli", "Tools/Cli/Composition.cs", 0)]
    [DataRow("Train.Cli", "Tools/Cli/Nested/Composition.cs", 1)]
    [DataRow("Train.SceneScale", "Tools/SceneScale/Composition.cs", 0)]
    [DataRow("Train.SceneScale", "Tools/SceneScale/Program.cs", 1)]
    [DataRow("Train.SceneScale", "Tools/SceneScale/Nested/Composition.cs", 1)]
    [DataRow("Train.SceneScale", "Tools/Cli/Composition.cs", 1)]
    [DataRow("Train.Cli", "Tools/SceneScale/Composition.cs", 1)]
    [DataRow("Train.AssetExport", "App/Composition/Factory.cs", 1)]
    [DataRow("Train.Core.Tests", "Tests/Core/Factory.cs", 0)]
    [DataRow("Train.Benchmarks", "Tests/Benchmarks/Factory.cs", 0)]
    public void Composition_permissions_depend_on_project_and_actual_source_path(string project, string path, int count) =>
        Assert.AreEqual(count, Check(project, path, "", "Train.Core.CoreFactory.Create()").Count);

    [TestMethod]
    public void A_matching_type_name_in_another_assembly_is_not_a_real_factory() =>
        Assert.AreEqual(0, Check("Train", "App/Flow.cs", "", "Train.Core.CoreFactory.Create()", "Unrelated").Count);

    private static List<string> Check(string project, string path, string imports, string reference, string assembly = "Train.Core")
    {
        var root = Path.GetFullPath("workspace");
        var declarations = CSharpSyntaxTree.ParseText("""
            namespace System { public class Object { } public struct Void { } }
            namespace Train.Core { public static class CoreFactory { public static void Create() { } } }
            """, path: Path.Combine(root, "Core", "Factory.cs"));
        var consumer = CSharpSyntaxTree.ParseText($"{imports} public class Consumer {{ public delegate void CreateFactory(); public void Run() {{ { (reference.EndsWith("()", StringComparison.Ordinal) ? reference + ";" : "CreateFactory factory = " + reference + ";") } }} }}",
            path: Path.Combine(root, path));
        var compilation = CSharpCompilation.Create(assembly, [declarations, consumer], options:
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        return FactoryReferences.CheckCompilation(root, project, compilation);
    }
}
