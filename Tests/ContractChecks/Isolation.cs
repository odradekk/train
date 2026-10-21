using System.Text.Json;
using System.Diagnostics;
using System.Xml.Linq;

namespace Train.ContractChecks;

internal static class Isolation
{
    private const int SessionTimeoutSeconds = 420;

    internal static async Task<int> Run(string root, string configuration, string output,
        IReadOnlyDictionary<string, ProjectRule> projects, IReadOnlyList<string> tests)
    {
        if (tests is not { Count: > 0 } || tests.Any(test => !projects.ContainsKey(test)))
            throw new InvalidOperationException("isolationTests must name at least one registered test project.");
        var failures = new List<string>();
        var watch = Stopwatch.StartNew();
        foreach (var test in tests)
        {
            if (failures.Count != 0) break;
            var evidence = Path.Combine(output, test);
            // Windows 子进程的工作目录仍受路径长度限制，隔离树不嵌套在模块日志目录中。
            var isolated = Path.Combine(output, "isolated-" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                Directory.CreateDirectory(evidence);
                Directory.CreateDirectory(isolated);
                var isolatedProjects = new Dictionary<string, ProjectRule>(projects, StringComparer.Ordinal);
                if (test == "Train.Godot.Tests")
                    isolatedProjects["Train"] = projects["Train"] with
                    {
                        References = ["Train.Core.Contracts", "Train.Content.Contracts", "Train.Scenes.Contracts",
                            "Train.Core.ContractSupport", "Train.Content.ContractSupport", "Train.Scenes.ContractSupport"]
                    };
                var closure = ProjectAnalysis.Closure(isolatedProjects, test);
                var inputs = new HashSet<string>(StringComparer.Ordinal);
                var replacements = new Dictionary<string, string>();
                foreach (var name in closure)
                {
                    var directory = Path.GetDirectoryName(Path.GetFullPath(Path.Combine(root, isolatedProjects[name].Path)))!;
                    if (name == "Train")
                    {
                        CopyFile(Path.Combine(root, "Train.csproj"));
                        CopyFile(Path.Combine(root, "project.godot"));
                        foreach (var production in new[] { "App", "Rendering", "Ui", "Tests/Engine/Bootstrap" })
                            CopyDirectory(Path.Combine(root, production));
                        CopyConfiguration(root);
                        continue;
                    }
                    if (!ProjectAnalysis.IsChildPath(directory, root))
                        throw new InvalidOperationException("isolation input outside repository: " + directory);
                    if (Path.GetRelativePath(directory, output) == "." || ProjectAnalysis.IsChildPath(output, directory))
                        throw new InvalidOperationException("isolation output overlaps input: " + directory);
                    CopyDirectory(directory);
                    for (var parent = Directory.GetParent(directory); parent != null &&
                         (parent.FullName == root || ProjectAnalysis.IsChildPath(parent.FullName, root)); parent = parent.Parent)
                        CopyConfiguration(parent.FullName);
                }
                CopyConfiguration(root);
                if (test == "Train.Content.Tests")
                {
                    CopyDirectory(Path.Combine(root, "builtin", "core"));
                    CopyDirectory(Path.Combine(root, "docs", "content", "examples", "example-pack"));
                    CopyDirectory(Path.Combine(root, "docs", "content", "examples", "example-addon"));
                }
                if (test == "Train.Godot.Tests")
                {
                    var projectPath = Path.Combine(isolated, "Train.csproj");
                    var projectXml = XDocument.Load(projectPath);
                    var projectElement = projectXml.Root!;
                    var xmlNamespace = projectElement.Name.Namespace;
                    var implementations = new[] { "Train.Core", "Train.Content", "Train.Scenes" };
                    foreach (var reference in projectElement.Descendants(xmlNamespace + "ProjectReference").ToArray())
                        if (implementations.Contains(Path.GetFileNameWithoutExtension((string?)reference.Attribute("Include"))))
                            reference.Remove();
                    projectElement.Add(new XElement(xmlNamespace + "ItemGroup",
                        new[] { "Core", "Content", "Scenes" }.Select(module => new XElement(xmlNamespace + "ProjectReference",
                            new XAttribute("Include", $"Tests/ContractSupport/{module}/Train.{module}.ContractSupport.csproj")))));
                    // ContractSupport 传递的 MSTest 全局 using 与 Debug Bootstrap 的 ITestMethod 重名。
                    projectElement.Add(new XElement(xmlNamespace + "Target", new XAttribute("Name", "RemoveIsolationTestUsing"),
                        new XAttribute("BeforeTargets", "GenerateGlobalUsings"),
                        new XElement(xmlNamespace + "ItemGroup", new XElement(xmlNamespace + "Using",
                            new XAttribute("Remove", "Microsoft.VisualStudio.TestTools.UnitTesting")))));
                    projectXml.Save(projectPath);
                    var replacement = "Tests/Godot/Isolation/HostComposition.cs";
                    Directory.CreateDirectory(Path.Combine(isolated, "App", "Composition"));
                    File.Copy(Path.Combine(isolated, replacement), Path.Combine(isolated, "App/Composition/HostComposition.cs"));
                    replacements.Add("App/Composition/HostComposition.cs", replacement);
                }
                // 查找在隔离根终止，不能继承外层工作区或用户目录中的构建配置。
                foreach (var file in new[] { "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props" })
                    if (!File.Exists(Path.Combine(isolated, file))) File.WriteAllText(Path.Combine(isolated, file), "<Project />");
                var config = Path.Combine(isolated, "NuGet.Config");
                if (!File.Exists(config))
                    File.WriteAllText(config, "<configuration><packageSources><clear /><add key=\"nuget.org\" value=\"https://api.nuget.org/v3/index.json\" /></packageSources></configuration>");
                await File.WriteAllTextAsync(Path.Combine(evidence, "inputs.json"), JsonSerializer.Serialize(new
                {
                    root, isolated, test, projects = closure, files = inputs.Order(StringComparer.Ordinal), replacements,
                    configurationFiles = Directory.GetFiles(isolated).Select(Path.GetFileName)
                }, new JsonSerializerOptions { WriteIndented = true }));
                var project = Path.Combine(isolated, isolatedProjects[test].Path);
                await ToolProcess.Run("dotnet", ["build", project, "-c", configuration, "--nologo",
                    "-maxCpuCount:1", "-nodeReuse:false", "-property:UseSharedCompilation=false", "-property:RestoreConfigFile=" + config],
                    isolated, Path.Combine(evidence, "build.log"), 300);
                var analysis = await ProjectAnalysis.Check(isolated, configuration, evidence, isolatedProjects, [test], isolated: true);
                failures.AddRange(analysis);
                if (analysis.Count != 0) continue;
                if (test == "Train.Godot.Tests")
                {
                    var resolved = Directory.GetFiles(evidence, "*.facts.json").SelectMany(file =>
                    {
                        using var projectFacts = JsonDocument.Parse(File.ReadAllText(file));
                        return projectFacts.RootElement.GetProperty("Items").GetProperty("ReferencePath").EnumerateArray()
                            .Where(item => ProjectAnalysis.IsFirstParty(item.GetProperty("Filename").GetString()!))
                            .Select(item => item.Clone()).ToArray();
                    }).ToArray();
                    await File.WriteAllTextAsync(Path.Combine(evidence, "first-party-assemblies.json"),
                        JsonSerializer.Serialize(resolved, new JsonSerializerOptions { WriteIndented = true }));
                    if (resolved.Any(item => item.GetProperty("Filename").GetString() is "Train.Core" or "Train.Content" or "Train.Scenes"))
                        throw new InvalidOperationException("Godot isolation resolved a forbidden implementation assembly.");
                }
                using var facts = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(evidence, test + ".facts.json")));
                var testAssembly = facts.RootElement.GetProperty("Properties").GetProperty("TargetPath").GetString()!;
                var session = Stopwatch.StartNew();
                var testLog = Path.Combine(evidence, "tests.log");
                try
                {
                    await ToolProcess.Run("dotnet", ["test", "--test-modules", Path.GetRelativePath(isolated, testAssembly).Replace(Path.DirectorySeparatorChar, '/'),
                        "--root-directory", isolated, "--results-directory", evidence, "--minimum-expected-tests", "1", "--",
                        "--filter", "TestCategory!=stage", "--report-trx", "--report-trx-filename", "tests.trx", "--timeout", $"{SessionTimeoutSeconds - 5}s",
                        "--hangdump", "--hangdump-timeout", $"{SessionTimeoutSeconds - 10}s"], isolated, testLog, SessionTimeoutSeconds);
                }
                catch (Exception exception) when (session.Elapsed.TotalSeconds >= SessionTimeoutSeconds - 10)
                {
                    throw new InvalidOperationException($"test session timed out after {session.Elapsed.TotalSeconds:F2}s (limit={SessionTimeoutSeconds}s); log={testLog}", exception);
                }
                Console.WriteLine($"isolation passed: {test}; evidence={evidence}");

                void CopyConfiguration(string directory)
                {
                    foreach (var file in Directory.GetFiles(directory))
                    {
                        var name = Path.GetFileName(file);
                        if (name == "global.json" || name.StartsWith("Directory.Build.", StringComparison.Ordinal) ||
                            name == "Directory.Packages.props" || name.Equals("NuGet.Config", StringComparison.OrdinalIgnoreCase)) CopyFile(file);
                    }
                }

                void CopyDirectory(string source)
                {
                    if (test == "Train.Godot.Tests" && Path.GetFullPath(source) == Path.Combine(root, "App", "Composition")) return;
                    foreach (var file in Directory.GetFiles(source)) CopyFile(file);
                    foreach (var directory in Directory.GetDirectories(source))
                        if (Path.GetFileName(directory) is not ("bin" or "obj" or ".godot")) CopyDirectory(directory);
                }

                void CopyFile(string source)
                {
                    var relative = Path.GetRelativePath(root, source);
                    if (!inputs.Add(relative)) return;
                    var destination = Path.Combine(isolated, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Copy(source, destination);
                }
            }
            catch (Exception exception) { failures.Add($"isolation failed: {test}: {exception.Message}"); }
            finally
            {
                try { if (Directory.Exists(isolated)) Directory.Delete(isolated, recursive: true); }
                catch (Exception exception) { failures.Add($"isolation cleanup failed: {test}: {exception.Message}"); }
            }
        }
        await File.WriteAllTextAsync(Path.Combine(output, "isolation-summary.json"), JsonSerializer.Serialize(new
        { seconds = watch.Elapsed.TotalSeconds, tests, failures }, new JsonSerializerOptions { WriteIndented = true }));
        foreach (var failure in failures) Console.Error.WriteLine(failure);
        return failures.Count == 0 ? 0 : 1;
    }
}
