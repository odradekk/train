using Chickensoft.GoDotTest;
using Chickensoft.GodotTestDriver;
using Godot;
using Train.Core.Contracts;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class SaveTests
{
    [Test]
    public async Task Loading_a_save_in_game_closes_the_previous_sessions_panels()
    {
        var fixture = new Fixture(TestScene.GetTree());
        var setup = new SaveSetup { InCombat = true };
        var owned = new HashSet<Guid>();
        Train.App.App? app = null;
        try
        {
            app = fixture.LoadScene<Train.App.App>("res://App/App.tscn");
            app.CreateHost = setup.Create;
            await fixture.AddToRoot(app);
            await _input.WaitUntil(() => app.Saves!.Status is not null);
            owned.Add(app.Saves!.Status!.Identity.Id);
            var ui = app.GetNode<GameUi>("GameUi");
            var combat = Find<CombatPanel>(ui, "CombatPanel");
            await _input.Activate(Find<Button>(ui, "Nodes"));
            await _input.ClickItem(NodeObjects(ui), FirstSelectableItem(NodeObjects(ui)));
            await _input.Activate(Find<Button>(ui, "Fleet"));
            await _input.ClickItem(Find<Tree>(Find<PanelContainer>(ui, "FleetList"), "Objects"), 0);
            await _input.Activate(Find<Button>(((GameShell)ui.World).InspectorContent!, "OpenCombat"));
            await _input.Activate(Find<Button>(ui, "EconomyReports"));
            Require(ui.FindChildren("Description", "Label", true, false).Count == 1 && combat.Visible &&
                combat.ContextTrain == new TrainId(7) && Find<PanelContainer>(ui, "EconomyReports").IsVisibleInTree(),
                "没有打开上一局的节点和列车详情、战斗面板与经营报告。");
            var binding = app.Host!.GetStatus()!.Binding;
            var panel = app.GetNode<SavePanel>("GameUi/Root/SavePanel");
            await _input.ChooseMenu(Find<MenuButton>(ui, "GameMenu"), "存档与读档");
            await _input.Activate(UiTestInput.SaveButton(panel, "保存"));
            await _input.WaitUntil(() => !app.Saves.HasPendingWork && panel.Entries.Count > 0);
            var identity = app.Saves.Status!.Identity;
            var list = Find<ItemList>(panel, "Saves");
            var row = Enumerable.Range(0, list.ItemCount).Single(index =>
                list.GetItemText(index).StartsWith(identity.Name + " · ", StringComparison.Ordinal));
            await _input.ClickItem(list, row);
            await _input.Activate(UiTestInput.SaveButton(panel, "读取"));
            await _input.WaitUntil(() => app.Host.GetStatus() is { } status && status.Binding != binding);
            await _input.Frames();
            Require(ui.FindChildren("Description", "Label", true, false).Count == 0 && !combat.Visible &&
                combat.ContextTrain is null && combat.SelectedCombat is null &&
                ui.FindChildren("EconomyReports", "PanelContainer", true, false).Count == 0,
                "读档后仍留着上一局的详情、战斗面板或经营报告。");
        }
        finally { await Cleanup(fixture, app, owned); }
    }
}
