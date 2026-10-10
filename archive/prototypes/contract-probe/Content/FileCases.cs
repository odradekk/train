// 本原型拥有的真实临时文件边界；不冒充生产存档容器或内容包加载器。
// 子进程在指定发布边界直接退出，用文件字节验证结果，而不是靠等待时间推断。
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Collections.Immutable;
using Train.Content.Contracts;
using static Train.ContractProbe.Content.Program;

namespace Train.ContractProbe.Content;

internal static class FileCases
{
    internal static void Run(string root, Action<string, Action> test)
    {
        test("io-single-file-exit-before-replace", () => CrashBoundary(root, "before"));
        test("io-single-file-exit-after-replace", () => CrashBoundary(root, "after"));
        test("io-locked-target-keeps-old", () => LockedTarget(root));
        test("io-external-edit-rejects-overwrite", () => ExternalEdit(root));
        test("io-package-partial-write-reports-files", () => PartialWrite(root));
        test("io-port-read-retains-returned-bytes", () => FixedRead(root));
    }

    private static string OwnDirectory(string root, string name)
    {
        var path = Path.Combine(Path.GetFullPath(root), name);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>已完成编码的文件在同目录替换；这是平台假设原型，取消只在替换之前裁决。</summary>
    private static void Replace(string target, string value)
    {
        var temporary = target + ".probe-new";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream, leaveOpen: true))
        {
            writer.Write(value);
            writer.Flush();
            stream.Flush(true);
        }
        File.Move(temporary, target, true);
    }

    internal static int WriteChild(string root, string stage)
    {
        var target = Path.Combine(root, "save.json");
        File.WriteAllText(target + ".probe-new", "{\"revision\":2}");
        using (var stream = new FileStream(target + ".probe-new", FileMode.Open, FileAccess.ReadWrite, FileShare.None)) stream.Flush(true);
        if (stage == "before") Environment.Exit(23);
        if (stage != "after") throw new ArgumentException("unknown_child_stage");
        File.Move(target + ".probe-new", target, true);
        Environment.Exit(24);
        return -1;
    }

    private static void CrashBoundary(string root, string stage)
    {
        var path = OwnDirectory(root, stage);
        var target = Path.Combine(path, "save.json");
        File.WriteAllText(target, "{\"revision\":1}");
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { Assembly.GetExecutingAssembly().Location, "--write-child", path, stage }) start.ArgumentList.Add(argument);
        using var child = Process.Start(start) ?? throw new InvalidOperationException("child_not_started");
        var stdout = child.StandardOutput.ReadToEndAsync();
        var stderr = child.StandardError.ReadToEndAsync();
        if (!child.WaitForExit(30_000)) { child.Kill(true); throw new TimeoutException("child_timeout"); }
        File.WriteAllText(Path.Combine(path, "child.log"), $"exit={child.ExitCode}\n{stdout.GetAwaiter().GetResult()}\n{stderr.GetAwaiter().GetResult()}");
        Check(child.ExitCode == (stage == "before" ? 23 : 24), "wrong_child_exit");
        using var state = JsonDocument.Parse(File.ReadAllText(target));
        Check(state.RootElement.GetProperty("revision").GetInt32() == (stage == "before" ? 1 : 2), "replace_boundary_wrong");
        Check(File.Exists(target + ".probe-new") == (stage == "before"), "temporary_file_not_distinguished");
    }

    private static void LockedTarget(string root)
    {
        var path = Path.Combine(OwnDirectory(root, "locked"), "save.json");
        File.WriteAllText(path, "old");
        using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            try { Replace(path, "new"); throw new InvalidOperationException("locked_replace_was_allowed"); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { Console.WriteLine($"EXPECTED locked_file: {e.GetType().Name}"); }
        }
        Check(File.ReadAllText(path) == "old", "locked_target_changed");
    }

    private static Dictionary<string, string> Capture(string root)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        void Walk(string directory)
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("linked_source");
                var relative = Path.GetRelativePath(root, path);
                if ((attributes & FileAttributes.Directory) != 0) { result.Add(relative, "directory"); Walk(path); }
                else result.Add(relative, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
            }
        }
        Walk(root);
        return result;
    }

    private static bool SameFiles(Dictionary<string, string> expected, Dictionary<string, string> actual)
        => expected.Count == actual.Count && expected.All(item => actual.TryGetValue(item.Key, out var value) && value == item.Value);

    private static void ExternalEdit(string root)
    {
        var path = OwnDirectory(root, "external");
        File.WriteAllText(Path.Combine(path, "a.json"), "{\"value\":1}");
        var basis = Capture(path);
        var originalTime = File.GetLastWriteTimeUtc(Path.Combine(path, "a.json"));
        File.WriteAllText(Path.Combine(path, "a.json"), "{\"value\":9}");
        File.SetLastWriteTimeUtc(Path.Combine(path, "a.json"), originalTime);
        var draft = "{\"value\":2}";
        var rejected = !SameFiles(basis, Capture(path));
        if (!rejected) Replace(Path.Combine(path, "a.json"), draft);
        Check(rejected && File.ReadAllText(Path.Combine(path, "a.json")) == "{\"value\":9}" && draft == "{\"value\":2}", "external_overwritten");
        // 同时间戳或同长度仍以字节比较；新增文件也属于完整来源变化。
        var afterEdit = Capture(path);
        File.WriteAllText(Path.Combine(path, "added.json"), "{}");
        Check(!SameFiles(afterEdit, Capture(path)), "new_file_not_detected");
        var afterAdd = Capture(path);
        Directory.CreateDirectory(Path.Combine(path, "models"));
        File.WriteAllBytes(Path.Combine(path, "models", "probe.glb"), [1, 2, 3]);
        Check(!SameFiles(afterAdd, Capture(path)), "nested_binary_not_detected");
        var afterNested = Capture(path);
        File.Delete(Path.Combine(path, "added.json"));
        Check(!SameFiles(afterNested, Capture(path)), "removed_file_not_detected");
    }

    private static void PartialWrite(string root)
    {
        var path = OwnDirectory(root, "partial");
        var first = Path.Combine(path, "a.json");
        var second = Path.Combine(path, "b.json");
        File.WriteAllText(first, "{\"revision\":1}");
        File.WriteAllText(second, "{\"revision\":1}");
        var completed = new List<string>();
        var failed = false;
        using (var held = new FileStream(second, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            try
            {
                Replace(first, "{\"revision\":2}"); completed.Add("a.json");
                Replace(second, "{\"revision\":2}"); completed.Add("b.json");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { failed = true; }
        }
        Check(failed && completed.SequenceEqual(["a.json"]), "partial_result_not_reported");
        Check(File.ReadAllText(first) == "{\"revision\":2}" && File.ReadAllText(second) == "{\"revision\":1}", "partial_disk_misrepresented");
        // 重启后的原始 JSON 错误可由真实解码检出；包语义加载仍须真实内容模块验证。
        File.WriteAllText(second, "{broken");
        try { using var document = JsonDocument.Parse(File.ReadAllText(second)); throw new InvalidOperationException("invalid_json_accepted"); }
        catch (JsonException) { Console.WriteLine("EXPECTED real_json_decode_failure"); }
        Check(File.Exists(second + ".probe-new"), "failed_write_evidence_lost");
    }

    private static void FixedRead(string root)
    {
        var path = Path.Combine(OwnDirectory(root, "fixed-read"), "asset.json");
        File.WriteAllBytes(path, [1, 2, 3]);
        IContentFiles port = new SingleFilePort(path);
        var bytes = ((ContentIoResult<ImmutableArray<byte>>.Success)port.Read(SingleFilePort.Address, 3)).Value;
        File.WriteAllBytes(path, [4, 5, 6]);
        Check(bytes.AsSpan().SequenceEqual(new byte[] { 1, 2, 3 }), "returned_bytes_changed");
        Check(port.Read(SingleFilePort.Address, 2) is ContentIoResult<ImmutableArray<byte>>.Failure { Kind: ContentIoFailure.LimitExceeded }, "read_was_truncated");
        Expect("unconfigured:file", () => port.Read(new(new("probe"), "other"), 3));
    }
}

/// <summary>只支持一个已分配文件句柄的真实 I/O 替身；未实现通用目录、PCK 或链接路径适配。</summary>
internal sealed class SingleFilePort(string path) : IContentFiles
{
    internal static readonly ContentPath Address = new(new("probe"), "bytes.bin");
    public ContentIoResult<ImmutableArray<ContentFileEntry>> Enumerate(ContentPath directory) => throw Unsupported.Call(nameof(Enumerate));
    public ContentIoResult<ImmutableArray<byte>> Read(ContentPath file, long maximumBytes)
    {
        if (file != Address) throw Unsupported.Call("file");
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        var bytes = File.ReadAllBytes(path);
        return bytes.LongLength > maximumBytes
            ? new ContentIoResult<ImmutableArray<byte>>.Failure(ContentIoFailure.LimitExceeded, "probe limit")
            : new ContentIoResult<ImmutableArray<byte>>.Success(bytes.ToImmutableArray());
    }
}
