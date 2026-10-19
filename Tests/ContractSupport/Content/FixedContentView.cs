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

    private ImmutableArray<VoxelCatalogEntry> voxels;
    public ImmutableArray<VoxelCatalogEntry> Voxels
    {
        get => voxels.IsDefault ? throw Unconfigured(nameof(Voxels)) : voxels;
        init => voxels = value;
    }

    private ImmutableArray<BlueprintSummary> blueprints;
    public ImmutableArray<BlueprintSummary> Blueprints
    {
        get => blueprints.IsDefault ? throw Unconfigured(nameof(Blueprints)) : blueprints;
        init => blueprints = value;
    }

    private ImmutableArray<SavedContentDraft> localDrafts;
    public ImmutableArray<SavedContentDraft> LocalDrafts
    {
        get => localDrafts.IsDefault ? throw Unconfigured(nameof(LocalDrafts)) : localDrafts;
        init => localDrafts = value;
    }

    private ImmutableArray<GenerationProfileSummary> generationProfiles;
    public ImmutableArray<GenerationProfileSummary> GenerationProfiles
    {
        get => generationProfiles.IsDefault ? throw Unconfigured(nameof(GenerationProfiles)) : generationProfiles;
        init => generationProfiles = value;
    }

    private ImmutableDictionary<BlueprintAddress, ContentLookup<BlueprintRecord>> blueprintRecords = ImmutableDictionary<BlueprintAddress, ContentLookup<BlueprintRecord>>.Empty;
    public IReadOnlyDictionary<BlueprintAddress, ContentLookup<BlueprintRecord>> BlueprintRecords
    {
        init => blueprintRecords = value.ToImmutableDictionary();
    }
    public ContentLookup<BlueprintRecord> FindBlueprint(BlueprintAddress address)
        => blueprintRecords.TryGetValue(address, out var result) ? result : throw Unconfigured(nameof(FindBlueprint));

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

    private ImmutableDictionary<VoxelDefinitionId, ContentLookup<VoxelDisplayProjection>> voxelDisplays = ImmutableDictionary<VoxelDefinitionId, ContentLookup<VoxelDisplayProjection>>.Empty;
    public IReadOnlyDictionary<VoxelDefinitionId, ContentLookup<VoxelDisplayProjection>> VoxelDisplays
    {
        init => voxelDisplays = value.ToImmutableDictionary();
    }
    public ContentLookup<VoxelDisplayProjection> FindVoxelDisplay(VoxelDefinitionId id)
        => voxelDisplays.TryGetValue(id, out var result) ? result : throw Unconfigured(nameof(FindVoxelDisplay));

    private ImmutableDictionary<CoreDefinitionReference, ContentLookup<ContentDisplay>> definitionDisplays = ImmutableDictionary<CoreDefinitionReference, ContentLookup<ContentDisplay>>.Empty;
    public IReadOnlyDictionary<CoreDefinitionReference, ContentLookup<ContentDisplay>> DefinitionDisplays
    {
        init => definitionDisplays = value.ToImmutableDictionary();
    }
    public ContentLookup<ContentDisplay> FindDefinitionDisplay(CoreDefinitionReference id)
        => definitionDisplays.TryGetValue(id, out var result) ? result : throw Unconfigured(nameof(FindDefinitionDisplay));

    private ImmutableDictionary<GoodId, ContentLookup<GoodPresentation>> goods = ImmutableDictionary<GoodId, ContentLookup<GoodPresentation>>.Empty;
    public IReadOnlyDictionary<GoodId, ContentLookup<GoodPresentation>> Goods
    {
        init => goods = value.ToImmutableDictionary();
    }
    public ContentLookup<GoodPresentation> FindGood(GoodId id)
        => goods.TryGetValue(id, out var result) ? result : throw Unconfigured(nameof(FindGood));

    private ImmutableDictionary<AssetRevision, ContentLookup<ContentAsset>> assets = ImmutableDictionary<AssetRevision, ContentLookup<ContentAsset>>.Empty;
    public IReadOnlyDictionary<AssetRevision, ContentLookup<ContentAsset>> Assets
    {
        init => assets = value.ToImmutableDictionary();
    }
    public ContentLookup<ContentAsset> FindAsset(AssetRevision id)
        => assets.TryGetValue(id, out var result) ? result : throw Unconfigured(nameof(FindAsset));

    private ImmutableDictionary<SettlementId, ContentLookup<SettlementLayoutProjection>> settlementLayouts = ImmutableDictionary<SettlementId, ContentLookup<SettlementLayoutProjection>>.Empty;
    public IReadOnlyDictionary<SettlementId, ContentLookup<SettlementLayoutProjection>> SettlementLayouts
    {
        init => settlementLayouts = value.ToImmutableDictionary();
    }
    public ContentLookup<SettlementLayoutProjection> FindSettlementLayout(SettlementId id)
        => settlementLayouts.TryGetValue(id, out var result) ? result : throw Unconfigured(nameof(FindSettlementLayout));

    private ImmutableDictionary<CreatureDefinitionId, ContentLookup<CreatureAppearanceProjection>> creatures = ImmutableDictionary<CreatureDefinitionId, ContentLookup<CreatureAppearanceProjection>>.Empty;
    public IReadOnlyDictionary<CreatureDefinitionId, ContentLookup<CreatureAppearanceProjection>> Creatures
    {
        init => creatures = value.ToImmutableDictionary();
    }
    public ContentLookup<CreatureAppearanceProjection> FindCreature(CreatureDefinitionId id)
        => creatures.TryGetValue(id, out var result) ? result : throw Unconfigured(nameof(FindCreature));

    public ContentLookup<LairAppearanceProjection> FindLair(LairTypeId id) => throw Unconfigured(nameof(FindLair));

    private ImmutableDictionary<TerrainTypeId, ContentLookup<TerrainAppearanceProjection>> terrains = ImmutableDictionary<TerrainTypeId, ContentLookup<TerrainAppearanceProjection>>.Empty;
    public IReadOnlyDictionary<TerrainTypeId, ContentLookup<TerrainAppearanceProjection>> Terrains
    {
        init => terrains = value.ToImmutableDictionary();
    }
    public ContentLookup<TerrainAppearanceProjection> FindTerrain(TerrainTypeId id)
        => terrains.TryGetValue(id, out var result) ? result : throw Unconfigured(nameof(FindTerrain));

    private ImmutableDictionary<RuleDefinitionId, ContentLookup<RailwayAppearanceProjection>> railwayAppearances = ImmutableDictionary<RuleDefinitionId, ContentLookup<RailwayAppearanceProjection>>.Empty;
    public IReadOnlyDictionary<RuleDefinitionId, ContentLookup<RailwayAppearanceProjection>> RailwayAppearances
    {
        init => railwayAppearances = value.ToImmutableDictionary();
    }
    public ContentLookup<RailwayAppearanceProjection> FindRailwayAppearance(RuleDefinitionId id)
        => railwayAppearances.TryGetValue(id, out var result) ? result : throw Unconfigured(nameof(FindRailwayAppearance));


    private static InvalidOperationException Unconfigured(string member)
        => new($"UNCONFIGURED: {member}");
}
