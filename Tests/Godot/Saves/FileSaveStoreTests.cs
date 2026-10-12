
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.App.Saves;

namespace Train.Godot.Tests.Saves;

[TestClass]
[TestCategory("family:headless")]
[TestCategory("scope:saves")]
public sealed class FileSaveStoreTests
{
    [TestMethod]
    public void Utf8_text_round_trips_and_overwrite_uses_the_same_target()
    {
        using var files = new SaveFiles();
        var id = Guid.Parse("12345678-abcd-4321-abcd-123456789abc");
        var target = Path.Combine(files.Root, "saves", "12345678-abcd-4321-abcd-123456789abc", "save.json");
        const string original = "{\"name\":\"中文存档 🚂\"}\n";
        const string replacement = "不是 JSON\r\n第二行";

        Assert.IsTrue(files.Store.TryWrite(id, original, out var writeStatus));
        Assert.AreEqual(SaveStoreStatus.Saved, writeStatus);
        Assert.AreEqual(target, files.Store.PathFor(id));
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes(original), File.ReadAllBytes(target));
        Assert.IsTrue(files.Store.TryRead(id, out var text, out var readStatus));
        Assert.AreEqual(SaveStoreStatus.Loaded, readStatus);
        Assert.AreEqual(original, text);

        Assert.IsTrue(files.Store.TryWrite(id, replacement, out writeStatus));
        Assert.AreEqual(SaveStoreStatus.Saved, writeStatus);
        Assert.IsTrue(files.Store.TryRead(id, out text, out readStatus));
        Assert.AreEqual(SaveStoreStatus.Loaded, readStatus);
        Assert.AreEqual(replacement, text);
        CollectionAssert.AreEqual(new[] { target }, Directory.GetFiles(files.Store.DirectoryFor(id)));
    }

    [TestMethod]
    public void Missing_target_does_not_read_or_publish_a_candidate()
    {
        using var files = new SaveFiles();
        var id = Guid.NewGuid();
        Assert.IsFalse(files.Store.TryRead(id, out var text, out var status));
        Assert.AreEqual(SaveStoreStatus.NotFound, status);
        Assert.AreEqual("", text);
        Assert.IsFalse(Directory.Exists(files.Store.DirectoryFor(id)));

        Directory.CreateDirectory(files.Store.DirectoryFor(id));
        var candidate = files.CandidateFor(id);
        File.WriteAllText(candidate, "未发布的候选");
        Assert.IsFalse(files.Store.TryRead(id, out text, out status));
        Assert.AreEqual(SaveStoreStatus.NotFound, status);
        Assert.AreEqual("", text);
        Assert.IsFalse(File.Exists(files.Store.PathFor(id)));
        Assert.AreEqual("未发布的候选", File.ReadAllText(candidate));
    }

    [TestMethod]
    public void Locked_target_returns_read_failed_with_empty_text()
    {
        using var files = new SaveFiles();
        var id = Guid.NewGuid();
        Assert.IsTrue(files.Store.TryWrite(id, "已有存档", out _));
        using var locked = new FileStream(files.Store.PathFor(id), FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Assert.IsFalse(files.Store.TryRead(id, out var text, out var status));
        Assert.AreEqual(SaveStoreStatus.ReadFailed, status);
        Assert.AreEqual("", text);
    }

    [TestMethod]
    public void Failed_overwrite_keeps_the_previous_file()
    {
        using var files = new SaveFiles();
        var id = Guid.NewGuid();
        Assert.IsTrue(files.Store.TryWrite(id, "上一份有效存档", out _));
        var before = File.ReadAllBytes(files.Store.PathFor(id));
        var candidate = files.CandidateFor(id);

        if (OperatingSystem.IsWindows())
        {
            // Windows 独占句柄阻止目标替换，失败发生在候选写完之后。
            using var locked = new FileStream(files.Store.PathFor(id), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Assert.IsFalse(files.Store.TryWrite(id, "不能发布的修改", out var status));
            Assert.AreEqual(SaveStoreStatus.WriteFailed, status);
        }
        else
        {
            // Unix 上打开文件不阻止路径替换，沿用目录障碍来触发实际文件写入失败。
            Directory.CreateDirectory(candidate);
            Assert.IsFalse(files.Store.TryWrite(id, "不能发布的修改", out var status));
            Assert.AreEqual(SaveStoreStatus.WriteFailed, status);
        }

        CollectionAssert.AreEqual(before, File.ReadAllBytes(files.Store.PathFor(id)));
        Assert.IsFalse(File.Exists(candidate));
        Assert.IsTrue(files.Store.TryRead(id, out var text, out var readStatus));
        Assert.AreEqual(SaveStoreStatus.Loaded, readStatus);
        Assert.AreEqual("上一份有效存档", text);
    }

    [TestMethod]
    public void Publication_failure_cleans_candidate_and_preserves_target_directory()
    {
        using var files = new SaveFiles();
        var id = Guid.NewGuid();
        Directory.CreateDirectory(files.Store.PathFor(id));
        var marker = Path.Combine(files.Store.PathFor(id), "existing.txt");
        File.WriteAllText(marker, "保留目录中的文件");

        Assert.IsFalse(files.Store.TryWrite(id, "候选内容", out var status));
        Assert.AreEqual(SaveStoreStatus.WriteFailed, status);
        Assert.IsFalse(File.Exists(files.CandidateFor(id)));
        Assert.IsTrue(Directory.Exists(files.Store.PathFor(id)));
        Assert.AreEqual("保留目录中的文件", File.ReadAllText(marker));
    }

    [TestMethod]
    public void New_save_refuses_to_replace_existing_file_and_cleans_candidate()
    {
        using var files = new SaveFiles();
        var id = Guid.NewGuid();
        Assert.IsTrue(files.Store.TryWriteNew(id, "原存档", out var status));
        Assert.AreEqual(SaveStoreStatus.Saved, status);
        var before = File.ReadAllBytes(files.Store.PathFor(id));

        Assert.IsFalse(files.Store.TryWriteNew(id, "发生身份冲突的存档", out status));
        Assert.AreEqual(SaveStoreStatus.WriteFailed, status);
        CollectionAssert.AreEqual(before, File.ReadAllBytes(files.Store.PathFor(id)));
        Assert.IsFalse(File.Exists(files.CandidateFor(id)));
    }

    [TestMethod]
    public void Locked_candidate_returns_write_failed_even_when_cleanup_cannot_finish()
    {
        using var files = new SaveFiles();
        var id = Guid.NewGuid();
        Assert.IsTrue(files.Store.TryWrite(id, "原存档", out _));
        var before = File.ReadAllBytes(files.Store.PathFor(id));
        File.WriteAllText(files.CandidateFor(id), "残留候选");
        using (var locked = new FileStream(files.CandidateFor(id), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.IsFalse(files.Store.TryWrite(id, "修改后的存档", out var status));
            Assert.AreEqual(SaveStoreStatus.WriteFailed, status);
            if (OperatingSystem.IsWindows())
            {
                // 同一个独占句柄同时阻止候选写入和失败后的删除。
                Assert.IsTrue(File.Exists(files.CandidateFor(id)));
            }
        }

        CollectionAssert.AreEqual(before, File.ReadAllBytes(files.Store.PathFor(id)));
    }

    private sealed class SaveFiles : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("train-save-store-").FullName;
        public FileSaveStore Store { get; }

        public SaveFiles() => Store = new FileSaveStore(Root);

        public string CandidateFor(Guid id) => Path.Combine(Store.DirectoryFor(id), FileSaveStore.CandidateFileName);

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
