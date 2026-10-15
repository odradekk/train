using System.Collections.Immutable;
using Train.Content.Loading;
using Train.Content.Designs;
using Train.Content.Persistence;
using System.Text.Json;
using C = Train.Content.Contracts;
using K = Train.Core.Contracts;

namespace Train.Content;

public static class ContentFactory
{
    public static C.IContentFactory Create() => new InitialContentFactory();
}

internal sealed class InitialContentFactory : C.IContentFactory
{
    public C.ContentResult<ImmutableArray<C.PackageStatus>> Discover(C.ContentInstallation installation, C.IContentFiles files)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(files);
        try
        {
            return new C.ContentResult<ImmutableArray<C.PackageStatus>>.Success(
                new PackageLoader(installation, files).Discover().Select(package => package.Status).ToImmutableArray());
        }
        catch (Exception exception)
        {
            return new C.ContentResult<ImmutableArray<C.PackageStatus>>.Faulted("content_fault", exception.Message, exception.StackTrace);
        }
    }

    public C.ContentResult<C.IContentCandidate> PrepareLoad(C.ContentLoadRequest request, C.IContentFiles files, C.IAssetInspector inspector)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Installation);
        ArgumentNullException.ThrowIfNull(request.Purpose);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(inspector);
        if (request.Installation.UserLibrary is not null)
            throw new NotImplementedException("用户蓝图库尚未实现。");
        switch (request.Purpose)
        {
            case C.ContentLoadPurpose.Restore restore:
                ArgumentNullException.ThrowIfNull(restore.Saved);
                if (restore.Saved is not ContentCapture) throw new ArgumentException("需要本内容实现产生的捕获。", nameof(request));
                ValidateReferences(restore.References);
                break;
            case C.ContentLoadPurpose.Reload reload:
                ArgumentNullException.ThrowIfNull(reload.Basis);
                if (reload.Basis is not ContentView) throw new ArgumentException("需要本内容实现产生的固定视图。", nameof(request));
                ValidateReferences(reload.References);
                break;
        }
        try
        {
            var loader = new InitialLoader(request.Installation, files, inspector);
            var loaded = loader.Load();
            if (request.Purpose is C.ContentLoadPurpose.Reload && !loader.AssetFailures.IsEmpty)
            {
                if (loaded is C.ContentResult<C.IContentCandidate>.Success abandoned) abandoned.Value.Dispose();
                return new C.ContentResult<C.IContentCandidate>.Failure(loader.AssetFailures.Select(d => d with { Stage = "reload" }).ToImmutableArray());
            }
            if (loaded is not C.ContentResult<C.IContentCandidate>.Success success || request.Purpose is C.ContentLoadPurpose.Initial)
                return loaded;
            using var initial = success.Value;
            if (request.Purpose is C.ContentLoadPurpose.Reload reload)
            {
                var basis = (ContentView)reload.Basis;
                var saved = ContentCapture.Create(basis, reload.References);
                var installed = (ContentView)initial.View;
                var changes = installed.ChangesFrom(basis);
                var registered = saved.AssemblyDisplays.Select(d => d.Composition).ToHashSet();
                if (installed.AssemblyDisplays.Any(d => !registered.Contains(d.Composition))) changes |= C.ContentChange.Persistent;
                var generation = changes == C.ContentChange.None ? basis.Generation : new C.ContentGeneration(checked(basis.Generation.Value + 1));
                var view = installed.WithCapture(saved, reload.References, generation, registerAssemblies: true);
                var missing = view.Missing(reload.References, "reload");
                var previousMissing = basis.Missing(reload.References).Select(m => ((C.ContentReference.Core)m.Reference).Definition).ToHashSet();
                var forbidden = missing.Where(m =>
                {
                    var definition = ((C.ContentReference.Core)m.Reference).Definition;
                    var declared = reload.References.Definitions.Where(d => d.Definition == definition).ToArray();
                    return declared.Any(d => !d.WasMissing) || declared.Length == 0 && !previousMissing.Contains(definition);
                }).ToImmutableArray();
                var kindChanges = reload.References.Definitions.Where(d => !d.WasMissing)
                    .Select(d => ChangedProcessKind(basis, installed, d)).OfType<C.ContentDiagnostic>().ToImmutableArray();
                var failures = forbidden.SelectMany(m => m.Diagnostics)
                    .Where(d => !kindChanges.Any(change => change.Subject == d.Subject && change.Field == d.Field))
                    .Concat(kindChanges).ToImmutableArray();
                if (!failures.IsEmpty)
                    return new C.ContentResult<C.IContentCandidate>.Failure(failures);
                return new C.ContentResult<C.IContentCandidate>.Success(new ContentCandidate(changes == C.ContentChange.None ? basis : view,
                    initial.Report with { RuntimeMissing = missing }, basis, changes));
            }
            var restore = (C.ContentLoadPurpose.Restore)request.Purpose;
            var decoded = (ContentCapture)restore.Saved;
            var restored = ((ContentView)initial.View).WithCapture(decoded, restore.References);
            return new C.ContentResult<C.IContentCandidate>.Success(new ContentCandidate(restored,
                initial.Report with { RuntimeMissing = restored.Missing(restore.References) }));
        }
        catch (DefinitionFormatException exception)
        {
            return new C.ContentResult<C.IContentCandidate>.Failure([CaptureDiagnostic(exception)]);
        }
        catch (NotImplementedException) { throw; }
        catch (Exception exception)
        {
            return new C.ContentResult<C.IContentCandidate>.Faulted("content_fault", exception.Message, exception.StackTrace);
        }
    }

    private static C.ContentDiagnostic? ChangedProcessKind(ContentView basis, ContentView installed, K.RequiredDefinition required)
    {
        if (required.Definition is not K.CoreDefinitionReference.Process process || basis.FindProcess(process.Id) is not { } before ||
            installed.FindProcess(before.Id) is not { } after || before.Projection.Details.GetType() == after.Projection.Details.GetType())
            return null;
        return new("reference_missing", C.ContentDiagnosticSeverity.Error, "reload", new C.ContentReference.Core(required.Definition),
            new(new(before.Id.Split(':')[0]), after.Path), required.Field, null, "流程种类改变视为删除原流程；仍被引用时不能发布。");
    }

    public C.ContentResult<C.DesignEvaluation> EvaluateDesign(C.IContentView content, C.DesignDraft draft)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(draft);
        if (content is not ContentView view) throw new ArgumentException("需要本工厂产生的固定内容视图。", nameof(content));
        try
        {
            return new C.ContentResult<C.DesignEvaluation>.Success(DesignEvaluator.Evaluate(view, draft));
        }
        catch (NotImplementedException) { throw; }
        catch (Exception exception)
        {
            return new C.ContentResult<C.DesignEvaluation>.Faulted("content_fault", exception.Message, exception.StackTrace);
        }
    }
    public C.ContentResult<C.DesignChangeEvaluation> CompareDesigns(C.IContentView content, K.DesignHash current, C.DesignDraft target) => throw new NotImplementedException();
    public C.ContentResult<C.IContentCandidate> PrepareEdit(C.IContentView basis, C.ContentEdit edit) => throw new NotImplementedException();
    public C.IContentView WithSettlementCopies(C.IContentView basis, ImmutableArray<K.OwnedSettlementTemplateReference> copies)
    {
        ArgumentNullException.ThrowIfNull(basis);
        if (basis is not ContentView view) throw new ArgumentException("需要本工厂产生的固定内容视图。", nameof(basis));
        return view.WithSettlementCopies(copies);
    }
    public C.ContentResult<K.WorldGenerationInput> CreateWorldInput(C.IContentView content, C.GenerationProfileId profile, ulong seed, string generatorVersion)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(generatorVersion);
        if (content is not ContentView view) throw new ArgumentException("需要本工厂产生的固定内容视图。", nameof(content));
        try { return view.CreateWorldInput(profile, seed, generatorVersion); }
        catch (Exception exception)
        {
            return new C.ContentResult<K.WorldGenerationInput>.Faulted("content_fault", exception.Message, exception.StackTrace);
        }
    }
    public C.ContentResult<C.IContentCapture> Capture(C.IContentView content, K.CoreReferenceManifest references)
    {
        ArgumentNullException.ThrowIfNull(content);
        ValidateReferences(references);
        if (content is not ContentView view) throw new ArgumentException("需要本工厂产生的固定内容视图。", nameof(content));
        return CaptureResult<C.IContentCapture>(() => ContentCapture.Create(view, references));
    }
    public C.ContentResult<C.IContentCapture> DecodeCapture(string payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return CaptureResult<C.IContentCapture>(() =>
        {
            using var document = ContentCapture.Parse(payload);
            return ContentCapture.Read(document.RootElement);
        });
    }

    private static C.ContentResult<T> CaptureResult<T>(Func<T> action) where T : notnull
    {
        try { return new C.ContentResult<T>.Success(action()); }
        catch (DefinitionFormatException exception)
        {
            return new C.ContentResult<T>.Failure([CaptureDiagnostic(exception)]);
        }
        catch (JsonException exception)
        {
            return new C.ContentResult<T>.Failure([new("invalid_json", C.ContentDiagnosticSeverity.Error,
                "capture", null, null, exception.Path ?? "", null, exception.Message)]);
        }
        catch (NotImplementedException) { throw; }
        catch (Exception exception)
        {
            return new C.ContentResult<T>.Faulted("content_fault", exception.Message, exception.StackTrace);
        }
    }
    private static C.ContentDiagnostic CaptureDiagnostic(DefinitionFormatException exception) => new(
        exception.Code is "invalid_json" or "unsupported_format" ? exception.Code : "inconsistent_capture",
        C.ContentDiagnosticSeverity.Error, "capture", null, null, exception.Field, null, exception.Message);
    public C.ContentResult<C.IContentReplayCapture> CaptureReplay(ImmutableArray<C.IContentView> history, K.CoreReferenceManifest references)
    {
        ValidateReferences(references);
        if (history.IsDefault) throw new ArgumentException("历史数组必须初始化。", nameof(history));
        foreach (var view in history)
        {
            ArgumentNullException.ThrowIfNull(view);
            if (view is not ContentView) throw new ArgumentException("需要本内容实现产生的固定视图。", nameof(history));
        }
        return CaptureResult<C.IContentReplayCapture>(() => ContentReplayCapture.Create(history, references));
    }

    private static void ValidateReferences(K.CoreReferenceManifest references)
    {
        ArgumentNullException.ThrowIfNull(references);
        if (references.Designs.IsDefault || references.Templates.IsDefault || references.Definitions.IsDefault || references.Inputs.IsDefault ||
            references.Templates.Any(template => template is null || template.Plots.IsDefault || template.Plots.Any(plot => plot is null)) ||
            references.Definitions.Any(definition => definition is null || definition.Definition is null))
            throw new ArgumentException("引用清单及其数组必须初始化。", nameof(references));
    }
    public C.ContentResult<C.IContentReplayCapture> DecodeReplay(string payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return CaptureResult<C.IContentReplayCapture>(() =>
        {
            using var document = ContentCapture.Parse(payload);
            return ContentReplayCapture.Read(document.RootElement);
        });
    }
    public C.ContentResult<string> EncodeDraft(C.SavedContentDraft draft) => throw new NotImplementedException();
    public C.ContentResult<C.SavedContentDraft> DecodeDraft(string payload) => throw new NotImplementedException();
    public C.ContentResult<C.IAuthoringPackage> OpenAuthoringPackage(C.ContentRoot source, C.PackageId package, C.PackageSourceVersion sourceVersion, C.IContentFiles files) => throw new NotImplementedException();
    public C.ContentResult<C.PackageWritePlan> PreparePackageWrite(C.IAuthoringPackage basis, ImmutableArray<C.PackageEdit> edits) => throw new NotImplementedException();
}
