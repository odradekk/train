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
    private string? _identityCoreBackup;

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

    public void WriteFerrousOre(
        int maxDurability = 100,
        long value = 10,
        double mass = 1,
        string name = "Ferrous Ore",
        string description = "开发用占位初级货物，用于验证内容加载、校验与热重载。",
        string collisionKind = "full_box")
    {
        WriteIsolatedText(FerrousOreJsonRelative, FerrousOreJson(maxDurability, value, mass, name, description, collisionKind));
    }

    public void WriteAsymmetricCubePngs()
    {
        WriteAsymmetricPng(FerrousOreTopRelative, new Color(0.95f, 0.82f, 0.12f), new Color(0.12f, 0.78f, 0.88f));
        WriteAsymmetricPng(FerrousOreSideRelative, new Color(0.18f, 0.86f, 0.28f), new Color(0.86f, 0.18f, 0.72f));
    }

    public void WriteRefitMarkerMax(int maxDurability) =>
        PatchIsolatedText(RefitMarkerJsonRelative, text => ReplaceNumber(text, "max_durability", maxDurability));

    public void WriteRefitMarkerName(string name) =>
        PatchIsolatedText(RefitMarkerJsonRelative, text => ReplaceFirstString(text, "name", name));

    public void WriteAsymmetricMarkerTop() =>
        WriteAsymmetricPng(RefitMarkerTopRelative, new Color(0.95f, 0.82f, 0.12f), new Color(0.12f, 0.78f, 0.88f));

    public void WriteIdentityCoreHeavyValue(long value) =>
        PatchIsolatedText(IdentityCoreHeavyJsonRelative, text => ReplaceNumber(text, "value", value));

    public void WriteVehicleShellCollision(string kind) =>
        PatchIsolatedText(VehicleShellJsonRelative, text =>
            text.Replace("\"kind\": \"full_box\"", $"\"kind\": \"{kind}\"", StringComparison.Ordinal)
                .Replace("\"kind\":\"full_box\"", $"\"kind\":\"{kind}\"", StringComparison.Ordinal));

    public void AddHollowShellExtraCell() =>
        PatchIsolatedText(HollowShellCompositeRelative, text =>
            text.Replace(
                "{ \"x\": 0, \"y\": 0, \"z\": -1, \"definition\": \"vehicle-shell\", \"orientation\": \"px_py\" }",
                "{ \"x\": 0, \"y\": 0, \"z\": -1, \"definition\": \"vehicle-shell\", \"orientation\": \"px_py\" },\n        { \"x\": 2, \"y\": 1, \"z\": 0, \"definition\": \"vehicle-shell\", \"orientation\": \"px_py\" }",
                StringComparison.Ordinal));

    public void RemoveIdentityCore()
    {
        var path = IsolatedPath(IdentityCoreJsonRelative);
        _identityCoreBackup ??= File.ReadAllText(path);
        File.Delete(path);
    }

    public void RestoreIdentityCore()
    {
        if (_identityCoreBackup is null)
            return;
        WriteIsolatedText(IdentityCoreJsonRelative, _identityCoreBackup);
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

    public void WriteLedge(
        double offsetX = 0.125,
        double offsetY = 0,
        double offsetZ = 0.25,
        double sizeX = 0.25,
        double sizeY = 0.875,
        double sizeZ = 0.5,
        string orientations = "all_24")
    {
        WriteIsolatedText(LedgeJsonRelative, LedgeJson(
            ContentFixturesCollision(offsetX, offsetY, offsetZ, sizeX, sizeY, sizeZ),
            orientations));
    }

    public void WriteInvalidLedge()
    {
        WriteIsolatedText(LedgeJsonRelative, LedgeJson(
            ContentFixturesCollision(0.8, 0, 0.25, 0.4, 0.875, 0.5),
            "all_24"));
    }

    public void WriteProbeMod()
    {
        var root = Path.Combine(_userDataDirectory, "mods", ProbeFolder);
        var resources = Path.Combine(root, "resources");
        Directory.CreateDirectory(resources);
        File.WriteAllText(Path.Combine(root, ModDiscovery.ManifestFileName), ProbeManifest());
        File.WriteAllText(Path.Combine(resources, "probe.voxels.json"), ProbeVoxels());
        File.WriteAllBytes(Path.Combine(resources, "probe.png"), File.ReadAllBytes(IsolatedPath(FerrousOreTopRelative)));
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

    private const string FerrousOreJsonRelative = "voxels/ferrous-ore.voxels.json";
    private const string LedgeJsonRelative = "voxels/ledge.voxels.json";
    private const string FerrousOreTopRelative = "voxels/ferrous-ore/top.png";
    private const string FerrousOreSideRelative = "voxels/ferrous-ore/side.png";
    private const string RefitMarkerJsonRelative = "voxels/refit-marker.voxels.json";
    private const string RefitMarkerTopRelative = "voxels/refit-marker/top.png";
    private const string IdentityCoreJsonRelative = "voxels/identity-core.voxels.json";
    private const string IdentityCoreHeavyJsonRelative = "voxels/identity-core-heavy.voxels.json";
    private const string VehicleShellJsonRelative = "voxels/vehicle-shell.voxels.json";
    private const string HollowShellCompositeRelative = "composites/dev-hollow-shell.composites.json";

    private void PatchIsolatedText(string relativePath, Func<string, string> patch)
    {
        var path = IsolatedPath(relativePath);
        File.WriteAllText(path, patch(File.ReadAllText(path)));
    }

    private static string ReplaceNumber(string json, string field, long value) =>
        System.Text.RegularExpressions.Regex.Replace(
            json,
            $"\"{field}\":\\s*-?\\d+",
            $"\"{field}\": {value}",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static string ReplaceFirstString(string json, string field, string value)
    {
        var pattern = $"\"{field}\":\\s*\"[^\"]*\"";
        return System.Text.RegularExpressions.Regex.Replace(
            json,
            pattern,
            $"\"{field}\": \"{value}\"",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));
    }

    private static string FerrousOreJson(
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
              "id": "ferrous-ore",
              "name": "{{name}}",
              "description": "{{description}}",
              "value": {{value}},
              "mass": {{VoxelDefinition.FormatMass(mass)}},
              "max_durability": {{maxDurability}},
              "tags": ["raw_good"],
              "behaviors": [],
              "allowed_orientations": "all_24",
              "presentation": {
                "kind": "cube",
                "icon": "voxels/ferrous-ore/icon.png",
                "voxel_top": "voxels/ferrous-ore/top.png",
                "voxel_side": "voxels/ferrous-ore/side.png",
                "voxel_bottom": "voxels/ferrous-ore/bottom.png"
              },
              "collision": { "kind": "{{collisionKind}}" }
            }
          ]
        }
        """;

    private static string ContentFixturesCollision(
        double offsetX,
        double offsetY,
        double offsetZ,
        double sizeX,
        double sizeY,
        double sizeZ) =>
        $$"""
        {
                "kind": "local_box",
                "offset": {
                  "x": {{VoxelDefinition.FormatMass(offsetX)}},
                  "y": {{VoxelDefinition.FormatMass(offsetY)}},
                  "z": {{VoxelDefinition.FormatMass(offsetZ)}}
                },
                "size": {
                  "x": {{VoxelDefinition.FormatMass(sizeX)}},
                  "y": {{VoxelDefinition.FormatMass(sizeY)}},
                  "z": {{VoxelDefinition.FormatMass(sizeZ)}}
                }
              }
        """;

    private static string LedgeJson(string collision, string orientations) =>
        $$"""
        {
          "schema_version": 1,
          "voxels": [
            {
              "id": "ledge",
              "name": "偏心台沿",
              "description": "开发用单个局部碰撞盒。",
              "value": 10,
              "mass": 1,
              "max_durability": 100,
              "tags": [],
              "behaviors": [],
              "allowed_orientations": "{{orientations}}",
              "presentation": {
                "kind": "cube",
                "icon": "voxels/ferrous-ore/icon.png",
                "voxel_top": "voxels/ferrous-ore/top.png",
                "voxel_side": "voxels/ferrous-ore/side.png",
                "voxel_bottom": "voxels/ferrous-ore/bottom.png"
              },
              "collision": {{collision}}
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
