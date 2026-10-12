using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.App.Assets;
using Train.Content.Contracts;
using Train.Content.ContractSupport;

namespace Train.Godot.Tests.Assets;

[TestClass]
[TestCategory("integration")]
public sealed class DiskContentFilesTests
{
    [TestMethod]
    public void Reads_complete_fixed_bytes_and_enumerates_only_direct_children()
    {
        using var files = new DiskFiles();
        File.WriteAllBytes(files.File("first.bin"), [1, 2, 3]);
        File.WriteAllBytes(files.File("second.bin"), [4, 5, 6]);
        Directory.CreateDirectory(files.File("nested"));
        File.WriteAllText(files.File("nested/child.txt"), "child");
        ContentFilesScenario.AssertFixedReadAndLimit(files.Port, files.Path("first.bin"),
            ImmutableArray.Create<byte>(1, 2, 3), files.Path("second.bin"), ImmutableArray.Create<byte>(4, 5, 6));
        var retained = Success(files.Port.Read(files.Path("first.bin"), 3));
        File.WriteAllBytes(files.File("first.bin"), [9]);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, retained.ToArray());
        var entries = Success(files.Port.Enumerate(files.Path(""))).ToDictionary(entry => entry.Name);
        Assert.AreEqual(3, entries.Count);
        Assert.AreEqual(ContentEntryKind.File, entries["first.bin"].Kind);
        Assert.AreEqual(ContentEntryKind.Directory, entries["nested"].Kind);
    }

    [TestMethod]
    public void Missing_locked_and_zero_byte_limits_are_distinct()
    {
        using var files = new DiskFiles();
        Failure(files.Port.Read(files.Path("missing.bin"), 0), ContentIoFailure.Missing);
        Failure(files.Port.Enumerate(files.Path("missing")), ContentIoFailure.Missing);
        File.WriteAllBytes(files.File("empty.bin"), []);
        Assert.AreEqual(0, Success(files.Port.Read(files.Path("empty.bin"), 0)).Length);
        File.WriteAllBytes(files.File("full.bin"), [1]);
        Failure(files.Port.Read(files.Path("full.bin"), 0), ContentIoFailure.LimitExceeded);
        using var locked = new FileStream(files.File("full.bin"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Failure(files.Port.Read(files.Path("full.bin"), 1), ContentIoFailure.Inaccessible);
    }

    [TestMethod]
    public void Invalid_relative_paths_are_rejected_before_io()
    {
        using var files = new DiskFiles();
        foreach (var path in new[] { "", "/file", "../file", "nested/../file", "nested//file", "nested/", "C:/file", "nested\\file" })
            Assert.ThrowsExactly<ArgumentException>(() => files.Port.Read(files.Path(path), 1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => files.Port.Read(files.Path("file"), -1));
    }

    [TestMethod]
    public void Junctions_are_listed_but_never_traversed_in_paths_roots_or_root_ancestors()
    {
        using var files = new DiskFiles();
        var target = Directory.CreateDirectory(files.File("target")).FullName;
        File.WriteAllBytes(Path.Combine(target, "asset.bin"), [1]);
        Directory.CreateDirectory(Path.Combine(target, "nested"));
        var junction = files.File("link");
        using var process = Process.Start(new ProcessStartInfo("cmd.exe")
        {
            Arguments = $"/c mklink /J \"{junction}\" \"{target}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        })!;
        process.WaitForExit();
        Assert.AreEqual(0, process.ExitCode, process.StandardError.ReadToEnd());
        try
        {
            Assert.AreEqual(ContentEntryKind.Link,
                Success(files.Port.Enumerate(files.Path(""))).Single(entry => entry.Name == "link").Kind);
            Failure(files.Port.Read(files.Path("link/asset.bin"), 1), ContentIoFailure.LinkedPath);
            Failure(files.Port.Enumerate(files.Path("link")), ContentIoFailure.LinkedPath);
            Failure(new DiskContentFiles(junction).Read(files.Path("asset.bin"), 1), ContentIoFailure.LinkedPath);
            Failure(new DiskContentFiles(Path.Combine(junction, "nested")).Enumerate(files.Path("")), ContentIoFailure.LinkedPath);
        }
        finally
        {
            Directory.Delete(junction);
        }
    }

    [TestMethod]
    public void Bounded_reader_detects_growth_after_first_chunk_and_reads_only_limit_plus_one()
    {
        using var files = new DiskFiles();
        var path = files.File("growing.bin");
        File.WriteAllBytes(path, new byte[8192]);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var growing = new GrowingFile(input, path);
        Failure(DiskContentFiles.ReadBounded(growing, 8192), ContentIoFailure.LimitExceeded);
        Assert.AreEqual(8193L, growing.BytesRead);
        Assert.AreEqual(8194L, new FileInfo(path).Length);
    }

    private static T Success<T>(ContentIoResult<T> result) where T : notnull
    {
        Assert.IsInstanceOfType<ContentIoResult<T>.Success>(result);
        return ((ContentIoResult<T>.Success)result).Value;
    }

    private static void Failure<T>(ContentIoResult<T> result, ContentIoFailure kind) where T : notnull
    {
        Assert.IsInstanceOfType<ContentIoResult<T>.Failure>(result);
        Assert.AreEqual(kind, ((ContentIoResult<T>.Failure)result).Kind);
    }

    private sealed class DiskFiles : IDisposable
    {
        public string Root { get; } = Directory.CreateDirectory(System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "disk-content-" + Guid.NewGuid().ToString("N"))).FullName;
        public DiskContentFiles Port { get; }
        public DiskFiles() => Port = new DiskContentFiles(Root);
        public string File(string relative) => System.IO.Path.Combine(Root, relative);
        public ContentPath Path(string relative) => new(new ContentRoot("disk"), relative);
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private sealed class GrowingFile(FileStream input, string path) : Stream
    {
        public long BytesRead { get; private set; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = input.Read(buffer, offset, count);
            if (BytesRead == 0 && read > 0)
            {
                using var append = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                append.Write([1, 2]);
            }
            BytesRead += read;
            return read;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
