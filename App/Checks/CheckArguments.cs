
using Godot;

namespace Train.App.Checks;

internal sealed record CheckArguments(string Name, string UserDataDirectory, string? ContentRoot)
{
    public static CheckArguments? Parse(IEnumerable<string> arguments)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var argument in arguments)
        {
            if (!argument.StartsWith("--check", StringComparison.Ordinal))
                continue;
            var parts = argument.Split('=', 2);
            if (parts.Length != 2 || parts[1].Length == 0 ||
                parts[0] is not ("--check" or "--check-user-data" or "--check-content-root") ||
                !values.TryAdd(parts[0], parts[1]))
                throw new ArgumentException($"Invalid check argument: {argument}");
        }
        if (!values.TryGetValue("--check", out var name))
        {
            if (values.Count != 0)
                throw new ArgumentException("Check directories require --check=<name>.");
            return null;
        }
        if (name is not ("logging" or "content" or "mods-export" or "content-export" or "main-menu" or
            "simulation-host"))
            throw new ArgumentException($"Unknown check: {name}");
        values.TryGetValue("--check-content-root", out var contentRoot);
        if (name == "content" && contentRoot is null)
            throw new ArgumentException("The content check requires --check-content-root=<directory>.");
        var userData = values.GetValueOrDefault("--check-user-data", OS.GetUserDataDir());
        return new CheckArguments(name, Path.GetFullPath(userData),
            contentRoot is null ? null : Path.GetFullPath(contentRoot));
    }
}
