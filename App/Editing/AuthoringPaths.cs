namespace Train.App.Editing;

internal static class AuthoringPaths
{
    internal const string UserLibraryFolder = "content-library";

    internal static string UserLibraryDirectory(string userData) => Path.Combine(userData, UserLibraryFolder);

    internal static string UserDraftsDirectory(string userData) =>
        Path.Combine(userData, "authoring-drafts", "user-library");
}
