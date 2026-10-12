using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using System.Xml.Linq;

namespace Train.ContractChecks;

internal sealed record BoundaryRules(Dictionary<string, ProjectRule> Projects, string[] IsolationTests);
internal sealed record ProjectRule(string Path, string[] References, string[]? Friends = null);

internal static class ProjectAnalysis
{
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    internal static bool IsChildPath(string child, string parent) => Path.GetFullPath(child).StartsWith(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)) + Path.DirectorySeparatorChar, PathComparison);

    internal static bool IsFirstParty(string name) => name == "Train" || name.StartsWith("Train.", StringComparison.Ordinal);

    internal static HashSet<string> Closure(IReadOnlyDictionary<string, ProjectRule> projects, string name)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        Add(name);
        return result;
        void Add(string project)
        {
            if (!result.Add(project)) return;
            foreach (var dependency in projects[project].References) Add(dependency);
        }
    }

    internal static async Task<int> CheckWorkspace(string root, string configuration, string output,
        IReadOnlyDictionary<string, ProjectRule> projects)
    {
        Directory.CreateDirectory(output);
        var inventory = await ToolProcess.Run("git", ["ls-files", "--cached", "--others", "--exclude-standard", "--", "*.csproj"],
            root, Path.Combine(output, "inventory.log"));
        var registered = projects.Values.Select(project => project.Path.Replace('\\', '/')).ToHashSet(StringComparer.Ordinal);
        var failures = inventory.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(path => !path.StartsWith("archive/", StringComparison.Ordinal) && !registered.Contains(path))
            .Select(path => "unregistered project: " + path).ToList();
        failures.AddRange(await Check(root, configuration, output, projects, projects.Keys, isolated: false));
        foreach (var failure in failures) Console.Error.WriteLine(failure);
        Console.WriteLine($"registered projects={projects.Count}, failures={failures.Count}; evidence={output}");
        return failures.Count == 0 ? 0 : 1;
    }

    internal static async Task<List<string>> Check(string root, string configuration, string output,
        IReadOnlyDictionary<string, ProjectRule> projects, IEnumerable<string> names, bool isolated)
    {
        var failures = new List<string>();
        var reports = new List<object>();
        var requested = names.SelectMany(name => Closure(projects, name)).Distinct(StringComparer.Ordinal).ToArray();
        var facts = await QueryProjects(root, configuration, output, projects, requested, isolated);
        foreach (var name in requested)
        {
            var result = Inspect(name);
            failures.AddRange(result.Failures);
            reports.Add(result.Report);
        }
        await File.WriteAllTextAsync(Path.Combine(output, "references.json"), JsonSerializer.Serialize(reports,
            new JsonSerializerOptions { WriteIndented = true }));
        return failures;

        (List<string> Failures, object Report) Inspect(string name)
        {
            var projectFailures = new List<string>();
            var rule = projects[name];
            var project = Path.GetFullPath(Path.Combine(root, rule.Path));
            var owner = Path.GetDirectoryName(project)!;
            var projectConfiguration = name == "Train" && configuration == "Release" ? "ExportRelease" : configuration;
            var data = facts[name];
            var items = data.GetProperty("Items");
            var closure = Closure(projects, name);
            closure.Remove(name);
            var assembly = data.GetProperty("Properties").GetProperty("TargetPath").GetString()!;
            foreach (var item in items.GetProperty("ProjectReference").EnumerateArray())
            {
                var target = item.GetProperty("Filename").GetString()!;
                var path = item.GetProperty("FullPath").GetString()!;
                // SDK 把传递项目引用加入求值集合，因此按允许闭包核对。
                if (!projects.TryGetValue(target, out var targetRule) || !closure.Contains(target) ||
                    !Path.GetFullPath(path).Equals(Path.GetFullPath(Path.Combine(root, targetRule.Path)), PathComparison))
                    projectFailures.Add($"forbidden project reference: {name} -> {target} ({path})");
                if (isolated && !IsChildPath(path, root)) projectFailures.Add($"project reference outside isolation: {name} ({path})");
            }
            foreach (var item in items.GetProperty("ReferencePath").EnumerateArray())
            {
                var target = item.GetProperty("Filename").GetString()!;
                var path = item.GetProperty("FullPath").GetString()!;
                CheckDependency(target, "resolved assembly");
                if (!IsFirstParty(target)) continue;
                if (isolated && !IsChildPath(path, root)) projectFailures.Add($"assembly resolved outside isolation: {name} -> {target} ({path})");
                if (projects.TryGetValue(target, out var targetRule) &&
                    !IsChildPath(path, Path.GetDirectoryName(Path.Combine(root, targetRule.Path))!))
                    projectFailures.Add($"assembly resolved outside owner: {name} -> {target} ({path})");
            }
            foreach (var item in items.GetProperty("Compile").EnumerateArray())
            {
                var path = item.GetProperty("FullPath").GetString()!;
                if (!IsChildPath(path, owner)) projectFailures.Add($"linked source outside owner: {name} ({path})");
                if (isolated && !IsChildPath(path, root)) projectFailures.Add($"compile source outside isolation: {name} ({path})");
                if (name == "Train")
                {
                    var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                    if (!new[] { "App/", "Ui/", "Rendering/", ".godot/mono/temp/obj/" }.Any(relative.StartsWith) &&
                        !(projectConfiguration == "Debug" && relative.StartsWith("Tests/Engine/Bootstrap/", StringComparison.Ordinal)))
                        projectFailures.Add("host compiles foreign source: " + path);
                }
            }
            var friends = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in items.GetProperty("InternalsVisibleTo").EnumerateArray())
                friends.Add(item.GetProperty("Identity").GetString()!);
            if (isolated && !IsChildPath(assembly, root)) projectFailures.Add($"output outside isolation: {name} ({assembly})");
            using var stream = File.OpenRead(assembly);
            using var pe = new PEReader(stream);
            var metadata = pe.GetMetadataReader();
            var assemblyReferences = metadata.AssemblyReferences.Select(handle =>
                metadata.GetString(metadata.GetAssemblyReference(handle).Name)).ToArray();
            foreach (var target in assemblyReferences) CheckDependency(target, "output assembly reference");
            foreach (var handle in metadata.GetAssemblyDefinition().GetCustomAttributes())
            {
                var attribute = metadata.GetCustomAttribute(handle);
                if (attribute.Constructor.Kind != HandleKind.MemberReference) continue;
                var constructor = metadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
                if (constructor.Parent.Kind != HandleKind.TypeReference) continue;
                var type = metadata.GetTypeReference((TypeReferenceHandle)constructor.Parent);
                if (metadata.GetString(type.Namespace) != "System.Runtime.CompilerServices" ||
                    metadata.GetString(type.Name) != "InternalsVisibleToAttribute") continue;
                var blob = metadata.GetBlobReader(attribute.Value);
                blob.ReadUInt16();
                friends.Add(blob.ReadSerializedString()!);
            }
            foreach (var friend in friends)
                if (!(rule.Friends ?? []).Contains(friend)) projectFailures.Add($"forbidden friend assembly: {name} -> {friend}");
            var report = new { name, project, assembly, items = items.Clone(), assemblyReferences, friends,
                processId = data.GetProperty("Properties").GetProperty("ProcessId").GetInt32(),
                assemblySha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(assembly))) };

            void CheckDependency(string target, string kind)
            {
                if (name is not ("Train" or "Train.Godot.Tests" or "Train.Engine.Tests" or "Train.Godot.ContractSamples") && target.StartsWith("GodotSharp", StringComparison.Ordinal))
                    projectFailures.Add($"forbidden {kind}: {name} -> {target}");
                if (IsFirstParty(target) && !closure.Contains(target)) projectFailures.Add($"forbidden {kind}: {name} -> {target}");
            }
            return (projectFailures, report);
        }
    }

    private static async Task<Dictionary<string, JsonElement>> QueryProjects(string root, string configuration,
        string output, IReadOnlyDictionary<string, ProjectRule> projects, string[] names, bool isolated)
    {
        Directory.CreateDirectory(output);
        var assembly = typeof(ProjectAnalysis).Assembly.Location;
        var targets = Path.Combine(Path.GetDirectoryName(assembly)!, "ProjectFacts.targets");
        var properties = $"CustomAfterMicrosoftCommonTargets={targets};ContractChecksAssembly={assembly};UseSharedCompilation=false;BuildProjectReferences=false;RestoreRecursive=false";
        var projectItems = names.Select(name =>
        {
            var sample = !isolated && projects[name].Path.Replace('\\', '/').StartsWith("prototypes/", StringComparison.Ordinal);
            var config = name == "Train" && configuration == "Release" ? "ExportRelease" : configuration;
            return new XElement("ContractProject", new XAttribute("Include", Path.Combine(root, projects[name].Path)),
                new XElement("AdditionalProperties", $"Configuration={config};ContractFactsFile={Path.Combine(output, name + ".facts.json")};ContractBuildSample={sample.ToString().ToLowerInvariant()}"),
                new XElement("Sample", sample.ToString().ToLowerInvariant()));
        }).ToArray();
        var sampleItems = projectItems.Where(item => item.Element("Sample")!.Value == "true").Select(item =>
        {
            var sample = new XElement(item);
            sample.Name = "ContractSample";
            return sample;
        });
        var traversal = new XDocument(new XElement("Project",
            new XElement("ItemGroup", projectItems, sampleItems),
            new XElement("Target", new XAttribute("Name", "CollectAll"),
                // 新的全局属性让 Build 重新求值，导入 Restore 刚生成的 NuGet 配置。
                new XElement("MSBuild", new XAttribute("Projects", "@(ContractSample)"), new XAttribute("Targets", "Restore"),
                    new XAttribute("Properties", properties + ";ContractRestorePhase=true"), new XAttribute("BuildInParallel", "false")),
                new XElement("MSBuild", new XAttribute("Projects", "@(ContractProject)"), new XAttribute("Targets", "CollectContractFacts"),
                    new XAttribute("Properties", properties), new XAttribute("BuildInParallel", "false")))));
        var traversalPath = Path.Combine(output, "query.proj");
        traversal.Save(traversalPath);
        await ToolProcess.Run("dotnet", ["msbuild", traversalPath, "-target:CollectAll", "-maxCpuCount:1", "-nodeReuse:false", "-verbosity:quiet"],
            root, Path.Combine(output, "query.log"), 300);
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            using var data = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(output, name + ".facts.json")));
            result.Add(name, data.RootElement.Clone());
        }
        return result;
    }
}
