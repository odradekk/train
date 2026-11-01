using System.Collections.Immutable;
using Chickensoft.GoDotTest;
using Chickensoft.GodotTestDriver;
using Godot;
using Train.App;
using Train.App.Logging;
using Train.App.Notifications;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using Train.Scenes.Contracts;
using Train.Scenes.ContractSupport;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class CombatUiTests(Node testScene) : TestClass(testScene)
{
    private readonly UiTestInput _input = new(testScene);

    [Test]
    public async Task Shared_policy_stops_a_four_times_encounter_frame_and_slows_to_one()
    {
        var fixture = new Fixture(TestScene.GetTree());
        var backup = BackupSettings();
        Train.App.App? app = null;
        try
        {
            ClearSettings();
            var setup = new HostSimulationTests.HostSetup();
            app = await Boot(fixture, (loaded, logger) => setup.Create(logger, loaded.Notifications.Decide));
            Require(app.Notifications.Action(NotificationCategory.Encounter) == NotificationAction.SlowToOne,
                "生产策略对象的遇敌默认不是降到 1×。");
            Queue(setup.Session, 1, new CoreEvent.CombatStarted(new(1), new WorldLocation.Node(new(1)), []));
            Queue(setup.Session, 2, null);
            Require(app.Host!.SetSpeed(SimulationSpeed.Four) is null && app.Host.SetPaused(false) is null,
                "无法把共享策略宿主设为 4×。");
            app.Host.ProcessFrame(SimulationTime.StepSeconds);
            var status = app.Host.GetStatus()!;
            var count = setup.Session.AdvanceCount;
            var speed = status.Speed;
            var paused = status.PlayerPaused;
            var step = status.View.Position.CompletedSteps;
            // App._Process 每帧都会推进；读完结果后立刻暂停，避免剩下的队列在下一帧被吃掉。
            app.Host.SetPaused(true);
            Require(count == 1 && step == 1 && speed == SimulationSpeed.One && !paused &&
                app.Host.LastNotification == NotificationAction.SlowToOne,
                $"4× 遇敌没有在本帧停住并降到 1×：count={count}，step={step}，speed={speed}，paused={paused}。");
        }
        finally
        {
            await Cleanup(fixture, app);
            RestoreSettings(backup);
        }
    }

    [Test]
    public async Task Encounter_pause_setting_stops_the_next_step_on_the_shared_policy()
    {
        var fixture = new Fixture(TestScene.GetTree());
        var backup = BackupSettings();
        Train.App.App? app = null;
        try
        {
            ClearSettings();
            var setup = new HostSimulationTests.HostSetup();
            app = await Boot(fixture, (loaded, logger) => setup.Create(logger, loaded.Notifications.Decide));
            var ui = app.GetNode<GameUi>("GameUi");
            await _input.ChooseMenu(Find<MenuButton>(ui, "GameMenu"), "设置");
            var choice = Find<OptionButton>(ui, "Notify_encounter");
            await _input.WaitUntil(() => choice.IsVisibleInTree());
            await _input.Choose(choice, (int)NotificationAction.Pause);
            Require(app.Notifications.Action(NotificationCategory.Encounter) == NotificationAction.Pause,
                "设置面板没有把暂停写进共享策略。");
            Queue(setup.Session, 1, new CoreEvent.CombatJoined(new(1), new CombatUnitId.Instance(new InstanceId.Vehicle(new(7)))));
            Queue(setup.Session, 2, null);
            Require(app.Host!.SetSpeed(SimulationSpeed.Four) is null && app.Host.SetPaused(false) is null,
                "无法在更改遇敌设置后继续推进。");
            app.Host.ProcessFrame(SimulationTime.StepSeconds);
            app.Host.ProcessFrame(SimulationTime.StepSeconds);
            var status = app.Host.GetStatus()!;
            if (!status.PlayerPaused) app.Host.SetPaused(true);
            Require(setup.Session.AdvanceCount == 1 && status.View.Position.CompletedSteps == 1 &&
                status.PlayerPaused && status.Speed == SimulationSpeed.Four,
                $"遇敌暂停没有挡住下一步：count={setup.Session.AdvanceCount}，step={status.View.Position.CompletedSteps}，paused={status.PlayerPaused}，speed={status.Speed}。");
        }
        finally
        {
            await Cleanup(fixture, app);
            RestoreSettings(backup);
        }
    }

    [Test]
    public async Task Combat_commands_follow_the_view_and_unimplemented_commands_stay_unaccepted()
    {
        var fixture = new Fixture(TestScene.GetTree());
        var host = new CombatHost(rich: true);
        Train.App.App? app = null;
        try
        {
            app = await Boot(fixture, (loaded, logger) => host.Create(logger, loaded.Notifications.Decide), () => new FixedDisplayFactory());
            var ui = app.GetNode<GameUi>("GameUi");
            var combat = await SelectBattlefield(ui);
            var state = Find<Label>(combat, "CombatState").Text;
            Require(state.Contains("敌对") && state.Contains("士气：0.40") && state.Contains("已投降") &&
                state.Contains("没有可达路径") && !state.Contains("no_route") && !state.Contains('\r'),
                state);
            Require(Find<OptionButton>(combat, "TrainPriority").Selected == (int)TargetPriority.ArmedFirst &&
                Find<OptionButton>(combat, "TrainScope").Selected == (int)TargetScope.All &&
                Find<OptionButton>(combat, "TrainEncounter").Selected == (int)EncounterAction.Engage &&
                Find<SpinBox>(combat, "TrainRetreatRatio").Value == 0.5 &&
                Find<OptionButton>(combat, "SettlementPriority").Selected == (int)TargetPriority.LowestDurability &&
                Find<OptionButton>(combat, "SettlementScope").Selected == (int)TargetScope.ArmedOnly,
                "交战控件没有装入查询里的规则。");
            var view = host.Session.CurrentView;
            var rules = new TrainEngagementRules(TargetPriority.ArmedFirst, TargetScope.All, EncounterAction.Engage, 0.5);
            host.Session.QueueSubmit(new CoreCommand.Retreat(new(7)), CommandSource.UserInterface,
                Completed(1, view, view, new CommandDecision.Accepted([], [])), view);
            host.Session.QueueSubmit(new CoreCommand.FocusFire(new(5), new CombatController.Train(new(7)),
                new WorldObjectId.Instance(new InstanceId.Building(new(13)))), CommandSource.UserInterface,
                Completed(2, view, view, new CommandDecision.Accepted([], [])), view);
            host.Session.QueueSubmit(new CoreCommand.SetTrainEngagement(new(7), rules), CommandSource.UserInterface,
                Completed(3, view, view, new CommandDecision.Accepted([], [])), view);
            host.Session.QueueSubmit(new CoreCommand.OccupySettlement(new(4), new(7)), CommandSource.UserInterface,
                Completed(4, view, view, new CommandDecision.Rejected([new("invalid_state", "relation", null, [])])), view);
            Require(Button(combat, "Vehicle11").Text.Contains("我方") && Button(combat, "Building13").Text.Contains("敌方") &&
                Button(combat, "DeclareWar").Disabled && Button(combat, "ApplySettlementEngagement").Disabled &&
                combat.FindChildren("*", "Label", true, false).Cast<Label>().Any(label => label.Text == "撤退阈值（结构耐久比例）"),
                $"筛选或规则标题不对：{Button(combat, "Vehicle11").Text} / {Button(combat, "Building13").Text}");
            await Press(Button(combat, "Retreat"));
            await Press(Button(combat, "FocusFire"));
            await Press(Button(combat, "ApplyTrainEngagement"));
            await Press(Button(combat, "OccupyTrain7"));
            await _input.WaitUntil(() => !Button(combat, "Occupy").Disabled);
            await Press(Button(combat, "Occupy"));
            Require(host.Session.SubmitCount == 4 && !app.Host!.GetStatus()!.IsFrozen,
                $"已实现命令没有按脚本接受或拒绝：count={host.Session.SubmitCount}。");
            host.Gate!.ThrowUnimplemented = true;
            await Press(Button(combat, "Occupy"));
            Require(ui.ErrorText == "该操作尚未实现。" && host.Session.SubmitCount == 4 &&
                app.Host?.GetStatus() is { IsFrozen: false },
                $"未实现命令被当成接受或冻结了核心：{ui.ErrorText}，count={host.Session.SubmitCount}。");
        }
        finally { await Cleanup(fixture, app); }
    }

    [Test]
    public async Task Vanished_combat_keeps_the_selection_and_the_notice_reports_missing()
    {
        var fixture = new Fixture(TestScene.GetTree());
        var host = new CombatHost(rich: true);
        var scenes = new FixedDisplayFactory();
        Train.App.App? app = null;
        try
        {
            app = await Boot(fixture, (loaded, logger) => host.Create(logger, loaded.Notifications.Decide), () => scenes);
            var ui = app.GetNode<GameUi>("GameUi");
            var combat = await SelectBattlefield(ui);
            host.Session.QueueAdvance(new(1, new(1, 0),
                [new(new(1, 0), 0, new CoreEvent.CombatStarted(new(5), new WorldLocation.Edge(new(1), 100), []))], null, null),
                host.View(new(1, 0), combatPresent: false));
            Require(app.Host!.SetPaused(false) is null, "无法推进到战斗消失的边界。");
            app.Host.ProcessFrame(SimulationTime.StepSeconds);
            app.Host.SetPaused(true);
            await _input.Frames();
            Require(host.Session.AdvanceCount == 1 && combat.SelectedCombat == new CombatId(5) &&
                combat.SelectedParticipant == new CombatUnitId.Instance(new InstanceId.Vehicle(new(11))) &&
                combat.SelectedFocus == new WorldObjectId.Instance(new InstanceId.Building(new(13))) &&
                Find<Label>(combat, "CombatState").Text.Contains("已不存在"),
                $"count={host.Session.AdvanceCount} {Find<Label>(combat, "CombatState").Text}");
            await Press(Find<Button>(ui, "Notices"));
            var log = Find<PanelContainer>(ui, "NoticeLog");
            var row = log.FindChildren("Notice", "Button", true, false).Cast<Button>().Single();
            Require(row.Text.Contains("遭遇战斗"), row.Text);
            await Press(row);
            Require(ui.ErrorText == "已不存在" && scenes.OpenCalls.Count == 0,
                $"消失的战斗通知打开了场景或没有说明缺失：{ui.ErrorText}，opens={scenes.OpenCalls.Count}。");
            await Press(Find<CheckBox>(log, "FilterWarning"));
            Require(!row.Visible, "等级过滤仍然显示警告记录。");
        }
        finally { await Cleanup(fixture, app); }
    }

    [Test]
    public async Task Surrender_or_relation_notice_selects_the_settlement_until_it_is_gone()
    {
        var fixture = new Fixture(TestScene.GetTree());
        var host = new CombatHost(rich: true);
        var scenes = new FixedDisplayFactory();
        Train.App.App? app = null;
        try
        {
            app = await Boot(fixture, (_, logger) => host.Create(logger), () => scenes);
            host.Session.QueueAdvance(new(1, new(1, 0),
            [
                new(new(1, 0), 0, new CoreEvent.SettlementSurrendered(new(5), new(4))),
                new(new(1, 0), 1, new CoreEvent.SettlementRelationChanged(new(4),
                    SettlementRelation.NonHostile, SettlementRelation.PlayerOwned))
            ], null, null), host.View(new(1, 0)));
            Require(app.Host!.SetPaused(false) is null, "无法推进投降和关系变化。");
            app.Host.ProcessFrame(SimulationTime.StepSeconds);
            app.Host.SetPaused(true);
            await _input.Frames();
            var ui = app.GetNode<GameUi>("GameUi");
            await Press(Find<Button>(ui, "Notices"));
            var log = Find<PanelContainer>(ui, "NoticeLog");
            var rows = log.FindChildren("*", "Button", true, false).Cast<Button>().ToArray();
            var surrender = rows.Single(button => button.Text.Contains("投降"));
            var relation = rows.Single(button => button.Text.Contains("变为自有"));
            Require(surrender.Text.Contains("警告") && surrender.Text.Contains("Town") &&
                relation.Text.Contains("完成") && relation.Text.Contains("Town"),
                $"{surrender.Text} / {relation.Text}");
            await Press(surrender);
            Require(((GameShell)ui.World).SelectedObject == new WorldObjectId.Settlement(new(4)) &&
                Find<PanelContainer>(ui, "Settlement").IsVisibleInTree(), "投降通知没有选中聚居地检查器。");
            ((GameShell)ui.World).Inspect(null, null);
            await Press(relation);
            Require(((GameShell)ui.World).SelectedObject == new WorldObjectId.Settlement(new(4)) &&
                scenes.OpenCalls.Count == 0, "关系变化通知没有选中聚居地，或跳进了场景。");
            host.Session.QueueAdvance(new(1, new(2, 0), [], null, null), host.View(new(2, 0), settlementPresent: false));
            Require(app.Host.SetPaused(false) is null, "无法推进到聚居地消失。");
            app.Host.ProcessFrame(SimulationTime.StepSeconds);
            app.Host.SetPaused(true);
            await _input.Frames();
            var opens = scenes.OpenCalls.Count;
            await Press(surrender);
            Require(ui.ErrorText == "已不存在" && scenes.OpenCalls.Count == opens,
                $"聚居地消失后仍打开场景：{ui.ErrorText}，opens={scenes.OpenCalls.Count}。");
        }
        finally { await Cleanup(fixture, app); }
    }

    [Test]
    public async Task One_retreat_notice_per_train_and_cleared_lair_notice_selects_its_node()
    {
        var fixture = new Fixture(TestScene.GetTree());
        var host = new CombatHost(rich: true);
        var scenes = new FixedDisplayFactory();
        Train.App.App? app = null;
        try
        {
            app = await Boot(fixture, (_, logger) => host.Create(logger), () => scenes);
            host.Session.QueueAdvance(new(1, new(1, 0),
            [
                new(new(1, 0), 0, new CoreEvent.CombatRetreatStarted(new(5),
                    new CombatUnitId.Instance(new InstanceId.Vehicle(new(11))), 3)),
                new(new(1, 0), 1, new CoreEvent.CombatRetreatStarted(new(5),
                    new CombatUnitId.Instance(CombatHost.SecondVehicle), 3)),
                new(new(1, 0), 2, new CoreEvent.LairCleared(new(3), new(2), new Money(0)))
            ], null, null), host.View(new(1, 0)));
            Require(app.Host!.SetPaused(false) is null, "无法推进撤退和清剿。");
            app.Host.ProcessFrame(SimulationTime.StepSeconds);
            app.Host.SetPaused(true);
            await _input.Frames();
            var ui = app.GetNode<GameUi>("GameUi");
            await Press(Find<Button>(ui, "Notices"));
            var rows = Find<PanelContainer>(ui, "NoticeLog").FindChildren("*", "Button", true, false).Cast<Button>().ToArray();
            var retreats = rows.Where(button => button.Text.Contains("开始撤退")).ToArray();
            var cleared = rows.Where(button => button.Text.Contains("生物聚落被清剿")).ToArray();
            Require(retreats.Length == 1 && retreats[0].Text.Contains("列车 7") && cleared.Length == 1,
                $"同一列车的一次撤退应只有一条通知，清剿应有一条：{string.Join(" | ", rows.Select(button => button.Text))}");
            await Press(cleared[0]);
            Require(((GameShell)ui.World).SelectedObject == new WorldObjectId.Node(new(2)) &&
                ((GameShell)ui.World).InspectorContent?.IsVisibleInTree() == true &&
                scenes.OpenCalls.Count == 0 && ui.ErrorText != "已不存在", "清剿通知没有选中节点检查器，或跳进了场景。");
        }
        finally { await Cleanup(fixture, app); }
    }

    [Test]
    public async Task Combat_marker_opens_the_combat_edge_location()
    {
        var fixture = new Fixture(TestScene.GetTree());
        var host = new CombatHost(rich: true);
        var scale = TestScene.GetTree().Root.ContentScaleFactor;
        FixedDisplaySession? scene = null;
        var target = new WorldSceneTarget.Location(new WorldLocation.Edge(new(1), 100));
        Train.App.App? app = null;
        try
        {
            UiCapture.ConfigureWindowIfNeeded(TestScene.GetTree().Root);
            TestScene.GetTree().Root.ContentScaleFactor = 1;
            app = await Boot(fixture, (_, logger) => host.Create(logger), () => new FixedDisplayFactory
            {
                OpenWorldCall = (binding, _) =>
                {
                    scene = new FixedDisplaySession
                    {
                        Id = new(Guid.Parse("00000000-0000-0000-0000-000000320354")),
                        Binding = binding,
                        RequestResults = ImmutableDictionary<WorldSceneTarget, SceneResult<SceneRequestId>>.Empty.Add(
                            target, new SceneResult<SceneRequestId>.Success(new(1))),
                        PresentResults = [new SceneResult<ISceneFrame>.Failure([
                            new("combat_probe", SceneStage.Present, null, null, null, null, "只记录战斗地点。")])]
                    };
                    return new SceneResult<IWorldSceneSession>.Success(scene);
                }
            });
            var map = app.GetNode<MacroMap>("GameUi/Root/World/MacroMap");
            await _input.WaitUntil(() =>
            {
                var start = map.MapToScreen(new(250, 300));
                var end = map.MapToScreen(new(750, 300));
                return map.IsVisibleInTree() && map.Size.X > 100 && start.DistanceTo(end) > 80;
            });
            var start = map.MapToScreen(new(250, 300));
            var end = map.MapToScreen(new(750, 300));
            var point = map.GlobalPosition + start.Lerp(end, 100f / 600f);
            await _input.Click(point);
            Require(scene?.RequestCalls.Count == 1 && scene.RequestCalls[0] == target,
                $"战斗标记没有打开线路 100 m：count={scene?.RequestCalls.Count}，point={point}，start={start}，end={end}。");
        }
        finally
        {
            TestScene.GetTree().Root.ContentScaleFactor = scale;
            await Cleanup(fixture, app);
        }
    }

    [Test]
    public async Task Total_vehicle_loss_notifies_without_freezing_and_save_actions_remain()
    {
        var fixture = new Fixture(TestScene.GetTree());
        var host = new CombatHost(rich: false);
        var owned = new HashSet<Guid>();
        Train.App.App? app = null;
        try
        {
            app = await Boot(fixture, (loaded, logger) => host.Create(logger, loaded.Notifications.Decide));
            await _input.WaitUntil(() => app.Saves!.Status is not null);
            var first = host.Session;
            const string expected = "运行因车辆全部被毁结束。游戏继续。可以在「存档」里读档，或「返回菜单」后新游戏。";
            first.QueueAdvance(new(1, new(1, 0), [new(new(1, 0), 0, new CoreEvent.RunReported(new(new(7), new(1, 0),
                RunEndReason.AllVehiclesDestroyed, new(new(0, 0), new Money(0), new Money(0), new Money(0), 0, 0, null, []))))],
                null, null), host.View(new(1, 0)));
            Require(app.Host!.SetPaused(false) is null, "无法推进列车全毁边界。");
            app.Host.ProcessFrame(SimulationTime.StepSeconds);
            app.Host.SetPaused(true);
            var ui = app.GetNode<GameUi>("GameUi");
            var notices = ui.FindChildren("Notice", "Button", true, false).Cast<Button>().Select(button => button.Text).ToArray();
            Require(NotificationPolicy.RunDestroyedText == expected && notices.Any(text => text.Contains(expected) && text.Contains("列车 7") && text.Contains("严重")) &&
                !app.Host.GetStatus()!.IsFrozen && first.SubmitCount == 0 &&
                !expected.Contains("核心模拟发生故障") && !ui.ErrorText.Contains("核心模拟发生故障"),
                $"全毁通知或冻结状态不对：{string.Join(" | ", notices)} / {ui.ErrorText}");
            await _input.ChooseMenu(Find<MenuButton>(ui, "GameMenu"), "存档与读档");
            var menu = app.GetNode<MainMenu>("MainMenu");
            var panel = menu.GetChildren().OfType<SavePanel>().Single();
            await _input.WaitUntil(() => panel.Visible && UiTestInput.SaveButton(panel, "保存").Visible);
            Require(panel.FindChildren("*", "Label", true, false).Cast<Label>().All(label => !label.Text.Contains("核心已冻结")),
                "存档面板把未冻结的全毁说成核心已冻结。");
            owned.Add(app.Saves!.Status!.Identity.Id);
            await _input.Activate(UiTestInput.SaveButton(panel, "保存"), panel);
            await _input.WaitUntil(() => !app.Saves.HasPendingWork && panel.Entries.Count > 0);
            await _input.Activate(UiTestInput.SaveButton(panel, "读取最近存档"), panel);
            await _input.WaitUntil(() => host.Restores >= 2 && app.Host.GetStatus() is not null &&
                ui.GetNode<MacroMap>("Root/World/MacroMap").IsVisibleInTree() &&
                !ui.FindChildren("Notice", "Button", true, false).Cast<Button>().Any(button => button.Text.Contains(expected)));
            Require(!app.Host.GetStatus()!.IsFrozen && first.SubmitCount == 0 && host.Session.SubmitCount == 0,
                "读档后核心被冻结，或界面自动提交了命令。");
            if (!panel.Visible) await _input.ChooseMenu(Find<MenuButton>(ui, "GameMenu"), "存档与读档");
            await _input.WaitUntil(() => panel.Visible);
            await _input.Activate(UiTestInput.SaveButton(panel, "返回菜单"), panel);
            await _input.WaitUntil(() => app.Host.GetStatus() is null || LeaveDialog(panel)?.Visible == true);
            if (app.Host.GetStatus() is not null)
            {
                var leave = LeaveDialog(panel)!;
                Require(!leave.DialogText.Contains("核心已冻结"), leave.DialogText);
                var discard = leave.FindChildren("*", "Button", true, false).Cast<Button>().Single(button => button.Text == "不保存");
                await _input.Activate(discard, leave);
                await _input.WaitUntil(() => app.Host.GetStatus() is null);
            }
        }
        finally { await Cleanup(fixture, app, owned); }
    }

    private async Task<Train.App.App> Boot(Fixture fixture, Func<Train.App.App, TrainLogger, HostCoordinator> create,
        Func<IScenesFactory>? scenes = null)
    {
        var app = fixture.LoadScene<Train.App.App>("res://App/App.tscn");
        app.CreateHost = logger => create(app, logger);
        if (scenes is not null) app.CreateScenes = scenes;
        await fixture.AddToRoot(app);
        await _input.WaitUntil(() => app.Host!.GetStatus() is not null);
        await _input.Frames(2);
        return app;
    }

    private async Task Cleanup(Fixture fixture, Train.App.App? app, HashSet<Guid>? owned = null)
    {
        try
        {
            if (app?.Saves is { } saves)
            {
                if (owned is not null && saves.Status is { } status) owned.Add(status.Identity.Id);
                saves.Cancel();
                await _input.WaitUntil(() => !saves.HasPendingWork);
            }
        }
        finally
        {
            try { await fixture.Cleanup(); }
            finally { if (owned is not null) UiTestInput.DeleteSaves(owned); }
        }
    }

    private static void Queue(ScriptedCoreSession session, ulong step, CoreEvent? item)
    {
        ImmutableArray<EventEnvelope> events = item is null ? [] : [new(new(step, 0), 0, item)];
        session.QueueAdvance(new(1, new(step, 0), events, null, null), new FixedCoreReadView(new(step, 0), HostCoreScenario.Input, null, new(1))
        {
            Economy = UiWorldFixture.Economy,
            World = new(0, new("fixture:railway"), 100, 100, [], [], [], [], [])
        });
    }

    private static (string Path, byte[]? Bytes) BackupSettings()
    {
        var path = ProjectSettings.GlobalizePath("user://settings.cfg");
        return (path, File.Exists(path) ? File.ReadAllBytes(path) : null);
    }

    private static void RestoreSettings((string Path, byte[]? Bytes) backup)
    {
        if (backup.Bytes is null) { if (File.Exists(backup.Path)) File.Delete(backup.Path); }
        else File.WriteAllBytes(backup.Path, backup.Bytes);
    }

    private static void ClearSettings()
    {
        var path = ProjectSettings.GlobalizePath("user://settings.cfg");
        if (File.Exists(path)) File.Delete(path);
    }

    private static ConfirmationDialog? LeaveDialog(SavePanel panel) =>
        panel.GetChildren().OfType<ConfirmationDialog>().SingleOrDefault(dialog => dialog.Title == "未保存的更改");

    private static Button Button(Node parent, string name) =>
        parent.FindChildren(name, "Button", true, false).Cast<Button>().Single();

    private sealed class CombatHost(bool rich)
    {
        private readonly FixedCoreCapture _capture = new(new(new(0, 0), "combat", "combat"),
            HostCoreScenario.References, "combat core", HostCoreScenario.Input, new(1));
        private readonly CoreSessionOptions _options = new(new("fixture", "fixture", "fixture", "fixture", "fixture"), true, 1);
        internal static readonly InstanceId SecondVehicle = new InstanceId.Vehicle(new(12));
        internal GateSession? Gate { get; private set; }
        internal ScriptedCoreSession Session => Gate!.Inner;
        internal int Restores { get; private set; }

        internal HostCoordinator Create(TrainLogger logger, Func<CoreEvent, NotificationAction>? policy = null)
        {
            var core = new ScriptedCoreFactory
            {
                DecodeStateCall = payload => payload == "combat core"
                    ? new CoreResult<ICoreCapture>.Success(_capture)
                    : throw new InvalidOperationException("UNCONFIGURED: core payload"),
                PrepareRestoreCall = (_, projection, _) =>
                {
                    if (projection.Identity != HostCoreScenario.Input)
                        throw new InvalidOperationException("UNCONFIGURED: projection");
                    Restores++;
                    var session = new ScriptedCoreSession(View(new(0, 0)));
                    session.SetCapture(CapturePurpose.Save, new CoreResult<ICoreCapture>.Success(_capture));
                    session.ReadRecordingCall = () => new(new(0, 0), [new(new(1), _capture)], [], [_capture.Checkpoint], [], [], _capture.References);
                    session.ConfirmRecordingStartCall = (point, _) => point.Value == 1
                        ? new([], []) : throw new InvalidOperationException("UNCONFIGURED: recording point");
                    Gate = new(session);
                    return new CoreResult<ICoreSessionCandidate>.Success(new ScriptedCoreCandidate(Gate, HostCoreScenario.References));
                }
            };
            var content = new ScriptedContentFactory
            {
                CaptureReplayCall = (history, _) => new ContentResult<IContentReplayCapture>.Success(
                    new FixedContentReplayCapture(history, "combat recording")),
                CaptureCall = (_, _) => new ContentResult<IContentCapture>.Success(new FixedContentCapture("combat content")),
                DecodeCaptureCall = payload => payload == "combat content"
                    ? new ContentResult<IContentCapture>.Success(new FixedContentCapture(payload))
                    : throw new InvalidOperationException("UNCONFIGURED: content payload"),
                PrepareLoadCall = (request, _, _) => request.Purpose is ContentLoadPurpose.Restore
                    ? new ContentResult<IContentCandidate>.Success(new ScriptedContentCandidate(Content()))
                    : throw new InvalidOperationException("UNCONFIGURED: purpose")
            };
            var host = new HostCoordinator(core, content, new ScriptedContentFiles(), new UnconfiguredAssetInspector(),
                new(new("builtin"), new("mods"), null, "combat-ui", []), logger, policy, sessionOptions: _options);
            if (host.BeginRestore("combat core", "combat content", _options).Problem is { } problem)
                throw new InvalidOperationException(problem.Code);
            return host;
        }

        internal FixedCoreReadView View(SimulationPosition position, bool combatPresent = true, bool settlementPresent = true) =>
            rich ? Battlefield(position, combatPresent, settlementPresent) : new(position, HostCoreScenario.Input, null, new(1))
            {
                Economy = UiWorldFixture.Economy,
                World = new(0, new("fixture:railway"), 100, 100, [], [], [], [], [])
            };

        private static FixedContentView Content()
        {
            var scene = new DesignSceneProjection(null, null, null, true, null, null, [], [], [], [], []);
            var layout = new SettlementLayoutProjection(new(4), new("preview:town"),
                new(new("ground"), scene, new("Ground", "")), [], new("Town", ""));
            return new(HostContentScenario.CreateProjection(HostCoreScenario.Input), new(1))
            {
                Designs = new Dictionary<DesignHash, ContentLookup<DesignEvaluation>>
                { [new("station")] = new ContentLookup<DesignEvaluation>.NotFound() },
                SettlementLayouts = new Dictionary<SettlementId, ContentLookup<SettlementLayoutProjection>>
                {
                    [new(4)] = new ContentLookup<SettlementLayoutProjection>.Found(layout)
                }
            };
        }

        private static FixedCoreReadView Battlefield(SimulationPosition position, bool combatPresent, bool settlementPresent)
        {
            var terrain = new TerrainView(new("fixture:plain"), 0, []);
            NodeOccupant occupant = settlementPresent
                ? new NodeOccupant.Settlement(new(4), SettlementRelation.Hostile) : new NodeOccupant.Empty();
            var settlementNode = new NodeMapItem(new(1), new(250, 300), terrain, occupant, [], 0, 2, null);
            var far = settlementNode with { Id = new(2), Position = new(750, 300), Occupant = new NodeOccupant.Empty(), Platforms = 0 };
            var edge = new EdgeMapItem(new(1), new(1), new(2), 600, terrain, 1, 1, [], []);
            var trainPosition = new TrainPosition.Node(new(1), null, null, null, null, NodeTravelPhase.Waiting, 80, null);
            var train = new TrainView(new(7), trainPosition, TrainActivity.Stopped, [new(11)], new(1),
                0, 20, 10, 100, 0, 30, null, null, 0,
                new(TargetPriority.ArmedFirst, TargetScope.All, EncounterAction.Engage, 0.5),
                [new("no_route", "route", new WorldObjectId.Train(new(7)), [])], null, [],
                TrainOrderFacing.FirstEndLeading, null);
            var nodeLocation = new WorldLocation.Node(new(1));
            var vehicle = new InstanceView(new InstanceId.Vehicle(new(11)), new("wagon"), new("fixture"),
                nodeLocation, SettlementRelation.PlayerOwned, new InstanceDetails.Vehicle(SizeClass.Medium, new(7)),
                InstanceLock.None, new(100, 100), [], []);
            var building = new InstanceView(new InstanceId.Building(new(13)), new("station"), new("fixture"),
                nodeLocation, SettlementRelation.Hostile, new InstanceDetails.Building(new(1), PlotSize.SmallStation),
                InstanceLock.None, new(100, 100), [], []);
            var edgeLocation = new WorldLocation.Edge(new(1), 100);
            var combat = new CombatView(new(5), edgeLocation, 3,
            [
                new(new CombatUnitId.Instance(vehicle.Id), false, 0, null),
                new(new CombatUnitId.Instance(building.Id), false, 0, null)
            ]);
            var listed = combatPresent
                ? ImmutableArray.Create(new CombatMapItem(combat.Id, edgeLocation))
                : ImmutableArray.Create(new CombatMapItem(new(9), nodeLocation));
            var settlement = new SettlementView(new(4), new(1), new("preview:town"), new("preview:town"),
                SettlementRelation.Hostile, new(2),
                [new(new(1), 1, PlotSize.SmallStation, [BuildingKind.Station], new PlotOccupant.Building(new(13)))],
                [], 0, 2, 0, [], [], [], new(0, 0, 0, 0),
                new(new(new("preview:population"), null, []), 0, 0, 0, 0, new PopulationExecution.NotStarted([]), null, []),
                new(TargetPriority.LowestDurability, TargetScope.ArmedOnly), 0.4, true, [], [], [],
                new(new(new("preview:market"), null, []), new(0, 0, 0, 0, 0, 0, 0), []));
            return new(position, HostCoreScenario.Input, null, new(1))
            {
                Economy = UiWorldFixture.Economy,
                World = new(1, new("fixture:railway"), 1000, 600, [settlementNode, far], [edge],
                    [new(train.Id, trainPosition, train.Activity)], [], listed),
                Nodes = new Dictionary<NodeId, Lookup<NodeView>>
                {
                    [settlementNode.Id] = new Lookup<NodeView>.Found(new(settlementNode, [edge.Id], 80, 0, 80)),
                    [far.Id] = new Lookup<NodeView>.Found(new(far, [edge.Id], 80, 0, 80))
                },
                Edges = new Dictionary<EdgeId, Lookup<EdgeView>>
                {
                    [edge.Id] = new Lookup<EdgeView>.Found(new(edge, [], null))
                },
                Locations = new Dictionary<WorldLocation, Lookup<LocationView>>
                {
                    [nodeLocation] = new Lookup<LocationView>.Found(new(nodeLocation, [train.Id],
                        [vehicle.Id, building.Id], [], [], occupant)),
                    [new WorldLocation.Node(far.Id)] = new Lookup<LocationView>.Found(
                        new(new WorldLocation.Node(far.Id), [], [], [], [], far.Occupant))
                },
                Trains = new Dictionary<TrainId, Lookup<TrainView>> { [train.Id] = new Lookup<TrainView>.Found(train) },
                Instances = new Dictionary<InstanceId, Lookup<InstanceView>>
                {
                    [vehicle.Id] = new Lookup<InstanceView>.Found(vehicle),
                    [SecondVehicle] = new Lookup<InstanceView>.Found(vehicle with { Id = SecondVehicle }),
                    [building.Id] = new Lookup<InstanceView>.Found(building)
                },
                Inventories = new Dictionary<InventoryId, Lookup<InventoryView>>
                { [settlement.Inventory] = new Lookup<InventoryView>.Found(new(settlement.Inventory,
                    new InventoryOwner.Settlement(settlement.Id), nodeLocation, InventoryAccess.ReadWrite, 100, 0, 0, 100, 0, [], [])) },
                Settlements = new Dictionary<SettlementId, Lookup<SettlementView>>
                {
                    [settlement.Id] = settlementPresent
                        ? new Lookup<SettlementView>.Found(settlement) : new Lookup<SettlementView>.Missing()
                },
                Combats = new Dictionary<CombatId, Lookup<CombatView>>
                {
                    [combat.Id] = combatPresent
                        ? new Lookup<CombatView>.Found(combat) : new Lookup<CombatView>.Missing(),
                    [new(9)] = new Lookup<CombatView>.Found(new(new(9), nodeLocation, 4, []))
                }
            };
        }
    }

    private sealed class GateSession(ScriptedCoreSession inner) : ICoreSession
    {
        internal ScriptedCoreSession Inner { get; } = inner;
        internal bool ThrowUnimplemented { get; set; }
        public ICoreReadView Read() => Inner.Read();
        public AdvanceResult Advance(uint steps) => Inner.Advance(steps);
        public SubmissionResult Submit(CoreCommand command, CommandSource source)
        {
            if (ThrowUnimplemented && command is CoreCommand.DeclareWar or CoreCommand.OccupySettlement)
                throw new NotImplementedException();
            return Inner.Submit(command, source);
        }
        public CoreResult<ICoreCapture> Capture(CapturePurpose purpose) => Inner.Capture(purpose);
        public CoreRecordingWindow ReadRecording() => Inner.ReadRecording();
        public CoreResult<RecordingStart> CaptureRecordingStart() => Inner.CaptureRecordingStart();
        public RecordingMaintenance ConfirmRecordingStart(RecordingPointId point, RecordingWriteOutcome outcome)
            => Inner.ConfirmRecordingStart(point, outcome);
        public void Dispose() => Inner.Dispose();
    }
}
