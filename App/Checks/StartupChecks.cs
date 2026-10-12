
using Godot;
using Train.Content;
using Train.Content.Voxels;
using Train.App.Logging;
using Train.Ui;

namespace Train.App.Checks;

internal static class StartupChecks
{
    internal static readonly VoxelDefinitionId GrainId = new("core", "grain");
    private const string GrainImagePath = "voxels/grain/placeholder.png";
    internal const string VoxelJson = "res://builtin/core/resources/voxels/grain.voxels.json";
    internal const string IconPng = "res://builtin/core/resources/" + GrainImagePath;

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

    internal static bool ContentExport(ContentReloader reloader, ContentReloadResult loaded, TrainLogger logger)
    {
        var snapshot = reloader.Current;
        var ok = ReportCatalog(snapshot, loaded, "CONTENT_EXPORT");
        ok &= Report(CanReadResource(VoxelJson), "CONTENT_EXPORT_JSON_READABLE");
        ok &= Report(CanReadResource(IconPng), "CONTENT_EXPORT_PNG_READABLE");
        ok &= Report(logger.JsonlPath is not null && File.Exists(logger.JsonlPath), "CONTENT_EXPORT_JSONL_CREATED");
        return ok;
    }

    internal static bool ModsExport(ContentReloader reloader, ContentReloadResult loaded, string userData)
    {
        var snapshot = reloader.Current;
        var ok = ReportCatalog(snapshot, loaded, "MODS_EXPORT");
        var mods = Path.Combine(userData, "mods");
        if (!Directory.Exists(mods))
            return Report(true, "MODS_EXPORT_ROOT_UNAVAILABLE") && ok;
        var empty = !Directory.EnumerateFileSystemEntries(mods).Any();
        GD.Print(empty ? "MODS_EXPORT_ROOT_EMPTY" : "MODS_EXPORT_ROOT_PRESENT");
        return empty && ok;
    }

    internal static bool ReportCatalog(ContentSnapshot? snapshot, ContentReloadResult loaded, string prefix)
    {
        var grain = snapshot?.Voxels.GetValueOrDefault(GrainId);
        var ok = Report(loaded.IsCommitted && grain is not null, $"{prefix}_GRAIN_LOADED");
        var presentation = grain?.Definition.Presentation;
        var decoded = loaded.IsCommitted && grain is not null && presentation is not null &&
            presentation.IconPath == GrainImagePath && presentation.TopPath == GrainImagePath &&
            presentation.SidePath == GrainImagePath && presentation.BottomPath == GrainImagePath &&
            grain.PresentationImages.Count == 1 && grain.PresentationImages.TryGetValue(GrainImagePath, out var image) &&
            image.Width > 0 && image.Height > 0;
        ok &= Report(decoded, $"{prefix}_PNG_DECODED");
        ok &= Report(loaded.IsCommitted, $"{prefix}_INIT_OK");
        return ok;
    }

    internal static bool CanReadResource(string path)
    {
        using var file = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Read);
        return file is not null && file.GetLength() > 0 &&
            (ulong)file.GetBuffer((long)file.GetLength()).Length == file.GetLength();
    }

    internal static bool Report(bool condition, string marker)
    {
        if (condition)
            GD.Print(marker);
        return condition;
    }
}
