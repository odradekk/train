using System.Collections.Immutable;
using Train.App.Editing;
using Train.Content.Contracts;

namespace Train.App.Authoring;

internal sealed partial class AuthoringService
{
    private int writePhase;
    internal bool? LastPublishSucceeded { get; private set; }
    internal void Publish()
    {
        if (!CanUse || IsBusy || package is null || snapshot is null || Target is null) { Report("当前没有可写的源包，或仍有操作进行中。"); return; }
        if (editor.RequestClose(BeginWrite) is { } failure) Report(failure.Message);
    }

    private void BeginWrite()
    {
        if (edits.Count == 0 && assetEdits.Count == 0) { Report("没有已保存的包修改。"); return; }
        var fixedPackage = package!; var fixedSource = snapshot!; var target = Target!;
        var changes = PendingChanges;
        written = null; LastWrite = null; LastPublishSucceeded = null; writeIssues = [];
        WriteStage = AuthoringWriteStage.None;
        Volatile.Write(ref writePhase, (int)AuthoringWriteStage.None);
        var begun = host.BeginPackageWrite((request, files, assets) =>
        {
            Volatile.Write(ref writePhase, (int)AuthoringWriteStage.ValidateDrafts);
            var planResult = host.ContentFactory.PreparePackageWrite(fixedPackage, changes);
            if (planResult is not ContentResult<PackageWritePlan>.Success planned) return FailureCandidate(planResult);
            var plan = planned.Value;
            if (plan.Package != fixedPackage.Package || plan.SourceVersion != fixedSource.Version)
                throw new InvalidDataException("补丁与固定源包不匹配。");
            if (plan.Diagnostics.Any(d => d.Severity == ContentDiagnosticSeverity.Error))
                return new ContentResult<IContentCandidate>.Failure(plan.Diagnostics);
            var root = target.IsCore ? request.Installation.BuiltIn : request.Installation.Mods;
            var prefix = target.IsCore ? "" : Path.GetFileName(target.Directory);
            Volatile.Write(ref writePhase, (int)AuthoringWriteStage.ValidateCandidate);
            var checkedLoad = host.ContentFactory.PrepareLoad(request,
                new PackageSourceFiles(files, fixedSource.WithChanges(plan.Changes), root, prefix), assets);
            if (checkedLoad is not ContentResult<IContentCandidate>.Success candidate) return checkedLoad;
            using (var check = candidate.Value)
            {
                var status = check.Report.Packages.FirstOrDefault(p => p.Metadata?.Id == target.Package);
                if (status?.State != PackageState.Available)
                    return new ContentResult<IContentCandidate>.Failure(status?.Diagnostics ?? [new("authoring_package_unavailable",
                        ContentDiagnosticSeverity.Error, "authoring", null, new(target.Package, ""), "package", null,
                        "校验结果中没有目标源包，未写入文件。")]);
            }
            Volatile.Write(ref writePhase, (int)AuthoringWriteStage.CheckSource);
            Volatile.Write(ref written, PackageWriter.Execute(fixedSource, plan, target.IsCore,
                sourceChecked: () => Volatile.Write(ref writePhase, (int)AuthoringWriteStage.WriteFiles)));
            if (!written.Success)
                return new ContentResult<IContentCandidate>.Failure([new(
                    written.Failed is null ? "stale_basis" : "package_write_failed",
                    ContentDiagnosticSeverity.Error, "authoring", null,
                    new(target.Package, written.Failed?.RelativePath ?? ""), "package", null,
                    written.Message ?? "包写入失败。")]);
            return host.ContentFactory.PrepareLoad(request, files, assets);
        });
        if (begun.Problem is { } problem) Reject(problem);
        else { operation = begun.Operation; Report("正在校验候选包并逐文件写入；期间请勿使用外部工具修改此包。"); }
    }

    private static ContentResult<IContentCandidate> FailureCandidate<T>(ContentResult<T> result) where T : notnull => result switch
    {
        ContentResult<T>.Failure failure => new ContentResult<IContentCandidate>.Failure(failure.Diagnostics),
        ContentResult<T>.Faulted failure => new ContentResult<IContentCandidate>.Faulted(failure.Code, failure.Message, failure.StackTrace),
        _ => throw new InvalidOperationException()
    };

    private void OperationEnded(HostOperationResult result)
    {
        if (operation == 0 || result.Operation != operation) return;
        operation = 0; LastWrite = written; LastPublishSucceeded = result.Succeeded;
        if (written?.Success == true) WriteStage = AuthoringWriteStage.ReloadContent;
        else WriteStage = (AuthoringWriteStage)Volatile.Read(ref writePhase);
        writeIssues = result.Diagnostics.Select(item => Issue(item)).ToImmutableArray();
        var changedFiles = written is { Completed.IsEmpty: false };
        if (changedFiles) { package = null; snapshot = null; }
        if (changedFiles || written?.Success == true) { edits.Clear(); assetEdits.Clear(); }
        var outcome = result.Succeeded ? "文件已写入，内容重载成功；草稿还在。"
            : written?.Success == true ? "文件已写入，但内容重载失败。游戏里的内容没有切换，草稿还在。"
            : WriteStage == AuthoringWriteStage.WriteFiles ? "写入已停止，已完成项保留。游戏里的内容没有切换，草稿还在。"
            : "一个文件也没写，游戏里的内容保持原样，草稿还在。";
        Report(outcome + (result.Succeeded ? "" : " " + result.Message + " "
                + string.Join("；", result.Diagnostics.Select(item => $"{item.Code}: {item.Message} {item.Source?.Path} {item.Field}")
                    .Concat(result.Problems.Select(problem => CommandDiagnostics.Describe(problem, host.Content)))))
            + (changedFiles ? "请重新载入源包后继续创作。" : ""));
    }

    private void ProcessWriteProgress()
    {
        if (operation == 0) return;
        var stage = (AuthoringWriteStage)Volatile.Read(ref writePhase);
        if (stage == AuthoringWriteStage.WriteFiles && Volatile.Read(ref written) is { Success: true })
            stage = AuthoringWriteStage.ReloadContent;
        if (stage == WriteStage) return;
        WriteStage = stage; Changed?.Invoke();
    }

    private sealed class PackageSourceFiles(IContentFiles installation, IContentFiles source, ContentRoot root, string prefix) : IContentFiles
    {
        private bool Map(ContentPath path, out ContentPath local)
        {
            local = path;
            if (path.Root != root) return false;
            if (prefix.Length == 0) return true;
            if (path.RelativePath == prefix) { local = path with { RelativePath = "" }; return true; }
            if (!path.RelativePath.StartsWith(prefix + "/", StringComparison.Ordinal)) return false;
            local = path with { RelativePath = path.RelativePath[(prefix.Length + 1)..] }; return true;
        }
        public ContentIoResult<ImmutableArray<ContentFileEntry>> Enumerate(ContentPath directory) =>
            Map(directory, out var local) ? source.Enumerate(local) : installation.Enumerate(directory);
        public ContentIoResult<ImmutableArray<byte>> Read(ContentPath file, long maximumBytes) =>
            Map(file, out var local) ? source.Read(local, maximumBytes) : installation.Read(file, maximumBytes);
    }
}
