using Godot;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class CombatUiTests
{
    private static FixedContentView Content()
    {
        var scene = new DesignSceneProjection(null, null, null, true, null, null, [], [], [], [], []);
        var layout = new SettlementLayoutProjection(new(4), new("preview:town"),
            new(new("ground"), scene, new("Ground", "")), [], new("Town", ""));
        return new(HostContentScenario.CreateProjection(HostCoreScenario.Input), new(1))
        {
            DefinitionDisplays = new Dictionary<CoreDefinitionReference, ContentLookup<ContentDisplay>>
            { [new CoreDefinitionReference.Terrain(new("fixture:plain"))] = new ContentLookup<ContentDisplay>.NotFound() },
            Designs = new Dictionary<DesignHash, ContentLookup<DesignEvaluation>>
            { [new("station")] = new ContentLookup<DesignEvaluation>.NotFound() },
            SettlementLayouts = new Dictionary<SettlementId, ContentLookup<SettlementLayoutProjection>>
            { [new(4)] = new ContentLookup<SettlementLayoutProjection>.Found(layout) }
        };
    }

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
        await _input.ClickItem(NodeObjects(ui), FirstSelectableItem(NodeObjects(ui)));
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
