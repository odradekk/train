using Train.Content.Contracts;

namespace Train.App.Editing;

internal static class AuthoringPaths
{
    internal const string UserLibraryFolder = "content-library";

    internal static string UserLibraryDirectory(string userData) => Path.Combine(userData, UserLibraryFolder);

    internal static string UserDraftsDirectory(string userData) =>
        Path.Combine(userData, "authoring-drafts", "user-library");

    internal static string PackageDraftsDirectory(string userData, PackageId package)
    {
        var id = package.Value;
        var device = id?.Split('.')[0].ToUpperInvariant();
        if (device is "CON" or "PRN" or "AUX" or "NUL" or "COM1" or "COM2" or "COM3" or "COM4" or "COM5" or "COM6" or "COM7" or "COM8" or "COM9"
            or "LPT1" or "LPT2" or "LPT3" or "LPT4" or "LPT5" or "LPT6" or "LPT7" or "LPT8" or "LPT9" || string.IsNullOrWhiteSpace(id) || id is "." or ".." ||
            id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || id.EndsWith('.') || id.EndsWith(' '))
            throw new InvalidDataException("包身份不能用作安全的文件名。");
        return Path.Combine(userData, "authoring-drafts", "packages", id);
    }
}
