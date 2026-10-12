using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;

namespace Train.ContractChecks;

internal sealed record BoundaryRules(Dictionary<string, ProjectRule> Projects);
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
        var pending = new Queue<string>(names);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (pending.Count != 0 && failures.Count == 0)
        {
            var batch = new List<string>();
            while (pending.TryPeek(out var next) && batch.Count < (isolated ? 1 : 4))
            {
                // 调用样例会构建共享契约，逐个执行；其余只读取已构建的项目。
                if (batch.Count != 0 && (IsSample(next) || IsSample(batch[0]))) break;
                pending.Dequeue();
                if (seen.Add(next)) batch.Add(next);
            }
            foreach (var result in await Task.WhenAll(batch.Select(Inspect)))
            {
                failures.AddRange(result.Failures);
                reports.Add(result.Report);
                foreach (var dependency in result.Dependencies) pending.Enqueue(dependency);
            }
        }
        await File.WriteAllTextAsync(Path.Combine(output, "references.json"), JsonSerializer.Serialize(reports,
            new JsonSerializerOptions { WriteIndented = true }));
        return failures;

        bool IsSample(string name) => projects[name].Path.Replace('\\', '/').StartsWith("prototypes/", StringComparison.Ordinal);

        async Task<(List<string> Failures, object Report, List<string> Dependencies)> Inspect(string name)
        {
            var projectFailures = new List<string>();
            var dependencies = new List<string>();
            var rule = projects[name];
            var project = Path.GetFullPath(Path.Combine(root, rule.Path));
            var owner = Path.GetDirectoryName(project)!;
            var projectConfiguration = name == "Train" && configuration == "Release" ? "ExportRelease" : configuration;
            var arguments = new List<string> { "msbuild", project, "-nodeReuse:false", "-property:UseSharedCompilation=false",
                "-property:Configuration=" + projectConfiguration, "-verbosity:quiet",
                "-getItem:ProjectReference,ReferencePath,Compile,InternalsVisibleTo", "-getProperty:TargetPath" };
            if (!isolated && IsSample(name))
                arguments.AddRange(["-restore", "-target:Build"]);
            else arguments.AddRange(["-target:ResolveReferences", "-property:BuildProjectReferences=false"]);
            var raw = await ToolProcess.Run("dotnet", arguments, owner, Path.Combine(output, name + ".msbuild.log"));
            using var data = JsonDocument.Parse(raw);
            var items = data.RootElement.GetProperty("Items");
            var closure = Closure(projects, name);
            closure.Remove(name);
            var assembly = data.RootElement.GetProperty("Properties").GetProperty("TargetPath").GetString()!;
            foreach (var item in items.GetProperty("ProjectReference").EnumerateArray())
            {
                var target = item.GetProperty("Filename").GetString()!;
                var path = item.GetProperty("FullPath").GetString()!;
                // SDK 把传递项目引用加入求值集合，因此按允许闭包核对。
                if (!projects.TryGetValue(target, out var targetRule) || !closure.Contains(target) ||
                    !Path.GetFullPath(path).Equals(Path.GetFullPath(Path.Combine(root, targetRule.Path)), PathComparison))
                    projectFailures.Add($"forbidden project reference: {name} -> {target} ({path})");
                if (isolated && !IsChildPath(path, root)) projectFailures.Add($"project reference outside isolation: {name} ({path})");
                if (projects.ContainsKey(target) && IsChildPath(path, root)) dependencies.Add(target);
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
                    if (!new[] { "App/", "Ui/", "Rendering/", ".godot/mono/temp/obj/" }.Any(relative.StartsWith))
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
                assemblySha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(assembly))) };

            void CheckDependency(string target, string kind)
            {
                if (name is not ("Train" or "Train.Godot.Tests" or "Train.Godot.ContractSamples") && target.StartsWith("GodotSharp", StringComparison.Ordinal))
                    projectFailures.Add($"forbidden {kind}: {name} -> {target}");
                if (IsFirstParty(target) && !closure.Contains(target)) projectFailures.Add($"forbidden {kind}: {name} -> {target}");
            }
            return (projectFailures, report, dependencies);
        }
    }
}
