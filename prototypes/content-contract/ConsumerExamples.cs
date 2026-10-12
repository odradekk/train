using System.Collections.Immutable;
using Train.Core.Contracts;
using Train.Content.Contracts;

namespace Train.Content.ContractSamples;

public sealed record NewGameCandidates(ICoreSessionCandidate Core, IContentCandidate Installed,
    IContentView Content);

public static class ConsumerExamples
{
    public static ImmutableArray<VoxelCatalogEntry> ReadVoxelPalette(IContentView content, bool scene)
        => content.Voxels.Where(entry => (entry.Good is null) == scene).ToImmutableArray();

    public static void LocateMissingComposites(DesignSceneProjection scene,
        Action<CompositeDefinitionId, PartId, GridPosition> showDiagnostic)
    {
        foreach (var item in scene.NestedObjects)
            if (item.Details is NestedObjectDetails.MissingFunctionalComposite missing)
                showDiagnostic(missing.Definition, missing.Number, item.At);
    }

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

    public static (ICoreSession Core, IContentView Content) ActivateNew(NewGameCandidates candidates)
    {
        using var installed = candidates.Installed;
        using var preparedCore = candidates.Core;
        installed.Activate();
        var core = preparedCore.Activate();
        return (core, candidates.Content);
    }

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

    public static ContentLookup<SettlementLayoutProjection> ReadSettlementLayout(
        IContentView content, SettlementId settlement) => content.FindSettlementLayout(settlement);

    public static ContentResult<DesignEvaluation> InspectDraft(IContentFactory factory,
        IContentView content, DesignDraft draft) => factory.EvaluateDesign(content, draft);

    public static ContentResult<IContentCandidate> CopyBlueprint(IContentFactory factory,
        IContentView content, DesignDraft draft) => factory.PrepareEdit(content,
            new ContentEdit.SaveBlueprint(new BlueprintSaveTarget.New(BlueprintLibrary.Local), draft));

    public static ContentResult<IContentCapture> CaptureContent(IContentFactory factory,
        IContentView content, ICoreCapture core) => factory.Capture(content, core.References);

    public static ContentResult<IContentCandidate> PrepareRestore(IContentFactory factory,
        ContentInstallation installation, IContentCapture savedContent, ICoreCapture savedCore,
        IContentFiles files, IAssetInspector assets) => factory.PrepareLoad(
            new ContentLoadRequest(installation,
                new ContentLoadPurpose.Restore(savedContent, savedCore.References)), files, assets);

    public static ContentLookup<ContentAsset> ReadAsset(IContentView view, AssetRevision revision)
        => view.FindAsset(revision);

    public static ContentResult<PackageWritePlan> EncodeAuthoringChange(IContentFactory factory,
        IAuthoringPackage source, DefinitionDraft draft) => factory.PreparePackageWrite(source,
            [new PackageEdit.WriteDefinition(draft)]);

    public static ContentResult<IContentReplayCapture> CaptureReplay(IContentFactory factory,
        ImmutableArray<IContentView> retained, CoreRecordingWindow recording)
        => factory.CaptureReplay(retained, recording.RequiredReferences);
}
