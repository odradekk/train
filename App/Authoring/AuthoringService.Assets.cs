using System.Collections.Immutable;
using Train.Content.Contracts;

namespace Train.App.Authoring;

internal sealed partial class AuthoringService
{
    internal string? SelectAsset(string source)
    {
        if (!CanUse || IsBusy || package is null || Target is null || Form is null) return null;
        try
        {
            var address = Address(Form);
            var directory = address.Kind switch
            {
                DefinitionKind.Voxel => "voxels", DefinitionKind.Blueprint => "blueprints",
                DefinitionKind.SettlementType => "settlement-types", DefinitionKind.LairType => "lair-types",
                DefinitionKind.SettlementTemplate => "settlement-templates",
                _ => throw new InvalidOperationException("此定义不支持资产。")
            };
            var prefix = "resources/" + directory + "/";
            var definitionPath = Path.GetFullPath(Path.Combine(Target.Directory, prefix, address.LocalId));
            var sharedPath = Path.GetFullPath(Path.Combine(Target.Directory, prefix, "shared"));
            var full = Path.GetFullPath(source);
            var inDefinition = IsWithin(full, definitionPath);
            var inShared = IsWithin(full, sharedPath);
            var field = inDefinition ? Path.GetRelativePath(definitionPath, full).Replace('\\', '/')
                : inShared ? "shared/" + Path.GetRelativePath(sharedPath, full).Replace('\\', '/') : Path.GetFileName(full);
            var relative = prefix + (inShared ? field : address.LocalId + "/" + field);
            var bytes = ImmutableArray.CreateRange(File.ReadAllBytes(full));
            var staged = assetEdits.OfType<PackageEdit.WriteAsset>().FirstOrDefault(edit => string.Equals(edit.PackageRelativePath, relative,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
            var original = snapshot?.Read(new(default, relative), long.MaxValue);
            var destination = Path.Combine(Target.Directory, relative);
            if (File.Exists(destination) && !File.ReadAllBytes(destination).AsSpan().SequenceEqual(bytes.AsSpan())
                || staged is not null && !staged.Bytes.AsSpan().SequenceEqual(bytes.AsSpan())
                || staged is null && original is ContentIoResult<ImmutableArray<byte>>.Success existing
                    && !existing.Value.AsSpan().SequenceEqual(bytes.AsSpan()))
            { Report("包内已有同名的不同资产，请先重命名所选文件。"); return null; }
            if (staged is null) assetEdits.Add(new PackageEdit.WriteAsset(relative, bytes));
            Report("资产已加入待写入修改。");
            return field;
        }
        catch (Exception error) { Report(error.Message); return null; }
    }

    private static bool IsWithin(string file, string directory) => file.StartsWith(directory.TrimEnd(Path.DirectorySeparatorChar)
        + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
