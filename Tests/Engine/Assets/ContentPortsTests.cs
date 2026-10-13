using System.Collections.Immutable;
using Chickensoft.GoDotTest;
using Godot;
using Train.App.Assets;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using static Train.Engine.Tests.Assertions;
using Environment = System.Environment;

namespace Train.Engine.Tests.Assets;

public class ContentPortsTests(Node testScene) : TestClass(testScene)
{
    [Test]
    public async Task Background_calls_wait_for_pumping_and_shutdown_releases_waiters()
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        var mainThread = Environment.CurrentManagedThreadId;
        Require(dispatcher.Invoke(() => Environment.CurrentManagedThreadId) == mainThread, "主线程调用没有内联执行。");
        var queued = new TaskCompletionSource<Task<int>>(TaskCreationOptions.RunContinuationsAsynchronously);
        await Task.Run(() => queued.SetResult(dispatcher.Enqueue(() => Environment.CurrentManagedThreadId)));
        var pending = await queued.Task;
        Require(!pending.IsCompleted, "后台调用在泵送前完成。");
        dispatcher.Pump();
        Require(await pending == mainThread, "请求没有在主线程执行。");

        var cancelled = dispatcher.Enqueue(() => 1);
        dispatcher.Dispose();
        try { await cancelled; throw new InvalidOperationException("关闭后调用仍然成功。"); }
        catch (TaskCanceledException) { }
        try { dispatcher.Invoke(() => 1); throw new InvalidOperationException("关闭后仍然接收新请求。"); }
        catch (ObjectDisposedException) { }
    }

    [Test]
    public void Resource_roots_read_fixed_bytes_and_enumerate_source_tree_names()
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        var fixture = "res://.godot/validation/content-files-" + Guid.NewGuid().ToString("N");
        var physical = ProjectSettings.GlobalizePath(fixture);
        Directory.CreateDirectory(physical);
        try
        {
            System.IO.File.WriteAllBytes(Path.Combine(physical, "first.bin"), [1, 2, 3]);
            System.IO.File.WriteAllBytes(Path.Combine(physical, "second.bin"), [4, 5, 6]);
            var fixtureRoot = new ContentRoot("fixture");
            var port = new ContentFiles(dispatcher, new Dictionary<ContentRoot, ContentRootLocation>
            {
                [fixtureRoot] = new ContentRootLocation.Resource(fixture)
            });
            ContentFilesScenario.AssertFixedReadAndLimit(port, new(fixtureRoot, "first.bin"),
                ImmutableArray.Create<byte>(1, 2, 3), new(fixtureRoot, "second.bin"), ImmutableArray.Create<byte>(4, 5, 6));
        }
        finally
        {
            System.IO.File.Delete(Path.Combine(physical, "first.bin"));
            System.IO.File.Delete(Path.Combine(physical, "second.bin"));
            Directory.Delete(physical);
        }
        var root = new ContentRoot("builtin");
        IContentFiles files = new ContentFiles(dispatcher, new Dictionary<ContentRoot, ContentRootLocation>
        {
            [root] = new ContentRootLocation.Resource("res://builtin/legacy-host/resources/voxels/grain")
        });
        var entries = files.Enumerate(new(root, ""));
        Require(entries is ContentIoResult<ImmutableArray<ContentFileEntry>>.Success found &&
            found.Value.Any(entry => entry.Name == "placeholder.png" && entry.Kind == ContentEntryKind.File), "源码树枚举没有返回 PNG 文件名。");
        var path = new ContentPath(root, "placeholder.png");
        var result = files.Read(path, 4 * 1024 * 1024);
        Require(result is ContentIoResult<ImmutableArray<byte>>.Success, "原始 PNG 读取失败。");
        var bytes = ((ContentIoResult<ImmutableArray<byte>>.Success)result).Value;
        Require(bytes.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71 }), "读取的不是原始 PNG 字节。");
        Require(files.Read(path, bytes.Length - 1) is ContentIoResult<ImmutableArray<byte>>.Failure { Kind: ContentIoFailure.LimitExceeded }, "资源读取未遵守大小上限。");
        Require(files.Read(new(root, "missing.png"), 100) is ContentIoResult<ImmutableArray<byte>>.Failure { Kind: ContentIoFailure.Missing }, "缺失资源错误分类不正确。");
    }
}
