// 内容契约的消费样例；装配传入工厂和端口，没有内容算法或假生产实现。
// 只证明调用结构可编译；真实行为及替换由契约原型验证。
using System.Collections.Immutable;
using Train.Core.Contracts;
using Train.Content.Contracts;

namespace Train.Content.ContractSamples;

public sealed record NewGameCandidates(ICoreSessionCandidate Core, IContentCandidate Content);
public sealed record FoundingCandidates(IPreparedCoreCommand Core, IContentCandidate? Content);

public static class ConsumerExamples
{
    /// <summary>玩家体素栏读取 Good 的质量与价值；场景体素栏以 Good 为 null 筛选同一固定目录。</summary>
    public static ImmutableArray<VoxelCatalogEntry> ReadVoxelPalette(IContentView content, bool scene)
        => content.Voxels.Where(entry => (entry.Good is null) == scene).ToImmutableArray();

    /// <summary>核心选定聚居地后，内容在同一个固定候选上准备每实例模板，再交宿主统一激活。</summary>
    public static NewGameCandidates? PrepareNew(IContentFactory contentFactory, ICoreFactory coreFactory,
        ContentInstallation installation, IContentFiles files, IAssetInspector assets,
        GenerationProfileId profile, ulong seed, string generatorVersion,
        CoreSessionOptions options, Action<object> report)
    {
        var loaded = contentFactory.PrepareLoad(
            new ContentLoadRequest(installation, new ContentLoadPurpose.Initial()), files, assets);
        if (loaded is not ContentResult<IContentCandidate>.Success load)
        {
            report(loaded);
            return null;
        }
        using var installed = load.Value;
        report(installed.Report);
        var input = contentFactory.CreateWorldInput(installed.View, profile, seed, generatorVersion);
        if (input is not ContentResult<WorldGenerationInput>.Success world)
        {
            report(input);
            return null;
        }
        var coreResult = coreFactory.PrepareNew(world.Value, installed.View.Core, options);
        if (coreResult is not CoreResult<ICoreSessionCandidate>.Success core)
        {
            report(coreResult);
            return null;
        }
        var copies = contentFactory.PrepareSettlementCopies(installed.View, core.Value.References);
        if (copies is ContentResult<IContentCandidate>.Success content)
            return new NewGameCandidates(core.Value, content.Value);
        core.Value.Dispose();
        report(copies);
        return null;
    }

    /// <summary>只在全部确认后于协调线程调用；两个激活都只交出已经准备好的状态。</summary>
    public static (ICoreSession Core, IContentView Content) ActivateNew(NewGameCandidates candidates)
    {
        var core = candidates.Core.Activate();
        var content = candidates.Content.Activate();
        candidates.Core.Dispose();
        candidates.Content.Dispose();
        return (core, content);
    }

    /// <summary>宿主已经锁住当前操作；建城准备预留最终聚居地身份，拒绝/故障也交回核心裁决。</summary>
    public static FoundingCandidates? PrepareFounding(IContentFactory contentFactory,
        ICoreFactory coreFactory, ICoreSession core, IContentView content,
        NodeId node, TrainId materialSource, Action<object> report)
    {
        var prepared = coreFactory.PrepareCommand(core.CaptureCommandBasis(),
            new CoreCommand.FoundSettlement(node, materialSource), CommandSource.UserInterface, null);
        if (prepared.Decision != PreparedDecision.Execute)
            return new FoundingCandidates(prepared, null);
        var copies = contentFactory.PrepareSettlementCopies(content, prepared.RequiredReferences);
        if (copies is ContentResult<IContentCandidate>.Success success)
            return new FoundingCandidates(prepared, success.Value);
        report(copies);
        report(core.CompletePrepared(prepared, PreparedDisposition.Cancel));
        return null;
    }

    /// <summary>执行结果已成功接受时才激活内容；取消或拒绝保留原内容。宿主此前已核对操作与基准。</summary>
    public static (PreparedCompletion Result, IContentView Content) CompleteFounding(
        ICoreSession core, IContentView current, FoundingCandidates candidates, bool cancel)
    {
        using var content = candidates.Content;
        var result = core.CompletePrepared(candidates.Core,
            cancel ? PreparedDisposition.Cancel : PreparedDisposition.Commit);
        if (result is PreparedCompletion.Completed
            { Result.Receipt.Outcome: CommandOutcome.Accepted } && content is not null)
            return (result, content.Activate());
        return (result, current);
    }

    /// <summary>模板本地号来自核心查询；场景无需猜测运行时 PlotId 与模板的对应。</summary>
    public static ContentLookup<SettlementLayoutProjection> ReadSettlementLayout(
        IContentView content, SettlementId settlement) => content.FindSettlementLayout(settlement);

    /// <summary>内容返回编号与坐标、BOM 和缺失诊断；调用方不复算。</summary>
    public static ContentResult<DesignEvaluation> InspectDraft(IContentFactory factory,
        IContentView content, DesignDraft draft) => factory.EvaluateDesign(content, draft);

    /// <summary>复制到本局蓝图库取得新 UUID；调用方从 Edit 的 BlueprintSaved 读取身份。</summary>
    public static ContentResult<IContentCandidate> CopyBlueprint(IContentFactory factory,
        IContentView content, DesignDraft draft) => factory.PrepareEdit(content,
            new ContentEdit.SaveBlueprint(new BlueprintSaveTarget.New(BlueprintLibrary.Local), draft));

    /// <summary>使用核心公开引用捕获，随后 Encode 仅使用固定结果；示例不写实际文件。</summary>
    public static ContentResult<IContentCapture> CaptureContent(IContentFactory factory,
        IContentView content, ICoreCapture core) => factory.Capture(content, core.References);

    /// <summary>读档已经解码核心与内容载荷；缺失确认仍针对返回的固定候选。</summary>
    public static ContentResult<IContentCandidate> PrepareRestore(IContentFactory factory,
        ContentInstallation installation, IContentCapture savedContent, ICoreCapture savedCore,
        IContentFiles files, IAssetInspector assets) => factory.PrepareLoad(
            new ContentLoadRequest(installation,
                new ContentLoadPurpose.Restore(savedContent, savedCore.References)), files, assets);

    /// <summary>Godot 上传的是内容检查过的同一份字节；不得按 Sources 再读路径。</summary>
    public static ContentLookup<ContentAsset> ReadAsset(IContentView view, AssetRevision revision)
        => view.FindAsset(revision);

    /// <summary>准备包补丁不发布；宿主应用到完整暂存包，再对暂存根调用 PrepareLoad。</summary>
    public static ContentResult<PackageWritePlan> EncodeAuthoringChange(IContentFactory factory,
        IAuthoringPackage source, DefinitionDraft draft) => factory.PreparePackageWrite(source,
            [new PackageEdit.WriteDefinition(draft)]);

    /// <summary>只读取被保留的原内容输入，不用当前目录替换缺失历史。</summary>
    public static ContentResult<IContentReplayCapture> CaptureReplay(IContentFactory factory,
        ImmutableArray<IContentView> retained, CoreRecordingWindow recording)
        => factory.CaptureReplay(retained, recording.RequiredReferences);
}
