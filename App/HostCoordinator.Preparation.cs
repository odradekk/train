using System.Collections.Immutable;
using Train.App.Recording;
using Train.App.Saves;
using Train.Content.Contracts;
using Train.Core.Contracts;

namespace Train.App;

internal sealed partial class HostCoordinator
{
    private PreparedOperation PrepareReplay(string path, RuntimeIdentity runtime, Func<string, RecordingPackage> read)
    {
        ICoreReplay? replay = null;
        try
        {
            var package = read(path);
            var input = SelectReplayInput(package);
            var decoded = _contentFactory.DecodeReplay(package.ContentPayload);
            if (decoded is ContentResult<IContentReplayCapture>.Failure invalid)
                return new(new ContentResult<IContentCandidate>.Failure(invalid.Diagnostics), null, null);
            if (decoded is ContentResult<IContentReplayCapture>.Faulted failed)
                return new(new ContentResult<IContentCandidate>.Faulted(failed.Code, failed.Message, failed.StackTrace), null, null);
            var history = ((ContentResult<IContentReplayCapture>.Success)decoded).Value.History;
            var result = _core.PrepareReplay(input, runtime, history.Select(view => view.Core).ToImmutableArray());
            if (result is CoreResult<ICoreReplay>.Failure rejected)
                return new(null, new CoreResult<ICoreSessionCandidate>.Failure(rejected.Problems), null);
            if (result is CoreResult<ICoreReplay>.Faulted fault)
                return new(null, new CoreResult<ICoreSessionCandidate>.Faulted(fault.Fault, fault.Diagnostic), null);
            replay = ((CoreResult<ICoreReplay>.Success)result).Value;
            var view = replay.Read();
            _ = ReplayContent(history, view.InputIdentity);
            return new(null, null, null) { Replay = replay, History = history,
                Recording = new(Path.GetFullPath(path), view.Position, input.EndPosition, input.Commands, package.Header.Gaps) };
        }
        catch
        {
            replay?.Dispose();
            throw;
        }
    }

    private PreparedOperation PrepareContentEdit(IContentView basis, ContentEdit edit,
        Func<IContentCandidate, FileWriteResult>? write)
    {
        var result = _contentFactory.PrepareEdit(basis, edit);
        if (result is not ContentResult<IContentCandidate>.Success prepared) return new(result, null, null);
        try
        {
            var written = write?.Invoke(prepared.Value);
            ContentResult<IContentCandidate>? failure = written is { Published: false }
                ? new ContentResult<IContentCandidate>.Faulted(written.Code ?? "content_write_failed",
                    written.Message ?? "内容文件未写入。", null)
                : null;
            if (write is null && !prepared.Value.RequiredWrites.IsEmpty)
                failure = new ContentResult<IContentCandidate>.Faulted("content_write_required", "内容文件尚未写入。", null);
            if (failure is null) return new(result, null, null);
            prepared.Value.Dispose();
            return new(failure, null, null);
        }
        catch { prepared.Value.Dispose(); throw; }
    }

    private PreparedOperation PrepareNew(GenerationProfileId profile, ulong seed,
        string generatorVersion, CoreSessionOptions options)
    {
        var content = _contentFactory.PrepareLoad(new(_installation, new ContentLoadPurpose.Initial()), _files, _assets);
        if (content is not ContentResult<IContentCandidate>.Success loaded) return new(content, null, null);
        ICoreSessionCandidate? candidate = null;
        try
        {
            var world = _contentFactory.CreateWorldInput(loaded.Value.View, profile, seed, generatorVersion);
            if (world is ContentResult<WorldGenerationInput>.Failure rejected)
            { loaded.Value.Dispose(); return new(new ContentResult<IContentCandidate>.Failure(rejected.Diagnostics), null, null); }
            if (world is ContentResult<WorldGenerationInput>.Faulted faulted)
            { loaded.Value.Dispose(); return new(new ContentResult<IContentCandidate>.Faulted(faulted.Code, faulted.Message, faulted.StackTrace), null, null); }
            var core = _core.PrepareNew(((ContentResult<WorldGenerationInput>.Success)world).Value, loaded.Value.View.Core, options);
            candidate = (core as CoreResult<ICoreSessionCandidate>.Success)?.Value;
            var paired = candidate is not null && candidate.References.Templates.Length > 0
                ? _contentFactory.WithSettlementCopies(loaded.Value.View, candidate.References.Templates) : loaded.Value.View;
            return new(content, core, paired);
        }
        catch { candidate?.Dispose(); loaded.Value.Dispose(); throw; }
    }

    internal PreparedOperation PrepareRestore(string corePayload, string contentPayload, CoreSessionOptions options)
    {
        var state = _core.DecodeState(corePayload);
        if (state is CoreResult<ICoreCapture>.Failure rejected)
            return new(null, new CoreResult<ICoreSessionCandidate>.Failure(rejected.Problems), null);
        if (state is CoreResult<ICoreCapture>.Faulted failed)
            return new(null, new CoreResult<ICoreSessionCandidate>.Faulted(failed.Fault, failed.Diagnostic), null);
        var saved = ((CoreResult<ICoreCapture>.Success)state).Value;
        var embedded = _contentFactory.DecodeCapture(contentPayload);
        if (embedded is ContentResult<IContentCapture>.Failure invalid)
            return new(new ContentResult<IContentCandidate>.Failure(invalid.Diagnostics), null, null);
        if (embedded is ContentResult<IContentCapture>.Faulted faulted)
            return new(new ContentResult<IContentCandidate>.Faulted(faulted.Code, faulted.Message, faulted.StackTrace), null, null);
        var content = _contentFactory.PrepareLoad(new(_installation,
            new ContentLoadPurpose.Restore(((ContentResult<IContentCapture>.Success)embedded).Value, saved.References)), _files, _assets);
        if (content is not ContentResult<IContentCandidate>.Success loaded) return new(content, null, null);
        try { return new(content, _core.PrepareRestore(saved, loaded.Value.View.Core, options), loaded.Value.View)
            { Saved = new(saved.Revision, ((ContentResult<IContentCapture>.Success)embedded).Value.Revision, "") }; }
        catch { loaded.Value.Dispose(); throw; }
    }
}
