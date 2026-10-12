using System.Collections.Frozen;
using System.Collections.Immutable;
using C = Train.Content.Contracts;
using K = Train.Core.Contracts;

namespace Train.Content.Loading;

internal sealed record LoadedVoxel(ParsedVoxel Definition, C.VoxelAppearanceProjection Appearance, C.VoxelDisplayProjection Display);

internal sealed class ContentView : C.IContentView
{
    private readonly FrozenDictionary<K.VoxelDefinitionId, LoadedVoxel> _voxels;
    private readonly FrozenDictionary<C.AssetRevision, C.ContentAsset> _assets;
    public C.ContentGeneration Generation => new(1);
    public C.ContentRevision PersistentRevision => new(0);
    public K.CoreContentProjection Core { get; }
    public ImmutableArray<C.PackageStatus> Packages { get; }
    public ImmutableArray<C.VoxelCatalogEntry> Voxels { get; }
    public ImmutableArray<C.BlueprintSummary> Blueprints => [];
    public ImmutableArray<C.SavedContentDraft> LocalDrafts => [];
    public ImmutableArray<C.GenerationProfileSummary> GenerationProfiles => [];

    internal ContentView(ImmutableArray<LoadedVoxel> voxels, ImmutableArray<C.PackageStatus> packages,
        ImmutableArray<C.ContentAsset> assets)
    {
        _voxels = voxels.ToFrozenDictionary(voxel => voxel.Definition.Definition);
        _assets = assets.ToFrozenDictionary(asset => asset.Revision);
        Packages = packages;
        Voxels = voxels.Select(voxel => new C.VoxelCatalogEntry(voxel.Definition.Definition, voxel.Definition.Good)).ToImmutableArray();
        var identity = ContentHash.FromCanonicalJson(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("encoding_version", 1);
            writer.WriteStartArray("voxels");
            foreach (var voxel in voxels)
            {
                writer.WriteStartObject();
                writer.WriteString("id", voxel.Definition.Definition.Value);
                writer.WriteString("revision", voxel.Definition.Revision.Value);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        });
        Core = new(Identity: new(identity), Goods: voxels.Where(voxel => voxel.Definition.Good is not null)
            .Select(voxel => voxel.Definition.Good!).ToImmutableArray(), Designs: [], DesignGoods: [], Processes: [],
            PopulationRules: [], MarketRules: [], ConstructionRules: [], RailwayRules: [], CombatRules: [],
            SettlementTypes: [], SettlementTemplates: [], Creatures: [], LairTypes: [], MissingReferences: []);
    }

    public C.ContentLookup<C.VoxelAppearanceProjection> FindVoxelAppearance(K.VoxelDefinitionId definition) =>
        _voxels.TryGetValue(definition, out var voxel) ? new C.ContentLookup<C.VoxelAppearanceProjection>.Found(voxel.Appearance) : new C.ContentLookup<C.VoxelAppearanceProjection>.NotFound();
    public C.ContentLookup<C.VoxelDisplayProjection> FindVoxelDisplay(K.VoxelDefinitionId definition) =>
        _voxels.TryGetValue(definition, out var voxel) ? new C.ContentLookup<C.VoxelDisplayProjection>.Found(voxel.Display) : new C.ContentLookup<C.VoxelDisplayProjection>.NotFound();
    public C.ContentLookup<C.ContentDisplay> FindDefinitionDisplay(K.CoreDefinitionReference definition) =>
        definition is K.CoreDefinitionReference.Voxel reference && _voxels.TryGetValue(reference.Id, out var voxel)
            ? new C.ContentLookup<C.ContentDisplay>.Found(new(voxel.Display.Display, voxel.Display.Icon, null))
            : new C.ContentLookup<C.ContentDisplay>.NotFound();
    public C.ContentLookup<C.GoodPresentation> FindGood(K.GoodId good) =>
        good is K.GoodId.Voxel reference && _voxels.TryGetValue(reference.Definition, out var voxel) && voxel.Definition.Good is not null
            ? new C.ContentLookup<C.GoodPresentation>.Found(new(good, new(voxel.Display.Display, voxel.Display.Icon, null), null, false))
            : new C.ContentLookup<C.GoodPresentation>.NotFound();
    public C.ContentLookup<C.ContentAsset> FindAsset(C.AssetRevision revision) =>
        _assets.TryGetValue(revision, out var asset) ? new C.ContentLookup<C.ContentAsset>.Found(asset) : new C.ContentLookup<C.ContentAsset>.NotFound();
    public C.ContentLookup<C.BlueprintRecord> FindBlueprint(C.BlueprintAddress address) => new C.ContentLookup<C.BlueprintRecord>.NotFound();
    public C.ContentLookup<C.DesignEvaluation> FindDesign(K.DesignHash hash) => new C.ContentLookup<C.DesignEvaluation>.NotFound();
    public C.ContentLookup<C.SettlementLayoutProjection> FindSettlementLayout(K.SettlementId settlement) => new C.ContentLookup<C.SettlementLayoutProjection>.NotFound();
    public C.ContentLookup<C.CreatureAppearanceProjection> FindCreature(K.CreatureDefinitionId creature) => new C.ContentLookup<C.CreatureAppearanceProjection>.NotFound();
    public C.ContentLookup<C.LairAppearanceProjection> FindLair(K.LairTypeId lair) => new C.ContentLookup<C.LairAppearanceProjection>.NotFound();
    public C.ContentLookup<C.TerrainAppearanceProjection> FindTerrain(K.TerrainTypeId terrain) => new C.ContentLookup<C.TerrainAppearanceProjection>.NotFound();
    public C.ContentLookup<C.RailwayAppearanceProjection> FindRailwayAppearance(K.RuleDefinitionId rule) => new C.ContentLookup<C.RailwayAppearanceProjection>.NotFound();
}
