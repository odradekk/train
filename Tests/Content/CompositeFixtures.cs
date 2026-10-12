using Train.Content;
using Train.Content.Voxels;
using Train.Content.Tests;

namespace Train.Content.Tests;

internal static class CompositeFixtures
{
    public const string StructureId = "vehicle-shell";
    public const string PropulsionId = "propulsion-unit";
    public const string ControlId = "control-unit";
    public const string CargoId = "cargo-hold";

    public static string Voxel(int x, int y, int z, string definition = StructureId, string orientation = "px_py") =>
        $"{{\"x\":{x},\"y\":{y},\"z\":{z},\"definition\":\"{definition}\",\"orientation\":\"{orientation}\"}}";

    public static string Composite(
        string id = "dev-train",
        string name = "开发列车",
        string description = "测试车辆",
        string size = "small",
        string? voxels = null)
    {
        voxels ??= string.Join(",",
            Voxel(0, 0, 0),
            Voxel(1, 0, 0, PropulsionId),
            Voxel(2, 0, 0, ControlId),
            Voxel(3, 0, 0, CargoId));
        return $"{{\"id\":\"{id}\",\"name\":\"{name}\",\"description\":\"{description}\",\"size_class\":\"{size}\",\"voxels\":[{voxels}]}}";
    }

    public static string File(string? displayName, string composites)
    {
        var display = displayName is null ? "" : $"\"display_name\":\"{displayName}\",";
        return $"{{\"schema_version\":2,{display}\"composites\":[{composites}]}}";
    }

    public static MemoryContentSource VehiclePackage()
    {
        var source = ContentFixtures.ValidFerrousOre();
        AddVoxel(source, StructureId, "壳体", 10, 1, 100);
        AddVoxel(source, PropulsionId, "动力", 30, 3,
            behaviorsJson: "[{\"type\":\"propulsion\",\"traction\":12.5,\"rated_speed\":20}]");
        AddVoxel(source, ControlId, "控制", 20, 2,
            behaviorsJson: "[{\"type\":\"control\"}]");
        AddVoxel(source, CargoId, "货舱", 40, 4,
            behaviorsJson: "[{\"type\":\"cargo\",\"capacity\":4}]");
        ContentFixtures.AddSquareImages(source, "iron.png");
        source.AddDefinitionFile("core", "composites/dev-train.composites.json", File(null, Composite()));
        return source;
    }

    public static void AddVoxel(
        MemoryContentSource source,
        string id,
        string name,
        long value,
        double mass,
        int maxDurability = 100,
        string behaviorsJson = "[]",
        string orientations = "all_24") =>
        source.AddDefinitionFile("core", $"voxels/{id}.voxels.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good(id, name, name + "说明", value, mass, maxDurability,
                tagsJson: "[]", behaviorsJson: behaviorsJson, orientations: orientations)));

    public static (ContentReloader Reloader, RecordingContentLogger Logger) Load(MemoryContentSource? source = null)
    {
        var session = new RecordingContentLogger();
        var reloader = ContentFixtures.Create(source ?? VehiclePackage(), session);
        var result = reloader.Reload();
        if (!result.IsCommitted)
            throw new InvalidOperationException("Composite fixture catalog failed to commit.");
        return (reloader, session);
    }
}
