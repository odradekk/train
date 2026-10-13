using System.Text.Json;

namespace Train.ContractChecks;

internal static class Isolation
{
    internal static async Task<int> Run(string root, string configuration, string output,
        IReadOnlyDictionary<string, ProjectRule> projects, IReadOnlyList<string> tests)
    {
        if (tests is not { Count: > 0 } || tests.Any(test => !projects.ContainsKey(test)))
            throw new InvalidOperationException("isolationTests must name at least one registered test project.");
        var failures = new List<string>();
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
                var closure = ProjectAnalysis.Closure(projects, test);
                var inputs = new HashSet<string>(StringComparer.Ordinal);
                foreach (var name in closure)
                {
                    var directory = Path.GetDirectoryName(Path.GetFullPath(Path.Combine(root, projects[name].Path)))!;
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
                // 查找在隔离根终止，不能继承外层工作区或用户目录中的构建配置。
                foreach (var file in new[] { "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props" })
                    if (!File.Exists(Path.Combine(isolated, file))) File.WriteAllText(Path.Combine(isolated, file), "<Project />");
                var config = Path.Combine(isolated, "NuGet.Config");
                if (!File.Exists(config))
                    File.WriteAllText(config, "<configuration><packageSources><clear /><add key=\"nuget.org\" value=\"https://api.nuget.org/v3/index.json\" /></packageSources></configuration>");
                await File.WriteAllTextAsync(Path.Combine(evidence, "inputs.json"), JsonSerializer.Serialize(new
                {
                    root, isolated, test, projects = closure, files = inputs.Order(StringComparer.Ordinal),
                    configurationFiles = Directory.GetFiles(isolated).Select(Path.GetFileName)
                }, new JsonSerializerOptions { WriteIndented = true }));
                var project = Path.Combine(isolated, projects[test].Path);
                await ToolProcess.Run("dotnet", ["build", project, "-c", configuration, "--nologo",
                    "-maxCpuCount:1", "-nodeReuse:false", "-property:UseSharedCompilation=false", "-property:RestoreConfigFile=" + config],
                    isolated, Path.Combine(evidence, "build.log"), 300);
                var analysis = await ProjectAnalysis.Check(isolated, configuration, evidence, projects, [test], isolated: true);
                failures.AddRange(analysis);
                if (analysis.Count != 0) continue;
                using var facts = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(evidence, test + ".facts.json")));
                var testAssembly = facts.RootElement.GetProperty("Properties").GetProperty("TargetPath").GetString()!;
                await ToolProcess.Run("dotnet", ["test", "--test-modules", Path.GetRelativePath(isolated, testAssembly).Replace(Path.DirectorySeparatorChar, '/'),
                    "--root-directory", isolated, "--results-directory", evidence, "--minimum-expected-tests", "1", "--",
                    "--report-trx", "--report-trx-filename", "tests.trx", "--timeout", "115s",
                    "--hangdump", "--hangdump-timeout", "110s"], isolated, Path.Combine(evidence, "tests.log"), 120);
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
        foreach (var failure in failures) Console.Error.WriteLine(failure);
        return failures.Count == 0 ? 0 : 1;
    }
}
