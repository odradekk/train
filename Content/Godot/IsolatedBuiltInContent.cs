using Train.Core.Content;
using Godot;

namespace Train.Content;

internal static class IsolatedBuiltInContent
{
    public const string OverlayFolder = "builtin-resources";

    public static string Seed(string userDataDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(userDataDirectory);
        var destination = Path.Combine(userDataDirectory, OverlayFolder);
        CopyVirtualTree(GodotContentSource.BuiltInResourcesRoot, destination);
        if (!Directory.Exists(destination))
            throw new InvalidOperationException("Isolated built-in content could not be seeded.");
        return destination;
    }

    private static void CopyVirtualTree(string virtualDirectory, string physicalDirectory)
    {
        Directory.CreateDirectory(physicalDirectory);
        using var dir = DirAccess.Open(virtualDirectory);
        if (dir is null)
            throw new InvalidOperationException($"Could not read {virtualDirectory} to seed isolated content.");

        dir.ListDirBegin();
        for (var name = dir.GetNext(); name.Length > 0; name = dir.GetNext())
        {
            if (name is "." or ".." || name.EndsWith(".import", StringComparison.OrdinalIgnoreCase))
                continue;

            var virtualPath = virtualDirectory.TrimEnd('/') + "/" + name;
            var physicalPath = Path.Combine(physicalDirectory, name);
            if (dir.CurrentIsDir())
            {
                CopyVirtualTree(virtualPath, physicalPath);
                continue;
            }

            var bytes = Godot.FileAccess.GetFileAsBytes(virtualPath);
            if (bytes is null || bytes.Length == 0)
                throw new InvalidOperationException($"Could not read {virtualPath} while seeding isolated content.");
            File.WriteAllBytes(physicalPath, bytes);
        }

        dir.ListDirEnd();
    }
}
