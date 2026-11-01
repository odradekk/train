using Godot;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class CombatUiTests
{
    private async Task<CombatPanel> SelectBattlefield(GameUi ui)
    {
        await Press(Find<Button>(ui, "Combats"));
        await _input.ClickItem(Find<ItemList>(Find<PanelContainer>(ui, "CombatsList"), "Objects"), 0);
        var combat = Find<CombatPanel>(ui, "CombatPanel");
        await _input.WaitUntil(() => combat.FindChildren("Vehicle11", "Button", true, false).Count == 1);
        await Press(Button(combat, "Vehicle11"));
        await _input.WaitUntil(() => combat.FindChildren("FocusVehicle11", "Button", true, false).Count == 0 &&
            combat.FindChildren("FocusBuilding13", "Button", true, false).Count == 1 &&
            Find<Label>(combat, "CombatState").Text.Contains("士气：0.40"));
        await Press(Button(combat, "FocusBuilding13"));
        await _input.WaitUntil(() => !Button(combat, "Retreat").Disabled && !Button(combat, "FocusFire").Disabled);
        await OpenSettlementCombat(ui);
        return combat;
    }

    private async Task OpenSettlementCombat(GameUi ui)
    {
        await _input.Click(Find<Button>(ui, "Nodes").GetGlobalRect().GetCenter());
        await _input.ClickItem(Find<ItemList>(Find<PanelContainer>(ui, "NodesList"), "Objects"), 0);
        var settlement = Find<PanelContainer>(ui, "Settlement");
        var entry = Find<Button>(settlement, "OpenSettlementCombat");
        await _input.Click(entry.GetGlobalRect().GetCenter());
        Require(Find<Button>(ui, "Occupy").IsVisibleInTree() &&
            Find<Button>(ui, "DeclareWar").IsVisibleInTree(), "聚居地战斗入口没有打开宣战和占领。");
    }

    private async Task Press(Button button)
    {
        for (var parent = button.GetParent(); parent is not null; parent = parent.GetParent())
            if (parent is ScrollContainer scroll) scroll.EnsureControlVisible(button);
        await _input.Frames();
        await _input.Activate(button);
    }

}
