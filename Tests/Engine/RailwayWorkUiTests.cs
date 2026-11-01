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

public partial class RailwayWorkUiTests(Node testScene) : TestClass(testScene)
{
    private readonly UiTestInput _input = new(testScene);

    [Test]
    public async Task Map_marks_use_warning_and_severe_only_for_breaks_and_started_work()
    {
        var tree = TestScene.GetTree();
        var fixture = new Fixture(tree);
        var scale = tree.Root.ContentScaleFactor;
        var view = RailwayWorkUiFixture.Create();
        var content = RailwayWorkUiFixture.Content();
        try
        {
            tree.Root.ContentScaleFactor = 1;
            var map = fixture.AutoFree(new MacroMap { Name = "RailwayMap" });
            map.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            string? displayFailure = null;
            map.DisplayFailed += message => displayFailure = message;
            await fixture.AddToRoot(map);
            await _input.Frames(2);
            if (map.Size.X < 100)
            {
                map.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopLeft);
                map.Size = tree.Root.GetVisibleRect().Size;
            }
            map.SetView(view, content);
            await _input.Frames(2);
            Require(displayFailure is null, "地图符号绘制失败：" + displayFailure);
            Require(map.HitTest(Midpoint(map, view, RailwayWorkUiFixture.ZeroTrackEdge)) == new WorldObjectId.Edge(new(RailwayWorkUiFixture.ZeroTrackEdge))
                && map.HitTest(Midpoint(map, view, RailwayWorkUiFixture.BreakEdge)) == new WorldObjectId.Edge(new(RailwayWorkUiFixture.BreakEdge))
                && map.HitTest(Midpoint(map, view, RailwayWorkUiFixture.ActiveEdge)) == new WorldObjectId.Edge(new(RailwayWorkUiFixture.ActiveEdge)),
                "线路中点没有命中对应线路。");
            if (DisplayServer.GetName() == "headless") return;
            RenderingServer.ForceDraw();
            using var image = map.GetViewport().GetTexture().GetImage()
                ?? throw new InvalidOperationException("窗口模式无法读取地图像素。");
            var zero = Midpoint(map, view, RailwayWorkUiFixture.ZeroTrackEdge);
            var broken = Marks(map, view, RailwayWorkUiFixture.BreakEdge);
            var active = Marks(map, view, RailwayWorkUiFixture.ActiveEdge);
            Require(!RegionHas(image, map, zero, UiStyle.Warning, 18) && !RegionHas(image, map, zero, UiStyle.Error, 18),
                "零轨线路中点附近出现了施工或断点颜色。");
            Require(RegionHas(image, map, broken.Break, UiStyle.Error, 3),
                "断点标记不是严重色。" + Describe(image, map, broken.Break));
            Require(!RegionHas(image, map, broken.Construction, UiStyle.Warning, 8),
                "只有断点的线路画了施工标记。");
            Require(RegionHas(image, map, active.Construction, UiStyle.Warning, 3),
                "已开工线路的施工标记不是警告色。" + Describe(image, map, active.Construction));
            Require(RegionHas(image, map, active.Break, UiStyle.Error, 3),
                "已开工线路的断点标记不是严重色。" + Describe(image, map, active.Break));
        }
        finally
        {
            await fixture.Cleanup();
            tree.Root.ContentScaleFactor = scale;
        }
    }

    [Test]
    public async Task Panel_shows_core_query_and_submits_build_repair_and_auto_repair()
    {
        var initial = RailwayWorkUiFixture.Create();
        var closed = RailwayWorkUiFixture.WithoutAutomaticRepair(2);
        var repaired = RailwayWorkUiFixture.WithAcceptedRepair(3);
        var setup = new HostSimulationTests.HostSetup(initial, RailwayWorkUiFixture.Content());
        var computed = (long)Math.Ceiling(RailwayWorkUiFixture.ActiveLengthMeters * RailwayWorkUiFixture.RailUnitsPerMeter);
        Require(RailwayWorkUiFixture.StartedUnits != computed && RailwayWorkUiFixture.AcceptedUnits != computed,
            "夹具已投入件数不能等于按长度估算的件数。");
        await Run(setup, async ui =>
        {
            await UiCapture.Capture(TestScene, "railway-map");
            var panel = await OpenFromList(ui, (int)RailwayWorkUiFixture.ActiveEdge - 1);
            var summary = Find<Label>(panel, "Summary").Text;
            var works = Find<Label>(panel, "Works").Text;
            Require(summary.Contains("线路 3") && summary.Contains("400") && summary.Contains("铁轨 1 / 上限 2")
                && summary.Contains("聚居地 1") && !summary.Contains("自动修复：关闭") && !summary.Contains("聚居地 2"),
                "施工面板没有显示线路查询或列入了非自有聚居地：" + summary);
            var help = panel.GetNode<VBoxContainer>("Layout/Content/Body").GetChild<Label>(-1);
            Require(Find<ItemList>(panel, "Breaks").GetItemText(0).Contains("断点 2") && works.Contains("任务 2")
                && works.Contains("任务 3") && works.Contains("夹具钢轨 × 7") && works.Contains("进度 25%")
                && works.Contains("货物不足：夹具钢轨") && works.Contains("对象：聚居地 1") &&
                !works.Contains("insufficient_goods") && !works.Contains("voxel:") && !works.Contains($"× {computed}")
                && !summary.Contains('\r') && !works.Contains('\r')
                && help.Text.Contains("件数和是否允许在受理或拒绝后由核心给出") && !help.Text.Contains("工期"),
                "施工面板没有照查询显示断点、已投入货物和未开工阻塞：" + works + "\n" + help.Text);
            await _input.Frames(2);
            var scroll = panel.GetNode<ScrollContainer>("Layout/Content");
            var autoRepair = Find<Button>(panel, "DisableAutomaticRepair").GetGlobalRect();
            Require(scroll.ScrollVertical == 0 && autoRepair.End.Y <= scroll.GetGlobalRect().End.Y + 1,
                "1080p 下要滚动才能看到关闭自动修复。");
            Require(Find<OptionButton>(panel, "MaterialSource").ItemCount == 1, "材料来源应只有自有聚居地。");
            await UiCapture.Capture(TestScene, "railway-panel");
            await _input.Activate(Find<Button>(panel, "Close"));
            await _input.Frames(2);
            Require(!panel.Visible && Find<GameShell>(ui, "World").WorkspaceContent is null && setup.Session.SubmitCount == 0,
                "关闭施工未清空工作区或提交了命令。");

            await OpenFromList(ui, (int)RailwayWorkUiFixture.ActiveEdge - 1);
            setup.Session.QueueSubmit(new CoreCommand.SetAutomaticRailRepair(new(RailwayWorkUiFixture.ActiveEdge), null),
                CommandSource.UserInterface, Completed(1, initial, closed, new CommandDecision.Accepted([], [])), closed);
            setup.Session.QueueSubmit(new CoreCommand.ExpandRail(new(RailwayWorkUiFixture.ActiveEdge), new(1)),
                CommandSource.UserInterface, Completed(2, closed, closed, new CommandDecision.Rejected([RailwayWorkUiFixture.Blocker()])), closed);
            setup.Session.QueueSubmit(new CoreCommand.RepairRail(new(RailwayWorkUiFixture.ActiveEdge), new(2), new(1)),
                CommandSource.UserInterface, Completed(3, closed, repaired, new CommandDecision.Accepted([], [])), repaired);
            await _input.Activate(Find<Button>(panel, "DisableAutomaticRepair"));
            await _input.Frames(2);
            summary = Find<Label>(panel, "Summary").Text;
            works = Find<Label>(panel, "Works").Text;
            Require(summary.Contains("自动修复：关闭") && works.Contains("任务 2") && !works.Contains("任务 3")
                && works.Contains("夹具钢轨 × 7"),
                "关闭自动修复后没有重新查询：" + summary + "\n" + works);
            await _input.Activate(Find<Button>(panel, "ExpandRail"));
            await _input.Frames(2);
            Require(ui.ErrorText.Contains("货物不足：夹具钢轨。对象：聚居地 1。") && !ui.ErrorText.Contains("insufficient_goods") &&
                !ui.ErrorText.Contains("voxel:"), "加轨拒绝没有显示中文问题和货物：" + ui.ErrorText);
            Require(Find<Label>(panel, "Works").Text.Contains("夹具钢轨 × 7") && !Find<Label>(panel, "Works").Text.Contains($"× {computed}"),
                "拒绝后面板改用了长度估算。");
            await DismissError(ui);
            await _input.ClickItem(Find<ItemList>(panel, "Breaks"), 0);
            await _input.Activate(Find<Button>(panel, "RepairRail"));
            await _input.Frames(2);
            works = Find<Label>(panel, "Works").Text;
            Require(works.Contains("夹具钢轨 × 11") && !works.Contains($"× {computed}"),
                "受理后面板的已投入件数不是查询结果：" + works);
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Long_break_list_shows_every_row_and_the_wheel_scrolls_the_panel()
    {
        var setup = new HostSimulationTests.HostSetup(RailwayWorkUiFixture.WithManyBreaks(), RailwayWorkUiFixture.Content());
        await Run(setup, async ui =>
        {
            var panel = await OpenFromList(ui, (int)RailwayWorkUiFixture.ActiveEdge - 1);
            var breaks = Find<ItemList>(panel, "Breaks");
            await _input.Frames(2);
            Require(breaks.ItemCount == 60 && Enumerable.Range(0, breaks.ItemCount)
                    .All(index => breaks.GetItemRect(index).End.Y <= breaks.Size.Y),
                $"断点列表没有完整显示每一行：高度 {breaks.Size.Y}，末行 {breaks.GetItemRect(breaks.ItemCount - 1)}");
            var scroll = panel.GetNode<ScrollContainer>("Layout/Content");
            var wheel = breaks.GlobalPosition + breaks.Size / 2;
            for (var notch = 0; notch < 3; notch++)
            {
                await _input.MouseButton(wheel, MouseButton.WheelDown, true);
                await _input.MouseButton(wheel, MouseButton.WheelDown, false);
            }
            await _input.WaitUntil(() => scroll.ScrollVertical > 0);
            var row = Enumerable.Range(2, breaks.ItemCount - 2).First(index =>
                scroll.GetGlobalRect().Grow(-20).HasPoint(breaks.GlobalPosition + breaks.GetItemRect(index).GetCenter()));
            await _input.ClickItem(breaks, row);
            Require(breaks.IsSelected(row), $"滚动后无法选中第 {row + 1} 个断点。");
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Map_click_opens_line_detail_and_the_detail_button_opens_the_panel()
    {
        var setup = new HostSimulationTests.HostSetup(RailwayWorkUiFixture.Create(), RailwayWorkUiFixture.Content());
        LineScene? opened = null;
        var scenes = new FixedDisplayFactory
        {
            OpenWorldCall = (binding, _) => new SceneResult<IWorldSceneSession>.Success(opened = new(binding))
        };
        await Run(setup, async ui =>
        {
            var map = ui.GetNode<MacroMap>("Root/World/MacroMap");
            var edge = ui.View!.World.Edges.Single(item => item.Id.Value == RailwayWorkUiFixture.ActiveEdge);
            var start = ui.View.World.Nodes.Single(item => item.Id == edge.Start).Position;
            var end = ui.View.World.Nodes.Single(item => item.Id == edge.End).Position;
            var point = map.GlobalPosition + (map.MapToScreen(start) + map.MapToScreen(end)) / 2;
            var screen = TestScene.GetViewport().GetScreenTransform() * point;
            using (var motion = new InputEventMouseMotion { Position = screen, GlobalPosition = screen })
                Input.ParseInputEvent(motion);
            await _input.Frames();
            Require(TestScene.GetTree().Root.GuiGetHoveredControl() == map, "线路中点的点击被其他界面遮挡。");
            await _input.Click(point);
            await _input.Frames(2);
            var panel = Find<PanelContainer>(ui, "RailwayWork");
            Require(!panel.Visible, "点线路直接打开了施工面板。");
            Require(opened?.Target is WorldSceneTarget.Location
            {
                Value: WorldLocation.Edge { Id.Value: RailwayWorkUiFixture.ActiveEdge, DistanceMeters: var distance }
            } && distance == RailwayWorkUiFixture.ActiveLengthMeters / 2,
                "点线路没有进入该线路中点。实际目标：" + opened?.Target);
            Require(ui.GetParent().FindChild("WorldSceneView", true, false) is Node3D { Visible: true },
                "点线路后没有进入线路场景。");
            var panels = ui.GetNode<WorldPanels>("Root/World/WorldPanels");
            Require(panels.IsObjectOpen(new WorldObjectId.Edge(new(RailwayWorkUiFixture.ActiveEdge))), "点线路后没有打开线路详情。");
            var description = Find<Label>(LatestDetail(ui), "Description").Text;
            Require(description.Contains("线路长度") && !description.Contains('\r'), description);
            Require(description.Contains("1 号轨：空闲"), $"没有列车的轨道冒号后为空：{description}");
            await DismissError(ui);
            await _input.Activate(Find<Button>(LatestDetail(ui), "RailwayWork"));
            Require(panel.Visible && Find<Label>(panel, "Works").Text.Contains("任务 3"),
                "线路详情的施工按钮没有打开施工面板。");
        }, scenes);
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Missing_railway_rule_stays_submittable()
    {
        var setup = new HostSimulationTests.HostSetup(RailwayWorkUiFixture.Create(), RailwayWorkUiFixture.Content(false));
        await Run(setup, async ui =>
        {
            var panel = await OpenFromList(ui, (int)RailwayWorkUiFixture.ActiveEdge - 1);
            var summary = Find<Label>(panel, "Summary").Text;
            Require(summary.Contains("铁路规则不可用"), "缺少铁路规则时面板没有说明：" + summary);
            Require(!Find<Button>(panel, "ExpandRail").Disabled, "缺少铁路规则时加轨按钮被预先禁用。");
            await _input.ClickItem(Find<ItemList>(panel, "Breaks"), 0);
            Require(!Find<Button>(panel, "RepairRail").Disabled, "选中断点后，缺少铁路规则仍预先禁用了修复。");
        });
        setup.Session.AssertComplete();
    }

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
            var list = Find<ItemList>(Find<PanelContainer>(ui, "EdgesList"), "Objects");
            var index = Enumerable.Range(0, list.ItemCount).FirstOrDefault(i => list.GetItemText(i).StartsWith("线路 16 ·"), -1);
            Require(index >= 0, "线路列表没有线路 16：" + string.Join(" | ", Enumerable.Range(0, list.ItemCount).Select(list.GetItemText)));
            for (var frame = 0; frame < 180 && list.GetItemRect(index).Size.Y <= 0; frame++) await _input.Frames();
            Require(list.IsVisibleInTree() && list.GetItemRect(index).Size.Y > 0,
                $"线路列表无法点击：visible={list.IsVisibleInTree()} size={list.Size} rect={list.GetItemRect(index)} count={list.ItemCount}");
            list.Select(index);
            list.EnsureCurrentIsVisible();
            await _input.Frames(2);
            var visible = list.GetItemRect(index).GetCenter()
                - new Vector2((float)list.GetHScrollBar().Value, (float)list.GetVScrollBar().Value);
            Require(list.GetItemAtPosition(visible, true) == index,
                $"线路 16 的可见位置没有命中该行。rect={list.GetItemRect(index)} scroll={list.GetVScrollBar().Value} point={visible}");
            await _input.Click(list.GlobalPosition + visible);
            await _input.Activate(Find<Button>(LatestDetail(ui), "RailwayWork"));
            var panel = Find<PanelContainer>(ui, "RailwayWork");
            var broken = Edge(host, 16);
            Require(broken.Map.Breaks.Length == 1, "线路 16 的断点数不是 1：" +
                string.Join(",", broken.Map.Breaks.Select(item => item.Id.Value)));
            await _input.ClickItem(Find<ItemList>(panel, "Breaks"), 0);
            var source = Find<OptionButton>(panel, "MaterialSource");
            Require(source.ItemCount == 1 && source.GetItemText(source.Selected).Contains("聚居地 6"),
                "材料来源不是聚居地 6：" + string.Join(" | ", Enumerable.Range(0, source.ItemCount).Select(source.GetItemText)));
            await _input.Activate(Find<Button>(panel, "RepairRail"));
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
