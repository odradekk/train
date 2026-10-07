// Train.App.Checks 解析独立自检的启动参数。
// 检查选择来自命令行，测试输入与输出目录由统一运行器准备。

using Godot;

namespace Train.App.Checks;

/// <summary>保存本次只运行一项自检所需的名称和物理目录。</summary>
/// <param name="Name">已登记的 App 自检名称。</param>
/// <param name="UserDataDirectory">日志与外部内容的物理用户根；未提供参数时使用 Godot 用户目录。</param>
/// <param name="ContentRoot">initial 和 added 目录的父目录；未提供时为 null，仅 content 检查使用。</param>
/// <example>--check=content --check-content-root=C:/run/content-input 读取两份预先准备好的内容，不改写输入。</example>
internal sealed record CheckArguments(string Name, string UserDataDirectory, string? ContentRoot)
{
    /// <summary>读取检查参数；没有 --check 时返回 null，未知或重复的检查参数被拒绝。</summary>
    /// <param name="arguments">Godot 在 -- 分隔符后交给应用的参数。</param>
    /// <returns>本次检查配置；普通启动返回 null。</returns>
    /// <exception cref="ArgumentException">检查名无效、检查参数重复，或 content 缺少输入根。</exception>
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
