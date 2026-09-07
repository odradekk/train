using Godot;
using Train.Content;
using Train.Domain.Voxels;

namespace Train;

internal sealed class VoxelLiveReloadDriver
{
    public const string ProbeNamespace = "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee";
    public const string ProbeLocalId = "probe";
    public const string ProbeFolder = "voxel-reload-probe";

    private const string IronOreJsonPath = "res://builtin/core/resources/voxels/iron-ore.voxels.json";
    private const string IronOreTopPath = "res://builtin/core/resources/voxels/iron-ore/top.png";

    private readonly string _userDataDirectory;
    private string? _ironOreJsonBackup;
    private byte[]? _ironOreTopBackup;

    public VoxelDefinitionId ProbeId { get; } = new(ProbeNamespace, ProbeLocalId);

    public VoxelLiveReloadDriver(string userDataDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(userDataDirectory);
        _userDataDirectory = userDataDirectory;
    }

    public void BackupBuiltIn()
    {
        _ironOreJsonBackup = Godot.FileAccess.GetFileAsString(IronOreJsonPath);
        _ironOreTopBackup = Godot.FileAccess.GetFileAsBytes(IronOreTopPath);
        if (string.IsNullOrEmpty(_ironOreJsonBackup) || _ironOreTopBackup is null || _ironOreTopBackup.Length == 0)
            throw new InvalidOperationException("Could not backup built-in iron-ore content for live reload smoke.");
    }

    public void RestoreBuiltIn()
    {
        if (_ironOreJsonBackup is not null)
            WriteVirtualText(IronOreJsonPath, _ironOreJsonBackup);
        if (_ironOreTopBackup is not null)
            WriteVirtualBytes(IronOreTopPath, _ironOreTopBackup);
    }

    public void WriteIronOre(
        int maxDurability = 100,
        long value = 10,
        double mass = 1,
        string name = "铁矿",
        string description = "开发用占位自然资源种类，用于验证内容加载、校验与热重载。")
    {
        WriteVirtualText(IronOreJsonPath, IronOreJson(maxDurability, value, mass, name, description));
    }

    public void WriteGreenTopPng()
    {
        var image = Image.CreateEmpty(16, 16, false, Image.Format.Rgba8);
        image.Fill(new Color(0.12f, 0.78f, 0.28f));
        var error = image.SavePng(IronOreTopPath);
        if (error != Error.Ok)
            throw new InvalidOperationException($"Failed to write isolated iron-ore top.png: {error}.");
    }

    public void WriteProbeMod()
    {
        var root = Path.Combine(_userDataDirectory, "mods", ProbeFolder);
        var resources = Path.Combine(root, "resources");
        Directory.CreateDirectory(resources);
        File.WriteAllText(Path.Combine(root, ModDiscovery.ManifestFileName), ProbeManifest());
        File.WriteAllText(Path.Combine(resources, "probe.voxels.json"), ProbeVoxels());
        var png = Godot.FileAccess.GetFileAsBytes("res://builtin/core/resources/voxels/iron-ore/top.png");
        if (png is null || png.Length == 0)
            throw new InvalidOperationException("Could not copy an isolated probe PNG from built-in content.");
        File.WriteAllBytes(Path.Combine(resources, "probe.png"), png);
    }

    public void InvalidateProbeMod()
    {
        var voxels = Path.Combine(_userDataDirectory, "mods", ProbeFolder, "resources", "probe.voxels.json");
        File.WriteAllText(voxels, "{ \"schema_version\": 2, \"voxels\": [] }");
    }

    public static ContentReloadResult Reload(Main main) =>
        main.Reloader?.Reload() ?? throw new InvalidOperationException("Content reloader is unavailable.");

    private static void WriteVirtualText(string path, string text)
    {
        using var file = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Write);
        if (file is null)
            throw new InvalidOperationException($"Could not write {path}.");
        file.StoreString(text);
    }

    private static void WriteVirtualBytes(string path, byte[] bytes)
    {
        using var file = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Write);
        if (file is null)
            throw new InvalidOperationException($"Could not write {path}.");
        file.StoreBuffer(bytes);
    }

    private static string IronOreJson(int maxDurability, long value, double mass, string name, string description) =>
        $$"""
        {
          "schema_version": 1,
          "voxels": [
            {
              "id": "iron-ore",
              "name": "{{name}}",
              "description": "{{description}}",
              "value": {{value}},
              "mass": {{VoxelDefinition.FormatMass(mass)}},
              "max_durability": {{maxDurability}},
              "tags": ["natural_resource"],
              "behaviors": [],
              "allowed_orientations": "all_24",
              "presentation": {
                "kind": "cube",
                "icon": "voxels/iron-ore/icon.png",
                "voxel_top": "voxels/iron-ore/top.png",
                "voxel_side": "voxels/iron-ore/side.png",
                "voxel_bottom": "voxels/iron-ore/bottom.png"
              },
              "collision": { "kind": "full_box" }
            }
          ]
        }
        """;

    private static string ProbeManifest() =>
        $$"""
        {
          "schema_version": 1,
          "id": "{{ProbeNamespace}}",
          "name": "Voxel Reload Probe",
          "version": "1.0.0",
          "authors": ["smoke"]
        }
        """;

    private static string ProbeVoxels() =>
        """
        {
          "schema_version": 1,
          "voxels": [
            {
              "id": "probe",
              "name": "探测块",
              "description": "隔离热重载测试用的 Mod 体素。",
              "value": 3,
              "mass": 1,
              "max_durability": 100,
              "tags": [],
              "behaviors": [],
              "allowed_orientations": "all_24",
              "presentation": {
                "kind": "cube",
                "icon": "probe.png",
                "voxel_top": "probe.png",
                "voxel_side": "probe.png",
                "voxel_bottom": "probe.png"
              },
              "collision": { "kind": "full_box" }
            }
          ]
        }
        """;
}
