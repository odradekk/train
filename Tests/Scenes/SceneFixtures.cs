using System.Collections.Immutable;
using System.Numerics;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes.Tests;

internal static class SceneFixtures
{
    internal static readonly VoxelDefinitionId Voxel = new("test:cube");
    internal static readonly DesignHash Design = new("test-design");
    internal static readonly WorldObjectId Subject = new WorldObjectId.Train(new TrainId(1));
    internal static readonly ScenePose Identity = new(Vector3.Zero, Quaternion.Identity);
    internal static VoxelAppearanceProjection Cube => new(Voxel, AllowedVoxelOrientations.All24,
        new VoxelCollision.None(), new VoxelPresentation.Cube([], MaterialCategory.PaintedMetal, 1,
            new("tile"), new("tile"), new("tile"), new("tile")), new(false, null));
    internal static VoxelAppearanceProjection TexturedCube(int span, bool front = false) => Cube with
    {
        Presentation = new VoxelPresentation.Cube([], MaterialCategory.PaintedMetal, span,
            new("top"), new("side"), new("bottom"), new(front ? "front" : "side"))
    };
    internal static FixedContentView Content(DesignEvaluation? evaluation = null, DesignHash? design = null,
        VoxelAppearanceProjection? appearance = null) => new()
    {
        Generation = new(1),
        Core = new(new("scenes-test"), [], [], [], [], [], [], [], [], [], [], [], [], [], []),
        Designs = evaluation is null ? new Dictionary<DesignHash, ContentLookup<DesignEvaluation>>() :
            new Dictionary<DesignHash, ContentLookup<DesignEvaluation>> { [design ?? Design] = new ContentLookup<DesignEvaluation>.Found(evaluation) },
        VoxelAppearances = new Dictionary<VoxelDefinitionId, ContentLookup<VoxelAppearanceProjection>>
        { [Voxel] = new ContentLookup<VoxelAppearanceProjection>.Found(appearance ?? Cube) }
    };
    internal static SceneVoxelProjection Cell(GridPosition at, int number = 1, bool missing = false,
        ImmutableArray<int> path = default, VoxelShape? shape = null) => new(at, Voxel,
        VoxelOrientation.PxPy, shape, null, new(number), new DesignElementPath.Cell([], number - 1),
        path.IsDefault ? [] : path, at, VoxelOrientation.PxPy, missing);
    internal static DesignSceneProjection Projection(ImmutableArray<SceneVoxelProjection> cells,
        ImmutableArray<NestedObjectProjection> nested = default) => new(Design, null, null, true, null, null,
        cells, cells.Select(cell => new DesignElementProjection(cell.Number, cell.Element,
            new ComputedValue<ImmutableArray<GridPosition>>.Known([cell.At]), false, cell.Missing)).ToImmutableArray(),
        nested.IsDefault ? [] : nested, [], []);
    internal static DesignDraft Draft => new(new("Test", ""), new DesignSpecification.SceneModel(null), new([], [], []));
    internal static DesignEvaluation.Complete Evaluation(DesignSceneProjection projection,
        ImmutableArray<ContentDiagnostic> diagnostics = default) => new(Design, new("composition"), Draft, null,
            projection, true, diagnostics.IsDefault ? [] : diagnostics);
    internal static SceneFrame Frame(RenderedDesign geometry, ImmutableArray<SceneBounds> fog = default) => new(
        new(new(Guid.NewGuid()), new(1), new(1), 1), new(0, 0, 0), 0, SceneReadiness.Ready,
        geometry.Objects, geometry.Picking, fog.IsDefault ? [] : fog.Select(bounds => new SceneFog(bounds)).ToImmutableArray(), geometry.Diagnostics);
    internal static RenderedDesign Build(DesignSceneProjection projection, ScenePose? pose = null,
        IContentView? content = null, ImmutableArray<ContentDiagnostic> diagnostics = default)
    {
        return DesignGeometry.Build(content ?? Content(), Evaluation(projection, diagnostics), Subject,
            pose ?? Identity, NextId());
    }
    internal static Func<string, SceneObjectId> NextId()
    {
        ulong id = 0;
        return _ => new(++id);
    }
    internal sealed class BlockedAppearanceView(IContentView inner) : IContentView, IDisposable
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ManualResetEventSlim Release { get; } = new();
        public ContentLookup<VoxelAppearanceProjection> FindVoxelAppearance(VoxelDefinitionId id)
        {
            Entered.TrySetResult();
            Release.Wait();
            return inner.FindVoxelAppearance(id);
        }
        public ContentGeneration Generation => inner.Generation;
        public CoreContentProjection Core => inner.Core;
        public ContentLookup<DesignEvaluation> FindDesign(DesignHash id) => inner.FindDesign(id);
        public ContentRevision PersistentRevision => inner.PersistentRevision;
        public ImmutableArray<PackageStatus> Packages => inner.Packages;
        public ImmutableArray<VoxelCatalogEntry> Voxels => inner.Voxels;
        public ImmutableArray<BlueprintSummary> Blueprints => inner.Blueprints;
        public ImmutableArray<SavedContentDraft> LocalDrafts => inner.LocalDrafts;
        public ImmutableArray<GenerationProfileSummary> GenerationProfiles => inner.GenerationProfiles;
        public ContentLookup<BlueprintRecord> FindBlueprint(BlueprintAddress id) => inner.FindBlueprint(id);
        public ContentLookup<VoxelDisplayProjection> FindVoxelDisplay(VoxelDefinitionId id) => inner.FindVoxelDisplay(id);
        public ContentLookup<ContentDisplay> FindDefinitionDisplay(CoreDefinitionReference id) => inner.FindDefinitionDisplay(id);
        public ContentLookup<GoodPresentation> FindGood(GoodId id) => inner.FindGood(id);
        public ContentLookup<ContentAsset> FindAsset(AssetRevision id) => inner.FindAsset(id);
        public ContentLookup<SettlementLayoutProjection> FindSettlementLayout(SettlementId id) => inner.FindSettlementLayout(id);
        public ContentLookup<CreatureAppearanceProjection> FindCreature(CreatureDefinitionId id) => inner.FindCreature(id);
        public ContentLookup<LairAppearanceProjection> FindLair(LairTypeId id) => inner.FindLair(id);
        public ContentLookup<TerrainAppearanceProjection> FindTerrain(TerrainTypeId id) => inner.FindTerrain(id);
        public ContentLookup<RailwayAppearanceProjection> FindRailwayAppearance(RuleDefinitionId id) => inner.FindRailwayAppearance(id);
        public void Dispose() => Release.Dispose();
    }
}
