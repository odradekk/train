using System.Text.Json.Nodes;
using Chickensoft.GoDotTest;
using Chickensoft.GodotTestDriver;
using Godot;
using Train.App.Assets;
using Train.Core.Contracts;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public class EconomyPlanUiTests(Node testScene) : TestClass(testScene)
{
    [Test]
    public async Task One_content_publication_displays_current_definitions_and_retained_execution_parameters()
    {
        var directory = ContentFixture.CopyBuiltIn();
        var fixture = new Fixture(TestScene.GetTree());
        using var dispatcher = new GodotMainThreadDispatcher();
        var scale = TestScene.GetTree().Root.ContentScaleFactor;
        try
        {
            var processPath = Path.Combine(directory, "resources/processes/test.process.json");
            Directory.CreateDirectory(Path.GetDirectoryName(processPath)!);
            var process = JsonNode.Parse("""
                {"schema_version":1,"id":"test","simulation":{"kind":"production","inputs":[],
                "outputs":[{"voxel":"staple-food","quantity":2}],"work_required":100000,
                "power_per_work":0,"labor_per_work":0,"facility_tag":"food_processing"},
                "display":{"name":"界面验证流程","description":"长批次用于核对冻结规格"}}
                """)!;
            File.WriteAllText(processPath, process.ToJsonString());
            foreach (var path in Directory.EnumerateFiles(Path.Combine(directory, "resources/settlement-templates"), "*.json"))
            {
                var template = JsonNode.Parse(File.ReadAllText(path))!;
                var plot = template["plots"]!.AsArray().Single(item => item!["id"]!.GetValue<int>() == 7)!;
                plot["size"] = "small"; plot["building"] = "food-factory";
                template["production_plans"] = JsonNode.Parse("""[{"plot":7,"capability":"production","process":"test","enabled":true,"priority":0}]""");
                File.WriteAllText(path, template.ToJsonString());
            }
            var app = fixture.LoadScene<Train.App.App>("res://App/App.tscn");
            app.CreateHost = logger => ContentFixture.CreateHost(logger, directory, dispatcher);
            await fixture.AddToRoot(app);
            var host = app.Host!;
            await FramesUntil(TestScene, () => !host.IsBusy, dispatcher, timeout: ContentPreparationTimeout);
            host.BeginNewGame(new("core:basic-through-world"), 1, "stage1-world-v1", host.SessionOptions!);
            await FramesUntil(TestScene, () => !host.IsBusy, dispatcher, timeout: ContentPreparationTimeout);
            Require(host.GetStatus() is not null && host.Step(120).Completed == 120, "冻结规格情景未能开始生产与人口消费。");
            var before = host.GetStatus()!.View;
            var settlementId = before.World.Nodes.Select(item => item.Occupant).OfType<NodeOccupant.Settlement>().First().Id;
            var prior = ((Lookup<SettlementView>.Found)before.FindSettlement(settlementId)).Value;
            Require(prior.Facilities.Single(item => item.Process == new ProcessRef.Production(new("core:test"))).Batch is not null && prior.Population.Execution is PopulationExecution.InProgress,
                "冻结规格情景没有活动批次或人口统计期。");
            process["simulation"]!["work_required"] = 100001;
            File.WriteAllText(processPath, process.ToJsonString());
            var populationPath = Path.Combine(directory, "resources/rules/basic-population.population-rule.json");
            var populationRule = JsonNode.Parse(File.ReadAllText(populationPath))!;
            populationRule["simulation"]!["labor_ratio"] = .59;
            File.WriteAllText(populationPath, populationRule.ToJsonString());
            Require(host.BeginReload(CommandSource.UserInterface).Problem is null, "内容发布请求被拒绝。");
            await FramesUntil(TestScene, () => !host.IsBusy, dispatcher, timeout: ContentPreparationTimeout);
            var status = host.GetStatus()!;
            var current = ((Lookup<SettlementView>.Found)status.View.FindSettlement(settlementId)).Value;
            var batch = current.Facilities.Single(item => item.Process == new ProcessRef.Production(new("core:test"))).Batch;
            var period = (PopulationExecution.InProgress)current.Population.Execution;
            Require(batch is { CurrentSpecificationDiffers: true, FrozenWorkRequired: 100000 } &&
                current.Population.CurrentRule.PublishedRevision != period.Period.ExecutionRevision,
                "一次真实发布没有保留旧批次与人口执行参数。");
            var saved = host.CaptureSave();
            Require(saved.Failure is null && saved.Capture is not null, "冻结参数情景未能保存。");
            var capture = saved.Capture!;
            var payload = JsonNode.Parse(capture.Core.Encode())!;
            var world = payload["world"]!;
            foreach (var settlement in world["settlements"]!.AsArray())
                settlement!["relation"] = (int)SettlementRelation.PlayerOwned;
            foreach (var node in world["nodes"]!.AsArray())
                if (node!["occupant"]?["$kind"]?.GetValue<string>() == "settlement")
                    node["occupant"]!["relation"] = (int)SettlementRelation.PlayerOwned;
            foreach (var instance in world["instances"]!.AsArray())
                instance!["ownership"] = (int)SettlementRelation.PlayerOwned;
            Require(host.BeginRestore(payload.ToJsonString(), capture.Content.Encode(), host.SessionOptions!, capture.Identity).Problem is null,
                "自有聚居地情景恢复请求失败。");
            await FramesUntil(TestScene, () => !host.IsBusy, dispatcher, timeout: ContentPreparationTimeout);
            status = host.GetStatus()!;
            current = ((Lookup<SettlementView>.Found)status.View.FindSettlement(settlementId)).Value;
            batch = current.Facilities.Single(item => item.Process == new ProcessRef.Production(new("core:test"))).Batch;
            period = (PopulationExecution.InProgress)current.Population.Execution;
            Require(current.Relation == SettlementRelation.PlayerOwned && batch is { CurrentSpecificationDiffers: true, FrozenWorkRequired: 100000 } &&
                current.Population.CurrentRule.PublishedRevision != period.Period.ExecutionRevision,
                "自有聚居地恢复没有保留真实发布后的冻结参数。");
            var ui = app.GetNode<GameUi>("GameUi");
            TestScene.GetTree().Root.ContentScaleFactor = 1;
            UiCapture.ConfigureWindowIfNeeded(TestScene.GetTree().Root);
            Find<EconomyPanels>(ui, "EconomyPanels").OpenSettlement(new(status.Binding, host.Content!.Generation, new WorldObjectId.Settlement(settlementId)), settlementId);
            var economy = Find<PanelContainer>(ui, "Settlement");
            var specificationLabel = economy.FindChildren("CurrentSpecification", "Label", true, false).Cast<Label>()
                .Single(label => label.Text.Contains("界面验证流程"));
            var specification = specificationLabel.Text;
            var batchText = Find<Label>(specificationLabel.GetParent(), "Batch").Text;
            var revision = host.Content.Core.Processes.Single(item => item.Details is ProcessDetails.Production { Id.Value: "core:test" }).Revision.Value;
            Require(specification.StartsWith($"当前定义：界面验证流程 · 修订 {revision[..8]}\n") &&
                batchText.StartsWith($"进行中批次：界面验证流程 · 启动修订 {batch!.StartedFromRevision.Value[..8]}\n"),
                "生产页未展示流程名称和八位短修订。");
            Require(!specification.Contains("core:test") && !batchText.Contains("core:test") &&
                !specification.Contains(revision) && !batchText.Contains(batch!.StartedFromRevision.Value),
                "生产页将定义标识用作名称，或仍显示完整修订。");
            var populationText = Find<Label>(economy, "Population").Text;
            Require(specification.Contains("100001") && batchText.Contains("100000") && batchText.Contains("不同"),
                "生产页未区分当前工作量与进行中批次冻结工作量。");
            Require(populationText.Contains(current.Population.CurrentRule.PublishedRevision!.Value.Value) &&
                populationText.Contains(period.Period.ExecutionRevision.Value), "人口页未同时展示当前与本期修订。");
            Require(populationText.Contains("不同") && populationText.Contains("下一期"),
                "人口页没有说明本期执行规则与当前发布规则不同及生效时机。");
            var input = new UiTestInput(TestScene);
            var tabs = Find<TabContainer>(economy, "Pages");
            await input.Frames(2);
            await input.ChooseTab(tabs, 4);
            economy.GetNode<ScrollContainer>("Layout/Content/Body/Pages/生产/Page").EnsureControlVisible(
                Find<Label>(specificationLabel.GetParent(), "Batch"));
            await UiCapture.Capture(TestScene, "production-frozen-batch");
            await input.ChooseTab(tabs, 5);
            await UiCapture.Capture(TestScene, "population-frozen-period");
        }
        finally
        {
            await fixture.Cleanup(); TestScene.GetTree().Root.ContentScaleFactor = scale;
            Directory.Delete(directory, true);
        }
    }

    [Test]
    public async Task Edited_goods_rule_executes_in_real_core_and_replacement_displays_its_report()
    {
        var directory = ContentFixture.CopyBuiltIn();
        var fixture = new Fixture(TestScene.GetTree());
        using var dispatcher = new GodotMainThreadDispatcher();
        var scale = TestScene.GetTree().Root.ContentScaleFactor;
        try
        {
            var profilePath = Path.Combine(directory, "resources/generation-profiles/basic-through-world.generation-profile.json");
            var profile = JsonNode.Parse(File.ReadAllText(profilePath))!;
            profile["start"]!["trains"]![0]!["inventory"] = JsonNode.Parse("""[{"voxel":"staple-food","quantity":11}]""");
            // 卖出情景需要 NPC 留有收购缺口，不能让开局库存先填满生活需求。
            profile["configuration"]!["npc_initialization"]!["fill_inputs_and_living_targets"] = false;
            File.WriteAllText(profilePath, profile.ToJsonString());
            var app = fixture.LoadScene<Train.App.App>("res://App/App.tscn");
            app.CreateHost = logger => ContentFixture.CreateHost(logger, directory, dispatcher);
            await fixture.AddToRoot(app);
            var host = app.Host!;
            await FramesUntil(TestScene, () => !host.IsBusy, dispatcher, timeout: ContentPreparationTimeout);
            Require(host.BeginNewGame(new("core:basic-through-world"), 1, "stage1-world-v1", host.SessionOptions!).Problem is null,
                "经济情景未能请求真实新局。");
            await FramesUntil(TestScene, () => !host.IsBusy, dispatcher, timeout: ContentPreparationTimeout);
            Require(host.GetStatus() is not null, "经济情景没有建立真实世界。");
            var view = host.GetStatus()!.View;
            var trainId = view.World.Trains[0].Id;
            var train = ((Lookup<TrainView>.Found)view.FindTrain(trainId)).Value;
            var node = ((TrainPosition.Node)train.Position).Id;
            Require(host.Submit(host.GetStatus()!.Binding, new CoreCommand.ReplaceRunPlan(trainId,
                new(false, [new PlanStop.Station(node, 60001, [])])), CommandSource.AutomationTest) is
                SubmissionResult.Completed { Receipt.Decision: CommandDecision.Accepted },
                "往返情景无法建立非整秒的原始停留时间。");
            TestScene.GetTree().Root.ContentScaleFactor = 1;
            UiCapture.ConfigureWindowIfNeeded(TestScene.GetTree().Root);
            var input = new UiTestInput(TestScene);
            await input.Frames(2);
            var ui = app.GetNode<GameUi>("GameUi");
            var panels = ui.GetNode<WorldPanels>("Root/World/WorldPanels");
            panels.OpenObject(new WorldObjectId.Train(trainId));
            await input.Activate(Find<Button>(ui, "EditPlan"));
            var editor = Find<TrainPlanWorkspace>(ui, "TrainPlanWorkspace");
            var stop = Find<VBoxContainer>(editor, "StopEditor");
            await input.Activate(Find<Button>(stop, "AddRule"));
            var rule = Find<VBoxContainer>(stop, "Rules").GetChild<VBoxContainer>(0);
            var goods = Find<OptionButton>(rule, "Good");
            var foodName = WorldPanels.GoodName(new GoodId.Voxel(new("core:staple-food")), host.Content!);
            var foodIndex = Enumerable.Range(0, goods.ItemCount).Single(index => goods.GetItemText(index) == foodName);
            await input.Choose(goods, foodIndex);
            var action = Find<OptionButton>(rule, "Action");
            await input.Choose(action, Enumerable.Range(0, action.ItemCount)
                .Single(index => action.GetItemId(index) == (int)TradeAction.Sell));
            await input.Choose(Find<OptionButton>(rule, "Inventory"), 1);
            await input.Enter(Find<LineEdit>(rule, "TargetQuantity"), "1");
            await input.Enter(Find<LineEdit>(rule, "UnitPrice"), "1");
            Require(Find<OptionButton>(stop, "Kind").IsItemDisabled(1), "含规则的站点仍可改为途经点并丢失规则。");
            await UiCapture.Capture(TestScene, "automatic-rule-editor");
            await input.Activate(Find<Button>(stop, "AddRule"));
            var secondRule = Find<VBoxContainer>(stop, "Rules").GetChild<VBoxContainer>(1);
            var secondGoods = Find<OptionButton>(secondRule, "Good");
            var otherGood = host.Content!.Core.Goods.First(item => item.Good != new GoodId.Voxel(new("core:staple-food"))).Good;
            var otherName = WorldPanels.GoodName(otherGood, host.Content);
            await input.Choose(secondGoods, Enumerable.Range(0, secondGoods.ItemCount)
                .Single(index => secondGoods.GetItemText(index) == otherName));
            var secondAction = Find<OptionButton>(secondRule, "Action");
            await input.Choose(secondAction, Enumerable.Range(0, secondAction.ItemCount)
                .Single(index => secondAction.GetItemId(index) == (int)TradeAction.Buy));
            await input.Choose(Find<OptionButton>(secondRule, "Inventory"), 1);
            await input.Enter(Find<LineEdit>(secondRule, "UnitPrice"), "2");
            await input.Activate(Find<Button>(editor, "Apply"));
            Require(!editor.Visible, "规则计划未被真实核心接受。");
            for (var step = 0; step < 5000; step++)
            {
                train = ((Lookup<TrainView>.Found)host.GetStatus()!.View.FindTrain(trainId)).Value;
                if (train.CurrentRun is { SalesReceipts.Value: > 0 }) break;
                Require(host.Step(1).Completed == 1, "真实核心无法推进到停靠交易。");
            }
            Require(train.CurrentRun is { SalesReceipts.Value: > 0, TransportedUnits: 10 }, "面板规则没有执行真实卖出交易。");
            await input.Frames(2);
            var title = Find<PanelContainer>(ui, "Details").GetNode<Label>("Layout/Heading/Title").Text;
            Require(title == $"列车 {trainId.Value} · 装卸", $"启动后列车详情标题没有跟随当前状态：{title}");
            var description = Find<Label>(Find<GameShell>(ui, "*").InspectorContent!, "Description").Text;
            Require(description.Contains("销售收入 " + train.CurrentRun!.SalesReceipts.Value) && description.Contains("运输数量 10"),
                "列车详情没有显示真实交易后的运行累计。");
            Require(train.CurrentRun!.LoadedCapacityRatio is null &&
                description.Contains("载运率 —（未进入线路，或进入时没有载货容量）"),
                "尚未进入线路的列车累计没有显示未定义载运率。");
            var originalPlan = train.Plan!;
            var originalStop = (PlanStop.Station)originalPlan.Stops.Single();
            Require(originalStop.DwellSteps == 60001 && originalStop.GoodsRules.Length == 2,
                "往返情景没有保留非整秒停留时间或两条有序规则。");
            await input.Activate(Find<Button>(ui, "EditPlan"));
            await input.Activate(Find<Button>(editor, "Apply"));
            var replaced = ((Lookup<TrainView>.Found)host.GetStatus()!.View.FindTrain(trainId)).Value.Plan;
            Require(replaced is { Stops.Length: 1 } && replaced.Loop == originalPlan.Loop &&
                replaced.Stops[0] is PlanStop.Station restored && restored.Node == originalStop.Node &&
                restored.DwellSteps == originalStop.DwellSteps && restored.GoodsRules.SequenceEqual(originalStop.GoodsRules),
                "面板重新应用后，规则动作、限价、货物、库存、目标、顺序或精确停留步数发生变化。");
            var report = host.GetStatus()!.View.Economy.RecentRunReports.Single(item => item.Totals.TransportedUnits == 10);
            Require(report.Reason == RunEndReason.PlanReplaced && report.Totals.TransportedUnits == 10,
                "替换计划没有产生真实的运行报告。");
            await input.Activate(Find<Button>(ui, "EconomyReports"));
            var reportText = Find<Label>(ui, "ReportsText").Text;
            Require(reportText.Contains("采购支出") && reportText.Contains("销售收入 " + report.Totals.SalesReceipts.Value) &&
                reportText.Contains("运输数量 10"), "真实运行报告没有出现在经营报告面板。");
            Require(report.Totals.LoadedCapacityRatio is null &&
                reportText.Contains("载运率 —（未进入线路，或进入时没有载货容量）"),
                "尚未进入线路的历史报告没有显示未定义载运率。");
            await UiCapture.Capture(TestScene, "economy-run-report");
        }
        finally
        {
            await fixture.Cleanup();
            TestScene.GetTree().Root.ContentScaleFactor = scale;
            Directory.Delete(directory, true);
        }
    }
}
