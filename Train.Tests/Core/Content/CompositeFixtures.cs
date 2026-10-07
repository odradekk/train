// Core 内容模块测试辅助。
// 构造蓝图、Archetype、接触锚点及其引用体素的最小内存内容包。
using Train.Core.Content;
using Train.Core.Content.Voxels;
using Train.Tests.Core.Logging;

namespace Train.Tests.Core.Content;

/// <summary>提供蓝图解析和目录发布测试需要的 JSON 片段及内存内容源。</summary>
/// <example>CompositeContentTests 用 VehiclePackage 构造三个体素和两个接触锚点，再通过 Load 验证定义发布。</example>
internal static class CompositeFixtures
{
    /// <summary>生成带构建约束及身份保留约束的 archetype JSON。</summary>
    public static string Archetype(
        string id = "dev-hollow-shell",
        int contactCount = 2,
        string coreTag = "identity_core") =>
        $"{{\"id\":\"{id}\",\"construction\":{{\"require_nonempty_six_connected\":true,\"require_declared_size\":true,\"contact_anchor_count\":{contactCount},\"contacts_must_be_distinct\":true,\"contacts_must_bind_existing_parts\":true}},\"identity_retention\":{{\"require_core_tag\":\"{coreTag}\"}}}}";

    /// <summary>生成以米为单位的位置及其绑定体素格坐标组成的接触锚点 JSON。</summary>
    public static string Contact(double x, double y, double z, int cx, int cy, int cz) =>
        $"{{\"x\":{VoxelDefinition.FormatMass(x)},\"y\":{VoxelDefinition.FormatMass(y)},\"z\":{VoxelDefinition.FormatMass(z)},\"cell\":{{\"x\":{cx},\"y\":{cy},\"z\":{cz}}}}}";

    /// <summary>生成蓝图中一个体素格、定义引用及朝向的 JSON。</summary>
    public static string Voxel(int x, int y, int z, string definition = "vehicle-shell", string orientation = "px_py") =>
        $"{{\"x\":{x},\"y\":{y},\"z\":{z},\"definition\":\"{definition}\",\"orientation\":\"{orientation}\"}}";

    /// <summary>生成蓝图 JSON；未指定体素和锚点时使用三个相邻体素及两个独立锚点。</summary>
    public static string Composite(
        string id = "dev-hollow-shell",
        string name = "开发空心壳",
        string description = "测试车辆",
        string archetype = "dev-hollow-shell",
        string size = "small",
        string? voxels = null,
        string? contacts = null)
    {
        voxels ??= string.Join(",",
            Voxel(0, 0, 0),
            Voxel(1, 0, 0, "identity-core"),
            Voxel(2, 0, 0));
        contacts ??= string.Join(",",
            Contact(1, -0.75, 1.5, 1, 0, 0),
            Contact(2, -0.75, 1.5, 2, 0, 0));
        return $"{{\"id\":\"{id}\",\"name\":\"{name}\",\"description\":\"{description}\",\"archetype\":\"{archetype}\",\"size_class\":\"{size}\",\"voxels\":[{voxels}],\"contacts\":[{contacts}]}}";
    }

    /// <summary>将 archetype 和蓝图片段包装成 schema 1 内容文件；null 表示省略显示名。</summary>
    public static string File(string? displayName, string archetypes, string composites)
    {
        var display = displayName is null ? "" : $"\"display_name\":\"{displayName}\",";
        return $"{{\"schema_version\":1,{display}\"archetypes\":[{archetypes}],\"composites\":[{composites}]}}";
    }

    /// <summary>创建包含壳体、身份核心和最小蓝图的内存内容包，供引用与重载测试使用。</summary>
    public static MemoryContentSource VehiclePackage()
    {
        var source = ContentFixtures.ValidFerrousOre();
        source.AddVoxelFile("core", "voxels/vehicle-shell.voxels.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good("vehicle-shell", "壳体", "壳体说明", value: 10, tagsJson: "[]")));
        source.AddVoxelFile("core", "voxels/identity-core.voxels.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good("identity-core", "核心", "核心说明", value: 10, tagsJson: "[\"identity_core\"]")));
        ContentFixtures.AddSquareImages(source, "iron.png");
        source.AddCompositeFile("core", "composites/dev-hollow-shell.composites.json", File(null, Archetype(), Composite()));
        return source;
    }

    /// <summary>加载指定的内存内容包并返回已发布的目录和日志会话；null 使用最小蓝图包。</summary>
    /// <exception cref="InvalidOperationException">测试输入无法发布为有效内容目录。</exception>
    public static (ContentReloader Reloader, LoggerSession Logger) Load(MemoryContentSource? source = null)
    {
        var session = LoggerSession.Create();
        var reloader = ContentFixtures.Create(source ?? VehiclePackage(), session.Logger);
        var result = reloader.Reload();
        if (!result.IsCommitted)
            throw new InvalidOperationException("Composite fixture catalog failed to commit.");
        return (reloader, session);
    }
}
