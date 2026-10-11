// Core 内容测试夹具：提供最小体素、Mod 与图片输入。
// 通过内存内容源提供确定的文件字节和解码结果。
using Train.Content;
using Train.Content.Voxels;


namespace Train.Content.Tests;

internal static class ContentFixtures
{
    public static MemoryContentSource CorePackage(bool available = true)
    {
        var source = new MemoryContentSource();
        source.AddPackage("core", available: available);
        return source;
    }

    public static MemoryContentSource ValidFerrousOre()
    {
        var source = CorePackage();
        source.AddDefinitionFile("core", "voxels/ferrous-ore.voxels.json", GoodsFile(Good("ferrous-ore", "Ferrous Ore", "说明")));
        AddSquareImages(source, "iron.png");
        return source;
    }

    public static ContentReloader Create(IContentSource source, IContentLogger logger) => new(source, logger);

    public static void AddSquareImages(MemoryContentSource source, params string[] paths)
    {
        foreach (var path in paths)
            source.AddImage("core", path, Square(16));
    }

    public static DecodedImage Square(int size, byte value = 1) =>
        new(size, size, "rgba8", Enumerable.Repeat(value, size * size * 4).ToArray());

    public static DecodedImage Rectangle(int width, int height) =>
        new(width, height, "rgba8", new byte[width * height * 4]);

    public static string GoodsFile(params string[] voxels) =>
        $"{{\"schema_version\":2,\"voxels\":[{string.Join(",", voxels)}]}}";

    public static string Good(
        string id,
        string name,
        string description,
        long value = 100,
        double mass = 1,
        int maxDurability = 100,
        string? icon = "iron.png",
        string? voxel = "iron.png",
        string? top = null,
        string? side = null,
        string? bottom = null,
        string tagsJson = "[\"raw_good\"]",
        string behaviorsJson = "[]",
        string orientations = "all_24",
        string collisionJson = "{\"kind\":\"full_box\"}")
    {
        var presentation = icon is null
            ? "null"
            : $"{{\"kind\":\"cube\",\"icon\":{Json(icon)},\"voxel_top\":{Json(top ?? voxel ?? icon)},\"voxel_side\":{Json(side ?? voxel ?? icon)},\"voxel_bottom\":{Json(bottom ?? voxel ?? icon)}}}";
        return $"{{\"id\":{Json(id)},\"name\":{Json(name)},\"description\":{Json(description)},\"value\":{value},\"mass\":{VoxelDefinition.FormatMass(mass)},\"max_durability\":{maxDurability},\"tags\":{tagsJson},\"behaviors\":{behaviorsJson},\"allowed_orientations\":{Json(orientations)},\"presentation\":{presentation},\"collision\":{collisionJson}}}";
    }

    public static string LocalBoxCollision(
        double offsetX = 0.125,
        double offsetY = 0,
        double offsetZ = 0.25,
        double sizeX = 0.25,
        double sizeY = 0.875,
        double sizeZ = 0.5) =>
        $"{{\"kind\":\"local_box\",\"offset\":{{\"x\":{VoxelDefinition.FormatMass(offsetX)},\"y\":{VoxelDefinition.FormatMass(offsetY)},\"z\":{VoxelDefinition.FormatMass(offsetZ)}}},\"size\":{{\"x\":{VoxelDefinition.FormatMass(sizeX)},\"y\":{VoxelDefinition.FormatMass(sizeY)},\"z\":{VoxelDefinition.FormatMass(sizeZ)}}}}}";


    public static string ManyGoods(int fromInclusive, int toInclusive)
    {
        var voxels = Enumerable.Range(fromInclusive, toInclusive - fromInclusive + 1)
            .Select(i => Good($"g{i:D5}", "矿", "说明"));
        return GoodsFile(voxels.ToArray());
    }

    private static string Json(string value)
    {
        var escaped = value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\0", "\\u0000");
        return $"\"{escaped}\"";
    }
}

internal sealed class SwitchableContentSource(IContentSource current) : IContentSource
{
    public IContentSource Current { get; set; } = current;
    public IReadOnlyList<ContentPackage> ListPackages() => Current.ListPackages();
    public IReadOnlyList<ContentDiagnostic> ListScanDiagnostics() => Current.ListScanDiagnostics();
    public ContentFileDiscovery ListDefinitionFiles(ContentPackage package) => Current.ListDefinitionFiles(package);
    public ContentBytesResult ReadFile(ContentPackage package, string normalizedRelativePath, int maxBytes) => Current.ReadFile(package, normalizedRelativePath, maxBytes);
    public ImageDecodeResult DecodePng(ContentPackage package, string normalizedRelativePath) => Current.DecodePng(package, normalizedRelativePath);
    public ImageDecodeResult DecodePng(ContentPackage package, string normalizedRelativePath, byte[] rawBytes) => Current.DecodePng(package, normalizedRelativePath, rawBytes);
}
