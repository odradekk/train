using Train.Content;
using Train.Content.Composites;
using Train.Content.Voxels;

namespace Train.Content.Tests;

internal sealed record PublishedContentExample(
    long Generation, int VoxelCount, int BlueprintCount, BlueprintAttributesExample? Blueprint);

internal sealed record BlueprintAttributesExample(double Mass, long BomValue, int BomUnits, long StructuralDurabilityMax);

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

    public static readonly ReloadResultExample InitialRejected = new(
        ContentReloadStatus.Rejected, false, null, ContentDiagnosticCodes.BuiltInPackageUnavailable);

    public static readonly ReloadResultExample FirstPublish = new(ContentReloadStatus.Committed, true, InitialContent, null);

    public static readonly ReloadResultExample Unchanged = new(ContentReloadStatus.Committed, false, InitialContent, null);

    public static readonly ReloadResultExample DependencyUpdated = new(ContentReloadStatus.Committed, true, UpdatedContent, null);

    public static readonly ReloadResultExample InvalidBuiltIn = new(
        ContentReloadStatus.Rejected, false, InitialContent, ContentDiagnosticCodes.MassInvalid);

    public static readonly ReloadResultExample OptionalPackageIsolated = new(
        ContentReloadStatus.CommittedWithWarnings, true, InitialContent, ContentDiagnosticCodes.MassInvalid);

    public static readonly ReloadResultExample AllDefinitionsRemoved = new(ContentReloadStatus.Committed, true, EmptyContent, null);

    public static readonly ReloadResultExample ConcurrentOrReentrant = new(
        ContentReloadStatus.Rejected, false, InitialContent, ContentDiagnosticCodes.ReloadAlreadyInProgress);

    public static readonly ReloadResultExample CollisionUpdated = new(
        ContentReloadStatus.Committed, true, InitialContent with { Generation = 2 }, null);
    public static readonly (bool VoxelSemantic, bool VoxelPublication, bool BlueprintSemantic, bool BlueprintPublication)
        CollisionRevisionChanges = (false, true, false, false);

    public static readonly byte[] PixelInput = { 1, 2, 3, 4 };
    public static readonly DecodedImage Image = new(1, 1, "rgba8", PixelInput);
}
