using System.Collections.Immutable;
using Train.Core.Contracts;
using Train.Content.Contracts;

namespace Train.Content.ContractSupport;

public sealed class FixedContentView : IContentView
{
    public FixedContentView() { }

    public FixedContentView(CoreContentProjection core, ContentGeneration generation = default,
        ContentRevision persistentRevision = default, ImmutableArray<PackageStatus> packages = default)
    {
        Core = core;
        Generation = generation;
        PersistentRevision = persistentRevision;
        Packages = packages.IsDefault ? [] : packages;
    }

    private ContentGeneration? generation;
    public ContentGeneration Generation
    {
        get => generation ?? throw Unconfigured(nameof(Generation));
        init => generation = value;
    }

    private ContentRevision? persistentRevision;
    public ContentRevision PersistentRevision
    {
        get => persistentRevision ?? throw Unconfigured(nameof(PersistentRevision));
        init => persistentRevision = value;
    }

    private CoreContentProjection? core;
    public CoreContentProjection Core
    {
        get => core ?? throw Unconfigured(nameof(Core));
        init => core = value;
    }

    private ImmutableArray<PackageStatus> packages;
    public ImmutableArray<PackageStatus> Packages
    {
        get => packages.IsDefault ? throw Unconfigured(nameof(Packages)) : packages;
        init => packages = value;
    }

    public ImmutableArray<VoxelCatalogEntry> Voxels => throw Unconfigured(nameof(Voxels));

    public ImmutableArray<BlueprintSummary> Blueprints => throw Unconfigured(nameof(Blueprints));

    public ImmutableArray<SavedContentDraft> LocalDrafts => throw Unconfigured(nameof(LocalDrafts));

    public ImmutableArray<GenerationProfileSummary> GenerationProfiles => throw Unconfigured(nameof(GenerationProfiles));

    public ContentLookup<BlueprintRecord> FindBlueprint(BlueprintAddress address) => throw Unconfigured(nameof(FindBlueprint));

    private ImmutableDictionary<DesignHash, ContentLookup<DesignEvaluation>> designs = ImmutableDictionary<DesignHash, ContentLookup<DesignEvaluation>>.Empty;
    public IReadOnlyDictionary<DesignHash, ContentLookup<DesignEvaluation>> Designs
    {
        init => designs = value.ToImmutableDictionary();
    }
    public ContentLookup<DesignEvaluation> FindDesign(DesignHash id)
        => designs.TryGetValue(id, out var result) ? result : throw Unconfigured(nameof(FindDesign));

    private ImmutableDictionary<VoxelDefinitionId, ContentLookup<VoxelAppearanceProjection>> voxelAppearances = ImmutableDictionary<VoxelDefinitionId, ContentLookup<VoxelAppearanceProjection>>.Empty;
    public IReadOnlyDictionary<VoxelDefinitionId, ContentLookup<VoxelAppearanceProjection>> VoxelAppearances
    {
        init => voxelAppearances = value.ToImmutableDictionary();
    }
    public ContentLookup<VoxelAppearanceProjection> FindVoxelAppearance(VoxelDefinitionId id)
        => voxelAppearances.TryGetValue(id, out var result) ? result : throw Unconfigured(nameof(FindVoxelAppearance));

    public ContentLookup<VoxelDisplayProjection> FindVoxelDisplay(VoxelDefinitionId id) => throw Unconfigured(nameof(FindVoxelDisplay));

    public ContentLookup<ContentDisplay> FindDefinitionDisplay(CoreDefinitionReference id) => throw Unconfigured(nameof(FindDefinitionDisplay));

    public ContentLookup<GoodPresentation> FindGood(GoodId id) => throw Unconfigured(nameof(FindGood));

    public ContentLookup<ContentAsset> FindAsset(AssetRevision id) => throw Unconfigured(nameof(FindAsset));

    public ContentLookup<SettlementLayoutProjection> FindSettlementLayout(SettlementId id) => throw Unconfigured(nameof(FindSettlementLayout));

    public ContentLookup<CreatureAppearanceProjection> FindCreature(CreatureDefinitionId id) => throw Unconfigured(nameof(FindCreature));

    public ContentLookup<LairAppearanceProjection> FindLair(LairTypeId id) => throw Unconfigured(nameof(FindLair));

    public ContentLookup<TerrainAppearanceProjection> FindTerrain(TerrainTypeId id) => throw Unconfigured(nameof(FindTerrain));

    public ContentLookup<RailwayAppearanceProjection> FindRailwayAppearance(RuleDefinitionId id) => throw Unconfigured(nameof(FindRailwayAppearance));


    private static InvalidOperationException Unconfigured(string member)
        => new($"UNCONFIGURED: {member}");
}
