// 内容消费原型：执行现有调用样例，以固定情景检查协调边界。
// 这些断言不验证尚不存在的内容算法、核心重放实现或完整游戏。
using System.Collections.Immutable;
using System.Text.Json;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Examples = Train.Content.ContractSamples.ConsumerExamples;

namespace Train.ContractProbe.Content;

internal static class Program
{
    private sealed record ProbeResult(string Name, string Status, string? Error = null);
    private static readonly List<ProbeResult> Results = [];
    internal static readonly CoreInputId Input = new(new string('a', 64));
    private static readonly DesignHash Hash = new(new string('b', 64));
    private static readonly CompositionHash Composition = new(new string('c', 64));
    private static readonly GoodId Steel = new GoodId.Voxel(new("core:steel"));
    private static readonly GoodsQuantity SteelUnit = new(Steel, 1);
    private static readonly DesignDraft Draft = new(new("probe", "固定单格结构样例"), new DesignSpecification.Vehicle(SizeClass.Small),
        new([new(new("core:steel"), VoxelOrientation.PxPy, null, null)], [new(new(0, 0, 0), 0)], []));
    private static readonly DesignProjection Design = new(Hash, Composition, new DesignKind.Vehicle(SizeClass.Small, 1), [], 1, 1, 1, new(1),
        [SteelUnit], [SteelUnit], 1, [new(Steel, 1, [], [SteelUnit], [])], true, []);
    private static readonly DesignInput DesignInput = new(Input, Design, []);
    private static readonly CoreProblem Rejection = new("insufficient_goods", "sources", null, []);
    private static readonly CoreFault Fault = new("internal", new(0, 1), "submit", "probe", null);
    private static readonly CommandDecision[] Decisions = [new CommandDecision.Accepted([], []), new CommandDecision.Rejected([Rejection]), new CommandDecision.Faulted(Fault)];

    internal static int Main(string[] args)
    {
        if (args is ["--write-child", var childRoot, var stage]) return FileCases.WriteChild(childRoot, stage);
        if (args is not [var resultPath, var ioRoot]) throw new ArgumentException("参数：结果 JSON 路径、本次专用 I/O 目录");
        Directory.CreateDirectory(ioRoot);
        foreach (var decision in Decisions)
        {
            Case($"blueprint-build-{decision.GetType().Name}", () => SaveAndBuild(decision));
            Case($"instance-apply-{decision.GetType().Name}", () => Apply(decision));
            Case($"reload-core-{decision.GetType().Name}", () => ReloadCore(decision));
        }
        Case("reload-unchanged", () => ReloadPresentation(ContentChange.None));
        Case("reload-presentation", () => ReloadPresentation(ContentChange.Presentation));
        Case("reload-not-accepted", NotAccepted);
        Case("invalid-blueprint-no-command", InvalidBlueprint);
        Case("retained-same-input-different-designs", RetainDistinctViews);
        Case("founding-copy-before-publication", () => Founding("success"));
        Case("founding-wrong-pair-detected", () => Founding("mismatch"));
        Case("founding-internal-fault-stops-group", () => Founding("fault"));
        Case("restore-failure-keeps-active", RestoreFailure);
        Case("new-game-pairs-before-activation", () => NewGameCases.Run(Old(), new View(Projection(), 2), false));
        Case("new-game-copy-failure-disposes-candidates", () => NewGameCases.Run(Old(), new View(Projection(), 2), true));
        Case("candidate-lifetime", CandidateLifetime);
        Case("unconfigured-double-rejected", Unconfigured);
        Case("rejection-atomicity-correct", () => CheckRejectedReload(false));
        Case("rejection-atomicity-broken-detected", () => Expect("old_active_changed", () => CheckRejectedReload(true)));
        Case("rejection-atomicity-restored", () => CheckRejectedReload(false));
        FileCases.Run(ioRoot, Case);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(resultPath))!);
        File.WriteAllText(resultPath, JsonSerializer.Serialize(Results, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        return Results.Any(item => item.Status == "failed") ? 1 : 0;
    }

    private static void Case(string name, Action body)
    {
        try { body(); Results.Add(new(name, "passed")); Console.WriteLine($"PASS {name}"); }
        catch (Exception e) { Results.Add(new(name, "failed", e.ToString())); Console.WriteLine($"FAIL {name}: {e}"); }
    }

    internal static void Check(bool condition, string code)
    {
        if (!condition) throw new InvalidOperationException(code);
    }

    internal static void Expect(string message, Action body)
    {
        try { body(); }
        catch (Exception e) when (e.Message.Contains(message, StringComparison.Ordinal))
        { Console.WriteLine($"EXPECTED {message}: {e.GetType().Name}"); return; }
        throw new InvalidOperationException($"expected_failure_missing:{message}");
    }

    private static CoreContentProjection Projection(ImmutableArray<DesignProjection> designs = default) => new(Input,
        [new(Steel, new(new string('3', 64)), new(1), 1, false, null)], designs.IsDefault ? [] : designs,
        [], [], [], [], [], [], [], [new(new("core:town"), new(new string('4', 64)), 1, [], 1, 1)],
        [new(new("core:template"), new("core:town"), 1, [new(1, PlotSize.Small, null)], [], [], [])], [], [], []);
    private static View Old() => new(Projection(), 1);
    private static SubmissionResult Completed(CommandDecision decision) => new SubmissionResult.Completed(new(1, new(0, 0), new(0, 1), decision), [], decision is CommandDecision.Faulted ? new("probe fault", null) : null);
    private static DesignEvaluation.Complete Evaluation() => new(Hash, Composition, Draft, DesignInput,
        new(Hash, new(new(0, 0, 0), new(0, 0, 0)), null, true, null, null,
            [new(new(0, 0, 0), new("core:steel"), VoxelOrientation.PxPy, null, null, new(1), new DesignElementPath.Cell([], 0), [], new(0, 0, 0), VoxelOrientation.PxPy, false)],
            [new(new(1), new DesignElementPath.Cell([], 0), new ComputedValue<ImmutableArray<GridPosition>>.Known([new(0, 0, 0)]), false, false)], [], []), true, []);

    private static void SaveAndBuild(CommandDecision decision)
    {
        var old = Old();
        var library = new View(Projection([Design]), 2) { Design = Evaluation() };
        var target = new View(Projection([Design]), 3) { Design = Evaluation() };
        var saved = new Candidate(old, library, ContentChange.Library | ContentChange.Persistent,
            new PreparedContentEdit.BlueprintSaved(new(BlueprintLibrary.Local, new("00000000-0000-0000-0000-000000000001")), Hash));
        var prepared = new Candidate(library, target, ContentChange.Persistent, new PreparedContentEdit.ConstructionTarget(DesignInput));
        var stage = 0;
        var factory = new Factory { Edit = (basis, edit) =>
        {
            if (stage++ == 0)
            {
                Check(ReferenceEquals(basis, old) && edit is ContentEdit.SaveBlueprint, "save_input");
                return new ContentResult<IContentCandidate>.Success(saved);
            }
            Check(stage == 2 && ReferenceEquals(basis, library) && edit is ContentEdit.ConstructionTarget, "construction_input");
            Check(saved.Activations == 1, "library_not_published_before_build");
            return new ContentResult<IContentCandidate>.Success(prepared);
        }};
        using var core = new CoreSession(command =>
        {
            Check(command is CoreCommand.StartOwnedJob { Request: JobRequest.BuildVehicle build } &&
                  ReferenceEquals(build.Design, DesignInput), "design_not_carried_by_command");
            Check(prepared.Activations == 0, "design_activated_before_acceptance");
            return Completed(decision);
        });
        var dependencies = new List<IContentView>();
        var result = Examples.SaveBlueprintAndBuild(factory, core, old, Draft, new(new(1), CapabilityKind.Construction), [], 0, dependencies, _ => throw new InvalidOperationException("unexpected_report"));
        Check(stage == 2 && core.Calls == 1, "wrong_build_flow");
        Check(ReferenceEquals(result.Content, decision is CommandDecision.Accepted ? target : library), "wrong_active_content");
        Check(dependencies.Count == 1 && ReferenceEquals(dependencies[0], target), "completed_dependency_lost");
        Check(old.Core.Identity == library.Core.Identity && library.Core.Identity == target.Core.Identity, "fixture_input_changed");
        Check(saved.Disposed && prepared.Disposed, "candidate_leak");
        Check(prepared.Activations == (decision is CommandDecision.Accepted ? 1 : 0), "wrong_activation");
    }

    private static void Apply(CommandDecision decision)
    {
        var old = Old();
        var next = new View(Projection([Design]), 2);
        var difference = new DesignChangeProjection(new(new string('d', 64)), Composition, DesignInput, [], [], [], 0);
        var candidate = new Candidate(old, next, ContentChange.Persistent,
            new PreparedContentEdit.InstanceTarget(new(difference, [], true, [])));
        var factory = new Factory { Edit = (basis, edit) =>
        {
            Check(ReferenceEquals(basis, old) && edit is ContentEdit.InstanceTarget, "instance_basis");
            return new ContentResult<IContentCandidate>.Success(candidate);
        }};
        using var core = new CoreSession(command =>
        {
            Check(command is CoreCommand.ApplyInstanceDesign c && ReferenceEquals(c.Change, difference), "difference_not_forwarded");
            return Completed(decision);
        });
        var dependencies = new List<IContentView>();
        var result = Examples.ApplyInstanceDesign(factory, core, old, new InstanceId.Vehicle(new(1)), difference.Before, Draft, dependencies, _ => throw new InvalidOperationException("unexpected_report"));
        Check(ReferenceEquals(result.Content, decision is CommandDecision.Accepted ? next : old), "apply_active_content");
        Check(dependencies.Count == 1 && ReferenceEquals(dependencies[0], next), "apply_dependency_lost");
        Check(candidate.Disposed, "apply_candidate_leak");
    }

    private static void ReloadCore(CommandDecision decision)
    {
        var old = Old();
        var next = new View(Projection() with { Identity = new(new string('e', 64)) }, 2);
        using var candidate = new Candidate(old, next, ContentChange.CoreInput);
        using var core = new CoreSession(command =>
        {
            Check(command is CoreCommand.PublishContent c && ReferenceEquals(c.Content, next.Core), "reload_projection");
            Check(candidate.Activations == 0, "reload_activated_before_submit");
            return Completed(decision);
        });
        var dependencies = new List<IContentView>();
        var result = Examples.Reload(core, old, candidate, dependencies);
        Check(ReferenceEquals(result.Content, decision is CommandDecision.Accepted ? next : old), "reload_active");
        Check(candidate.Activations == (decision is CommandDecision.Accepted ? 1 : 0), "reload_activation");
        Check(dependencies.Count == 1 && ReferenceEquals(dependencies[0], next), "reload_record_dependency");
        Check(result.Result is SubmissionResult.Completed { Receipt.Decision: var observed } && ReferenceEquals(observed, decision), "decision_lost");
    }

    private static void ReloadPresentation(ContentChange change)
    {
        var old = Old();
        var next = new View(Projection(), 2);
        using var candidate = new Candidate(old, next, change);
        using var core = new CoreSession(_ => throw Unsupported.Call("Submit"));
        var dependencies = new List<IContentView>();
        var result = Examples.Reload(core, old, candidate, dependencies);
        Check(ReferenceEquals(result.Content, change == ContentChange.None ? old : next), "presentation_active");
        Check(result.Result is null && core.Calls == 0 && dependencies.Count == 0, "presentation_core_command");
        Check(candidate.Activations == (change == ContentChange.None ? 0 : 1), "presentation_activation");
    }

    private static void NotAccepted()
    {
        var old = Old();
        using var candidate = new Candidate(old, new View(Projection(), 2), ContentChange.CoreInput);
        using var core = new CoreSession(_ => new SubmissionResult.NotAccepted(Rejection));
        var dependencies = new List<IContentView>();
        var result = Examples.Reload(core, old, candidate, dependencies);
        Check(ReferenceEquals(result.Content, old) && candidate.Activations == 0 && dependencies.Count == 0, "not_accepted_changed_state");
    }

    private static void InvalidBlueprint()
    {
        var old = Old();
        var failure = new ContentResult<IContentCandidate>.Failure([new("design_invalid", ContentDiagnosticSeverity.Error, "evaluate", null, null, "layout", null, "固定拒绝样例")]);
        var factory = new Factory { Edit = (_, _) => failure };
        using var core = new CoreSession(_ => throw Unsupported.Call("Submit"));
        object? report = null;
        var result = Examples.SaveBlueprintAndBuild(factory, core, old, Draft, new(new(1), CapabilityKind.Construction), [], 0, [], value => report = value);
        Check(ReferenceEquals(report, failure) && ReferenceEquals(result.Content, old) && core.Calls == 0, "invalid_design_published");
    }

    private static void RetainDistinctViews()
    {
        var first = new View(Projection([Design]), 1);
        var otherDesign = Design with { Design = new(new string('f', 64)) };
        var second = new View(Projection([otherDesign]), 2);
        var references = new CoreReferenceManifest([Hash, otherDesign.Design], [], [], [Input]);
        var window = new CoreRecordingWindow(new(0, 2), [], [], [], [], [], references);
        var capture = new ReplayCapture([first, second]);
        var factory = new Factory { Replay = (views, received) =>
        {
            Check(views.Length == 2 && ReferenceEquals(views[0], first) && ReferenceEquals(views[1], second), "same_input_deduplicated");
            Check(ReferenceEquals(received, references), "manifest_recomputed");
            return new ContentResult<IContentReplayCapture>.Success(capture);
        }};
        var result = Examples.CaptureReplay(factory, [first, second], window);
        Check(result is ContentResult<IContentReplayCapture>.Success s && ReferenceEquals(s.Value, capture), "capture_not_forwarded");
    }

    private static void Founding(string mode)
    {
        var old = Old();
        var next = new View(Projection(), 2);
        var owner = new OwnedSettlementTemplateReference(new(7), new("core:template"), [new(1, PlotSize.Small)]);
        var receivedOwner = mode == "mismatch" ? owner with { SourceTemplate = new("core:unknown") } : owner;
        var completed = Completed(new CommandDecision.Accepted([new WorldObjectId.Settlement(owner.Owner)], [receivedOwner]));
        var submitted = false;
        var paired = false;
        var published = false;
        var stopped = false;
        using var core = new CoreSession(command =>
        {
            Check(command is CoreCommand.FoundSettlement, "founding_command");
            submitted = true;
            return completed;
        });
        var factory = new Factory { Copies = (basis, copies) =>
        {
            Check(submitted && !published && ReferenceEquals(basis, old), "founding_pair_order");
            if (!copies.SequenceEqual([owner])) throw new ArgumentException("template_pair_mismatch");
            if (mode == "fault") throw new InvalidOperationException("association_internal_fault");
            paired = true;
            return next;
        }};
        var retained = new List<IContentView>();
        try
        {
            var result = Examples.FoundSettlement(factory, core, old, new(1), new(2), retained);
            Check(paired && ReferenceEquals(result.Content, next), "publish_before_pairing");
            published = true; // 代表事件、保存和推进的共同出口；异常时不会到达。
        }
        catch (Exception e) when ((mode == "mismatch" && e.Message == "template_pair_mismatch") ||
                                  (mode == "fault" && e.Message == "association_internal_fault"))
        { stopped = true; }
        Check(mode == "success" ? published && retained.Count == 1 : stopped && !published && retained.Count == 0, "incomplete_group_continued");
    }

    private static void RestoreFailure()
    {
        var old = Old();
        IContentView active = old;
        var failure = new ContentResult<IContentCandidate>.Failure([new("invalid_saved_content", ContentDiagnosticSeverity.Error, "restore", null, null, "content", null, "固定恢复拒绝")]);
        var factory = new Factory { Load = request =>
        {
            Check(request.Purpose is ContentLoadPurpose.Restore restore && ReferenceEquals(restore.References, NewGameCases.References), "load_purpose");
            return failure;
        }};
        var result = Examples.PrepareRestore(factory, NewGameCases.Installation, new ContentCapture(), new CoreCapture(), new NoFiles(), new NoAssets());
        if (result is ContentResult<IContentCandidate>.Success success) active = success.Value.Activate();
        Check(ReferenceEquals(active, old) && ReferenceEquals(result, failure), "failed_load_changed_active");
    }

    private static void CandidateLifetime()
    {
        var view = Old();
        using var candidate = new Candidate(null, view, ContentChange.None);
        Check(ReferenceEquals(candidate.Activate(), view), "candidate_value");
        Expect("candidate_already_activated", () => candidate.Activate());
        candidate.Dispose();
        Check(view.Core.Identity == Input, "disposed_view_invalidated");
        Expect("disposed", () => candidate.Activate());
    }

    private static void Unconfigured()
    {
        Expect("unconfigured:EvaluateDesign", () => new Factory().EvaluateDesign(Old(), Draft));
        Expect("unconfigured:FindAsset", () => Old().FindAsset(new("missing")));
        using var core = new CoreSession(_ => throw Unsupported.Call("Submit"));
        Expect("unconfigured:Advance", () => core.Advance(1));
    }

    private static void CheckRejectedReload(bool prematureActivation)
    {
        var old = Old();
        IContentView active = old;
        using var candidate = new Candidate(old, new View(Projection(), 2), ContentChange.CoreInput);
        using var core = new CoreSession(_ => Completed(new CommandDecision.Rejected([Rejection])));
        if (prematureActivation) active = candidate.Activate(); // 仅故意违规情景。
        active = Examples.Reload(core, active, candidate, []).Content;
        Check(ReferenceEquals(active, old), "old_active_changed");
        Check(candidate.Activations == 0, "rejected_candidate_activated");
    }
}
