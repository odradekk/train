using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.Contracts;
using Train.Core.Contracts;

namespace Train.Content.ContractSupport;

public static class HostContentScenario
{
    public static CoreContentProjection CreateProjection(CoreInputId identity) => new(identity,
        [new(new GoodId.Voxel(new VoxelDefinitionId("fixture:ore")),
            new SemanticRevision("fixture-good-v1"), new Money(100), 1, true, null)],
        [], [], [], [], [], [], [], [], [], [], [], [], []);

    public static void AssertRetainedView(IContentView view, CoreContentProjection core,
        ContentGeneration generation, ContentRevision revision)
    {
        Assert.AreSame(core, view.Core);
        Assert.AreEqual(generation, view.Generation);
        Assert.AreEqual(revision, view.PersistentRevision);
    }
}

public sealed class ScriptedContentFactory : IContentFactory
{
    public Func<ContentLoadRequest, IContentFiles, IAssetInspector, ContentResult<IContentCandidate>>? PrepareLoadCall { get; init; }
    public Func<IContentView, GenerationProfileId, ulong, string, ContentResult<WorldGenerationInput>>? CreateWorldInputCall { get; init; }
    public Func<string, ContentResult<IContentCapture>>? DecodeCaptureCall { get; init; }
    public Func<IContentView, ImmutableArray<OwnedSettlementTemplateReference>, IContentView>? WithSettlementCopiesCall { get; init; }

    public ContentResult<IContentCandidate> PrepareLoad(ContentLoadRequest request, IContentFiles files,
        IAssetInspector inspector) => (PrepareLoadCall ?? throw Unconfigured())(request, files, inspector);
    public ContentResult<WorldGenerationInput> CreateWorldInput(IContentView content,
        GenerationProfileId profile, ulong seed, string generatorVersion) =>
        (CreateWorldInputCall ?? throw Unconfigured())(content, profile, seed, generatorVersion);
    public ContentResult<IContentCapture> DecodeCapture(string payload) =>
        (DecodeCaptureCall ?? throw Unconfigured())(payload);
    public IContentView WithSettlementCopies(IContentView basis,
        ImmutableArray<OwnedSettlementTemplateReference> copies) =>
        (WithSettlementCopiesCall ?? throw Unconfigured())(basis, copies);

    public ContentResult<ImmutableArray<PackageStatus>> Discover(ContentInstallation installation, IContentFiles files) => throw Unconfigured();
    public ContentResult<DesignEvaluation> EvaluateDesign(IContentView content, DesignDraft draft) => throw Unconfigured();
    public ContentResult<DesignChangeEvaluation> CompareDesigns(IContentView content, DesignHash current, DesignDraft target) => throw Unconfigured();
    public ContentResult<IContentCandidate> PrepareEdit(IContentView basis, ContentEdit edit) => throw Unconfigured();
    public ContentResult<IContentCapture> Capture(IContentView content, CoreReferenceManifest references) => throw Unconfigured();
    public ContentResult<IContentReplayCapture> CaptureReplay(ImmutableArray<IContentView> history, CoreReferenceManifest references) => throw Unconfigured();
    public ContentResult<IContentReplayCapture> DecodeReplay(string payload) => throw Unconfigured();
    public ContentResult<string> EncodeDraft(SavedContentDraft draft) => throw Unconfigured();
    public ContentResult<SavedContentDraft> DecodeDraft(string payload) => throw Unconfigured();
    public ContentResult<IAuthoringPackage> OpenAuthoringPackage(ContentRoot source, PackageId package,
        PackageSourceVersion sourceVersion, IContentFiles files) => throw Unconfigured();
    public ContentResult<PackageWritePlan> PreparePackageWrite(IAuthoringPackage basis, ImmutableArray<PackageEdit> edits) => throw Unconfigured();

    private static InvalidOperationException Unconfigured() => new("UNCONFIGURED: 内容工厂入口未配置。");
}

public sealed class ScriptedContentCandidate : IContentCandidate
{
    private bool _disposed;

    public ScriptedContentCandidate(IContentView view, IContentView? basis = null,
        ContentChange changes = ContentChange.CoreInput, ContentPreparationReport? report = null)
    {
        View = view;
        Basis = basis;
        Changes = changes;
        Report = report ?? new ContentPreparationReport(view.Packages, [], [], []);
    }

    public IContentView? Basis { get; }
    public IContentView View { get; }
    public ContentChange Changes { get; }
    public ContentPreparationReport Report { get; }
    public PreparedContentEdit? Edit => null;
    public ImmutableArray<ExternalContentWrite> RequiredWrites => [];
    public int Activations { get; private set; }
    public int Disposals { get; private set; }

    public IContentView Activate()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Activations != 0)
            throw new InvalidOperationException("内容候选只能激活一次。");
        Activations++;
        return View;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Disposals++;
    }
}

public sealed class FixedContentView(CoreContentProjection core,
    ContentGeneration generation = default, ContentRevision persistentRevision = default,
    ImmutableArray<PackageStatus> packages = default) : IContentView
{
    public CoreContentProjection Core { get; } = core;
    public ContentGeneration Generation { get; } = generation;
    public ContentRevision PersistentRevision { get; } = persistentRevision;
    public ImmutableArray<PackageStatus> Packages { get; } = packages.IsDefault ? [] : packages;
    public ImmutableArray<VoxelCatalogEntry> Voxels => throw Unconfigured();
    public ImmutableArray<BlueprintSummary> Blueprints => throw Unconfigured();
    public ImmutableArray<SavedContentDraft> LocalDrafts => throw Unconfigured();
    public ImmutableArray<GenerationProfileSummary> GenerationProfiles => throw Unconfigured();
    public ContentLookup<BlueprintRecord> FindBlueprint(BlueprintAddress address) => throw Unconfigured();
    public ContentLookup<DesignEvaluation> FindDesign(DesignHash hash) => throw Unconfigured();
    public ContentLookup<VoxelAppearanceProjection> FindVoxelAppearance(VoxelDefinitionId definition) => throw Unconfigured();
    public ContentLookup<VoxelDisplayProjection> FindVoxelDisplay(VoxelDefinitionId definition) => throw Unconfigured();
    public ContentLookup<ContentDisplay> FindDefinitionDisplay(CoreDefinitionReference definition) => throw Unconfigured();
    public ContentLookup<GoodPresentation> FindGood(GoodId good) => throw Unconfigured();
    public ContentLookup<ContentAsset> FindAsset(AssetRevision revision) => throw Unconfigured();
    public ContentLookup<SettlementLayoutProjection> FindSettlementLayout(SettlementId settlement) => throw Unconfigured();
    public ContentLookup<CreatureAppearanceProjection> FindCreature(CreatureDefinitionId creature) => throw Unconfigured();
    public ContentLookup<LairAppearanceProjection> FindLair(LairTypeId lair) => throw Unconfigured();
    public ContentLookup<TerrainAppearanceProjection> FindTerrain(TerrainTypeId terrain) => throw Unconfigured();
    public ContentLookup<RailwayAppearanceProjection> FindRailwayAppearance(RuleDefinitionId rule) => throw Unconfigured();

    private static InvalidOperationException Unconfigured() => new("UNCONFIGURED: 内容视图查询未配置。");
}

public sealed class FixedContentCapture(string opaquePayload, ContentRevision revision = default,
    ImmutableArray<DesignHash> designs = default,
    ImmutableArray<OwnedSettlementTemplateReference> templates = default,
    ImmutableArray<PackageMetadata> referencedPackages = default) : IContentCapture
{
    public ContentRevision Revision { get; } = revision;
    public ImmutableArray<DesignHash> Designs { get; } = designs.IsDefault ? [] : designs;
    public ImmutableArray<OwnedSettlementTemplateReference> Templates { get; } = templates.IsDefault ? [] : templates;
    public ImmutableArray<PackageMetadata> ReferencedPackages { get; } = referencedPackages.IsDefault ? [] : referencedPackages;
    public string Encode() => opaquePayload;
}
