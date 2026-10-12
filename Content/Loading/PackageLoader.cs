using System.Collections.Immutable;
using System.Text.Json;
using C = Train.Content.Contracts;

namespace Train.Content.Loading;

internal sealed class PackageLoader(C.ContentInstallation installation, C.IContentFiles files)
{
    internal List<LoadPackage> Discover()
    {
        var core = new LoadPackage(installation.BuiltIn, "", true)
        {
            Metadata = new(new("core"), "core", installation.GameVersion, [], [])
        };
        var packages = new List<LoadPackage> { core };
        if (!SemVer.IsValid(installation.GameVersion))
            core.Reject("invalid_field", "", "GameVersion", "游戏版本必须是 SemVer。");
        var entries = files.Enumerate(new(installation.Mods, ""));
        if (entries is C.ContentIoResult<ImmutableArray<C.ContentFileEntry>>.Failure failure)
        {
            if (failure.Kind != C.ContentIoFailure.Missing)
            {
                var rejected = new LoadPackage(installation.Mods, "", false);
                rejected.Reject(ContentDocument.IoCode(failure.Kind), "", "", failure.Diagnostic);
                packages.Add(rejected);
            }
            return packages;
        }
        foreach (var entry in ((C.ContentIoResult<ImmutableArray<C.ContentFileEntry>>.Success)entries).Value.OrderBy(e => e.Name, StringComparer.Ordinal))
        {
            if (entry.Kind == C.ContentEntryKind.File) continue;
            var package = new LoadPackage(installation.Mods, entry.Name + "/", false);
            packages.Add(package);
            if (!ContentDocument.SafeEntry(entry.Name) || entry.Kind == C.ContentEntryKind.Link)
            {
                package.Reject("linked_path", entry.Name, "", "包目录路径无效或包含链接。");
                continue;
            }
            var path = package.Prefix + "mod.json";
            var read = files.Read(new(package.Root, path), 1024 * 1024);
            if (read is C.ContentIoResult<ImmutableArray<byte>>.Failure readFailure)
                package.Reject(ContentDocument.IoCode(readFailure.Kind), path, "", readFailure.Diagnostic);
            else
                ParseManifest(((C.ContentIoResult<ImmutableArray<byte>>.Success)read).Value.ToArray(), package, path);
            if (package.State == C.PackageState.Available && !installation.EnabledMods.Contains(package.Metadata!.Id))
                package.State = C.PackageState.Disabled;
        }
        foreach (var group in packages.Where(p => p.Metadata is not null).GroupBy(p => p.Metadata!.Id).Where(g => g.Count() > 1))
            foreach (var package in group)
                package.Reject("duplicate_identity", package.Prefix + "mod.json", "id", "包 UUID 重复。");

        ExcludeDependents(packages);
        var active = packages.Where(p => p.State == C.PackageState.Available).ToDictionary(p => p.Metadata!.Id);
        var visiting = new List<LoadPackage>();
        var visited = new HashSet<LoadPackage>();
        var ordered = new List<LoadPackage>();
        void Visit(LoadPackage package)
        {
            var cycleStart = visiting.IndexOf(package);
            if (cycleStart >= 0)
            {
                foreach (var member in visiting.Skip(cycleStart))
                    DependencyFailure(member, "dependency_cycle", "依赖存在循环。");
                return;
            }
            if (!visited.Add(package)) return;
            visiting.Add(package);
            foreach (var dependency in package.Metadata!.Dependencies.OrderBy(d => d.Package.Value, StringComparer.Ordinal))
                if (active.TryGetValue(dependency.Package, out var target)) Visit(target);
            visiting.RemoveAt(visiting.Count - 1);
            ordered.Add(package);
        }
        foreach (var package in active.Values.OrderBy(p => p.Metadata!.Id.Value, StringComparer.Ordinal)) Visit(package);
        ExcludeDependents(packages);
        return ordered.Where(p => p.State == C.PackageState.Available)
            .Concat(packages.Where(p => p.State != C.PackageState.Available)).ToList();
    }

    internal static void ExcludeDependents(List<LoadPackage> packages)
    {
        bool changed;
        do
        {
            changed = false;
            foreach (var package in packages.Where(p => p.State == C.PackageState.Available && !p.BuiltIn))
            {
                foreach (var dependency in package.Metadata!.Dependencies)
                {
                    var target = packages.FirstOrDefault(p => p.Metadata?.Id == dependency.Package && p.State == C.PackageState.Available);
                    if (target is null)
                        DependencyFailure(package, "dependency_missing", $"依赖包不可用：{dependency.Package.Value}。");
                    else if (CompareVersions(target.Metadata!.Version, dependency.MinimumVersion) < 0)
                        DependencyFailure(package, "dependency_version", $"依赖包版本过低：{dependency.Package.Value}。");
                    else continue;
                    changed = true;
                    break;
                }
            }
        } while (changed);
    }

    private static void DependencyFailure(LoadPackage package, string code, string message)
    {
        package.Reject(code, package.Prefix + "mod.json", "dependencies", message);
        package.State = C.PackageState.DependencyUnavailable;
    }

    private static void ParseManifest(byte[] bytes, LoadPackage package, string path)
    {
        using var document = ContentDocument.Parse(bytes, package, path);
        if (document is null) return;
        var root = document.RootElement;
        void Invalid(string field) => package.Reject("invalid_field", path, field, "manifest 字段无效。");
        bool Fields(JsonElement value, string[] allowed, string field)
        {
            if (value.ValueKind != JsonValueKind.Object) { Invalid(field); return false; }
            foreach (var property in value.EnumerateObject())
                if (!allowed.Contains(property.Name)) Invalid(field + property.Name);
            return true;
        }
        string? Text(JsonElement value, string field, int maximum, bool optional = false, bool trim = true)
        {
            if (!value.TryGetProperty(field, out var element))
            { if (!optional) Invalid(field); return null; }
            if (element.ValueKind != JsonValueKind.String) { Invalid(field); return null; }
            var text = trim ? element.GetString()!.Trim() : element.GetString()!;
            if (text.Length == 0 || text.Length > maximum) { Invalid(field); return null; }
            return text;
        }
        if (!Fields(root, ["schema_version", "id", "name", "version", "authors", "description", "dependencies"], "")) return;
        if (!root.TryGetProperty("schema_version", out var schema) || schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out var number) || number != 2)
            package.Reject("unsupported_format", path, "schema_version", "manifest 只支持 schema_version 2。");
        var id = Text(root, "id", 100, trim: false);
        if (!ModManifest.TryCanonicalUuid(id, out var canonical)) Invalid("id");
        var name = Text(root, "name", 100);
        var version = Text(root, "version", int.MaxValue, trim: false);
        if (!SemVer.IsValid(version)) Invalid("version");
        if (root.TryGetProperty("description", out var description) &&
            (description.ValueKind != JsonValueKind.String || description.GetString()!.Trim().Length > 4000)) Invalid("description");
        var authors = new List<string>();
        if (!root.TryGetProperty("authors", out var authorValues) || authorValues.ValueKind != JsonValueKind.Array) Invalid("authors");
        else
        {
            foreach (var author in authorValues.EnumerateArray())
            {
                var text = author.ValueKind == JsonValueKind.String ? author.GetString()!.Trim() : "";
                if (text.Length is < 1 or > 100 || authors.Contains(text)) Invalid("authors");
                authors.Add(text);
            }
            if (authors.Count is < 1 or > 16) Invalid("authors");
        }
        var dependencies = new List<C.PackageDependency>();
        if (root.TryGetProperty("dependencies", out var values))
        {
            if (values.ValueKind != JsonValueKind.Array) Invalid("dependencies");
            else foreach (var value in values.EnumerateArray())
            {
                if (!Fields(value, ["id", "name", "min_version"], "dependencies.")) continue;
                var dependency = Text(value, "id", 100, trim: false);
                var minimum = Text(value, "min_version", int.MaxValue, trim: false);
                Text(value, "name", 100, true);
                if (dependency != "core" && !ModManifest.TryCanonicalUuid(dependency, out _)) Invalid("dependencies.id");
                if (!SemVer.IsValid(minimum)) Invalid("dependencies.min_version");
                if (dependency == id || dependencies.Any(d => d.Package.Value == dependency)) Invalid("dependencies.id");
                if (dependency is not null && minimum is not null) dependencies.Add(new(new(dependency), minimum));
            }
        }
        if (!dependencies.Any(d => d.Package.Value == "core")) dependencies.Add(new(new("core"), "0.0.0-0"));
        // 格式错误的 manifest 仍保留可识别的身份，以检查重复 UUID。
        if (canonical.Length > 0)
        {
            package.Metadata = new(new(canonical), name ?? "", version ?? "", authors.ToImmutableArray(), dependencies.ToImmutableArray());
            for (var i = 0; i < package.Diagnostics.Count; i++)
                package.Diagnostics[i] = package.Diagnostics[i] with { Source = new(package.Metadata.Id, path) };
        }
    }

    private static int CompareVersions(string left, string right)
    {
        var a = left.Split('+')[0].Split('-', 2);
        var b = right.Split('+')[0].Split('-', 2);
        var ac = a[0].Split('.');
        var bc = b[0].Split('.');
        static int Numeric(string x, string y) => x.Length != y.Length ? x.Length.CompareTo(y.Length) : string.CompareOrdinal(x, y);
        for (var i = 0; i < 3; i++)
        {
            var comparison = Numeric(ac[i], bc[i]);
            if (comparison != 0) return comparison;
        }
        if (a.Length != b.Length) return a.Length == 1 ? 1 : -1;
        if (a.Length == 1) return 0;
        var ap = a[1].Split('.');
        var bp = b[1].Split('.');
        for (var i = 0; i < Math.Min(ap.Length, bp.Length); i++)
        {
            var an = ap[i].All(char.IsAsciiDigit);
            var bn = bp[i].All(char.IsAsciiDigit);
            var comparison = an && bn ? Numeric(ap[i], bp[i]) : an != bn ? (an ? -1 : 1) : string.CompareOrdinal(ap[i], bp[i]);
            if (comparison != 0) return comparison;
        }
        return ap.Length.CompareTo(bp.Length);
    }
}
