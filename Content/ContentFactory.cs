using System.Collections.Immutable;
using Train.Content.Loading;
using Train.Content.Designs;
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
        if (request.Purpose is not C.ContentLoadPurpose.Initial || request.Installation.UserLibrary is not null)
            throw new NotImplementedException("当前只实现不含用户蓝图库的 Initial 加载。");
        try
        {
            return new InitialLoader(request.Installation, files, inspector).Load();
        }
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
    public C.IContentView WithSettlementCopies(C.IContentView basis, ImmutableArray<K.OwnedSettlementTemplateReference> copies) => throw new NotImplementedException();
    public C.ContentResult<K.WorldGenerationInput> CreateWorldInput(C.IContentView content, C.GenerationProfileId profile, ulong seed, string generatorVersion) => throw new NotImplementedException();
    public C.ContentResult<C.IContentCapture> Capture(C.IContentView content, K.CoreReferenceManifest references) => throw new NotImplementedException();
    public C.ContentResult<C.IContentCapture> DecodeCapture(string payload) => throw new NotImplementedException();
    public C.ContentResult<C.IContentReplayCapture> CaptureReplay(ImmutableArray<C.IContentView> history, K.CoreReferenceManifest references) => throw new NotImplementedException();
    public C.ContentResult<C.IContentReplayCapture> DecodeReplay(string payload) => throw new NotImplementedException();
    public C.ContentResult<string> EncodeDraft(C.SavedContentDraft draft) => throw new NotImplementedException();
    public C.ContentResult<C.SavedContentDraft> DecodeDraft(string payload) => throw new NotImplementedException();
    public C.ContentResult<C.IAuthoringPackage> OpenAuthoringPackage(C.ContentRoot source, C.PackageId package, C.PackageSourceVersion sourceVersion, C.IContentFiles files) => throw new NotImplementedException();
    public C.ContentResult<C.PackageWritePlan> PreparePackageWrite(C.IAuthoringPackage basis, ImmutableArray<C.PackageEdit> edits) => throw new NotImplementedException();
}
