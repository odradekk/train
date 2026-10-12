using System.Diagnostics;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Train.ContractChecks.Tests;

[TestClass]
[TestCategory("integration")]
[DoNotParallelize]
public sealed class CliTests
{
    private static string root = null!;
    private static string tool = null!;
    private static string coreProject = null!;
    private static string originalCoreProject = null!;
    private static string contractSource = null!;
    private static (int ExitCode, string Output) isolationBaseline;
    private const string OriginalContract = "namespace Fixture; public sealed class Contract { public int Original() => 1; }";

    [ClassInitialize]
    public static void CreateFixture(TestContext context)
    {
        root = Path.Combine(Path.GetTempPath(), "cc-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        try
        {
            CreateFixtureFiles();
        }
        catch
        {
            DeleteFixture();
            throw;
        }
    }

    private static void CreateFixtureFiles()
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository != null && !File.Exists(Path.Combine(repository.FullName, "global.json"))) repository = repository.Parent;
        Assert.IsNotNull(repository);
        tool = Path.Combine(repository.FullName, "Tests", "ContractChecks", "bin",
            new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name, "net10.0", "Train.ContractChecks.dll");
        Write("global.json", File.ReadAllText(Path.Combine(repository.FullName, "global.json")));
        Write("Tests/Directory.Build.props", File.ReadAllText(Path.Combine(repository.FullName, "Tests", "Directory.Build.props")));
        Write(".gitignore", "bin/\nobj/\noutputs/\n");
        var projects = new Dictionary<string, object>();
        var fixturePaths = new List<string>();
        AddProject("Train.Core.Contracts", "Contracts/Core", [], OriginalContract);
        AddProject("Train.Core", "Core", ["Train.Core.Contracts"], "namespace Fixture; public sealed class Core { public int Value => new Contract().Original(); }");
        AddProject("Train.Content", "Content", [], "namespace Fixture; public sealed class Content { public int Value => 1; }");
        AddProject("Train.Hidden", "Hidden", [], "namespace Fixture; public sealed class Hidden { public static int Value => 1; }");
        AddProject("Train.Core.Tests", "Tests/Core", ["Train.Core"], "using Microsoft.VisualStudio.TestTools.UnitTesting; [TestClass] public class CoreTests { [TestMethod] public void Value() => Assert.AreEqual(1, new Fixture.Core().Value); }", true);
        AddProject("Train.Content.Tests", "Tests/Content", ["Train.Content"], "using Microsoft.VisualStudio.TestTools.UnitTesting; [TestClass] public class ContentTests { [TestMethod] public void Value() => Assert.AreEqual(1, new Fixture.Content().Value); }", true);
        Write("scripts/checks/project-boundaries.json", JsonSerializer.Serialize(new { projects }));
        RequireSuccess(Run("git", "init", "--quiet"));
        Write("Fixture.slnx", "<Solution>" + string.Concat(fixturePaths.Select(path => $"<Project Path=\"{path}\" />")) + "</Solution>");
        Build("Fixture.slnx");
        coreProject = Path.Combine(root, "Core", "Train.Core.csproj");
        originalCoreProject = File.ReadAllText(coreProject);
        contractSource = Path.Combine(root, "Contracts", "Core", "Fixture.cs");
        RequireSuccess(Check("snapshot", "baseline.txt"));
        isolationBaseline = Check("isolate", "outputs/isolate-valid");
        RequireSuccess(isolationBaseline);

        void AddProject(string name, string directory, string[] references, string source, bool test = false)
        {
            var path = directory + "/" + name + ".csproj";
            fixturePaths.Add(path);
            projects.Add(name, new { path, references });
            var includes = references.Select(reference => $"<ProjectReference Include=\"{Path.GetRelativePath(Path.Combine(root, directory), Path.Combine(root, ProjectDirectory(reference), reference + ".csproj"))}\" />");
            Write(path, $"<Project Sdk=\"{(test ? "MSTest.Sdk" : "Microsoft.NET.Sdk")}\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup><ItemGroup>{string.Concat(includes)}</ItemGroup></Project>");
            Write(directory + "/Fixture.cs", source);
        }
    }

    [ClassCleanup]
    public static void DeleteFixture()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    [TestCleanup]
    public void RestoreFixture()
    {
        if (!Directory.Exists(root) || coreProject == null || contractSource == null || originalCoreProject == null) return;
        File.WriteAllText(coreProject, originalCoreProject);
        File.WriteAllText(contractSource, OriginalContract);
        Write("Core/Fixture.cs", "namespace Fixture; public sealed class Core { public int Value => new Contract().Original(); }");
    }

    [TestMethod]
    public void Structure_rejects_a_forbidden_project_reference()
    {
        RequireSuccess(Check("structure", "outputs/structure-valid"));
        AddCoreReference("<ProjectReference Include=\"../Content/Train.Content.csproj\" />");
        var result = Check("structure", "outputs/structure-invalid");
        Assert.AreEqual(1, result.ExitCode, result.Output);
        StringAssert.Contains(result.Output, "forbidden project reference:");
        StringAssert.Contains(result.Output, "Train.Content");
    }

    [TestMethod]
    public void Verify_reports_both_sides_of_a_public_member_replacement()
    {
        Build("Contracts/Core/Train.Core.Contracts.csproj");
        RequireSuccess(Check("verify", "baseline.txt"));
        File.WriteAllText(contractSource, OriginalContract.Replace("Original", "Replacement", StringComparison.Ordinal));
        Build("Contracts/Core/Train.Core.Contracts.csproj");
        var result = Check("verify", "baseline.txt");
        Assert.AreEqual(1, result.ExitCode, result.Output);
        Assert.IsTrue(result.Output.Split('\n').Any(line => line.Contains("added public API:") && line.Contains("Replacement")), result.Output);
        Assert.IsTrue(result.Output.Split('\n').Any(line => line.Contains("removed public API:") && line.Contains("Original")), result.Output);
    }

    [TestMethod]
    public void Isolation_cannot_use_a_prebuilt_dependency_outside_the_allowed_closure()
    {
        RequireSuccess(isolationBaseline);
        Assert.IsTrue(File.Exists(Path.Combine(root, "Hidden", "bin", "Debug", "net10.0", "Train.Hidden.dll")));
        AddCoreReference("<ProjectReference Include=\"../Hidden/Train.Hidden.csproj\" />");
        Write("Core/Fixture.cs", "namespace Fixture; public sealed class Core { public int Value => Hidden.Value; }");
        var result = Check("isolate", "outputs/isolate-missing-invalid");
        Assert.AreEqual(1, result.ExitCode, result.Output);
        StringAssert.Contains(result.Output, "Train.Hidden");
        Assert.IsTrue(result.Output.Contains("forbidden project reference:") ||
            new[] { "MSB9008", "MSB3202", "CS0103", "CS0246" }.Any(result.Output.Contains), result.Output);
    }

    [TestMethod]
    public void Isolation_rejects_a_hint_path_resolved_in_the_workspace()
    {
        RequireSuccess(isolationBaseline);
        var dll = Path.Combine(root, "Hidden", "bin", "Debug", "net10.0", "Train.Hidden.dll");
        Assert.IsTrue(File.Exists(dll));
        AddCoreReference($"<Reference Include=\"Train.Hidden\"><HintPath>{System.Security.SecurityElement.Escape(dll)}</HintPath></Reference>");
        Write("Core/Fixture.cs", "namespace Fixture; public sealed class Core { public int Value => Hidden.Value; }");
        var result = Check("isolate", "outputs/isolate-hint-invalid");
        Assert.AreEqual(1, result.ExitCode, result.Output);
        StringAssert.Contains(result.Output, "assembly resolved outside isolation:");
        StringAssert.Contains(result.Output, "Train.Hidden.dll");
    }

    private static void AddCoreReference(string reference) => File.WriteAllText(coreProject, originalCoreProject.Replace("</ItemGroup>", reference + "</ItemGroup>", StringComparison.Ordinal));
    private static (int ExitCode, string Output) Check(string command, string output) => Run("dotnet", tool, command, root, "Debug", Path.Combine(root, output));
    private static void RequireSuccess((int ExitCode, string Output) result) => Assert.AreEqual(0, result.ExitCode, result.Output);
    private static void Build(string project) => RequireSuccess(Run("dotnet", "build", project, "--nologo", "-v:q", "-nodeReuse:false", "-property:UseSharedCompilation=false"));
    private static string ProjectDirectory(string name) => name switch
    {
        "Train.Core.Contracts" => "Contracts/Core",
        "Train.Core" => "Core",
        "Train.Content" => "Content",
        _ => throw new InvalidOperationException(name)
    };
    private static void Write(string relativePath, string content)
    {
        var path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
    private static (int ExitCode, string Output) Run(string executable, params string[] arguments)
    {
        var start = new ProcessStartInfo(executable) { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        start.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        start.Environment["UseSharedCompilation"] = "false";
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(90_000))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            Assert.Fail($"Timed out: {executable} {string.Join(' ', arguments)}");
        }
        return (process.ExitCode, stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
    }
}
