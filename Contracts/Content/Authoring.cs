using System.Collections.Immutable;
using Train.Core.Contracts;

namespace Train.Content.Contracts;

public abstract record DraftTarget
{
    private DraftTarget() { }
    public sealed record Instance(InstanceId Id) : DraftTarget;
    public sealed record LocalBlueprint(BlueprintId? Id) : DraftTarget;
    public sealed record UserBlueprint(BlueprintId? Id) : DraftTarget;
    public sealed record PackageDefinition(PackageId Package, DefinitionKind Kind,
        string LocalId, PackageSourceVersion SourceVersion) : DraftTarget;
}
public abstract record DraftPayload
{
    private DraftPayload() { }
    public sealed record Design(DesignDraft Value) : DraftPayload;
    public sealed record Definition(DefinitionDraft Value) : DraftPayload;
}
public sealed record SavedContentDraft(DraftId Id, EditRevision Revision,
    DraftTarget Target, DraftPayload Payload);

public readonly record struct AuthoringAssetPath(string Value);
public abstract record AuthoringVoxelPresentation
{
    private AuthoringVoxelPresentation() { }
    public sealed record Cube(ImmutableArray<VoxelShape> Shapes, MaterialCategory Material,
        int TileSpan, AuthoringAssetPath Top, AuthoringAssetPath Side,
        AuthoringAssetPath Bottom, AuthoringAssetPath? Front) : AuthoringVoxelPresentation;
    public sealed record Mesh(AuthoringAssetPath Model,
        ImmutableArray<MaterialCategory> SurfaceMaterials) : AuthoringVoxelPresentation;
}
public sealed record VoxelSimulationDraft(long? BaseValue, double? MassTons,
    long? MaxDurability, bool RawGood, double? FuelEnergyPerUnit,
    ImmutableArray<PartCapability> Behaviors);
public sealed record VoxelDefinitionDraft(string LocalId, DisplayText Display,
    AuthoringAssetPath? Icon, VoxelSimulationDraft? Simulation,
    AllowedVoxelOrientations Orientations, VoxelCollision Collision,
    AuthoringVoxelPresentation? Presentation, VoxelAppearanceBehaviors AppearanceBehaviors);
public sealed record SettlementTypeDraft(string LocalId, DisplayText Display,
    AuthoringAssetPath? MapSymbol, bool Major, int? Weight,
    ImmutableArray<TerrainTypeId> PreferredTerrain, long? MinimumPopulation, long? MaximumPopulation);
public sealed record LairTypeDraft(string LocalId, DisplayText Display,
    BlueprintId? SceneModel, int? GenerationWeight, ImmutableArray<WeightedCreature> Members,
    int? MaximumMembers, double? SpawnIntervalSeconds, double? RaidIntervalSeconds,
    int? RoamingRadiusHops, int? RaidRadiusHops, double? RailDamageWorkSeconds,
    long? ClearanceReward);
public sealed record TemplatePlotDraft(int Number, PlotSize Size, GridPosition At,
    VoxelOrientation Orientation, BlueprintId? PrebuiltBuilding);
public sealed record FoundingBuildingDraft(int PlotNumber, BlueprintId Building);
public sealed record SettlementTemplateDraft(string LocalId, SettlementTypeId? Type,
    int? Weight, BlueprintId? SceneModel, ImmutableArray<TemplatePlotDraft> Plots,
    ImmutableArray<FoundingBuildingDraft> FoundingBuildings,
    ImmutableArray<GoodsQuantity> Inventory, ImmutableArray<TemplateProductionPlan> ProductionPlans);
public abstract record DefinitionDraft
{
    private DefinitionDraft() { }
    public sealed record Voxel(VoxelDefinitionDraft Value) : DefinitionDraft;
    public sealed record Blueprint(string LocalId, DesignDraft Value) : DefinitionDraft;
    public sealed record SettlementType(SettlementTypeDraft Value) : DefinitionDraft;
    public sealed record LairType(LairTypeDraft Value) : DefinitionDraft;
    public sealed record SettlementTemplate(SettlementTemplateDraft Value) : DefinitionDraft;
}
public sealed record AuthoringDefinitionAddress(DefinitionKind Kind, string LocalId);
public interface IAuthoringPackage
{
    PackageId Package { get; }
    PackageSourceVersion SourceVersion { get; }
    PackageMetadata Metadata { get; }
    ImmutableArray<AuthoringDefinitionAddress> EditableDefinitions { get; }
    ContentLookup<DefinitionDraft> Read(AuthoringDefinitionAddress address);
}
public abstract record PackageEdit
{
    private PackageEdit() { }
    public sealed record WriteDefinition(DefinitionDraft Draft) : PackageEdit;
    public sealed record DeleteDefinition(AuthoringDefinitionAddress Definition) : PackageEdit;
    public sealed record WriteAsset(string PackageRelativePath, ImmutableArray<byte> Bytes) : PackageEdit;
    public sealed record DeleteAsset(string PackageRelativePath) : PackageEdit;
    public sealed record Manifest(PackageMetadata Value) : PackageEdit;
}
public sealed record PackageFileChange(string RelativePath, ImmutableArray<byte>? Bytes);
public sealed record PackageWritePlan(PackageId Package, PackageSourceVersion SourceVersion,
    ImmutableArray<PackageFileChange> Changes, ImmutableArray<ContentDiagnostic> Diagnostics);
