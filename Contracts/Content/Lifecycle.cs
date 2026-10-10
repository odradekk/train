// 内容准备、读取及持久捕获的入口；宿主统一切换活动组，内容不自行发布全局目录。
// 所有计算同步使用固定输入；返回的世代与捕获均可长期只读保留。
using System.Collections.Immutable;
using Train.Core.Contracts;

namespace Train.Content.Contracts;

public interface IContentFactory
{
    /// <summary>检查可用包的清单与依赖，供主菜单选择；不建立可用于游戏的内容世代。</summary>
    ContentResult<ImmutableArray<PackageStatus>> Discover(ContentInstallation installation, IContentFiles files);
    /// <summary>按请求读取并固定全部候选字节；包隔离、缺失确认和拒绝语义见模块文档。</summary>
    ContentResult<IContentCandidate> PrepareLoad(ContentLoadRequest request,
        IContentFiles files, IAssetInspector inspector);
    /// <summary>校验固定草稿、计算身份/属性/布局定位；不修改任何库或发布设计。</summary>
    ContentResult<DesignEvaluation> EvaluateDesign(IContentView content, DesignDraft draft);
    /// <summary>当前设计与合法目标之间的差额及编号对应；不读取或复制实例损伤。</summary>
    ContentResult<DesignChangeEvaluation> CompareDesigns(IContentView content,
        DesignHash current, DesignDraft target);
    /// <summary>准备本局草稿、蓝图库或实例目标变更；外部写入计划须先由宿主成功发布。</summary>
    ContentResult<IContentCandidate> PrepareEdit(IContentView basis, ContentEdit edit);
    /// <summary>
    /// 从同一固定视图中已完整校验的源模板复制并关联新所有者；只构造内存值，不再做 I/O 或合法性裁决。
    /// 核心候选或已接受建城回执提供 copies；来源或地块不配对属于调用前提错误。已有副本保持原值。
    /// </summary>
    /// <exception cref="ArgumentException">来源不属于 basis 的同一输入，或已有所有者/地块对应冲突。</exception>
    IContentView WithSettlementCopies(IContentView basis,
        ImmutableArray<OwnedSettlementTemplateReference> copies);
    /// <summary>选定配置并注入种子和宿主生成器版本；默认值与蓝图引用已由内容解析。</summary>
    ContentResult<WorldGenerationInput> CreateWorldInput(IContentView content,
        GenerationProfileId profile, ulong seed, string generatorVersion);
    /// <summary>捕获本局库/已保存草稿，以及核心公开引用的设计、模板副本和组装体显示记录。</summary>
    ContentResult<IContentCapture> Capture(IContentView content, CoreReferenceManifest references);
    ContentResult<IContentCapture> DecodeCapture(string payload);
    /// <summary>按先后顺序捕获原视图；同一核心输入可有不同持久修订，须覆盖全部模板所有者，缺项不从磁盘补造。</summary>
    ContentResult<IContentReplayCapture> CaptureReplay(ImmutableArray<IContentView> history,
        CoreReferenceManifest references);
    ContentResult<IContentReplayCapture> DecodeReplay(string payload);
    /// <summary>草稿可不合法，但须可编码；外部草稿文件由宿主读写。</summary>
    ContentResult<string> EncodeDraft(SavedContentDraft draft);
    ContentResult<SavedContentDraft> DecodeDraft(string payload);
    /// <summary>输入端口必须对应宿主已经固定的完整源包；源基准由宿主保管并在发布前复核。</summary>
    ContentResult<IAuthoringPackage> OpenAuthoringPackage(ContentRoot source,
        PackageId package, PackageSourceVersion sourceVersion, IContentFiles files);
    /// <summary>只编码指定变更，不抹掉未编辑文件；宿主逐文件写入后另行普通重载。</summary>
    ContentResult<PackageWritePlan> PreparePackageWrite(IAuthoringPackage basis,
        ImmutableArray<PackageEdit> edits);
}

public abstract record ContentLoadPurpose
{
    private ContentLoadPurpose() { }
    /// <summary>建立供新游戏选择的内容；本局内嵌内容为空。</summary>
    public sealed record Initial : ContentLoadPurpose;
    public sealed record Restore(IContentCapture Saved, CoreReferenceManifest References) : ContentLoadPurpose;
    public sealed record Reload(IContentView Basis, CoreReferenceManifest References) : ContentLoadPurpose;
}
public sealed record ContentLoadRequest(ContentInstallation Installation, ContentLoadPurpose Purpose);
[Flags]
public enum ContentChange { None = 0, Presentation = 1, CoreInput = 2, Persistent = 4, Library = 8 }
public sealed record MissingContentReference(ContentReference Reference,
    PackageId? Package, string Field, ImmutableArray<ContentDiagnostic> Diagnostics);
/// <summary>运行缺失与不可用库条目/草稿分开；前者才可能要求普通读档的候选确认。</summary>
public sealed record ContentPreparationReport(ImmutableArray<PackageStatus> Packages,
    ImmutableArray<MissingContentReference> RuntimeMissing,
    ImmutableArray<MissingContentReference> AuthoringMissing,
    ImmutableArray<ContentDiagnostic> Diagnostics);
/// <summary>绑定基准的一次性候选；Activate 不做 I/O、解析、业务计算或通知。</summary>
public interface IContentCandidate : IDisposable
{
    IContentView? Basis { get; }
    IContentView View { get; }
    ContentChange Changes { get; }
    ContentPreparationReport Report { get; }
    /// <summary>内容编辑的准备回执；加载候选为 null，不能把准备回执当作已提交成功。</summary>
    PreparedContentEdit? Edit { get; }
    /// <summary>外部蓝图文件的预备写入/删除；空数组表示本次只修改内存内容。</summary>
    ImmutableArray<ExternalContentWrite> RequiredWrites { get; }
    /// <summary>宿主在基准仍有效、缺失已确认且所需写入成功后调用一次。</summary>
    IContentView Activate();
}

/// <summary>某个固定内容版本的公开读取；没有解析器、可变索引或 Godot 对象。</summary>
public interface IContentView
{
    ContentGeneration Generation { get; }
    ContentRevision PersistentRevision { get; }
    CoreContentProjection Core { get; }
    ImmutableArray<PackageStatus> Packages { get; }
    /// <summary>固定世代内身份唯一的已接受可放置体素；不含缺失占位，显示与外观按身份查询。</summary>
    ImmutableArray<VoxelCatalogEntry> Voxels { get; }
    ImmutableArray<BlueprintSummary> Blueprints { get; }
    ImmutableArray<SavedContentDraft> LocalDrafts { get; }
    ImmutableArray<GenerationProfileSummary> GenerationProfiles { get; }
    ContentLookup<BlueprintRecord> FindBlueprint(BlueprintAddress address);
    ContentLookup<DesignEvaluation> FindDesign(DesignHash hash);
    ContentLookup<VoxelAppearanceProjection> FindVoxelAppearance(VoxelDefinitionId definition);
    ContentLookup<VoxelDisplayProjection> FindVoxelDisplay(VoxelDefinitionId definition);
    ContentLookup<ContentDisplay> FindDefinitionDisplay(CoreDefinitionReference definition);
    /// <summary>组装体货物使用第一次引入本局时保存的显示设计，不能改用后来同构成的重涂版本。</summary>
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
    /// <summary>New 的身份由内容分配；覆盖必须指定已有目标，不能覆盖包蓝图。</summary>
    public sealed record SaveBlueprint(BlueprintSaveTarget Target, DesignDraft Draft) : ContentEdit;
    public sealed record DeleteBlueprint(BlueprintAddress Target) : ContentEdit;
    /// <summary>为建造准备固定合法设计及嵌套依赖；不改变 CoreInputId，核心接受前不激活。</summary>
    public sealed record ConstructionTarget(DesignDraft Target) : ContentEdit;
    /// <summary>为立即应用或改装准备固定合法设计；失败不激活本局记录，录制仍可保留候选依赖。</summary>
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
    /// <summary>目标设计身份只从 Difference.Core.Target.Design.Design 读取。</summary>
    public sealed record InstanceTarget(DesignChangeEvaluation Difference) : PreparedContentEdit;
}
/// <summary>外部文件写入只针对用户蓝图库；null 字节表示删除，空字节为实际空文件。</summary>
public sealed record ExternalContentWrite(BlueprintId Blueprint, ImmutableArray<byte>? Bytes);

/// <summary>普通存档内容载荷；不内嵌外部旧定义或活动编辑工作副本。</summary>
public interface IContentCapture
{
    ContentRevision Revision { get; }
    ImmutableArray<DesignHash> Designs { get; }
    ImmutableArray<OwnedSettlementTemplateReference> Templates { get; }
    ImmutableArray<PackageMetadata> ReferencedPackages { get; }
    string Encode();
}
/// <summary>按原先后顺序重建的包内历史；同一核心输入可有多个视图，允许没有原运行世代号。</summary>
public interface IContentReplayCapture
{
    ImmutableArray<IContentView> History { get; }
    string Encode();
}
