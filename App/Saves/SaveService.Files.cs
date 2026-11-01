namespace Train.App.Saves;

internal sealed partial class SaveService
{
    private PendingFileOperation? _fileOperation;

    internal SaveFailure? Rename(SaveEntry entry, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return Fail("invalid-name", "存档名称不能为空。");
        name = name.Trim();
        return StartFileOperation(cancellation =>
        {
            var documents = _store.PathsFor(entry.Header.Id).Where(File.Exists)
                .Select(path => (Path: path, Document: _store.Read(path))).ToArray();
            if (documents.Length == 0) return new(false, "missing-save", "存档文件已不存在。");
            if (documents.Any(item => item.Document.Header.Id != entry.Header.Id))
                return new(false, "identity-mismatch", "存档文件与列表不一致，请重新打开列表。");
            foreach (var (path, document) in documents)
            {
                var result = _store.Write(path, document with { Header = document.Header with { Name = name } }, true, cancellation);
                if (!result.Published) return result with { Message = "未能重命名全部存档文件，请检查文件后重试。" };
            }
            return new(true, Message: "存档已重命名。");
        }, () => _host.RenameStoredSave(entry.Header.Id, name));
    }
    internal SaveFailure? Export(SaveEntry entry, string path, bool overwriteConfirmed = false)
    {
        CancelDetails();
        if (File.Exists(path) && !overwriteConfirmed)
            return Fail("overwrite-confirmation", "目标文件已存在，请确认覆盖。");
        return StartFileOperation(cancellation =>
        {
            var result = _store.Write(path, candidate => File.Copy(entry.Path, candidate),
                candidate => _ = _store.Read(candidate), overwriteConfirmed, cancellation);
            return result.Published ? result with { Message = "存档文件已导出。" } : result;
        }, () => { });
    }
    internal SaveFailure? Delete(SaveEntry entry) => StartFileOperation(cancellation =>
    {
        cancellation.ThrowIfCancellationRequested();
        File.Delete(entry.Path);
        return new(true, Message: "存档文件已删除。");
    }, () => { if (!entry.Automatic) _host.DeletedStoredSave(entry.Header.Id); });

    private SaveFailure? StartFileOperation(Func<CancellationToken, FileWriteResult> work, Action published)
    {
        CancelDetails();
        if (IsBusy) return Fail("busy", "正在处理其他存档操作。");
        var cancellation = new CancellationTokenSource();
        _fileOperation = new(published, cancellation,
            _host.BackgroundWork.Enqueue(() => work(cancellation.Token), cancellation.Token));
        LastMessage = "正在处理存档文件…";
        Changed?.Invoke();
        return null;
    }

    private void PumpFileOperation()
    {
        if (_fileOperation is not { } operation || !operation.Work.IsCompleted) return;
        _fileOperation = null;
        try
        {
            var result = operation.Work.GetAwaiter().GetResult();
            if (result.Published) operation.Published();
            LastMessage = result.Message ?? (result.Published ? "文件操作完成。" : "文件操作失败。");
            if (!result.Published) PlayerMessage = "文件操作未完成，请检查存档文件和访问权限。";
        }
        catch (OperationCanceledException) { LastMessage = "文件操作已取消。"; }
        catch (Exception error) { ReportFileFailure(error); }
        finally { operation.Cancellation.Dispose(); }
        Changed?.Invoke();
    }

    private sealed record PendingFileOperation(Action Published, CancellationTokenSource Cancellation,
        Task<FileWriteResult> Work);
}
