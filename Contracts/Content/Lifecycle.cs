using System.Collections.Immutable;
using Train.Core.Contracts;

namespace Train.Content.Contracts;

public interface IContentFactory
{
    ContentResult<ImmutableArray<PackageStatus>> Discover(ContentInstallation installation, IContentFiles files);
    ContentResult<IContentCandidate> PrepareLoad(ContentLoadRequest request,
        IContentFiles files, IAssetInspector inspector);
    ContentResult<DesignEvaluation> EvaluateDesign(IContentView content, DesignDraft draft);
    ContentResult<DesignChangeEvaluation> CompareDesigns(IContentView content,
        DesignHash current, DesignDraft target);
    ContentResult<IContentCandidate> PrepareEdit(IContentView basis, ContentEdit edit);
    IContentView WithSettlementCopies(IContentView basis,
        ImmutableArray<OwnedSettlementTemplateReference> copies);
    ContentResult<WorldGenerationInput> CreateWorldInput(IContentView content,
        GenerationProfileId profile, ulong seed, string generatorVersion);
    ContentResult<IContentCapture> Capture(IContentView content, CoreReferenceManifest references);
    ContentResult<IContentCapture> DecodeCapture(string payload);
    ContentResult<IContentReplayCapture> CaptureReplay(ImmutableArray<IContentView> history,
        CoreReferenceManifest references);
    ContentResult<IContentReplayCapture> DecodeReplay(string payload);
    ContentResult<string> EncodeDraft(SavedContentDraft draft);
    ContentResult<SavedContentDraft> DecodeDraft(string payload);
    ContentResult<IAuthoringPackage> OpenAuthoringPackage(ContentRoot source,
        PackageId package, PackageSourceVersion sourceVersion, IContentFiles files);
    ContentResult<PackageWritePlan> PreparePackageWrite(IAuthoringPackage basis,
        ImmutableArray<PackageEdit> edits);
}

public abstract record ContentLoadPurpose
{
    private ContentLoadPurpose() { }
    public sealed record Initial : ContentLoadPurpose;
    public sealed record Restore(IContentCapture Saved, CoreReferenceManifest References) : ContentLoadPurpose;
    public sealed record Reload(IContentView Basis, CoreReferenceManifest References) : ContentLoadPurpose;
}
public sealed record ContentLoadRequest(ContentInstallation Installation, ContentLoadPurpose Purpose);
[Flags]
public enum ContentChange { None = 0, Presentation = 1, CoreInput = 2, Persistent = 4, Library = 8 }
public sealed record MissingContentReference(ContentReference Reference,
    PackageId? Package, string Field, ImmutableArray<ContentDiagnostic> Diagnostics);
public sealed record ContentPreparationReport(ImmutableArray<PackageStatus> Packages,
    ImmutableArray<MissingContentReference> RuntimeMissing,
    ImmutableArray<MissingContentReference> AuthoringMissing,
    ImmutableArray<ContentDiagnostic> Diagnostics);
public interface IContentCandidate : IDisposable
{
    IContentView? Basis { get; }
    IContentView View { get; }
    ContentChange Changes { get; }
    ContentPreparationReport Report { get; }
    PreparedContentEdit? Edit { get; }
    ImmutableArray<ExternalContentWrite> RequiredWrites { get; }
    IContentView Activate();
}

public interface IContentView
{
    ContentGeneration Generation { get; }
    ContentRevision PersistentRevision { get; }
    CoreContentProjection Core { get; }
    ImmutableArray<PackageStatus> Packages { get; }
    ImmutableArray<VoxelCatalogEntry> Voxels { get; }
    ImmutableArray<BlueprintSummary> Blueprints { get; }
    ImmutableArray<SavedContentDraft> LocalDrafts { get; }
    ImmutableArray<GenerationProfileSummary> GenerationProfiles { get; }
    ContentLookup<BlueprintRecord> FindBlueprint(BlueprintAddress address);
    ContentLookup<DesignEvaluation> FindDesign(DesignHash hash);
    ContentLookup<VoxelAppearanceProjection> FindVoxelAppearance(VoxelDefinitionId definition);
    ContentLookup<VoxelDisplayProjection> FindVoxelDisplay(VoxelDefinitionId definition);
    ContentLookup<ContentDisplay> FindDefinitionDisplay(CoreDefinitionReference definition);
    ContentLookup<GoodPresentation> FindGood(GoodId good);
    ContentLookup<ContentAsset> FindAsset(AssetRevision revision);
    ContentLookup<SettlementLayoutProjection> FindSettlementLayout(SettlementId settlement);
    ContentLookup<CreatureAppearanceProjection> FindCreature(CreatureDefinitionId creature);
    ContentLookup<LairAppearanceProjection> FindLair(LairTypeId lair);
    ContentLookup<TerrainAppearanceProjection> FindTerrain(TerrainTypeId terrain);
    ContentLookup<RailwayAppearanceProjection> FindRailwayAppearance(RuleDefinitionId rule);
}
public sealed record GoodPresentation(GoodId Good, ContentDisplay Display,
    DesignHash? DisplayDesign, bool Missing);

public abstract record ContentEdit
{
    private ContentEdit() { }
    public sealed record SaveLocalDraft(SavedContentDraft Draft) : ContentEdit;
    public sealed record DeleteLocalDraft(DraftId Draft) : ContentEdit;
    public sealed record SaveBlueprint(BlueprintSaveTarget Target, DesignDraft Draft) : ContentEdit;
    public sealed record DeleteBlueprint(BlueprintAddress Target) : ContentEdit;
    public sealed record ConstructionTarget(DesignDraft Target) : ContentEdit;
    public sealed record InstanceTarget(DesignHash Current, DesignDraft Target) : ContentEdit;
}
public abstract record BlueprintSaveTarget
{
    private BlueprintSaveTarget() { }
    public sealed record New(BlueprintLibrary Library) : BlueprintSaveTarget;
    public sealed record Overwrite(BlueprintAddress Address) : BlueprintSaveTarget;
}
public abstract record PreparedContentEdit
{
    private PreparedContentEdit() { }
    public sealed record DraftSaved(DraftId Draft, EditRevision Revision) : PreparedContentEdit;
    public sealed record DraftDeleted(DraftId Draft) : PreparedContentEdit;
    public sealed record BlueprintSaved(BlueprintAddress Blueprint, DesignHash Design) : PreparedContentEdit;
    public sealed record BlueprintDeleted(BlueprintAddress Blueprint) : PreparedContentEdit;
    public sealed record ConstructionTarget(DesignInput Design) : PreparedContentEdit;
    public sealed record InstanceTarget(DesignChangeEvaluation Difference) : PreparedContentEdit;
}
public sealed record ExternalContentWrite(BlueprintId Blueprint, ImmutableArray<byte>? Bytes);

public interface IContentCapture
{
    ContentRevision Revision { get; }
    ImmutableArray<DesignHash> Designs { get; }
    ImmutableArray<OwnedSettlementTemplateReference> Templates { get; }
    ImmutableArray<PackageMetadata> ReferencedPackages { get; }
    string Encode();
}
public interface IContentReplayCapture
{
    ImmutableArray<IContentView> History { get; }
    string Encode();
}
