using Chickensoft.GoDotTest;
using Chickensoft.GodotTestDriver;
using Godot;
using Train.App.Saves;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class SaveTests
{
    [Test]
    public async Task Export_confirmation_preserves_the_target_until_confirmed_and_uses_the_selected_file()
    {
        var fixture = new Fixture(TestScene.GetTree());
        var owned = new HashSet<Guid>();
        Train.App.App? app = null;
        var target = Path.Combine(OS.GetUserDataDir(), "existing-export.json");
        try
        {
            app = fixture.LoadScene<Train.App.App>("res://App/App.tscn");
            app.CreateHost = new SaveSetup().Create;
            await fixture.AddToRoot(app);
            await _input.WaitUntil(() => app.Saves!.Status is not null);
            owned.Add(app.Saves!.Status!.Identity.Id);
            var ui = app.GetNode<GameUi>("GameUi");
            await _input.ChooseMenu(Find<MenuButton>(ui, "GameMenu"), "存档与读档");
            var panel = ui.GetNode<SavePanel>("Root/SavePanel");
            var name = Find<LineEdit>(panel, "SaveName");
            await _input.Enter(name, "导出首份存档");
            await _input.Activate(UiTestInput.SaveButton(panel, "保存"));
            await _input.WaitUntil(() => !app.Saves.HasPendingWork);
            var first = app.Saves.List().Single();
            owned.Add(first.Header.Id);
            await _input.Enter(name, "导出另一份存档");
            await _input.Activate(UiTestInput.SaveButton(panel, "保存"));
            await _input.WaitUntil(() => !app.Saves.HasPendingWork);
            owned.Add(app.Saves.Status!.Identity.Id);
            var list = Find<ItemList>(panel, "Saves");
            var secondRow = Enumerable.Range(0, list.ItemCount).Single(index =>
                list.GetItemText(index).StartsWith("导出另一份存档", StringComparison.Ordinal));
            await _input.ClickItem(list, secondRow);
            await _input.Activate(UiTestInput.SaveButton(panel, "导出文件"));
            var dialog = panel.GetChildren().OfType<FileDialog>().Single(item => item.Title == "导出存档");
            await _input.WaitUntil(() => dialog.Visible);
            await _input.Activate(dialog.GetCancelButton(), dialog);
            await _input.WaitUntil(() => !dialog.Visible);
            var firstRow = Enumerable.Range(0, list.ItemCount).Single(index =>
                list.GetItemText(index).StartsWith(first.Header.Name, StringComparison.Ordinal));
            await _input.ClickItem(list, firstRow);
            await _input.Activate(UiTestInput.SaveButton(panel, "导出文件"));
            await _input.WaitUntil(() => dialog.Visible);
            var source = File.ReadAllBytes(first.Path);
            byte[] existing = [4, 5, 6];
            File.WriteAllBytes(target, existing);
            dialog.Hide();
            dialog.EmitSignal(FileDialog.SignalName.FileSelected, target);
            await _input.Frames(2);
            Require(UiTestInput.SavePromptVisible(panel, "覆盖文件") &&
                Find<Label>(panel, "PromptMessage").Text.Contains("覆盖", StringComparison.Ordinal) &&
                File.ReadAllBytes(target).SequenceEqual(existing),
                "导出必须先显示中文覆盖确认，并保留已有目标文件。");
            RequireNoSaveIdentity(panel, first.Header.Id);
            await _input.Activate(UiTestInput.SavePromptButton(panel, "Confirm"));
            await _input.WaitUntil(() => !app.Saves.HasPendingWork);
            Require(File.ReadAllBytes(target).SequenceEqual(source), "确认导出后没有复制这次选中的源文件。");
        }
        finally
        {
            try { await Cleanup(fixture, app, owned); }
            finally { if (File.Exists(target)) File.Delete(target); }
        }
    }
}
