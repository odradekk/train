// 统一内容发布的数据例子；功能断言位于 ContentPublicationTests。
// 预期由输入和两格结构体素的手算结果给定，不通过被测重载器计算。
using Train.Content;
using Train.Content.Composites;
using Train.Content.Voxels;

namespace Train.Content.Tests;

/// <summary>一个已发布内容批次的可观察数据例子；索引为空时 Blueprint 为 null。</summary>
/// <remarks>结构；Generation 为正整数，计数为非负整数。两格结构各 2 t、Value 10、耐久 20，故总计 4 t、Value 20、结构耐久 40。</remarks>
internal sealed record PublishedContentExample(
    long Generation, int VoxelCount, int BlueprintCount, BlueprintAttributesExample? Blueprint);

/// <summary>蓝图属性的手算例子：质量单位为 t，BOM 数量为货物单位，Value 为基本货币。</summary>
internal sealed record BlueprintAttributesExample(double Mass, long BomValue, int BomUnits, long StructuralDurabilityMax);

/// <summary>一次重载应报告的状态、发布效果、保留内容及应出现的诊断类别。</summary>
/// <remarks>结构；Snapshot 为 null 是尚无成功发布，非 null 是完整已发布批次。Changed 只在实际发布新批次时为 true。</remarks>
internal sealed record ReloadResultExample(
    ContentReloadStatus Status, bool Changed, PublishedContentExample? Snapshot, string? DiagnosticCode);

internal static class ContentReloadExamples
{
    public static readonly VoxelDefinitionId StructureId = new("core", "structure");
    public static readonly CompositeDefinitionId BlueprintId = new("core", "pair");

    public static readonly string StructureFile = ContentFixtures.GoodsFile(
        ContentFixtures.Good("structure", "结构", "两格蓝图的材料", value: 10, mass: 2,
            maxDurability: 20, tagsJson: "[]", orientations: "none"));

    public static readonly string UpdatedStructureFile = ContentFixtures.GoodsFile(
        ContentFixtures.Good("structure", "结构", "两格蓝图的材料", value: 15, mass: 3,
            maxDurability: 20, tagsJson: "[]", orientations: "none"));

    public static readonly string CollisionStructureFile = ContentFixtures.GoodsFile(
        ContentFixtures.Good("structure", "结构", "两格蓝图的材料", value: 10, mass: 2,
            maxDurability: 20, tagsJson: "[]", orientations: "none", collisionJson: ContentFixtures.LocalBoxCollision()));

    public static readonly string BlueprintFile = $$"""
        {
          "schema_version": 2,
          "composites": [{
            "id": "pair", "name": "两格蓝图", "description": "同一种结构消耗两单位", "size_class": "small",
            "voxels": [
              { "x": 0, "y": 0, "z": 0, "definition": "core:structure", "orientation": "{{VoxelOrientation.Identity}}" },
              { "x": 1, "y": 0, "z": 0, "definition": "core:structure", "orientation": "{{VoxelOrientation.Identity}}" }
            ]
          }]
        }
        """;

    public static readonly PublishedContentExample InitialContent = new(1, 1, 1, new(4, 20, 2, 40));
    public static readonly PublishedContentExample UpdatedContent = new(2, 1, 1, new(6, 30, 2, 40));
    public static readonly PublishedContentExample EmptyContent = new(2, 0, 0, null);

    // 无内置包；没有可保留的已发布快照。
    public static readonly ReloadResultExample InitialRejected = new(
        ContentReloadStatus.Rejected, false, null, ContentDiagnosticCodes.BuiltInPackageUnavailable);

    // 尚无快照，输入 StructureFile 和 BlueprintFile，第一次成功发布。
    public static readonly ReloadResultExample FirstPublish = new(ContentReloadStatus.Committed, true, InitialContent, null);

    // 已发布 InitialContent；再次提供相同数据，包括只改变 JSON 排版的情况。
    public static readonly ReloadResultExample Unchanged = new(ContentReloadStatus.Committed, false, InitialContent, null);

    // 已发布 InitialContent；仅替换 UpdatedStructureFile。两个格的属性与体素一起进入世代 2。
    public static readonly ReloadResultExample DependencyUpdated = new(ContentReloadStatus.Committed, true, UpdatedContent, null);

    // 已发布 InitialContent；内置体素质量无效，保留整个世代 1。
    public static readonly ReloadResultExample InvalidBuiltIn = new(
        ContentReloadStatus.Rejected, false, InitialContent, ContentDiagnosticCodes.MassInvalid);

    // 首次加载有效内置内容和质量无效的可选 Mod；Mod 的全部定义被隔离。
    public static readonly ReloadResultExample OptionalPackageIsolated = new(
        ContentReloadStatus.CommittedWithWarnings, true, InitialContent, ContentDiagnosticCodes.MassInvalid);

    // 已发布 InitialContent；删除全部定义文件，合法空候选作为世代 2 发布。
    public static readonly ReloadResultExample AllDefinitionsRemoved = new(ContentReloadStatus.Committed, true, EmptyContent, null);

    // 已发布 InitialContent；另一轮重载正在构建候选或通知订阅者。
    public static readonly ReloadResultExample ConcurrentOrReentrant = new(
        ContentReloadStatus.Rejected, false, InitialContent, ContentDiagnosticCodes.ReloadAlreadyInProgress);

    // 只替换 CollisionStructureFile；碰撞属于表现，蓝图属性仍是 4 t、Value 20、结构耐久 40。
    public static readonly ReloadResultExample CollisionUpdated = new(
        ContentReloadStatus.Committed, true, InitialContent with { Generation = 2 }, null);
    public static readonly (bool VoxelSemantic, bool VoxelPublication, bool BlueprintSemantic, bool BlueprintPublication)
        CollisionRevisionChanges = (false, true, false, false);

    // 图像拥有像素副本。后续修改 PixelInput 不得改变 Image 中的 1、2、3、4。
    public static readonly byte[] PixelInput = { 1, 2, 3, 4 };
    public static readonly DecodedImage Image = new(1, 1, "rgba8", PixelInput);
}
