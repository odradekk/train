using System.Text.Json.Nodes;
using Godot;
using Train.App;
using Train.App.Assets;
using Train.App.Logging;
using Train.Content.Contracts;
using Train.Core.Contracts;

namespace Train.Engine.Tests;

internal static class ContentFixture
{
    internal static void WriteMod(string directory, Guid id)
    {
        var voxels = Path.Combine(directory, "resources", "voxels");
        Directory.CreateDirectory(Path.Combine(voxels, "engine-food"));
        File.WriteAllText(Path.Combine(directory, "mod.json"), $$"""
            {"schema_version":2,"id":"{{id:D}}","name":"Engine fixture","version":"1.0.0","authors":["Engine tests"]}
            """);
        var definition = JsonNode.Parse(Godot.FileAccess.GetFileAsString(
            "res://builtin/core/resources/voxels/staple-food.voxel.json"))!;
        definition["id"] = "engine-food";
        File.WriteAllText(Path.Combine(voxels, "engine-food.voxel.json"), definition.ToJsonString());
        foreach (var image in new[] { "icon.png", "top.png", "side.png" })
            File.WriteAllBytes(Path.Combine(voxels, "engine-food", image), Godot.FileAccess.GetFileAsBytes(
                "res://builtin/core/resources/voxels/staple-food/" + image));
    }

    internal static string CopyBuiltIn()
    {
        var destination = Path.Combine(Path.GetTempPath(), "engine-content-" + Guid.NewGuid().ToString("N"));
        var source = ProjectSettings.GlobalizePath("res://builtin/core");
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        return destination;
    }

    internal static HostCoordinator CreateHost(TrainLogger logger, string builtin,
        GodotMainThreadDispatcher dispatcher)
    {
        var root = new ContentRoot("builtin");
        var mods = new ContentRoot("mods");
        var files = new ContentFiles(dispatcher, new Dictionary<ContentRoot, ContentRootLocation>
        {
            [root] = new ContentRootLocation.Disk(builtin),
            [mods] = new ContentRootLocation.Disk(Path.Combine(builtin, "mods"))
        });
        return new(Train.Core.CoreFactory.Create(), Train.Content.ContentFactory.Create(), files,
            new GodotAssetInspector(dispatcher), new(root, mods, null, "0.0.56", []), logger,
            sessionOptions: new(new("engine-fixture", "core-session-v1", "Windows", "x64", "10"), true, 1));
    }
}
