using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.App;
using Train.App.Authoring;
using Train.App.Assets;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Godot.Tests.Host;

namespace Train.Godot.Tests.Authoring;

[TestClass]
[TestCategory("integration")]
public sealed class PackageFilesTests
{
    [TestMethod]
    public void Installation_snapshot_preserves_missing_roots_and_rebuilds_patched_directory_entries()
    {
        using var files = new PackageFiles();
        files.Put("core/original.txt", "original");
        var root = new ContentRoot("installation");
        var missing = new ContentRoot("missing");
        var source = new RootFiles(root, new DiskContentFiles(files.Root));
        var snapshot = ContentFilesSnapshot.Capture(source, [root, missing]);
        files.Put("core/original.txt", "external");
        var patched = snapshot.WithChanges(root, "core", [Change("new/first.txt", "1"), Change("new/second.txt", "2")]);
        var entries = ContentFilesSnapshot.Require(patched.Enumerate(new(root, "core")));
        Assert.AreEqual(1, entries.Count(e => e.Name == "new"));
        Assert.AreEqual("original", Encoding.UTF8.GetString(ContentFilesSnapshot.Require(snapshot.Read(new(root, "core/original.txt"), 99)).AsSpan()));
        Assert.IsInstanceOfType<ContentIoResult<ImmutableArray<ContentFileEntry>>.Failure>(patched.Enumerate(new(missing, "")));
        Assert.ThrowsExactly<ArgumentException>(() => snapshot.WithChanges(root, "core", [Change("original.txt/nested.txt", "invalid")]));
    }

    [TestMethod]
    public void Full_snapshot_detects_same_length_changes_and_added_or_deleted_entries()
    {
        using var files = new PackageFiles();
        files.Put("original.txt", "old");
        var snapshot = PackageSnapshot.Capture(files.Root);
        files.Put("original.txt", "new");
        Assert.IsFalse(snapshot.MatchesDisk());
        var refused = PackageWriter.Execute(snapshot, Plan(snapshot, Change("output.txt", "x")), false);
        Assert.IsFalse(refused.Success);
        Assert.AreEqual(0, refused.Completed.Length);
        Assert.IsFalse(File.Exists(files.PathFor("output.txt")));
        files.Put("original.txt", "old");
        Assert.IsTrue(snapshot.MatchesDisk());
        Directory.CreateDirectory(files.PathFor("empty"));
        Assert.IsFalse(snapshot.MatchesDisk());
        Directory.Delete(files.PathFor("empty"));
        files.Put("extra", "added");
        Assert.IsFalse(snapshot.MatchesDisk());
        File.Delete(files.PathFor("extra"));
        File.Delete(files.PathFor("original.txt"));
        Assert.IsFalse(snapshot.MatchesDisk());
    }

    [TestMethod]
    public void Explicit_delete_preserves_unmentioned_files_and_snapshot_overlay_is_immutable()
    {
        using var files = new PackageFiles();
        files.Put("delete.txt", "delete");
        files.Put("shared.png", "shared");
        var snapshot = PackageSnapshot.Capture(files.Root);
        ImmutableArray<PackageFileChange> changes = [new("delete.txt", null), Change("nested/new.txt", "new")];
        var overlay = snapshot.WithChanges(changes);
        Assert.IsInstanceOfType<ContentIoResult<ImmutableArray<byte>>.Failure>(overlay.Read(new(default, "delete.txt"), 99));
        Assert.IsInstanceOfType<ContentIoResult<ImmutableArray<byte>>.Success>(snapshot.Read(new(default, "delete.txt"), 99));
        Assert.IsTrue(PackageWriter.Execute(snapshot, Plan(snapshot, changes.ToArray()), false).Success);
        Assert.IsFalse(File.Exists(files.PathFor("delete.txt")));
        Assert.AreEqual("shared", File.ReadAllText(files.PathFor("shared.png")));
        Assert.AreEqual("new", File.ReadAllText(files.PathFor("nested/new.txt")));
    }

    [TestMethod]
    public void Keep_file_precedes_new_core_asset_and_preserves_existing_import_settings()
    {
        using var files = new PackageFiles();
        files.Put("existing.glb.import", "existing settings");
        var snapshot = PackageSnapshot.Capture(files.Root);
        var result = PackageWriter.Execute(snapshot, Plan(snapshot, Change("new.png", "png"), Change("existing.glb", "glb")), true);
        Assert.IsTrue(result.Success);
        Assert.AreEqual("new.png.import", result.Completed[0].RelativePath);
        Assert.AreEqual("[remap]\n\nimporter=\"keep\"\n", File.ReadAllText(files.PathFor("new.png.import")));
        Assert.AreEqual("existing settings", File.ReadAllText(files.PathFor("existing.glb.import")));
        snapshot = PackageSnapshot.Capture(files.Root);
        Assert.IsTrue(PackageWriter.Execute(snapshot, Plan(snapshot, Change("mod.png", "png")) with { Package = new("mod") }, true).Success);
        Assert.IsFalse(File.Exists(files.PathFor("mod.png.import")));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    public void Locked_file_stops_after_completed_items_and_preserves_draft()
    {
        using var files = new PackageFiles();
        files.Put("blocked.txt", "old");
        files.Put("draft.json", "saved draft");
        var snapshot = PackageSnapshot.Capture(files.Root);
        // The baseline remains readable while replacement is denied.
        using var locked = new FileStream(files.PathFor("blocked.txt"), FileMode.Open, FileAccess.Read, FileShare.Read);
        var result = PackageWriter.Execute(snapshot, Plan(snapshot, Change("first.txt", "first"), Change("blocked.txt", "new"), Change("last.txt", "last")), false);
        Assert.IsFalse(result.Success);
        Assert.AreEqual(1, result.Completed.Length);
        Assert.AreEqual(2, result.Remaining.Length);
        Assert.AreEqual("blocked.txt", result.Failed!.RelativePath);
        Assert.AreEqual("first", File.ReadAllText(files.PathFor("first.txt")));
        Assert.IsFalse(File.Exists(files.PathFor("last.txt")));
        Assert.AreEqual("saved draft", File.ReadAllText(files.PathFor("draft.json")));
    }

    [TestMethod]
    public async Task Process_interruption_leaves_only_acknowledged_writes_and_preserves_saved_draft()
    {
        using var files = new PackageFiles();
        files.Put("draft.json", "saved draft");
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true
        };
        foreach (var argument in new[] { typeof(PackageFilesTests).Assembly.Location, "--package-write-process", files.Root, "1" }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            Assert.AreEqual("completed:1", await process.StandardOutput.ReadLineAsync(timeout.Token));
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
        Assert.AreEqual("first", File.ReadAllText(files.PathFor("first.txt")));
        Assert.IsFalse(File.Exists(files.PathFor("second.txt")));
        Assert.IsFalse(File.Exists(files.PathFor("third.txt")));
        Assert.AreEqual("saved draft", File.ReadAllText(files.PathFor("draft.json")));

        var restart = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in new[] { typeof(PackageFilesTests).Assembly.Location, "--package-load-process", files.Root }) restart.ArgumentList.Add(argument);
        using var restarted = Process.Start(restart)!;
        using var restartTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var stdout = restarted.StandardOutput.ReadToEndAsync();
        var stderr = restarted.StandardError.ReadToEndAsync();
        try { await restarted.WaitForExitAsync(restartTimeout.Token); }
        finally
        {
            if (!restarted.HasExited) { restarted.Kill(entireProcessTree: true); await restarted.WaitForExitAsync(); }
        }
        Assert.AreEqual(0, restarted.ExitCode, await stderr);
        StringAssert.Contains(await stdout, "incomplete-test-package|部分写入：缺少 second.txt、third.txt。");
        Assert.AreEqual("first", File.ReadAllText(files.PathFor("first.txt")), "普通加载不能自动恢复旧包。");
        Assert.IsFalse(File.Exists(files.PathFor("second.txt")));
        Assert.IsFalse(File.Exists(files.PathFor("third.txt")));
        Assert.AreEqual("saved draft", File.ReadAllText(files.PathFor("draft.json")));
    }

    internal static int LoadInChild(string directory)
    {
        // This finite content scenario checks host restart/loading diagnostics, not the production package decoder.
        using var fixture = new HostFixture();
        var loadCalls = 0;
        fixture.Factory.PrepareLoadCall = (request, files, _) =>
        {
            loadCalls++;
            Assert.IsInstanceOfType<ContentLoadPurpose.Initial>(request.Purpose);
            Assert.AreEqual("first", Encoding.UTF8.GetString(ContentFilesSnapshot.Require(files.Read(new(request.Installation.BuiltIn, "first.txt"), 99)).AsSpan()));
            foreach (var missing in new[] { "second.txt", "third.txt" })
            {
                var read = files.Read(new(request.Installation.BuiltIn, missing), 99);
                Assert.IsInstanceOfType<ContentIoResult<ImmutableArray<byte>>.Failure>(read);
                Assert.AreEqual(ContentIoFailure.Missing, ((ContentIoResult<ImmutableArray<byte>>.Failure)read).Kind);
            }
            return new ContentResult<IContentCandidate>.Failure([new("incomplete-test-package", ContentDiagnosticSeverity.Error,
                "loading", null, new(new("core"), "second.txt"), "files", null, "部分写入：缺少 second.txt、third.txt。")]);
        };
        using var host = new HostCoordinator(fixture.Core, fixture.Factory, new DiskContentFiles(directory),
            new UnconfiguredAssetInspector(), new(new("builtin"), new("mods"), null, "test", []), fixture.Logger);
        HostOperationResult? ended = null;
        host.OperationEnded += result => ended = result;
        Assert.IsNull(host.BeginReload(CommandSource.UserInterface).Problem);
        Assert.IsTrue(SpinWait.SpinUntil(() => { host.ProcessFrame(0); return ended is not null; }, HostFixture.HangTimeout), "重启普通加载超时。");
        Assert.AreEqual(1, loadCalls);
        Assert.IsFalse(ended!.Succeeded);
        Assert.IsNull(host.Content);
        Assert.IsNull(host.GetStatus());
        Assert.AreEqual(1, ended.Diagnostics.Length);
        Assert.AreEqual("incomplete-test-package", ended.Diagnostics[0].Code);
        System.Console.WriteLine($"{ended.Diagnostics[0].Code}|{ended.Diagnostics[0].Message}");
        System.Console.Out.Flush();
        Assert.AreEqual("first", File.ReadAllText(Path.Combine(directory, "first.txt")));
        Assert.IsFalse(File.Exists(Path.Combine(directory, "second.txt")));
        Assert.IsFalse(File.Exists(Path.Combine(directory, "third.txt")));
        Assert.AreEqual("saved draft", File.ReadAllText(Path.Combine(directory, "draft.json")));
        return 0;
    }

    internal static int WriteInChild(string directory, int stopAfter)
    {
        var snapshot = PackageSnapshot.Capture(directory);
        var count = 0;
        var result = PackageWriter.Execute(snapshot, Plan(snapshot, Change("first.txt", "first"), Change("second.txt", "second"), Change("third.txt", "third")), false, _ =>
        {
            if (++count != stopAfter) return;
            System.Console.WriteLine($"completed:{count}");
            System.Console.Out.Flush();
            System.Console.ReadLine();
        });
        return result.Success ? 0 : 1;
    }

    private static PackageFileChange Change(string path, string text) => new(path, ImmutableArray.CreateRange(Encoding.UTF8.GetBytes(text)));
    private static PackageWritePlan Plan(PackageSnapshot snapshot, params PackageFileChange[] changes) => new(new("core"), snapshot.Version, [.. changes], []);
    private sealed class RootFiles(ContentRoot root, IContentFiles files) : IContentFiles
    {
        public ContentIoResult<ImmutableArray<ContentFileEntry>> Enumerate(ContentPath path) => path.Root == root
            ? files.Enumerate(path) : new ContentIoResult<ImmutableArray<ContentFileEntry>>.Failure(ContentIoFailure.Missing, "missing");
        public ContentIoResult<ImmutableArray<byte>> Read(ContentPath path, long maximumBytes) => path.Root == root
            ? files.Read(path, maximumBytes) : new ContentIoResult<ImmutableArray<byte>>.Failure(ContentIoFailure.Missing, "missing");
    }
    private sealed class PackageFiles : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "train-package-files-" + Guid.NewGuid().ToString("N"));
        public PackageFiles() => Directory.CreateDirectory(Root);
        public string PathFor(string path) => Path.Combine(Root, path.Replace('/', Path.DirectorySeparatorChar));
        public void Put(string path, string text) { Directory.CreateDirectory(Path.GetDirectoryName(PathFor(path))!); File.WriteAllText(PathFor(path), text); }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
