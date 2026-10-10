// 可抛弃的内容契约原型；只返回用例事先指定的值，不计算设计或模拟业务。
// 未列出的操作立即失败，不能把默认空结果算作已经覆盖的情景。
using System.Collections.Immutable;
using Train.Content.Contracts;
using Train.Core.Contracts;

namespace Train.ContractProbe.Content;

internal static class Unsupported
{
    public static Exception Call(string operation) => new InvalidOperationException($"unconfigured:{operation}");
}

internal sealed class View(CoreContentProjection core, ulong revision) : IContentView
{
    public ContentGeneration Generation => new(revision);
    public ContentRevision PersistentRevision => new(revision);
    public CoreContentProjection Core { get; } = core;
    public ImmutableArray<PackageStatus> Packages => [];
    public ImmutableArray<VoxelCatalogEntry> Voxels => [];
    public ImmutableArray<BlueprintSummary> Blueprints => [];
    public ImmutableArray<SavedContentDraft> LocalDrafts => [];
    public ImmutableArray<GenerationProfileSummary> GenerationProfiles => [];
    public DesignEvaluation? Design { get; init; }
    public ContentLookup<DesignEvaluation> FindDesign(DesignHash hash) => Design is DesignEvaluation.Complete c && c.Design == hash
        ? new ContentLookup<DesignEvaluation>.Found(c) : throw Unsupported.Call(nameof(FindDesign));
    public ContentLookup<BlueprintRecord> FindBlueprint(BlueprintAddress address) => throw Unsupported.Call(nameof(FindBlueprint));
    public ContentLookup<VoxelAppearanceProjection> FindVoxelAppearance(VoxelDefinitionId id) => throw Unsupported.Call(nameof(FindVoxelAppearance));
    public ContentLookup<VoxelDisplayProjection> FindVoxelDisplay(VoxelDefinitionId id) => throw Unsupported.Call(nameof(FindVoxelDisplay));
    public ContentLookup<ContentDisplay> FindDefinitionDisplay(CoreDefinitionReference id) => throw Unsupported.Call(nameof(FindDefinitionDisplay));
    public ContentLookup<GoodPresentation> FindGood(GoodId id) => throw Unsupported.Call(nameof(FindGood));
    public ContentLookup<ContentAsset> FindAsset(AssetRevision id) => throw Unsupported.Call(nameof(FindAsset));
    public ContentLookup<SettlementLayoutProjection> FindSettlementLayout(SettlementId id) => throw Unsupported.Call(nameof(FindSettlementLayout));
    public ContentLookup<CreatureAppearanceProjection> FindCreature(CreatureDefinitionId id) => throw Unsupported.Call(nameof(FindCreature));
    public ContentLookup<LairAppearanceProjection> FindLair(LairTypeId id) => throw Unsupported.Call(nameof(FindLair));
    public ContentLookup<TerrainAppearanceProjection> FindTerrain(TerrainTypeId id) => throw Unsupported.Call(nameof(FindTerrain));
    public ContentLookup<RailwayAppearanceProjection> FindRailwayAppearance(RuleDefinitionId id) => throw Unsupported.Call(nameof(FindRailwayAppearance));
}

internal sealed class Candidate(IContentView? basis, IContentView view, ContentChange changes,
    PreparedContentEdit? edit = null) : IContentCandidate
{
    public IContentView? Basis => basis;
    public IContentView View => view;
    public ContentChange Changes => changes;
    public ContentPreparationReport Report => new([], [], [], []);
    public PreparedContentEdit? Edit => edit;
    public ImmutableArray<ExternalContentWrite> RequiredWrites => [];
    public int Activations { get; private set; }
    public bool Disposed { get; private set; }
    public IContentView Activate()
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        if (Activations != 0) throw new InvalidOperationException("candidate_already_activated");
        Activations++;
        return view;
    }
    public void Dispose() => Disposed = true;
}

internal sealed class Factory : IContentFactory
{
    public Func<IContentView, ContentEdit, ContentResult<IContentCandidate>>? Edit { get; init; }
    public Func<IContentView, ImmutableArray<OwnedSettlementTemplateReference>, IContentView>? Copies { get; init; }
    public Func<ImmutableArray<IContentView>, CoreReferenceManifest, ContentResult<IContentReplayCapture>>? Replay { get; init; }
    public Func<ContentLoadRequest, ContentResult<IContentCandidate>>? Load { get; init; }
    public Func<IContentView, ContentResult<WorldGenerationInput>>? World { get; init; }
    public ContentResult<IContentCandidate> PrepareEdit(IContentView basis, ContentEdit edit)
        => Edit?.Invoke(basis, edit) ?? throw Unsupported.Call(nameof(PrepareEdit));
    public IContentView WithSettlementCopies(IContentView basis, ImmutableArray<OwnedSettlementTemplateReference> copies)
        => Copies?.Invoke(basis, copies) ?? throw Unsupported.Call(nameof(WithSettlementCopies));
    public ContentResult<IContentReplayCapture> CaptureReplay(ImmutableArray<IContentView> history, CoreReferenceManifest references)
        => Replay?.Invoke(history, references) ?? throw Unsupported.Call(nameof(CaptureReplay));
    public ContentResult<IContentCandidate> PrepareLoad(ContentLoadRequest request, IContentFiles files, IAssetInspector inspector)
        => Load?.Invoke(request) ?? throw Unsupported.Call(nameof(PrepareLoad));
    public ContentResult<WorldGenerationInput> CreateWorldInput(IContentView content, GenerationProfileId profile, ulong seed, string generatorVersion)
    {
        if (profile != new GenerationProfileId("core:profile") || seed != 1 || generatorVersion != "probe") throw Unsupported.Call(nameof(CreateWorldInput));
        return World?.Invoke(content) ?? throw Unsupported.Call(nameof(CreateWorldInput));
    }
    public ContentResult<ImmutableArray<PackageStatus>> Discover(ContentInstallation installation, IContentFiles files) => throw Unsupported.Call(nameof(Discover));
    public ContentResult<DesignEvaluation> EvaluateDesign(IContentView content, DesignDraft draft) => throw Unsupported.Call(nameof(EvaluateDesign));
    public ContentResult<DesignChangeEvaluation> CompareDesigns(IContentView content, DesignHash current, DesignDraft target) => throw Unsupported.Call(nameof(CompareDesigns));
    public ContentResult<IContentCapture> Capture(IContentView content, CoreReferenceManifest references) => throw Unsupported.Call(nameof(Capture));
    public ContentResult<IContentCapture> DecodeCapture(string payload) => throw Unsupported.Call(nameof(DecodeCapture));
    public ContentResult<IContentReplayCapture> DecodeReplay(string payload) => throw Unsupported.Call(nameof(DecodeReplay));
    public ContentResult<string> EncodeDraft(SavedContentDraft draft) => throw Unsupported.Call(nameof(EncodeDraft));
    public ContentResult<SavedContentDraft> DecodeDraft(string payload) => throw Unsupported.Call(nameof(DecodeDraft));
    public ContentResult<IAuthoringPackage> OpenAuthoringPackage(ContentRoot source, PackageId package, PackageSourceVersion version, IContentFiles files) => throw Unsupported.Call(nameof(OpenAuthoringPackage));
    public ContentResult<PackageWritePlan> PreparePackageWrite(IAuthoringPackage basis, ImmutableArray<PackageEdit> edits) => throw Unsupported.Call(nameof(PreparePackageWrite));
}

internal sealed class CoreSession(Func<CoreCommand, SubmissionResult> submit) : ICoreSession
{
    public int Calls { get; private set; }
    public SubmissionResult Submit(CoreCommand command, CommandSource source)
    {
        if (source != CommandSource.UserInterface) throw Unsupported.Call("Submit.Source");
        Calls++;
        return submit(command);
    }
    public ICoreReadView Read() => throw Unsupported.Call(nameof(Read));
    public AdvanceResult Advance(uint steps) => throw Unsupported.Call(nameof(Advance));
    public CoreResult<ICoreCapture> Capture(CapturePurpose purpose) => throw Unsupported.Call(nameof(Capture));
    public CoreRecordingWindow ReadRecording() => throw Unsupported.Call(nameof(ReadRecording));
    public CoreResult<RecordingStart> CaptureRecordingStart() => throw Unsupported.Call(nameof(CaptureRecordingStart));
    public RecordingMaintenance ConfirmRecordingStart(RecordingPointId point, RecordingWriteOutcome outcome) => throw Unsupported.Call(nameof(ConfirmRecordingStart));
    public void Dispose() { }
}

internal sealed record ReplayCapture(ImmutableArray<IContentView> History) : IContentReplayCapture
{
    public string Encode() => throw Unsupported.Call(nameof(Encode));
}

internal sealed class NoFiles : IContentFiles
{
    public ContentIoResult<ImmutableArray<ContentFileEntry>> Enumerate(ContentPath directory) => throw Unsupported.Call(nameof(Enumerate));
    public ContentIoResult<ImmutableArray<byte>> Read(ContentPath file, long maximumBytes) => throw Unsupported.Call(nameof(Read));
}

internal sealed class NoAssets : IAssetInspector
{
    public AssetInspection Inspect(AssetKind kind, AssetRevision revision, ImmutableArray<byte> bytes) => throw Unsupported.Call(nameof(Inspect));
}
