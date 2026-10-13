using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.App.Assets;
using Train.Content.Contracts;
using Train.Content.ContractSupport;

namespace Train.Godot.Tests.Assets;

[TestClass]
public sealed class DiskContentFilesTests
{
    [TestMethod]
    [TestCategory("integration")]
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
    [TestCategory("integration")]
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
    [TestCategory("integration")]
    public void Invalid_relative_paths_are_rejected_before_io()
    {
        using var files = new DiskFiles();
        foreach (var path in new[] { "", "/file", "../file", "nested/../file", "nested//file", "nested/", "C:/file", "nested\\file" })
            Assert.ThrowsExactly<ArgumentException>(() => files.Port.Read(files.Path(path), 1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => files.Port.Read(files.Path("file"), -1));
    }

    [TestMethod]
    [TestCategory("integration")]
    public void Junctions_at_or_inside_root_are_rejected_but_linked_ancestors_are_allowed()
    {
        using var files = new DiskFiles();
        var target = Directory.CreateDirectory(files.File("target")).FullName;
        File.WriteAllBytes(Path.Combine(target, "asset.bin"), [1]);
        Directory.CreateDirectory(Path.Combine(target, "nested"));
        File.WriteAllBytes(Path.Combine(target, "nested", "asset.bin"), [2]);
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
            CollectionAssert.AreEqual(new byte[] { 2 }, Success(new DiskContentFiles(Path.Combine(junction, "nested"))
                .Read(files.Path("asset.bin"), 1)).ToArray());
        }
        finally
        {
            Directory.Delete(junction);
        }
    }

    [TestMethod]
    public void Bounded_reader_reads_only_limit_plus_one_without_querying_stream_length()
    {
        using var input = new NonSeekableInput(new byte[8194]);
        Failure(DiskContentFiles.ReadBounded(input, 8192), ContentIoFailure.LimitExceeded);
        Assert.AreEqual(8193, input.BytesRead);
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

    private sealed class NonSeekableInput(byte[] bytes) : Stream
    {
        public int BytesRead { get; private set; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = Math.Min(count, bytes.Length - BytesRead);
            bytes.AsSpan(BytesRead, read).CopyTo(buffer.AsSpan(offset, read));
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
