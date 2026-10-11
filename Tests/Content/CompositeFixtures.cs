// Content 内容模块测试辅助。
// 构造 schema 2 蓝图及其引用体素的最小内存内容包。
using Train.Content;
using Train.Content.Voxels;
using Train.Content.Tests;

namespace Train.Content.Tests;

/// <summary>提供蓝图解析、属性重算和目录发布测试需要的 JSON 片段及内存内容源。</summary>
internal static class CompositeFixtures
{
    public const string StructureId = "vehicle-shell";
    public const string PropulsionId = "propulsion-unit";
    public const string ControlId = "control-unit";
    public const string CargoId = "cargo-hold";

    /// <summary>生成蓝图中一个体素格、定义引用及朝向的 JSON。</summary>
    public static string Voxel(int x, int y, int z, string definition = StructureId, string orientation = "px_py") =>
        $"{{\"x\":{x},\"y\":{y},\"z\":{z},\"definition\":\"{definition}\",\"orientation\":\"{orientation}\"}}";

    /// <summary>生成 schema 2 蓝图；未指定布局时使用四种最小部件各一格。</summary>
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

    /// <summary>将蓝图片段包装成 schema 2 内容文件；null 表示省略文件显示名。</summary>
    public static string File(string? displayName, string composites)
    {
        var display = displayName is null ? "" : $"\"display_name\":\"{displayName}\",";
        return $"{{\"schema_version\":2,{display}\"composites\":[{composites}]}}";
    }

    /// <summary>创建包含结构、动力、控制、货舱和最小蓝图的内存内容包。</summary>
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

    /// <summary>向 core 包加入一个使用共享测试图片的体素定义。</summary>
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

    /// <summary>加载指定内存内容包并返回已发布目录和诊断记录器；null 使用最小蓝图包。</summary>
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
