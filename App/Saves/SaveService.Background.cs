namespace Train.App.Saves;

internal sealed partial class SaveService
{
    private SaveFailure? StartWrite(WriteKind kind, SaveIdentity? target = null, string? path = null,
        bool overwrite = true, bool leaving = false)
    {
        if (_host.GetStatus() is { IsReplay: true } && kind != WriteKind.Export)
            return Fail("replay_read_only", "重放只允许显式导出边界为普通存档。");
        if (_read is not null || _import is not null || _restore is not null || (!leaving && _leave is not null))
            return Fail("busy", "正在处理其他存档操作。");
        var status = _host.GetStatus();
        if (status is null) return Fail("no-session", "当前没有对局。");
        if (_writes.Any(write => write.Capture.Binding == status.Binding)) return Fail("busy", "当前对局正在写入存档。");
        var (capture, failure) = _host.CaptureSave();
        if (failure is not null) return Fail(failure.Code, failure.Message);
        target ??= capture!.Identity;
        var slot = kind == WriteKind.Automatic ? NextSlot(target.Id) : -1;
        path = Path.GetFullPath(path ?? (slot >= 0 ? _store.AutoPathFor(target.Id, slot) : _store.PathFor(target.Id)));
        if (_writes.Any(write => string.Equals(write.Path, path, StringComparison.OrdinalIgnoreCase)))
            return Fail("busy", "目标文件正在写入。");
        var cancellation = new CancellationTokenSource();
        var savedAt = _date();
        var writeTarget = target;
        var writePath = path;
        var work = _host.BackgroundWork.Enqueue(() => _store.Write(writePath, capture!.Encode(writeTarget, savedAt),
            kind == WriteKind.SaveAs ? false : overwrite, cancellation.Token), cancellation.Token);
        _writes.Add(new(capture!, target, path, kind, slot, cancellation, work, leaving));
        LastMessage = "正在保存…";
        Changed?.Invoke();
        return null;
    }

    private void StartRead(SaveEntry entry)
    {
        if (_host.SessionOptions is null) { Fail("session-options-unavailable", "宿主尚未配置会话环境。"); return; }
        if (_host.LockLeaving() is not null) { Fail("busy", "正在处理其他操作。"); return; }
        var cancellation = new CancellationTokenSource();
        _read = new(entry, cancellation, _host.BackgroundWork.Enqueue(() => _store.Read(entry.Path), cancellation.Token));
        LastMessage = "正在读取存档…";
        Changed?.Invoke();
    }

    internal SaveFailure? Import(string path)
    {
        if (IsBusy) return Fail("busy", "正在处理其他存档操作。");
        if (_host.SessionOptions is not { } options)
            return Fail("session-options-unavailable", "宿主尚未配置会话环境。");
        var cancellation = new CancellationTokenSource();
        _import = new(_host.BackgroundWork.Enqueue(() =>
        {
            var document = _store.Read(path);
            return new ImportPreparation(document, _host.PrepareRestore(document.CorePayload, document.ContentPayload, options));
        }, cancellation.Token), cancellation);
        LastMessage = "正在校验导入存档…";
        Changed?.Invoke();
        return null;
    }

    private void PublishImport(ImportPreparation imported)
    {
        imported.Prepared.Dispose();
        var id = Guid.NewGuid();
        var document = imported.Document with { Header = imported.Document.Header with { Id = id } };
        var cancellation = _import!.Cancellation;
        _import!.Prepared = null;
        _import.Published = _host.BackgroundWork.Enqueue(() => _store.Write(_store.PathFor(id), document, false, cancellation.Token), cancellation.Token);
        LastMessage = "正在写入导入存档…";
        Changed?.Invoke();
    }
}
