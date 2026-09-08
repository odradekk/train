using Godot;
using Train.Content;
using Train.Domain.Voxels;

namespace Train;

internal sealed class VoxelLiveReloadDriver
{
    public const string ProbeNamespace = "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee";
    public const string ProbeLocalId = "probe";
    public const string ProbeFolder = "voxel-reload-probe";

    private readonly string _userDataDirectory;
    private readonly string _builtInResourcesRoot;

    public VoxelDefinitionId ProbeId { get; } = new(ProbeNamespace, ProbeLocalId);

    public VoxelLiveReloadDriver(string userDataDirectory, string builtInResourcesRoot)
    {
        ArgumentException.ThrowIfNullOrEmpty(userDataDirectory);
        ArgumentException.ThrowIfNullOrEmpty(builtInResourcesRoot);
        if (builtInResourcesRoot.StartsWith("res://", StringComparison.Ordinal))
            throw new InvalidOperationException("Live-reload test writes must use isolated physical content, not res://.");
        _userDataDirectory = userDataDirectory;
        _builtInResourcesRoot = builtInResourcesRoot;
    }

    public void WriteIronOre(
        int maxDurability = 100,
        long value = 10,
        double mass = 1,
        string name = "铁矿",
        string description = "开发用占位自然资源种类，用于验证内容加载、校验与热重载。",
        string collisionKind = "full_box")
    {
        WriteIsolatedText(IronOreJsonRelative, IronOreJson(maxDurability, value, mass, name, description, collisionKind));
    }

    public void WriteAsymmetricCubePngs()
    {
        WriteAsymmetricPng(IronOreTopRelative, new Color(0.95f, 0.82f, 0.12f), new Color(0.12f, 0.78f, 0.88f));
        WriteAsymmetricPng(IronOreSideRelative, new Color(0.18f, 0.86f, 0.28f), new Color(0.86f, 0.18f, 0.72f));
    }

    private void WriteAsymmetricPng(string relativePath, Color strip, Color block)
    {
        var image = Image.CreateEmpty(16, 16, false, Image.Format.Rgba8);
        image.Fill(new Color(0.42f, 0.18f, 0.10f));
        for (var x = 0; x < 16; x++)
            for (var y = 0; y < 4; y++)
                image.SetPixel(x, y, strip);
        for (var x = 12; x < 16; x++)
            for (var y = 0; y < 8; y++)
                image.SetPixel(x, y, block);
        var path = IsolatedPath(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var error = image.SavePng(path);
        if (error != Error.Ok)
            throw new InvalidOperationException($"Failed to write isolated {relativePath}: {error}.");
    }

    public void WriteProbeMod()
    {
        var root = Path.Combine(_userDataDirectory, "mods", ProbeFolder);
        var resources = Path.Combine(root, "resources");
        Directory.CreateDirectory(resources);
        File.WriteAllText(Path.Combine(root, ModDiscovery.ManifestFileName), ProbeManifest());
        File.WriteAllText(Path.Combine(resources, "probe.voxels.json"), ProbeVoxels());
        File.WriteAllBytes(Path.Combine(resources, "probe.png"), File.ReadAllBytes(IsolatedPath(IronOreTopRelative)));
    }

    public void InvalidateProbeMod()
    {
        var voxels = Path.Combine(_userDataDirectory, "mods", ProbeFolder, "resources", "probe.voxels.json");
        File.WriteAllText(voxels, "{ \"schema_version\": 2, \"voxels\": [] }");
    }

    public static ContentReloadResult Reload(Main main) =>
        main.Reloader?.Reload() ?? throw new InvalidOperationException("Content reloader is unavailable.");

    private void WriteIsolatedText(string relativePath, string text)
    {
        var path = IsolatedPath(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private string IsolatedPath(string relativePath) =>
        Path.Combine(_builtInResourcesRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private const string IronOreJsonRelative = "voxels/iron-ore.voxels.json";
    private const string IronOreTopRelative = "voxels/iron-ore/top.png";
    private const string IronOreSideRelative = "voxels/iron-ore/side.png";

    private static string IronOreJson(
        int maxDurability,
        long value,
        double mass,
        string name,
        string description,
        string collisionKind) =>
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
              "collision": { "kind": "{{collisionKind}}" }
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
