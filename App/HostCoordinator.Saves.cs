using Train.App.Saves;
using Train.Content.Contracts;
using Train.Core.Contracts;

namespace Train.App;

internal sealed partial class HostCoordinator
{
    internal SaveStatus? GetSaveStatus()
    {
        EnsureOwner();
        return _active is not { Replay: null } group ? null : new(group.Binding, group.Save,
            group.Saved != new SaveRevision(group.View.Revision, group.Content.PersistentRevision, group.Save.Name),
            group.Driver.Frozen);
    }

    internal (SaveCapture? Capture, SaveFailure? Failure) CaptureSave()
    {
        EnsureOwner();
        if (_pending is not null || _insideBoundary) return (null, new("busy", "正在处理其他操作。"));
        if (_active is not { } group) return (null, new("no_session", "当前没有对局。"));
        if (group.Driver.Frozen) return (null, new("frozen", "核心已冻结，不能保存普通存档。"));
        _insideBoundary = true;
        try
        {
            var core = CaptureCurrent();
            if (core is CoreResult<ICoreCapture>.Failure rejected)
                return (null, new(rejected.Problems[0].Code, "核心状态捕获失败。"));
            if (core is CoreResult<ICoreCapture>.Faulted failed)
                return (null, new(failed.Fault.Code, failed.Diagnostic?.Message ?? "核心状态捕获失败。"));
            var fixedCore = ((CoreResult<ICoreCapture>.Success)core).Value;
            var content = _contentFactory.Capture(group.Content, fixedCore.References);
            if (content is ContentResult<IContentCapture>.Failure invalid)
                return (null, new(invalid.Diagnostics[0].Code, invalid.Diagnostics[0].Message));
            if (content is ContentResult<IContentCapture>.Faulted faulted)
                return (null, new(faulted.Code, faulted.Message));
            var fixedContent = ((ContentResult<IContentCapture>.Success)content).Value;
            var identity = group.Replay is null ? group.Save : new SaveIdentity(Guid.NewGuid(), "重放边界副本");
            return (new(group.Binding, identity,
                new(fixedCore.Revision, fixedContent.Revision, identity.Name), fixedCore, fixedContent), null);
        }
        finally { _insideBoundary = false; }
    }

    internal bool CompleteSave(SaveCapture capture, SaveIdentity target)
    {
        EnsureOwner();
        if (_active is not { } group || group.Binding != capture.Binding || group.Save.Id != capture.Identity.Id)
            return false;
        group.Save = new(target.Id, group.Save.Name == capture.Identity.Name ? target.Name : group.Save.Name);
        group.Saved = capture.Revision with { Name = target.Name };
        return true;
    }

    internal CoreProblem? RenameSave(string name)
    {
        EnsureOwner();
        if (MutationProblem() is { } problem) return problem;
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _active!.Save = _active.Save with { Name = name };
        return null;
    }

    internal void RenameStoredSave(Guid id, string name)
    {
        EnsureOwner();
        if (_active is not { } group || group.Save.Id != id) return;
        group.Save = group.Save with { Name = name };
        if (group.Saved is { } saved) group.Saved = saved with { Name = name };
    }

    internal void DeletedStoredSave(Guid id)
    {
        EnsureOwner();
        if (_active is { } group && group.Save.Id == id) group.Saved = null;
    }
}
