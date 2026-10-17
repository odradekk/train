using Train.Content.Contracts;

namespace Train.App.Editing;

internal static class AuthoringPaths
{
    internal const string UserLibraryFolder = "content-library";

    internal static string UserLibraryDirectory(string userData) => Path.Combine(userData, UserLibraryFolder);

    internal static string UserDraftsDirectory(string userData) =>
        Path.Combine(userData, "authoring-drafts", "user-library");

    internal const string BuiltInCoreResourceDirectory = "res://builtin/core";
    internal static string ModsDirectory(string userData) => Path.Combine(userData, "mods");
    internal static string PackageDraftsDirectory(string userData, PackageId package) =>
        Path.Combine(userData, "authoring-drafts", "packages", package.Value);
}
