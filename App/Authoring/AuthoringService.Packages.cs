using System.Collections.Immutable;
using Train.Content.Contracts;

namespace Train.App.Authoring;

internal sealed record AuthoringSource(string Directory, PackageId? Package, string Name,
    PackageState State, ImmutableArray<ContentDiagnostic> Diagnostics);

internal sealed partial class AuthoringService
{
    private Task<(PackageSnapshot Snapshot, ContentResult<IAuthoringPackage> Result)>? opening;
    private Task<ImmutableArray<AuthoringSource>>? discovering;
    private Task<AuthoringTarget>? creating;
    internal ImmutableArray<AuthoringSource> Sources { get; private set; } = [];
    internal void RefreshSources()
    {
        if (discovering is not null || !CanUse) return;
        var loaded = host.Content!.Packages;
        discovering = Task.Run(() => LocalAuthoringPackages.Discover(host.ContentFactory, ModsDirectory, loaded));
    }

    internal void CreateMod(PackageMetadata metadata)
    {
        if (!CanOpen) { Report("请等待当前操作结束后再新建模组。"); return; }
        if (editor.RequestClose(() =>
        {
            creating = Task.Run(() => LocalAuthoringPackages.Create(host.ContentFactory, ModsDirectory, metadata));
            Report("正在创建本地模组…");
        }) is { } failure) Report(failure.Message);
    }

    internal void OpenCore()
    {
        if (coreDirectory is not null) Open(coreDirectory, new("core"));
        else Report("当前构建的内置包只读。");
    }

    internal void Open(string directory, PackageId id)
    {
        if (!CanOpen) { Report("创作工具需要已载入内容的开发构建，请等待当前操作结束。"); return; }
        try
        {
            var full = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
            var isCore = coreDirectory is not null && SamePath(full, coreDirectory) && id.Value == "core";
            if (!isCore && (!SamePath(Path.GetDirectoryName(full)!, ModsDirectory) || id.Value == "core"))
            { Report("请选择 user://mods 下的本地模组目录。"); return; }
            if (editor.RequestClose(() => StartOpen(new(id, full, isCore))) is { } failure) Report(failure.Message);
        }
        catch (Exception error) { Report(error.Message); }
    }

    private static bool SamePath(string first, string second) => string.Equals(first, second,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private void StartOpen(AuthoringTarget target)
    {
        Target = target;
        if (!target.IsCore) host.EnableAuthoringPackage(target.Package);
        edits.Clear(); assetEdits.Clear();
        captureIssue = null; writeIssues = []; LastWrite = null; LastPublishSucceeded = null; WriteStage = AuthoringWriteStage.None;
        ReadSource(target);
        Report("正在读取源包…");
    }

    private void ReadSource(AuthoringTarget target)
    {
        binding = host.GetStatus()?.Binding;
        package = null; snapshot = null;
        var manifestDiagnostics = Sources.FirstOrDefault(source => SamePath(source.Directory, target.Directory))?.Diagnostics ?? [];
        var loadedDiagnostics = host.Content!.Packages.FirstOrDefault(status => status.Metadata?.Id == target.Package)?.Diagnostics ?? [];
        sourceIssues = manifestDiagnostics.Concat(loadedDiagnostics).Distinct().Select(item => Issue(item)).ToImmutableArray();
        opening = Task.Run(() =>
        {
            var source = PackageSnapshot.Capture(target.Directory);
            return (source, host.ContentFactory.OpenAuthoringPackage(new("authoring"), target.Package, source.Version, source));
        });
    }

    private void ProcessPackages()
    {
        if (creating is { IsCompleted: true } created)
        {
            creating = null;
            try
            {
                var target = created.GetAwaiter().GetResult();
                StartOpen(target); RefreshSources();
            }
            catch (Exception error) { Report("新建模组失败：" + error.Message); }
        }
        if (discovering is { IsCompleted: true } discovered)
        {
            discovering = null;
            try { Sources = discovered.GetAwaiter().GetResult(); Changed?.Invoke(); }
            catch (Exception error) { Report("无法列出本地模组：" + error.Message); }
        }
        if (opening is { IsCompleted: true } opened)
        {
            opening = null;
            try
            {
                var value = opened.GetAwaiter().GetResult();
                if (binding != host.GetStatus()?.Binding) return;
                if (value.Result is ContentResult<IAuthoringPackage>.Success success)
                {
                    snapshot = value.Snapshot; package = success.Value;
                    if (LastPublishSucceeded is null) Report("源包已载入。最终复核到写入结束期间，请确保此包只有一个写入者。");
                    else Changed?.Invoke();
                }
                else
                {
                    if (value.Result is ContentResult<IAuthoringPackage>.Failure failure)
                    {
                        sourceIssues = sourceIssues.Concat(failure.Diagnostics.Select(item => Issue(item))).Distinct().ToImmutableArray();
                        ReportOpenFailure(LastPublishSucceeded is null ? Describe(value.Result) : "源包无法读取，请检查源包问题。");
                    }
                    else ReportOpenFailure(Describe(value.Result));
                }
            }
            catch (Exception error) { ReportOpenFailure(error.Message); }
        }
    }

    private void ReportOpenFailure(string message) =>
        Report(LastPublishSucceeded is null ? message : Message + " 自动重新读取源包失败：" + message);
}
