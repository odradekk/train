using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Chickensoft.GoDotTest;
using Chickensoft.GodotTestDriver;
using Godot;
using Train.App;
using Train.App.Assets;
using Train.App.Logging;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;
using Train.Scenes.ContractSupport;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class RailwayWorkUiTests
{
    [Test]
    [Chickensoft.GoDotTest.Timeout(180000)]
    public async Task Real_train_uses_the_repaired_track()
    {
        var directory = ContentFixture.CopyBuiltIn();
        var fixture = new Fixture(TestScene.GetTree());
        using var dispatcher = new GodotMainThreadDispatcher();
        var scale = TestScene.GetTree().Root.ContentScaleFactor;
        try
        {
            var app = fixture.LoadScene<Train.App.App>("res://App/App.tscn");
            app.CreateHost = logger => ContentFixture.CreateHost(logger, directory, dispatcher);
            await fixture.AddToRoot(app);
            var host = app.Host!;
            ICoreReadView Current() => host.GetStatus()!.View;
            await FramesUntil(TestScene, () => !host.IsBusy, dispatcher, TimeSpan.FromSeconds(90));
            Require(host.BeginNewGame(new("core:basic-through-world"), 40, "stage1-world-v1", host.SessionOptions!).Problem is null,
                "真实新局请求失败。");
            await FramesUntil(TestScene, () => host.GetStatus() is not null && !host.IsBusy, dispatcher, TimeSpan.FromSeconds(90));
            var railGood = host.Content!.Core.RailwayRules.Single(item => item.Id == Current().World.RailwayRule).RailGood;
            var saved = host.CaptureSave();
            if (saved.Failure is not null || saved.Capture is not { } capture)
                throw new InvalidOperationException("保存失败：" + saved.Failure?.Code + " " + saved.Failure?.Message);
            var payload = ClaimSettlement(capture.Core.Encode(), railGood.Value);
            Require(host.BeginRestore(payload, capture.Content.Encode(), host.SessionOptions!, capture.Identity).Problem is null,
                "修改后的存档恢复请求失败。");
            await FramesUntil(TestScene, () => host.GetStatus() is not null && !host.IsBusy, dispatcher, TimeSpan.FromSeconds(90));
            var view = Current();
            Require(view.FindTrain(new(1)) is Lookup<TrainView>.Found { Value.Position: TrainPosition.Node { Id.Value: 14 } },
                "恢复后列车 1 不在节点 14。" + DescribeTrain(view, new(1)));
            var edge16 = view.World.Edges.FirstOrDefault(edge => edge.Id.Value == 16);
            var edge12 = view.World.Edges.FirstOrDefault(edge => edge.Id.Value == 12);
            Require(edge16 is { Tracks: 1 } && Connects(edge16, 13, 14),
                "恢复后线路 16 不是连接 13—14 的单轨。" + DescribeEdges(view, 14));
            Require(edge12 is { Tracks: 0 } && Connects(edge12, 9, 14),
                "恢复后线路 12 不是节点 14 的零轨线路。" + DescribeEdges(view, 14));
            var settlement = view.FindSettlement(new(6)) as Lookup<SettlementView>.Found;
            Require(settlement is { Value.Node.Value: 14, Value.Relation: SettlementRelation.PlayerOwned },
                "恢复后聚居地 6 不是节点 14 的自有聚居地。" + DescribeSettlements(view));
            var inventory = view.FindInventory(settlement!.Value.Inventory) as Lookup<InventoryView>.Found;
            var rail = new GoodId.Voxel(railGood);
            var stock = inventory?.Value.Stacks.Where(stack => stack.Good == rail).Sum(stack => stack.Quantity) ?? 0;
            Require(stock >= 18, $"恢复后聚居地 6 的 {railGood.Value} 为 {stock}，需要至少 18。");

            Accept(host, new CoreCommand.ReplaceRunPlan(new(1), new(true, [new PlanStop.Waypoint(new(5)), new PlanStop.Waypoint(new(2))])));
            Accept(host, new CoreCommand.DebugAddRailBreak(new(16), 1, 200));
            Accept(host, new CoreCommand.StartTrain(new(1)));
            var started = (Lookup<TrainView>.Found)Current().FindTrain(new(1));
            Require(started.Value.Activity == TrainActivity.NoRoute,
                "断点后列车没有失去路径。" + DescribeTrain(Current(), new(1)));

            TestScene.GetTree().Root.ContentScaleFactor = 1;
            UiCapture.ConfigureWindowIfNeeded(TestScene.GetTree().Root);
            await _input.Frames(2);
            var ui = app.GetNode<GameUi>("GameUi");
            await _input.Activate(Find<Button>(ui, "Edges"));
            var list = Find<Tree>(Find<PanelContainer>(ui, "EdgesList"), "Objects");
            var index = EdgeRow(ui, new(16));
            var row = TreeItems(list)[index];
            for (var frame = 0; frame < 180 && list.GetItemAreaRect(row).Size.Y <= 0; frame++) await _input.Frames();
            Require(list.IsVisibleInTree() && list.GetItemAreaRect(row).Size.Y > 0,
                $"线路列表无法点击：visible={list.IsVisibleInTree()} size={list.Size} rect={list.GetItemAreaRect(row)} count={TreeItems(list).Length}");
            list.ScrollToItem(row);
            await _input.Frames(2);
            var visible = list.GetItemAreaRect(row).GetCenter();
            Require(list.GetItemAtPosition(visible) == row,
                $"线路 16 的可见位置没有命中该行。rect={list.GetItemAreaRect(row)} point={visible}");
            await _input.Click(list.GlobalPosition + visible);
            var panel = Find<PanelContainer>(ui, "LineInspector");
            var broken = Edge(host, 16);
            Require(broken.Map.Breaks.Length == 1, "线路 16 的断点数不是 1：" +
                string.Join(",", broken.Map.Breaks.Select(item => item.Id.Value)));
            var source = Find<OptionButton>(panel, "MaterialSource");
            Require(source.ItemCount == 1 && !string.IsNullOrWhiteSpace(source.GetItemText(source.Selected)),
                "材料来源不是聚居地 6：" + string.Join(" | ", Enumerable.Range(0, source.ItemCount).Select(source.GetItemText)));
            await _input.Activate(RepairButtons(panel).Single());
            await _input.Frames(2);
            var window = host.ReadRecording(host.GetStatus()!.Binding) ?? throw new InvalidOperationException("没有录制窗口。");
            var repair = window.Commands.LastOrDefault(command => command.Source == CommandSource.UserInterface
                && command.CanonicalPayload.Contains("\"kind\":\"rail.repair\""));
            if (repair is null)
                throw new InvalidOperationException("录制里没有界面提交的 rail.repair：" +
                    string.Join(" | ", window.Commands.Select(command => command.Source + " " + command.CanonicalPayload)));
            Require(repair.Result.Decision is CommandDecision.Accepted, "rail.repair 未被接受：" + repair.Result.Decision);
            var accepted = Edge(host, 16);
            var work = accepted.Map.Works.Single(item => item.Kind == RailwayWorkKind.RepairBreak);
            Require(!work.InvestedGoods.IsDefaultOrEmpty, "修复受理后查询没有已投入货物。");
            var shown = Find<Label>(panel, "Works").Text;
            foreach (var good in work.InvestedGoods)
                Require(shown.Contains($"{WorldPanels.GoodName(good.Good, host.Content!)} × {good.Quantity}"),
                    $"面板已投入货物与查询不符：面板“{shown}”，查询 {WorldPanels.GoodName(good.Good, host.Content!)} × {good.Quantity}。");

            for (var done = 0; done < 2880;)
            {
                var batch = Math.Min(50, 2880 - done);
                var stepped = host.Step((ulong)batch);
                Require(stepped.Completed == (ulong)batch, $"修复推进在 {done} 步后中断，完成 {stepped.Completed}：{stepped.Problem?.Code}/{stepped.Problem?.Field}");
                done += batch;
                if (done % 50 == 0) await _input.Frames();
            }
            var cleared = Edge(host, 16);
            Require(cleared.Map.Breaks.IsEmpty, "2880 步后断点仍在：" +
                string.Join(",", cleared.Map.Breaks.Select(item => $"{item.Id.Value}@{item.DistanceMeters}")));

            var entered = false;
            for (var step = 0; step < 15000 && !entered; step++)
            {
                var stepped = host.Step(1);
                Require(stepped.Completed == 1, $"通车推进在修复后 {step} 步中断：{stepped.Problem?.Code}/{stepped.Problem?.Field}。" +
                    DescribeTrain(Current(), new(1)));
                entered = Current().FindTrain(new(1)) is Lookup<TrainView>.Found
                {
                    Value.Position: TrainPosition.Edge { Id.Value: 16, Track: 1 }
                };
                if (step % 50 == 49) await _input.Frames();
            }
            Require(entered, "15000 步内列车 1 没有进入线路 16 的 1 号轨。" + DescribeTrain(Current(), new(1)));
        }
        finally
        {
            await fixture.Cleanup();
            TestScene.GetTree().Root.ContentScaleFactor = scale;
            Directory.Delete(directory, true);
        }
    }
}
