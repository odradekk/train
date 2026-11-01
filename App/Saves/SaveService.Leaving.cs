namespace Train.App.Saves;

internal enum LeaveAction { Continue, Load, Menu, Quit }
internal sealed record LeavePrompt(bool CanSave, bool Waiting, LeaveAction Action);
internal enum LeaveChoice { Save, Discard, Cancel }

internal sealed partial class SaveService
{
    private Action? _leave;
    private bool _leaveSaving;
    private LeaveAction _leaveAction;

    internal SaveFailure? ReturnToMenu() => RequestLeave(_host.LeaveSession, LeaveAction.Menu);

    internal SaveFailure? RequestLeave(Action continuation, LeaveAction action = LeaveAction.Continue)
    {
        SaveFailure? failure = null;
        var closeFailure = _editor.RequestClose(() => failure = LeaveAfterEditor(continuation, action));
        return closeFailure is not null ? Fail(closeFailure.Code, closeFailure.Message) : failure;
    }

    private SaveFailure? LeaveAfterEditor(Action continuation, LeaveAction action)
    {
        if (_leave is not null || _read is not null || _import is { Published: null } || _restore is not null)
            return Fail("busy", "正在处理其他操作，请先完成或取消。");
        if (_host.LockLeaving() is not null) return Fail("busy", "正在处理其他操作，请先完成或取消。");
        _leave = continuation;
        _leaveAction = action;
        _autoPending = null;
        ContinueLeaving();
        return null;
    }

    internal void ChooseLeave(LeaveChoice choice)
    {
        if (_leave is null || Leaving is not { } prompt) return;
        if (choice == LeaveChoice.Cancel)
        {
            foreach (var write in _writes.Where(write => write.Leaving)) write.Cancellation.Cancel();
            AbortLeaving();
            return;
        }
        if (prompt.Waiting) return;
        if (choice == LeaveChoice.Discard) { FinishLeaving(); return; }
        if (!prompt.CanSave) return;
        _leaveSaving = true;
        Leaving = new(true, true, _leaveAction);
        if (StartWrite(WriteKind.Manual, leaving: true) is not null) AbortLeaving();
    }

    private void ContinueLeaving()
    {
        if (_leave is null || _leaveSaving) return;
        LeavePrompt prompt;
        if (_writes.Count != 0 || _import?.Published is not null || _fileOperation is not null) prompt = new(false, true, _leaveAction);
        else if (Status is { Unsaved: true } status) prompt = new(!status.Frozen, false, _leaveAction);
        else { FinishLeaving(); return; }
        if (Leaving != prompt) { Leaving = prompt; Changed?.Invoke(); }
    }

    private void FinishLeaving()
    {
        var continuation = _leave!;
        var closesSession = _leaveAction == LeaveAction.Menu;
        _leave = null; Leaving = null; _leaveSaving = false;
        if (closesSession) continuation();
        _host.UnlockLeaving();
        if (!closesSession) continuation();
        Changed?.Invoke();
    }

    private void AbortLeaving()
    {
        _leave = null; Leaving = null; _leaveSaving = false;
        _host.UnlockLeaving();
        Changed?.Invoke();
    }
}
