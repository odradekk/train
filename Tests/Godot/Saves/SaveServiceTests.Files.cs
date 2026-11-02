using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.App.Saves;

namespace Train.Godot.Tests.Saves;

public sealed partial class SaveServiceTests
{
    [TestMethod]
    public void Renaming_rejects_a_mismatched_sibling_before_publishing_any_new_names()
    {
        using var run = new Run();
        var source = run.Source();
        File.Copy(source, run.Store.PathFor(run.Id));
        var automatic = run.Store.AutoPathFor(run.Id, 0);
        var wrong = run.Store.Read(source) with { Header = run.Store.ReadHeader(source) with { Id = Guid.NewGuid() } };
        File.WriteAllText(automatic, SaveContainer.Encode(wrong));
        var original = File.ReadAllBytes(run.Store.PathFor(run.Id));
        var before = run.Service.Status;
        Assert.IsNull(run.Service.Rename(run.Service.List().Single(), "不能部分改名"));
        run.Drain();
        CollectionAssert.AreEqual(original, File.ReadAllBytes(run.Store.PathFor(run.Id)));
        Assert.AreEqual(before, run.Service.Status);
    }

    [TestMethod]
    public void Renaming_another_saved_game_does_not_rename_the_active_session()
    {
        using var run = new Run();
        run.Service.Save(); run.Drain();
        var entry = run.Service.List().Single();
        run.Service.SaveAs("另一个对局"); run.Drain();
        var before = run.Service.Status;
        Assert.IsNull(run.Service.Rename(entry, "旧对局的新名称")); run.Drain();
        Assert.AreEqual(before, run.Service.Status);
        Assert.AreEqual("旧对局的新名称", run.Store.ReadHeader(entry.Path).Name);
    }

    [TestMethod]
    public void Failed_rename_keeps_the_original_file_and_active_name()
    {
        using var run = new Run(failPublish: true);
        var source = run.Source();
        File.Copy(source, run.Store.PathFor(run.Id));
        var entry = run.Service.List().Single();
        var original = File.ReadAllBytes(entry.Path);
        var before = run.Service.Status;
        Assert.IsNull(run.Service.Rename(entry, "写入失败")); run.Drain();
        CollectionAssert.AreEqual(original, File.ReadAllBytes(entry.Path));
        Assert.AreEqual(before, run.Service.Status);
        StringAssert.Contains(run.Service.PlayerMessage!, "未完成");
    }

    [TestMethod]
    [DataRow((int)LeaveAction.Load)]
    [DataRow((int)LeaveAction.Menu)]
    public void Leave_prompt_identifies_the_pending_load_or_return_to_menu(int actionValue)
    {
        var action = (LeaveAction)actionValue;
        using var run = new Run();
        var failure = action == LeaveAction.Menu ? run.Service.ReturnToMenu() :
            run.Service.Load(new(run.Source(), SaveContainerTests.Document().Header, false));
        Assert.IsNull(failure);
        Assert.AreEqual(action, run.Service.Leaving!.Action);
        run.Service.ChooseLeave(LeaveChoice.Cancel);
        Assert.IsNotNull(run.Service.Status);
        Assert.IsNull(run.Service.Leaving);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Renaming_a_save_updates_manual_and_automatic_files_and_preserves_dirty_progress(bool dirty)
    {
        using var run = new Run();
        Assert.IsNull(run.Service.Save());
        run.Drain();
        run.Service.SetAutoSaveInterval(TimeSpan.FromSeconds(1));
        run.Process();
        run.Now = 1;
        run.Process();
        run.Drain();
        var before = run.Service.List().ToDictionary(entry => entry.Path, entry => run.Store.Read(entry.Path));
        Assert.AreEqual(2, before.Count);
        if (dirty) run.PublishContent(9);
        var active = run.Service.Status!;
        Assert.AreEqual(dirty, active.Unsaved);
        Assert.IsNull(run.Service.Rename(run.Service.List().Single(entry => entry.Automatic), "山间铁路"));
        run.Drain();
        foreach (var (path, original) in before)
        {
            var renamed = run.Store.Read(path);
            Assert.AreEqual("山间铁路", renamed.Header.Name);
            Assert.AreEqual(original.Header.Id, renamed.Header.Id);
            Assert.AreEqual(original.Header.SavedAt, renamed.Header.SavedAt);
            Assert.AreEqual(original.Header.Position, renamed.Header.Position);
            Assert.AreEqual(original.CorePayload, renamed.CorePayload);
            Assert.AreEqual(original.ContentPayload, renamed.ContentPayload);
        }
        Assert.AreEqual(active with { Identity = active.Identity with { Name = "山间铁路" } }, run.Service.Status);
    }

    [TestMethod]
    public void Exporting_selected_file_works_from_menu_preserves_bytes_and_requires_overwrite_confirmation()
    {
        using var run = new Run();
        run.Service.Save();
        run.Drain();
        var manual = run.Service.List().Single();
        var automatic = run.Store.Read(manual.Path);
        automatic = automatic with { Header = automatic.Header with { Name = "自动存档" } };
        Assert.IsTrue(run.Store.Write(run.Store.AutoPathFor(run.Id, 0), automatic, false).Published);
        var entry = run.Service.List().Single(entry => entry.Automatic);
        var bytes = File.ReadAllBytes(entry.Path);
        Assert.IsNull(run.Service.ReturnToMenu());
        var target = Path.Combine(run.Root, "exports", "railway.json");
        Assert.IsNull(run.Service.Export(entry, target));
        run.Drain();
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(target));
        Assert.IsNull(run.Service.Status);
        File.WriteAllText(target, "existing file");
        Assert.AreEqual("overwrite-confirmation", run.Service.Export(entry, target)?.Code);
        Assert.AreEqual("existing file", File.ReadAllText(target));
        Assert.IsNull(run.Service.Export(entry, target, true));
        run.Drain();
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(target));
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(entry.Path));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    public void Deleting_selected_file_succeeds_while_a_list_header_reader_is_open()
    {
        using var run = new Run();
        Assert.IsNull(run.Service.Save());
        run.Drain();
        var entry = run.Service.List().Single();
        using var reader = FileSaveStore.OpenRead(entry.Path);
        Assert.IsNull(run.Service.Delete(entry));
        run.Drain();
        Assert.IsFalse(File.Exists(entry.Path), run.Service.LastMessage);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Deleting_selected_file_preserves_siblings_and_marks_deleted_current_manual_save_unsaved(bool automatic)
    {
        using var run = new Run();
        run.Service.Save();
        run.Drain();
        var document = run.Store.Read(run.Store.PathFor(run.Id));
        Assert.IsTrue(run.Store.Write(run.Store.AutoPathFor(run.Id, 0), document, false).Published);
        var selected = run.Service.List().Single(entry => entry.Automatic == automatic);
        var sibling = run.Service.List().Single(entry => entry.Automatic != automatic);
        var bytes = File.ReadAllBytes(sibling.Path);
        Assert.IsNull(run.Service.Delete(selected));
        run.Drain();
        Assert.IsFalse(File.Exists(selected.Path));
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(sibling.Path));
        Assert.AreEqual(!automatic, run.Service.Status!.Unsaved);
        Assert.AreEqual(run.Id, run.Service.Status.Identity.Id);
        Assert.AreEqual(sibling.Path, run.Service.List().Single().Path);
    }
}
