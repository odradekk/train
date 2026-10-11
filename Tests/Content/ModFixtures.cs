// Content 内容测试夹具：提供最小 Mod 清单和体素包输入。
// 供当前 Content 内容测试共用，不读取玩家目录中的外部 Mod。
using Train.Content;
using Train.Content.Voxels;

namespace Train.Content.Tests;

internal static class ModFixtures
{
    public const string SampleUuid = "550e8400-e29b-41d4-a716-446655440000";
    public const string OtherUuid = "11111111-2222-4333-8444-555555555555";

    public static MemoryContentSource CorePlusCopper()
    {
        var source = ContentFixtures.ValidFerrousOre();
        source.AddMod("pretty-name", SampleUuid);
        source.AddDefinitionFile(SampleUuid, "natural/copper.voxels.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good("copper", "铜", "铜说明", icon: "copper.png", voxel: "copper.png")));
        source.AddImage(SampleUuid, "copper.png", ContentFixtures.Square(16));
        return source;
    }

    public static string Manifest(
        string id,
        string name = "铜矿扩展",
        string version = "1.0.0",
        string[]? authors = null,
        string? description = "可选说明")
    {
        authors ??= new[] { "作者甲" };
        var authorJson = string.Join(",", authors.Select(Json));
        var descriptionJson = description is null ? "" : $",\"description\":{Json(description)}";
        return $"{{\"schema_version\":1,\"id\":{Json(id)},\"name\":{Json(name)},\"version\":{Json(version)},\"authors\":[{authorJson}]{descriptionJson}}}";
    }

    private static string Json(string value)
    {
        var escaped = value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        return $"\"{escaped}\"";
    }
}
