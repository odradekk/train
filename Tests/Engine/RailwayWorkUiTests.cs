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

public class RailwayWorkUiTests(Node testScene) : TestClass(testScene)
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
            var map = new MacroMap { Name = "RailwayMap" };
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
            var help = panel.GetNode<VBoxContainer>("RailwayWork/Layout/Content/Body").GetChild<Label>(-1);
            Require(Find<ItemList>(panel, "Breaks").GetItemText(0).Contains("断点 2") && works.Contains("任务 2")
                && works.Contains("任务 3") && works.Contains("夹具钢轨 × 7") && works.Contains("进度 25%")
                && works.Contains("货物不足：夹具钢轨") && works.Contains("对象：聚居地 1") &&
                !works.Contains("insufficient_goods") && !works.Contains("voxel:") && !works.Contains($"× {computed}")
                && !summary.Contains('\r') && !works.Contains('\r')
                && help.Text.Contains("件数和是否允许在受理或拒绝后由核心给出") && !help.Text.Contains("工期"),
                "施工面板没有照查询显示断点、已投入货物和未开工阻塞：" + works + "\n" + help.Text);
            await _input.Frames(2);
            var scroll = panel.GetNode<ScrollContainer>("RailwayWork/Layout/Content");
            var autoRepair = Find<Button>(panel, "DisableAutomaticRepair").GetGlobalRect();
            Require(scroll.ScrollVertical == 0 && autoRepair.End.Y <= scroll.GetGlobalRect().End.Y + 1,
                "1080p 下要滚动才能看到关闭自动修复。");
            Require(Find<OptionButton>(panel, "MaterialSource").ItemCount == 1, "材料来源应只有自有聚居地。");
            await UiCapture.Capture(TestScene, "railway-panel");
            await _input.Activate(Find<Button>(panel, "Close"));
            await _input.Frames(2);
            Require(!panel.GetNode<PanelContainer>("RailwayWork").Visible && setup.Session.SubmitCount == 0,
                "关闭施工面板提交了命令或面板仍然打开。");

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
            var scroll = panel.GetNode<ScrollContainer>("RailwayWork/Layout/Content");
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
            var panel = ui.GetNode<RailwayWorkPanel>("Root/World/RailwayWork");
            Require(!panel.GetNode<PanelContainer>("RailwayWork").Visible, "点线路直接打开了施工面板。");
            Require(opened?.Target is WorldSceneTarget.Location
            {
                Value: WorldLocation.Edge { Id.Value: RailwayWorkUiFixture.ActiveEdge, DistanceMeters: var distance }
            } && distance == RailwayWorkUiFixture.ActiveLengthMeters / 2,
                "点线路没有进入该线路中点。实际目标：" + opened?.Target);
            Require(ui.GetParent().FindChild("WorldSceneView", true, false) is Node3D { Visible: true },
                "点线路后没有进入线路场景。");
            var panels = ui.GetNode<WorldPanels>("Root/World/WorldPanels");
            Require(panels.IsObjectOpen(new WorldObjectId.Edge(new(RailwayWorkUiFixture.ActiveEdge))), "点线路后没有打开线路详情。");
            var description = Find<Label>(LatestDetail(panels), "Description").Text;
            Require(description.Contains("线路长度") && !description.Contains('\r'), description);
            Require(description.Contains("1 号轨：空闲"), $"没有列车的轨道冒号后为空：{description}");
            await DismissError(ui);
            await _input.Activate(Find<Button>(LatestDetail(panels), "RailwayWork"));
            Require(panel.GetNode<PanelContainer>("RailwayWork").Visible && Find<Label>(panel, "Works").Text.Contains("任务 3"),
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
            await FramesUntil(TestScene, () => !host.IsBusy, dispatcher, TimeSpan.FromSeconds(90));
            Require(host.BeginNewGame(new("core:basic-through-world"), 40, "stage1-world-v1", host.SessionOptions!).Problem is null,
                "真实新局请求失败。");
            await FramesUntil(TestScene, () => host.GetStatus() is not null && !host.IsBusy, dispatcher, TimeSpan.FromSeconds(90));
            var railGood = host.Content!.Core.RailwayRules.Single(item => item.Id == host.GetStatus()!.View.World.RailwayRule).RailGood;
            var saved = host.CaptureSave();
            if (saved.Failure is not null || saved.Capture is not { } capture)
                throw new InvalidOperationException("保存失败：" + saved.Failure?.Code + " " + saved.Failure?.Message);
            var payload = ClaimSettlement(capture.Core.Encode(), railGood.Value);
            Require(host.BeginRestore(payload, capture.Content.Encode(), host.SessionOptions!, capture.Identity).Problem is null,
                "修改后的存档恢复请求失败。");
            await FramesUntil(TestScene, () => host.GetStatus() is not null && !host.IsBusy, dispatcher, TimeSpan.FromSeconds(90));
            var view = host.GetStatus()!.View;
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
            var started = (Lookup<TrainView>.Found)host.GetStatus()!.View.FindTrain(new(1));
            Require(started.Value.Activity == TrainActivity.NoRoute,
                "断点后列车没有失去路径。" + DescribeTrain(host.GetStatus()!.View, new(1)));

            TestScene.GetTree().Root.ContentScaleFactor = 1;
            UiCapture.ConfigureWindowIfNeeded(TestScene.GetTree().Root);
            await _input.Frames(2);
            var ui = app.GetNode<GameUi>("GameUi");
            await _input.Activate(ui.GetNode<Button>("Root/World/Bar/TopBar/Edges"));
            var list = ui.GetNode<ItemList>("Root/World/WorldPanels/EdgesList/Layout/Content/Body/Objects");
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
            await _input.Activate(Find<Button>(LatestDetail(ui.GetNode<WorldPanels>("Root/World/WorldPanels")), "RailwayWork"));
            var panel = ui.GetNode<RailwayWorkPanel>("Root/World/RailwayWork");
            var broken = (Lookup<EdgeView>.Found)host.GetStatus()!.View.FindEdge(new(16));
            Require(broken.Value.Map.Breaks.Length == 1, "线路 16 的断点数不是 1：" +
                string.Join(",", broken.Value.Map.Breaks.Select(item => item.Id.Value)));
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
            var accepted = (Lookup<EdgeView>.Found)host.GetStatus()!.View.FindEdge(new(16));
            var work = accepted.Value.Map.Works.Single(item => item.Kind == RailwayWorkKind.RepairBreak);
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
            var cleared = (Lookup<EdgeView>.Found)host.GetStatus()!.View.FindEdge(new(16));
            Require(cleared.Value.Map.Breaks.IsEmpty, "2880 步后断点仍在：" +
                string.Join(",", cleared.Value.Map.Breaks.Select(item => $"{item.Id.Value}@{item.DistanceMeters}")));

            var entered = false;
            for (var step = 0; step < 15000 && !entered; step++)
            {
                var stepped = host.Step(1);
                Require(stepped.Completed == 1, $"通车推进在修复后 {step} 步中断：{stepped.Problem?.Code}/{stepped.Problem?.Field}。" +
                    DescribeTrain(host.GetStatus()!.View, new(1)));
                entered = host.GetStatus()!.View.FindTrain(new(1)) is Lookup<TrainView>.Found
                {
                    Value.Position: TrainPosition.Edge { Id.Value: 16, Track: 1 }
                };
                if (step % 50 == 49) await _input.Frames();
            }
            Require(entered, "15000 步内列车 1 没有进入线路 16 的 1 号轨。" + DescribeTrain(host.GetStatus()!.View, new(1)));
        }
        finally
        {
            await fixture.Cleanup();
            TestScene.GetTree().Root.ContentScaleFactor = scale;
            Directory.Delete(directory, true);
        }
    }

    private async Task Run(HostSimulationTests.HostSetup setup, Func<GameUi, Task> body,
        IScenesFactory? scenes = null)
    {
        var tree = TestScene.GetTree();
        var fixture = new Fixture(tree);
        var scale = tree.Root.ContentScaleFactor;
        try
        {
            var app = fixture.LoadScene<Train.App.App>("res://App/App.tscn");
            app.CreateHost = logger => setup.Create(logger);
            if (scenes is not null) app.CreateScenes = () => scenes;
            await fixture.AddToRoot(app);
            await _input.WaitUntil(() => app.Host!.GetStatus() is not null);
            tree.Root.ContentScaleFactor = 1;
            UiCapture.ConfigureWindowIfNeeded(tree.Root);
            await _input.Frames(2);
            await body(app.GetNode<GameUi>("GameUi"));
        }
        finally { await fixture.Cleanup(); tree.Root.ContentScaleFactor = scale; }
    }

    private async Task<RailwayWorkPanel> OpenFromList(GameUi ui, int edgeIndex)
    {
        await _input.Activate(ui.GetNode<Button>("Root/World/Bar/TopBar/Edges"));
        await _input.ClickItem(ui.GetNode<ItemList>("Root/World/WorldPanels/EdgesList/Layout/Content/Body/Objects"), edgeIndex);
        await _input.Activate(Find<Button>(LatestDetail(ui.GetNode<WorldPanels>("Root/World/WorldPanels")), "RailwayWork"));
        return ui.GetNode<RailwayWorkPanel>("Root/World/RailwayWork");
    }

    private async Task DismissError(GameUi ui)
    {
        var panel = ui.GetNode<PanelContainer>("Root/ErrorPanel");
        if (panel.Visible) await _input.Activate(Find<Button>(panel, "Close"));
    }

    private sealed class LineScene(SceneBinding binding) : IWorldSceneSession
    {
        public SceneSessionId Id { get; } = new(Guid.NewGuid());
        public SceneBinding Binding => binding;
        public WorldSceneTarget? Target { get; private set; }
        public SceneResult<SceneRequestId> RequestView(WorldSceneTarget target)
        {
            Target = target;
            return new SceneResult<SceneRequestId>.Success(new(1));
        }
        public SceneResult<SimulationPosition> Observe(SceneWorldSnapshot snapshot, ImmutableArray<EventEnvelope> events)
            => new SceneResult<SimulationPosition>.Success(snapshot.Core.Position);
        public SceneResult<ISceneFrame> Present(SceneFrameRequest request, ISceneFrame? displayedFrame)
            => new SceneResult<ISceneFrame>.Success(new FixedSceneFrame
            {
                Update = new(null, new(Id, new(1), new(1), 1), default, [], [], new([], []), []),
                TerrainHeightResult = new Lookup<double>.Missing()
            });
        public SceneResult<SceneUpdate?> PresentCandidate(SceneFrameStamp? prepared) => new SceneResult<SceneUpdate?>.Success(null);
        public SceneResult<PickSubject> Resolve(SceneHit hit) =>
            throw new InvalidOperationException("施工测试不拾取场景。");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static PanelContainer LatestDetail(WorldPanels panels) =>
        panels.FindChildren("Details", "PanelContainer", true, false).Cast<PanelContainer>().Last();

    private static void Accept(HostCoordinator host, CoreCommand command)
    {
        var result = host.Submit(host.GetStatus()!.Binding, command, CommandSource.AutomationTest);
        if (result is not SubmissionResult.Completed { Receipt.Decision: CommandDecision.Accepted })
            throw new InvalidOperationException(command.GetType().Name + " 被拒绝：" + DecisionText(result));
    }

    private static string DecisionText(SubmissionResult result) => result switch
    {
        SubmissionResult.NotAccepted rejected => CommandDiagnostics.Describe(rejected.Reason),
        SubmissionResult.Completed { Receipt.Decision: CommandDecision.Rejected rejected } =>
            string.Join("；", rejected.Problems.Select(problem => CommandDiagnostics.Describe(problem))),
        SubmissionResult.Completed { Receipt.Decision: CommandDecision.Faulted faulted } => CommandDiagnostics.Describe(faulted.Fault),
        SubmissionResult.Completed completed => completed.Receipt.Decision.GetType().Name,
        _ => result.ToString() ?? ""
    };

    private static string ClaimSettlement(string payload, string railGood)
    {
        var root = JsonNode.Parse(payload)!;
        var world = root["world"]!;
        JsonNode? settlement = null;
        foreach (var item in world["settlements"]!.AsArray())
            if (item!["node"]!["value"]!.GetValue<ulong>() == 14) settlement = item;
        if (settlement is null) throw new InvalidOperationException("存档里没有节点 14 的聚居地。");
        settlement["relation"] = (int)SettlementRelation.PlayerOwned;
        var settlementId = settlement["id"]!["value"]!.GetValue<ulong>();
        var buildings = new HashSet<ulong>();
        foreach (var plot in settlement["plots"]!.AsArray())
            if (plot!["building"]?["value"] is { } building) buildings.Add(building.GetValue<ulong>());
        var parked = new HashSet<ulong>();
        foreach (var vehicle in settlement["parked_vehicles"]!.AsArray()) parked.Add(vehicle!["value"]!.GetValue<ulong>());
        var claimedNode = false;
        foreach (var node in world["nodes"]!.AsArray())
        {
            if (node!["id"]!["value"]!.GetValue<ulong>() != 14) continue;
            var occupant = node["occupant"]!;
            if (occupant["$kind"]!.GetValue<string>() != "settlement" || occupant["id"]!["value"]!.GetValue<ulong>() != settlementId)
                throw new InvalidOperationException("节点 14 的占据者不是该聚居地：" + occupant.ToJsonString());
            occupant["relation"] = (int)SettlementRelation.PlayerOwned;
            claimedNode = true;
        }
        if (!claimedNode) throw new InvalidOperationException("存档里没有节点 14。");
        foreach (var instance in world["instances"]!.AsArray())
        {
            var id = instance!["id"]!;
            var value = id["id"]!["value"]!.GetValue<ulong>();
            var kind = id["$kind"]!.GetValue<string>();
            if (kind == "building" && buildings.Contains(value) || kind == "vehicle" && parked.Contains(value))
                instance["ownership"] = (int)SettlementRelation.PlayerOwned;
        }
        var inventoryId = settlement["inventory"]!["value"]!.GetValue<ulong>();
        var inventory = world["inventories"]!.AsArray().Single(item => item!["id"]!["value"]!.GetValue<ulong>() == inventoryId)!;
        var goods = inventory["goods"]!.AsArray();
        var key = "voxel:" + railGood;
        var insertAt = goods.Count;
        JsonNode? existing = null;
        for (var i = 0; i < goods.Count; i++)
        {
            var itemKey = GoodKey(goods[i]!["good"]!);
            if (itemKey == key) { existing = goods[i]; break; }
            if (string.CompareOrdinal(itemKey, key) > 0) { insertAt = i; break; }
        }
        if (existing is not null) existing["quantity"] = existing["quantity"]!.GetValue<long>() + 18;
        else
        {
            goods.Insert(insertAt, new JsonObject
            {
                ["good"] = new JsonObject { ["$kind"] = "voxel", ["definition"] = new JsonObject { ["value"] = railGood } },
                ["quantity"] = 18
            });
            AddRailReference(root, inventoryId, key, railGood);
        }
        return root.ToJsonString();
    }

    private static void AddRailReference(JsonNode root, ulong inventoryId, string key, string railGood)
    {
        var definitions = root["references"]!["definitions"]!.AsArray();
        var field = $"World.Inventories[{inventoryId}].Goods[{key}]";
        var wasMissing = false;
        var insertAt = definitions.Count;
        var placed = false;
        for (var i = 0; i < definitions.Count; i++)
        {
            var item = definitions[i]!;
            var itemKey = DefinitionSortKey(item["definition"]!);
            if (itemKey == key) wasMissing = item["was_missing"]!.GetValue<bool>();
            if (placed) continue;
            var itemField = item["field"]!.GetValue<string>();
            var comparison = string.CompareOrdinal(itemKey, key);
            if (comparison == 0 && itemField == field) return;
            if (comparison > 0 || comparison == 0 && string.CompareOrdinal(itemField, field) > 0)
            {
                insertAt = i;
                placed = true;
            }
        }
        definitions.Insert(insertAt, new JsonObject
        {
            ["definition"] = new JsonObject { ["$kind"] = "voxel", ["id"] = new JsonObject { ["value"] = railGood } },
            ["frozen_revision"] = null,
            ["field"] = field,
            ["was_missing"] = wasMissing
        });
    }

    private static string DefinitionSortKey(JsonNode definition)
    {
        var kind = definition["$kind"]!.GetValue<string>();
        if (kind == "process")
        {
            var id = definition["id"]!;
            return id["$kind"]!.GetValue<string>() + ":" + id["definition"]!["value"]!.GetValue<string>();
        }
        var prefix = kind switch
        {
            "voxel" => "voxel",
            "composite" => "composite",
            "rule" => "rule",
            "settlement_type" => "settlement",
            "template" => "template",
            "terrain" => "terrain",
            "creature" => "creature",
            "lair" => "lair",
            _ => throw new InvalidOperationException("未知引用种类：" + kind)
        };
        return prefix + ":" + definition["id"]!["value"]!.GetValue<string>();
    }

    private static string GoodKey(JsonNode good)
    {
        var kind = good["$kind"]!.GetValue<string>();
        var id = kind == "assembly" ? good["composition"]!["value"]!.GetValue<string>() : good["definition"]!["value"]!.GetValue<string>();
        return kind + ":" + id;
    }

    private static bool Connects(EdgeMapItem edge, ulong left, ulong right) =>
        edge.Start.Value == left && edge.End.Value == right || edge.Start.Value == right && edge.End.Value == left;

    private static string DescribeTrain(ICoreReadView view, TrainId id)
    {
        if (view.FindTrain(id) is not Lookup<TrainView>.Found train) return $"列车 {id.Value} 不存在。";
        var position = train.Value.Position switch
        {
            TrainPosition.Node node => $"节点 {node.Id.Value} 阶段 {node.Phase}",
            TrainPosition.Edge edge => $"线路 {edge.Id.Value} 轨 {edge.Track} 距离 {edge.DistanceMeters:0.##}",
            _ => "未知位置"
        };
        var blockers = train.Value.Blockers.IsDefaultOrEmpty ? "" : " 阻塞 " + string.Join("、", train.Value.Blockers.Select(item => item.Code));
        return $"列车 {id.Value} 活动 {train.Value.Activity}，{position}。{blockers}";
    }

    private static string DescribeEdges(ICoreReadView view, ulong node) =>
        string.Join("；", view.World.Edges.Where(edge => edge.Start.Value == node || edge.End.Value == node)
            .Select(edge => $"线路 {edge.Id.Value} {edge.Start.Value}—{edge.End.Value} {edge.Tracks} 轨"));

    private static string DescribeSettlements(ICoreReadView view) =>
        string.Join("；", view.World.Nodes.Select(node => node.Occupant).OfType<NodeOccupant.Settlement>()
            .Select(item => $"聚居地 {item.Id.Value} 关系 {item.Relation}"));

    private static Vector2 Midpoint(MacroMap map, ICoreReadView view, ulong edgeId)
    {
        var edge = view.World.Edges.Single(item => item.Id.Value == edgeId);
        var start = view.World.Nodes.Single(item => item.Id == edge.Start).Position;
        var end = view.World.Nodes.Single(item => item.Id == edge.End).Position;
        return (map.MapToScreen(start) + map.MapToScreen(end)) / 2;
    }

    private static (Vector2 Construction, Vector2 Break) Marks(MacroMap map, ICoreReadView view, ulong edgeId)
    {
        var edge = view.World.Edges.Single(item => item.Id.Value == edgeId);
        var start = map.MapToScreen(view.World.Nodes.Single(item => item.Id == edge.Start).Position);
        var end = map.MapToScreen(view.World.Nodes.Single(item => item.Id == edge.End).Position);
        var normal = (end - start).Normalized().Orthogonal();
        var midpoint = (start + end) / 2;
        return (midpoint - normal * 16, midpoint + normal * 16);
    }

    private static bool RegionHas(Image image, Control map, Vector2 local, Color expected, int radius)
    {
        for (var y = -radius; y <= radius; y++)
        for (var x = -radius; x <= radius; x++)
            if (Near(Pixel(image, map, local + new Vector2(x, y)), expected)) return true;
        return false;
    }

    private static string Describe(Image image, Control map, Vector2 local) => $" 实际 {Pixel(image, map, local)}，位置 {local}。";

    private static Color Pixel(Image image, Control map, Vector2 local)
    {
        var global = map.GetGlobalTransformWithCanvas() * local;
        var visible = map.GetViewport().GetVisibleRect().Size;
        var point = new Vector2(global.X * image.GetWidth() / visible.X, global.Y * image.GetHeight() / visible.Y);
        return image.GetPixel(Mathf.Clamp(Mathf.RoundToInt(point.X), 0, image.GetWidth() - 1),
            Mathf.Clamp(Mathf.RoundToInt(point.Y), 0, image.GetHeight() - 1));
    }

    private static bool Near(Color pixel, Color expected) =>
        Math.Abs(pixel.R - expected.R) < 0.08f && Math.Abs(pixel.G - expected.G) < 0.08f && Math.Abs(pixel.B - expected.B) < 0.08f;
}
