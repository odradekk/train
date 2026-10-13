using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.App.Saves;

namespace Train.Godot.Tests.Saves;

[TestClass]
[TestCategory("integration")]
public sealed class FileSaveStoreTests
{
    [TestMethod]
    public void Invalid_utf8_is_rejected_without_replacing_damaged_text_or_changing_the_file()
    {
        using var files = new SaveFiles();
        var document = SaveContainerTests.Document();
        var text = SaveContainer.Encode(document with { Header = document.Header with { Name = "readable-name" } });
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        var offset = System.Text.Encoding.UTF8.GetByteCount(text.AsSpan(0, text.IndexOf("readable-name", StringComparison.Ordinal)));
        bytes[offset] = 0xff;
        Directory.CreateDirectory(Path.GetDirectoryName(files.Target)!);
        File.WriteAllBytes(files.Target, bytes);
        Assert.ThrowsExactly<SaveFormatException>(() => files.Store.Read(files.Target));
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(files.Target));
    }

    [TestMethod]
    public void Valid_document_round_trips_and_export_can_use_external_target()
    {
        using var files = new SaveFiles();
        var document = SaveContainerTests.Document();
        var target = Path.Combine(files.Root, "export", "玩家存档.json");
        Assert.IsTrue(files.Store.Write(target, document, false).Published);
        Assert.AreEqual(document.CorePayload, files.Store.Read(target).CorePayload);
        Assert.AreEqual(document.Header.Id, files.Store.ReadHeader(target).Id);
        Assert.AreEqual(Path.Combine(files.Store.DirectoryFor(document.Header.Id), "autosave-2.json"), files.Store.AutoPathFor(document.Header.Id, 2));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => files.Store.AutoPathFor(document.Header.Id, 3));
        CollectionAssert.AreEqual(new[] { target }, Directory.GetFiles(Path.GetDirectoryName(target)!));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    public void Locked_target_failure_preserves_previous_file_and_cleans_candidate()
    {
        using var files = new SaveFiles();
        files.SaveOriginal();
        var before = File.ReadAllBytes(files.Target);
        using (var locked = new FileStream(files.Target, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var result = files.Store.Write(files.Target, SaveContainerTests.Document("replacement"), true);
            Assert.IsFalse(result.Published);
            Assert.AreEqual("write-failed", result.Code);
        }
        CollectionAssert.AreEqual(before, File.ReadAllBytes(files.Target));
        files.AssertOnlyTarget();
    }

    [TestMethod]
    public void Candidate_validation_failure_preserves_old_target()
    {
        using var files = new SaveFiles();
        files.SaveOriginal();
        var store = new FileSaveStore(files.Root, (path, _) => FileSaveStore.WriteCandidate(path, "broken"), File.Move);
        Assert.AreEqual("invalid-save", store.Write(files.Target, SaveContainerTests.Document(), true).Code);
        Assert.AreEqual("original", files.Store.Read(files.Target).CorePayload);
        files.AssertOnlyTarget();
    }

    [TestMethod]
    public void Cancellation_before_final_publication_preserves_old_target()
    {
        using var files = new SaveFiles();
        files.SaveOriginal();
        using var cancellation = new CancellationTokenSource();
        var store = new FileSaveStore(files.Root, (path, json) =>
        {
            FileSaveStore.WriteCandidate(path, json);
            cancellation.Cancel();
        }, (_, _, _) => Assert.Fail("取消后不得发布"));
        Assert.AreEqual("cancelled", store.Write(files.Target, SaveContainerTests.Document(), true, cancellation.Token).Code);
        Assert.AreEqual("original", files.Store.Read(files.Target).CorePayload);
        files.AssertOnlyTarget();
    }

    [TestMethod]
    public void Cancellation_after_replacement_is_success()
    {
        using var files = new SaveFiles();
        files.SaveOriginal();
        using var cancellation = new CancellationTokenSource();
        var store = new FileSaveStore(files.Root, FileSaveStore.WriteCandidate, (candidate, target, overwrite) =>
        {
            File.Move(candidate, target, overwrite);
            cancellation.Cancel();
        });
        var result = store.Write(files.Target, SaveContainerTests.Document("replacement"), true, cancellation.Token);
        Assert.IsTrue(result.Published, $"{result.Code}: {result.Message}; cleanup: {result.CleanupMessage}");
        Assert.IsNull(result.Code);
        Assert.AreEqual("replacement", files.Store.Read(files.Target).CorePayload);
        files.AssertOnlyTarget();
    }

    [TestMethod]
    public void Candidates_are_unique_and_existing_target_cannot_be_overwritten_without_permission()
    {
        using var files = new SaveFiles();
        files.SaveOriginal();
        var candidates = new List<string>();
        var store = new FileSaveStore(files.Root, (path, json) =>
        {
            candidates.Add(path);
            FileSaveStore.WriteCandidate(path, json);
        }, File.Move);
        for (var i = 0; i < 2; i++)
            Assert.IsFalse(store.Write(files.Target, SaveContainerTests.Document(), false).Published);
        Assert.AreNotEqual(candidates[0], candidates[1]);
        Assert.IsTrue(candidates.All(path => Path.GetDirectoryName(path) == Path.GetDirectoryName(files.Target)));
        Assert.AreEqual("original", files.Store.Read(files.Target).CorePayload);
        files.AssertOnlyTarget();
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    public void Cleanup_failure_preserves_primary_write_failure()
    {
        using var files = new SaveFiles();
        files.SaveOriginal();
        FileStream? locked = null;
        string? leftover = null;
        var store = new FileSaveStore(files.Root, (candidate, _) =>
        {
            leftover = candidate;
            locked = new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            throw new IOException("primary write failure");
        }, File.Move);
        try
        {
            var result = store.Write(files.Target, SaveContainerTests.Document(), true);
            Assert.IsFalse(result.Published);
            Assert.AreEqual("write-failed", result.Code);
            Assert.AreEqual("primary write failure", result.Message);
            Assert.IsNotNull(result.CleanupMessage);
            Assert.AreEqual("original", files.Store.Read(files.Target).CorePayload);
        }
        finally
        {
            locked?.Dispose();
            if (leftover is not null) File.Delete(leftover);
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    public void Cleanup_failure_after_publication_reports_success_with_cleanup_diagnostic()
    {
        using var files = new SaveFiles();
        files.SaveOriginal();
        FileStream? locked = null;
        string? leftover = null;
        using var cancellation = new CancellationTokenSource();
        var store = new FileSaveStore(files.Root, FileSaveStore.WriteCandidate, (candidate, target, overwrite) =>
        {
            File.Move(candidate, target, overwrite);
            leftover = candidate;
            locked = new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            cancellation.Cancel();
        });
        try
        {
            var result = store.Write(files.Target, SaveContainerTests.Document("replacement"), true, cancellation.Token);
            Assert.IsTrue(result.Published);
            Assert.IsNull(result.Code);
            Assert.IsNotNull(result.CleanupMessage);
            Assert.AreEqual("replacement", files.Store.Read(files.Target).CorePayload);
        }
        finally
        {
            locked?.Dispose();
            if (leftover is not null) File.Delete(leftover);
        }
    }
    private sealed class SaveFiles : IDisposable
    {
        internal string Root { get; } = Directory.CreateTempSubdirectory("train-save-store-").FullName;
        internal FileSaveStore Store { get; }
        internal string Target => Store.PathFor(Guid.Parse("12345678-abcd-4321-abcd-123456789abc"));
        internal SaveFiles() => Store = new(Root);
        internal void SaveOriginal() => Assert.IsTrue(Store.Write(Target, SaveContainerTests.Document("original"), false).Published);
        internal void AssertOnlyTarget() => CollectionAssert.AreEqual(new[] { Target }, Directory.GetFiles(Path.GetDirectoryName(Target)!));
        public void Dispose() => Directory.Delete(Root, true);
    }
}
