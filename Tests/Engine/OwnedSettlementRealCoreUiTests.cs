using System.Text.Json.Nodes;
using Chickensoft.GoDotTest;
using Chickensoft.GodotTestDriver;
using Godot;
using Train.App.Assets;
using Train.Core.Contracts;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public class OwnedSettlementRealCoreUiTests(Node testScene) : TestClass(testScene)
{
    [Test]
    public async Task Owned_inspector_sets_production_and_transfers_cargo_in_a_real_world()
    {
        var directory = ContentFixture.CopyBuiltIn();
        var fixture = new Fixture(TestScene.GetTree());
        using var dispatcher = new GodotMainThreadDispatcher();
        var window = TestScene.GetTree().Root;
        var scale = window.ContentScaleFactor;
        try
        {
            var profilePath = Path.Combine(directory, "resources/generation-profiles/basic-through-world.generation-profile.json");
            var profile = JsonNode.Parse(File.ReadAllText(profilePath))!;
            profile["start"]!["trains"]![0]!["inventory"] = JsonNode.Parse("""[{"voxel":"staple-food","quantity":3}]""");
            File.WriteAllText(profilePath, profile.ToJsonString());
            foreach (var path in Directory.EnumerateFiles(Path.Combine(directory, "resources/settlement-templates"), "*.json"))
            {
                var template = JsonNode.Parse(File.ReadAllText(path))!;
                var plot = template["plots"]!.AsArray().Single(item => item!["id"]!.GetValue<int>() == 7)!;
                plot["size"] = "small"; plot["building"] = "food-factory";
                template["production_plans"] = JsonNode.Parse("""[{"plot":7,"capability":"production","process":"staple-food-processing","enabled":false,"priority":0}]""");
                File.WriteAllText(path, template.ToJsonString());
            }
            var app = fixture.LoadScene<Train.App.App>("res://App/App.tscn");
            app.CreateHost = logger => ContentFixture.CreateHost(logger, directory, dispatcher);
            await fixture.AddToRoot(app);
            var host = app.Host!;
            await FramesUntil(TestScene, () => !host.IsBusy, dispatcher, timeout: ContentPreparationTimeout);
            Require(host.BeginNewGame(new("core:basic-through-world"), 1, "stage1-world-v1", host.SessionOptions!).Problem is null,
                "真实自有聚居地情景未能创建新局。");
            await FramesUntil(TestScene, () => !host.IsBusy, dispatcher, timeout: ContentPreparationTimeout);
            var before = host.GetStatus()!.View;
            var trainId = before.World.Trains[0].Id;
            var train = ((Lookup<TrainView>.Found)before.FindTrain(trainId)).Value;
            var node = ((TrainPosition.Node)train.Position).Id;
            var settlementId = ((NodeOccupant.Settlement)before.World.Nodes.Single(item => item.Id == node).Occupant).Id;
            var saved = host.CaptureSave();
            Require(saved.Failure is null && saved.Capture is not null, "真实情景未能保存。");
            var capture = saved.Capture!;
            var payload = JsonNode.Parse(capture.Core.Encode())!;
            var world = payload["world"]!;
            foreach (var settlement in world["settlements"]!.AsArray()) settlement!["relation"] = (int)SettlementRelation.PlayerOwned;
            foreach (var item in world["nodes"]!.AsArray())
                if (item!["occupant"]?["$kind"]?.GetValue<string>() == "settlement")
                    item["occupant"]!["relation"] = (int)SettlementRelation.PlayerOwned;
            foreach (var instance in world["instances"]!.AsArray()) instance!["ownership"] = (int)SettlementRelation.PlayerOwned;
            Require(host.BeginRestore(payload.ToJsonString(), capture.Content.Encode(), host.SessionOptions!, capture.Identity).Problem is null,
                "真实情景未能恢复为自有聚居地。");
            await FramesUntil(TestScene, () => !host.IsBusy, dispatcher, timeout: ContentPreparationTimeout);
            var status = host.GetStatus()!;
            var owned = ((Lookup<SettlementView>.Found)status.View.FindSettlement(settlementId)).Value;
            Require(owned.Relation == SettlementRelation.PlayerOwned, "恢复后必须为真实自有聚居地。");
            foreach (var command in new CoreCommand[] { new CoreCommand.ReplaceRunPlan(trainId,
                new(false, [new PlanStop.Station(node, 60000, [])])), new CoreCommand.StartTrain(trainId) })
                Require(host.Submit(status.Binding, command, CommandSource.AutomationTest) is
                    SubmissionResult.Completed { Receipt.Decision: CommandDecision.Accepted }, "列车应能请求在本站停靠。");
            for (var step = 0; step < 1000; step++)
            {
                train = ((Lookup<TrainView>.Found)host.GetStatus()!.View.FindTrain(trainId)).Value;
                if (train is { Activity: TrainActivity.Servicing, Position: TrainPosition.Node { Phase: NodeTravelPhase.Docked } }) break;
                Require(host.Step(1).Completed == 1, "真实核心应能推进到站台停稳。");
            }
            Require(train is { Activity: TrainActivity.Servicing, Position: TrainPosition.Node { Phase: NodeTravelPhase.Docked } },
                "装卸情景必须先让列车在本站站台停稳。");
            status = host.GetStatus()!;
            owned = ((Lookup<SettlementView>.Found)status.View.FindSettlement(settlementId)).Value;
            var process = new ProcessRef.Production(new("core:staple-food-processing"));
            var facility = owned.Facilities.Single(item => item.Process == process);
            Require(!facility.Enabled, "情景应从已停用的生产计划开始。");
            var ui = app.GetNode<GameUi>("GameUi");
            var input = new UiTestInput(TestScene);
            window.ContentScaleFactor = 1;
            UiCapture.ConfigureWindowIfNeeded(window);
            Find<EconomyPanels>(ui, "EconomyPanels").OpenSettlement(new(status.Binding, host.Content!.Generation,
                new WorldObjectId.Settlement(settlementId)), settlementId);
            await input.Frames(2);
            var panel = Find<PanelContainer>(ui, "Settlement");
            var tabs = Find<TabContainer>(panel, "Pages");
            await input.ClickTab(tabs, 2);
            await input.Activate(Find<Button>(panel, $"SelectBuilding_{facility.Key.Building.Value}"));
            var building = Find<VBoxContainer>(panel, $"Building_{facility.Key.Building.Value}");
            await input.Activate(Find<CheckButton>(building, "Enabled"));
            await input.Activate(Find<Button>(building, "PriorityHigh"));
            var configured = ((Lookup<SettlementView>.Found)host.GetStatus()!.View.FindSettlement(settlementId)).Value
                .Facilities.Single(item => item.Key == facility.Key);
            Require(configured.Enabled && configured.Priority == -1 && configured.Process == process &&
                host.GetStatus()!.View.Revision != status.View.Revision,
                "建筑页应把启用和高优先级立即写入真实核心，保留生产流程。");
            Require(Find<Label>(building, "PlanMessage").Text.Contains("已更新"), "真实核心受理后应就地反馈。");
            await input.ClickTab(tabs, 1);
            var stock = tabs.GetChild<Control>(1);
            var food = new GoodId.Voxel(new("core:staple-food"));
            var foodName = WorldPanels.GoodName(food, host.Content!);
            var foodRow = stock.FindChildren("SelectGood", "Button", true, false).Cast<Button>()
                .Single(button => button.FindChildren("*", "Label", true, false).Cast<Label>().Any(label => label.Text == foodName));
            await input.Activate(foodRow);
            await input.Choose(Find<OptionButton>(stock, "Action"), 1);
            await input.Choose(Find<OptionButton>(stock, "Train"), 1);
            await input.Enter(Find<LineEdit>(stock, "Quantity"), "1");
            long Quantity(ICoreReadView view, InventoryId id) => ((Lookup<InventoryView>.Found)view.FindInventory(id)).Value.Stacks
                .FirstOrDefault(item => item.Good == food)?.Quantity ?? 0;
            var view = host.GetStatus()!.View;
            var trainQuantity = Quantity(view, train.Inventory);
            var settlementQuantity = Quantity(view, owned.Inventory);
            var wallet = view.Economy.Wallet;
            var confirm = Find<Button>(stock, "ConfirmTrade");
            Require(!confirm.Disabled, "真实卸货报价应可执行：" + Find<Label>(stock, "Quote").Text);
            await input.Activate(confirm);
            view = host.GetStatus()!.View;
            Require(Quantity(view, train.Inventory) == trainQuantity - 1 && Quantity(view, owned.Inventory) == settlementQuantity + 1 &&
                view.Economy.Wallet == wallet, "卸货应在真实列车与自有库存间守恒，且不改变资金。");
            Require(Find<Label>(stock, "TradeResult").Text.Contains("已卸到库存") &&
                Find<Label>(stock, "TradeResult").Text.Contains("1 件"), "真实卸货事件应在原行反馈实际数量。");
            await input.Choose(Find<OptionButton>(stock, "Action"), 0);
            Require(!confirm.Disabled, "真实装车报价应可执行。");
            await input.Activate(confirm);
            view = host.GetStatus()!.View;
            Require(Quantity(view, train.Inventory) == trainQuantity && Quantity(view, owned.Inventory) == settlementQuantity &&
                view.Economy.Wallet == wallet, "装回列车应恢复两边库存，且不改变资金。");
        }
        finally
        {
            await fixture.Cleanup(); window.ContentScaleFactor = scale;
            Directory.Delete(directory, true);
        }
    }
}
