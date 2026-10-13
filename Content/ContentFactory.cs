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
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(inspector);
        if (request.Installation.UserLibrary is not null || request.Purpose is C.ContentLoadPurpose.Reload)
            throw new NotImplementedException("用户蓝图库与重载尚未实现。");
        try
        {
            var loaded = new InitialLoader(request.Installation, files, inspector).Load();
            if (loaded is not C.ContentResult<C.IContentCandidate>.Success success || request.Purpose is C.ContentLoadPurpose.Initial)
                return loaded;
            using var initial = success.Value;
            var restore = (C.ContentLoadPurpose.Restore)request.Purpose;
            var decoded = restore.Saved as ContentCapture;
            if (decoded is null)
            {
                var result = DecodeCapture(restore.Saved.Encode());
                if (result is C.ContentResult<C.IContentCapture>.Failure failed)
                    return new C.ContentResult<C.IContentCandidate>.Failure(failed.Diagnostics);
                if (result is C.ContentResult<C.IContentCapture>.Faulted fault)
                    return new C.ContentResult<C.IContentCandidate>.Faulted(fault.Code, fault.Message, fault.StackTrace);
                decoded = (ContentCapture)((C.ContentResult<C.IContentCapture>.Success)result).Value;
            }
            return CaptureResult<C.IContentCandidate>(() =>
            {
                var view = ((ContentView)initial.View).WithCapture(decoded, restore.References);
                var missing = view.Missing(restore.References);
                return new ContentCandidate(view, initial.Report with { RuntimeMissing = missing });
            });
        }
        catch (NotImplementedException) { throw; }
        catch (Exception exception)
        {
            return new C.ContentResult<C.IContentCandidate>.Faulted("content_fault", exception.Message, exception.StackTrace);
        }
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
        ArgumentNullException.ThrowIfNull(references);
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
            return new C.ContentResult<T>.Failure([new(
                exception.Code is "invalid_json" or "unsupported_format" ? exception.Code : "inconsistent_capture",
                C.ContentDiagnosticSeverity.Error, "capture", null, null, exception.Field, null, exception.Message)]);
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
    public C.ContentResult<C.IContentReplayCapture> CaptureReplay(ImmutableArray<C.IContentView> history, K.CoreReferenceManifest references) => throw new NotImplementedException();
    public C.ContentResult<C.IContentReplayCapture> DecodeReplay(string payload) => throw new NotImplementedException();
    public C.ContentResult<string> EncodeDraft(C.SavedContentDraft draft) => throw new NotImplementedException();
    public C.ContentResult<C.SavedContentDraft> DecodeDraft(string payload) => throw new NotImplementedException();
    public C.ContentResult<C.IAuthoringPackage> OpenAuthoringPackage(C.ContentRoot source, C.PackageId package, C.PackageSourceVersion sourceVersion, C.IContentFiles files) => throw new NotImplementedException();
    public C.ContentResult<C.PackageWritePlan> PreparePackageWrite(C.IAuthoringPackage basis, ImmutableArray<C.PackageEdit> edits) => throw new NotImplementedException();
}
