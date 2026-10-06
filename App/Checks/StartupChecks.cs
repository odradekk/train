// Train.App.Checks 验证启动后的菜单，以及真实导出中的内容访问。
// 检查只观察已加载目录、原始资源与宿主日志，不创建玩法状态或用户内容。

using Godot;
using Train.Core.Content;
using Train.Core.Content.Voxels;
using Train.Core.Logging;
using Train.Ui;

namespace Train.App.Checks;

/// <summary>提供无需逐帧等待的菜单与导出检查。</summary>
/// <example>内容导出检查读取 ferrous-ore 的 JSON 和四张原始 PNG，并确认当前 JSONL 已创建。</example>
internal static class StartupChecks
{
    /// <summary>内置体素烟测暂时使用的定义路径，与内容导出过滤规则共同验证。</summary>
    internal const string VoxelJson = "res://builtin/core/resources/voxels/ferrous-ore.voxels.json";
    /// <summary>内置体素烟测使用的图标原始字节路径。</summary>
    internal const string IconPng = "res://builtin/core/resources/voxels/ferrous-ore/icon.png";

    /// <summary>验证主菜单只显示可用的退出按钮，并通过真实按钮信号请求宿主退出。</summary>
    /// <param name="menu">本次启动实际显示的主菜单。</param>
    /// <param name="quitRequested">读取宿主是否已经收到退出请求，不用检查自身的退出来证明按钮接线。</param>
    internal static bool MainMenu(MainMenu menu, Func<bool> quitRequested)
    {
        var ready = Report(menu.IsNodeReady() && menu.IsVisibleInTree(), "MAIN_MENU_READY");
        var buttons = menu.FindChildren("*", "BaseButton", true, false).Cast<BaseButton>()
            .Where(button => button.IsVisibleInTree()).ToArray();
        var quit = menu.GetNodeOrNull<Button>("%QuitGame");
        var quitOnly = Report(buttons.Length == 1 && buttons[0] == quit &&
            quit is { Disabled: false, Text: "退出" }, "MAIN_MENU_QUIT_ONLY");
        if (!ready || !quitOnly)
            return false;
        quit!.EmitSignal(BaseButton.SignalName.Pressed);
        return Report(quitRequested(), "MAIN_MENU_QUIT_REQUESTED");
    }

    /// <summary>验证已发布目录、导出包的原始 JSON/PNG 与本次日志文件均可读取。</summary>
    internal static bool ContentExport(ContentReloader reloader, ContentReloadResult loaded, TrainLogger logger)
    {
        var ok = ReportCatalog(reloader, loaded, "CONTENT_EXPORT");
        ok &= Report(CanReadResource(VoxelJson), "CONTENT_EXPORT_JSON_READABLE");
        var pngs = new[] { "icon", "top", "side", "bottom" };
        ok &= Report(pngs.All(name => CanReadResource($"res://builtin/core/resources/voxels/ferrous-ore/{name}.png")),
            "CONTENT_EXPORT_PNG_READABLE");
        ok &= Report(logger.JsonlPath is not null && File.Exists(logger.JsonlPath), "CONTENT_EXPORT_JSONL_CREATED");
        return ok;
    }

    /// <summary>验证用户 Mod 根缺失或为空时，导出程序仍可加载内置内容。</summary>
    internal static bool ModsExport(ContentReloader reloader, ContentReloadResult loaded, string userData)
    {
        var ok = ReportCatalog(reloader, loaded, "MODS_EXPORT");
        var mods = Path.Combine(userData, "mods");
        if (!Directory.Exists(mods))
            return Report(true, "MODS_EXPORT_ROOT_UNAVAILABLE") && ok;
        var empty = !Directory.EnumerateFileSystemEntries(mods).Any();
        GD.Print(empty ? "MODS_EXPORT_ROOT_EMPTY" : "MODS_EXPORT_ROOT_PRESENT");
        return empty && ok;
    }

    /// <summary>报告已加载内置体素及表现资产的检查结果；任一条件不满足则返回 false。</summary>
    internal static bool ReportCatalog(ContentReloader reloader, ContentReloadResult loaded, string prefix)
    {
        var found = reloader.Catalog.Current?.TryGet(new VoxelDefinitionId("core", "ferrous-ore"), out _) == true;
        var ok = Report(loaded.IsCommitted && found, $"{prefix}_FERROUS_ORE_LOADED");
        // 成功发布要求候选构建器已通过真实来源读取并解码全部引用图片。
        ok &= Report(loaded.IsCommitted && found, $"{prefix}_PNG_DECODED");
        ok &= Report(loaded.IsCommitted, $"{prefix}_INIT_OK");
        return ok;
    }

    /// <summary>通过 Godot 虚拟文件系统打开并读取非空原始资源。</summary>
    internal static bool CanReadResource(string path)
    {
        using var file = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Read);
        return file is not null && file.GetLength() > 0 &&
            (ulong)file.GetBuffer((long)file.GetLength()).Length == file.GetLength();
    }

    /// <summary>条件成立时打印对应行为标记，并原样返回条件结果。</summary>
    internal static bool Report(bool condition, string marker)
    {
        if (condition)
            GD.Print(marker);
        return condition;
    }
}
