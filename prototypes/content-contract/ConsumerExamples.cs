// 内容契约的消费样例；装配传入工厂和端口，没有内容算法或假生产实现。
// 只证明调用结构可编译；真实行为及替换由契约原型验证。
using System.Collections.Immutable;
using Train.Core.Contracts;
using Train.Content.Contracts;

namespace Train.Content.ContractSamples;

public sealed record NewGameCandidates(ICoreSessionCandidate Core, IContentCandidate Installed,
    IContentView Content);

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
        var installed = load.Value;
        ICoreSessionCandidate? coreCandidate = null;
        var transferred = false;
        try
        {
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
            coreCandidate = core.Value;
            var content = contentFactory.WithSettlementCopies(installed.View, coreCandidate.References.Templates);
            var candidates = new NewGameCandidates(coreCandidate, installed, content);
            transferred = true;
            return candidates;
        }
        finally
        {
            if (!transferred)
            {
                coreCandidate?.Dispose();
                installed.Dispose();
            }
        }
    }

    /// <summary>只在全部确认后于协调线程调用；两个激活都只交出已经准备好的状态。</summary>
    public static (ICoreSession Core, IContentView Content) ActivateNew(NewGameCandidates candidates)
    {
        using var installed = candidates.Installed;
        using var preparedCore = candidates.Core;
        installed.Activate();
        var core = preparedCore.Activate();
        return (core, candidates.Content);
    }

    /// <summary>宿主锁住协调操作；接受后先关联副本，返回后才可观察事件或继续推进。</summary>
    public static (SubmissionResult Result, IContentView Content) FoundSettlement(
        IContentFactory contentFactory, ICoreSession core, IContentView content,
        NodeId node, TrainId materialSource, ICollection<IContentView> recordingDependencies)
    {
        var result = core.Submit(new CoreCommand.FoundSettlement(node, materialSource), CommandSource.UserInterface);
        if (result is SubmissionResult.Completed completed)
        {
            if (completed.Receipt.Decision is CommandDecision.Accepted accepted)
                content = contentFactory.WithSettlementCopies(content, accepted.SettlementCopies);
            recordingDependencies.Add(content);
        }
        return (result, content);
    }

    /// <summary>先保存本局蓝图，再取得固定设计并提交建造；宿主已结束编辑并锁住协调操作。</summary>
    public static (SubmissionResult? Result, IContentView Content) SaveBlueprintAndBuild(
        IContentFactory factory, ICoreSession core, IContentView current, DesignDraft draft,
        FacilityKey facility, ImmutableArray<InventoryId> sources, int priority,
        ICollection<IContentView> recordingDependencies, Action<object> report)
    {
        var saved = CopyBlueprint(factory, current, draft);
        if (saved is not ContentResult<IContentCandidate>.Success success)
        {
            report(saved);
            return (null, current);
        }
        using var blueprint = success.Value;
        if (blueprint.Edit is not PreparedContentEdit.BlueprintSaved receipt)
            throw new InvalidOperationException("保存蓝图缺少准备回执。");
        current = blueprint.Activate();
        var design = current.FindDesign(receipt.Design);
        if (design is not ContentLookup<DesignEvaluation>.Found
            { Value: DesignEvaluation.Complete { IsValid: true, Core: not null } complete })
        {
            report(design);
            return (null, current);
        }
        var prepared = factory.PrepareEdit(current, new ContentEdit.ConstructionTarget(complete.Definition));
        if (prepared is not ContentResult<IContentCandidate>.Success target)
        {
            report(prepared);
            return (null, current);
        }
        using var candidate = target.Value;
        if (candidate.Edit is not PreparedContentEdit.ConstructionTarget construction)
            throw new InvalidOperationException("建造缺少固定设计输入。");
        return SubmitCandidate(core, current, candidate,
            new CoreCommand.StartOwnedJob(new JobRequest.BuildVehicle(facility,
                construction.Design, sources, priority)), recordingDependencies);
    }

    /// <summary>固定差额直接同步提交；未接受保留旧活动内容，Completed 的候选仍供录制保留。</summary>
    public static (SubmissionResult? Result, IContentView Content) ApplyInstanceDesign(
        IContentFactory factory, ICoreSession core, IContentView current, InstanceId instance,
        DesignHash before, DesignDraft target, ICollection<IContentView> recordingDependencies,
        Action<object> report)
    {
        var prepared = factory.PrepareEdit(current, new ContentEdit.InstanceTarget(before, target));
        if (prepared is not ContentResult<IContentCandidate>.Success success)
        {
            report(prepared);
            return (null, current);
        }
        using var candidate = success.Value;
        if (candidate.Edit is not PreparedContentEdit.InstanceTarget edit)
            throw new InvalidOperationException("实例变更缺少固定差额。");
        return SubmitCandidate(core, current, candidate,
            new CoreCommand.ApplyInstanceDesign(instance, edit.Difference.Core), recordingDependencies);
    }

    /// <summary>宿主已结束编辑并锁住协调操作，确认缺失清单；仅核心输入变化才提交发布命令。</summary>
    public static (SubmissionResult? Result, IContentView Content) Reload(
        ICoreSession core, IContentView current, IContentCandidate candidate,
        ICollection<IContentView> recordingDependencies)
    {
        if ((candidate.Changes & ContentChange.CoreInput) == 0)
            return (null, candidate.Changes == ContentChange.None ? current : candidate.Activate());
        return SubmitCandidate(core, current, candidate,
            new CoreCommand.PublishContent(candidate.View.Core), recordingDependencies);
    }

    private static (SubmissionResult Result, IContentView Content) SubmitCandidate(
        ICoreSession core, IContentView current, IContentCandidate candidate, CoreCommand command,
        ICollection<IContentView> recordingDependencies)
    {
        var result = core.Submit(command, CommandSource.UserInterface);
        if (result is SubmissionResult.Completed completed)
        {
            recordingDependencies.Add(candidate.View);
            if (completed.Receipt.Decision is CommandDecision.Accepted)
                return (result, candidate.Activate());
        }
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

    /// <summary>准备包补丁不发布；宿主逐文件写入源包后，走普通内容重载。</summary>
    public static ContentResult<PackageWritePlan> EncodeAuthoringChange(IContentFactory factory,
        IAuthoringPackage source, DefinitionDraft draft) => factory.PreparePackageWrite(source,
            [new PackageEdit.WriteDefinition(draft)]);

    /// <summary>只读取被保留的原内容输入，不用当前目录替换缺失历史。</summary>
    public static ContentResult<IContentReplayCapture> CaptureReplay(IContentFactory factory,
        ImmutableArray<IContentView> retained, CoreRecordingWindow recording)
        => factory.CaptureReplay(retained, recording.RequiredReferences);
}
