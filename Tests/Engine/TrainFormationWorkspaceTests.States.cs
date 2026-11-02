using Chickensoft.GoDotTest;
using Chickensoft.GodotTestDriver;
using Godot;
using Train.App;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class TrainFormationWorkspaceTests
{
    [Test]
    public async Task Core_control_and_traction_problems_block_both_actions()
    {
        foreach (var (code, word) in new[] { ("control_missing", "控制"), ("traction_missing", "牵引") })
        {
            var view = World();
            view.FormationPreviews = [(null, [], new CoreResult<TrainFormationPreview>.Failure([new("composition_empty", "OrderedVehicles", null, [])])),
                (null, [new(1)], new CoreResult<TrainFormationPreview>.Success(new(12, 34, 56, 78, [], new(code, "Train.Vehicles", null, []))))];
            await WithWorkspace([], async (panel, binding, current, content) =>
            {
                Open(panel, binding, current, content);
                await new UiTestInput(TestScene).Activate(Find<CheckBox>(panel, "Vehicle1"));
                Require(Find<Button>(panel, "Form").Disabled && Find<Button>(panel, "FormOnly").Disabled &&
                    Find<Label>(panel, "Reason").Text.Contains(word), "核心发车问题未禁用编组或未说明原因。");
                await UiCapture.Capture(TestScene, code);
            }, view);
        }
    }

    [Test]
    public async Task Overload_warns_but_form_only_keeps_the_plan_closed()
    {
        var view = World();
        view.FormationPreviews = [(null, [], new CoreResult<TrainFormationPreview>.Failure([new("composition_empty", "OrderedVehicles", null, [])])),
            (null, [new(1)], new CoreResult<TrainFormationPreview>.Success(new(12, 34, 56, 78, [], new("overloaded", "Train.Acceleration", null, []))))];
        var submitted = new List<CoreCommand>();
        await WithWorkspace(submitted, async (panel, binding, current, content) =>
        {
            Open(panel, binding, current, content);
            var input = new UiTestInput(TestScene);
            await input.Activate(Find<CheckBox>(panel, "Vehicle1"));
            var text = Find<Label>(panel, "Statistics").Text;
            Require(text.Contains("12 米") && text.Contains("34 吨") && text.Contains("56 千牛") && text.Contains("78 件") &&
                Find<Label>(panel, "Reason").Text.Contains("过重") && !Find<Button>(panel, "Form").Disabled,
                "核心统计未显示，或超载错误地禁止编组。");
            var editPlan = true; panel.FormationSaved += (_, edit) => editPlan = edit;
            await UiCapture.Capture(TestScene, "formation-overloaded");
            await input.Activate(Find<Button>(panel, "FormOnly"));
            Require(submitted.Count == 1 && !editPlan && !panel.Visible, "只编组请求了编辑计划或未关闭。");
        }, view);
    }

    [Test]
    public async Task Rejection_keeps_the_draft_and_hides_internal_problem_identity()
    {
        var submitted = new List<CoreCommand>(); var attempts = 0;
        await WithWorkspace(submitted, async (panel, binding, view, content) =>
        {
            Open(panel, binding, view, content);
            var input = new UiTestInput(TestScene);
            await input.Activate(Find<CheckBox>(panel, "Vehicle1"));
            await input.Activate(Find<Button>(panel, "FormOnly"));
            var reason = Find<Label>(panel, "Reason").Text;
            Require(panel.Visible && Find<CheckBox>(panel, "Vehicle1").ButtonPressed &&
                Find<VBoxContainer>(panel, "OrderedVehicles").GetChildCount() == 1 && reason.Contains("地点") &&
                !reason.Contains("987654") && !reason.Contains("OrderedVehicles") && !reason.Contains("vehicles_not_colocated"),
                "拒绝丢失草稿或泄漏内部诊断。");
            await input.Activate(Find<Button>(panel, "FormOnly"));
            Require(submitted.Count == 2 && !panel.Visible, "拒绝后未能重试原草稿。");
        }, decide: _ => new SubmissionResult.Completed(new(1, default, default, attempts++ == 0
            ? new CommandDecision.Rejected([new("vehicles_not_colocated", "OrderedVehicles", new WorldObjectId.Train(new(987654)), [])])
            : new CommandDecision.Accepted([new WorldObjectId.Train(new(8))], [])), [], null));
    }

    [Test]
    public async Task Hidden_refresh_and_busy_leave_lock_preserve_the_draft_but_stale_binding_blocks_it()
    {
        await WithWorkspace([], async (panel, binding, view, content) =>
        {
            Open(panel, binding, view, content);
            await new UiTestInput(TestScene).Activate(Find<CheckBox>(panel, "Vehicle1"));
            panel.GetParent<Control>().Hide(); panel.Refresh(binding, view, content);
            panel.GetParent<Control>().Show(); panel.Refresh(binding, view, content);
            Require(Find<CheckBox>(panel, "Vehicle1").ButtonPressed, "隐藏刷新丢失了草稿。");
            var host = Find<GameUi>(TestScene.GetTree().Root, "GameUi").GetParent<Train.App.App>().Host!;
            Require(host.LockLeaving() is null, "未取得离开保护锁。");
            try
            {
                panel.Refresh(binding, view, content);
                Require(Find<Button>(panel, "Form").Disabled && Find<Label>(panel, "Reason").Text.Contains("处理"),
                    "忙碌时仍能提交编组。");
            }
            finally { host.UnlockLeaving(); }
            panel.Refresh(default, view, content);
            Require(Find<Button>(panel, "Form").Disabled && Find<Button>(panel, "FormOnly").Disabled &&
                Find<Label>(panel, "Reason").Text.Contains("已不存在"), "失效对局仍能提交草稿。");
        });
    }

    [Test]
    public async Task Adjusting_submits_original_train_and_does_not_request_plan_editing()
    {
        var view = UiWorldFixture.Formation(0, true);
        view.FormationPreviews = [(new(7), [new(2), new(1)], new CoreResult<TrainFormationPreview>.Success(new(12, 34, 56, 78, [], null))),
            (new(7), [new(1), new(2)], new CoreResult<TrainFormationPreview>.Success(new(12, 34, 56, 78, [], null)))];
        var submitted = new List<CoreCommand>();
        await WithWorkspace(submitted, async (panel, binding, current, content) =>
        {
            var train = ((Lookup<TrainView>.Found)current.FindTrain(new(7))).Value;
            panel.Open(new(binding, content.Generation, new WorldObjectId.Train(train.Id)), train, current, content);
            var input = new UiTestInput(TestScene);
            await input.Activate(Find<Button>(Find<VBoxContainer>(panel, "OrderedVehicles").GetChild(0), "MoveDown"));
            var saved = default(TrainId); var editPlan = true;
            panel.FormationSaved += (id, edit) => { saved = id; editPlan = edit; };
            await UiCapture.Capture(TestScene, "formation-adjust");
            await input.Activate(Find<Button>(panel, "Form"));
            Require(submitted.Single() is CoreCommand.ReplaceTrainFormation replace && replace.Train == train.Id &&
                replace.OrderedVehicles.SequenceEqual(new VehicleId[] { new(1), new(2) }) && saved == train.Id && !editPlan && !panel.Visible,
                "保存编组更换了列车身份或打开了计划。");
        }, view, _ => new SubmissionResult.Completed(new(1, default, default, new CommandDecision.Accepted([], [])), [], null));
    }

    [Test]
    public async Task Main_action_mounts_the_new_trains_plan_in_the_existing_shell()
    {
        var fixture = new Fixture(TestScene.GetTree());
        var before = World(); var after = UiWorldFixture.Formation(1, true); var content = Content();
        var setup = new HostSimulationTests.HostSetup(before, content);
        setup.Session.QueueSubmit(new CoreCommand.FormTrain([new(2), new(1)]), CommandSource.UserInterface,
            Completed(1, before, after, new CommandDecision.Accepted([new WorldObjectId.Train(new(7))], [])), after);
        try
        {
            var app = fixture.LoadScene<Train.App.App>("res://App/App.tscn"); app.CreateHost = logger => setup.Create(logger);
            await fixture.AddToRoot(app); await FramesUntil(TestScene, () => app.Host!.GetStatus() is not null);
            var ui = app.GetNode<GameUi>("GameUi"); var input = new UiTestInput(TestScene);
            await input.Activate(Find<Button>(ui, "Nodes"));
            await input.ClickItem(Find<ItemList>(Find<PanelContainer>(ui, "NodesList"), "Objects"), 0);
            var shell = (GameShell)ui.World;
            await input.Activate(Find<Button>(shell.InspectorContent!, "Form"));
            Require(shell.WorkspaceContent is TrainFormationWorkspace, "原入口没有打开编组工作区。");
            var panel = (TrainFormationWorkspace)shell.WorkspaceContent!;
            await input.Activate(Find<CheckBox>(panel, "Vehicle2")); await input.Activate(Find<CheckBox>(panel, "Vehicle1"));
            await input.Activate(Find<Button>(panel, "Form"));
            Require(shell.WorkspaceContent is TrainPlanWorkspace plan && plan.IsVisibleInTree() &&
                shell.SelectedObject == new WorldObjectId.Train(new(7)), "编组后未直接打开新列车的运行计划。");
            setup.Session.AssertComplete();
        }
        finally { await fixture.Cleanup(); }
    }

    [Test]
    public async Task Empty_candidates_disable_submission_and_cancel_sends_nothing()
    {
        var view = UiWorldFixture.Formation(0, true); view.FormationPreviews = TrainFormationTestScenario.Editing;
        var submitted = new List<CoreCommand>();
        await WithWorkspace(submitted, async (panel, binding, current, content) =>
        {
            Open(panel, binding, current, content);
            Require(Find<VBoxContainer>(panel, "AvailableVehicles").GetChildCount() == 0 &&
                Find<Button>(panel, "Form").Disabled && Find<Label>(panel, "Reason").Text.Contains("没有"), "空候选缺少禁用原因。");
            await new UiTestInput(TestScene).Activate(Find<Button>(panel, "Cancel"));
            Require(!panel.Visible && submitted.Count == 0, "取消提交了编组或没有关闭工作区。");
        }, view);
    }

    [Test]
    public async Task Adjusting_preloads_members_and_keeps_statistics_when_capacity_blocks_save()
    {
        var view = UiWorldFixture.Formation(0, true);
        view.FormationPreviews = [(new(7), [new(2), new(1)], new CoreResult<TrainFormationPreview>.Success(new(12, 34, 56, 78, [], null))),
            (new(7), [new(2)], new CoreResult<TrainFormationPreview>.Success(new(10, 34, 20, 1,
                [new("insufficient_capacity", "Train.Inventory.Capacity", new WorldObjectId.Train(new(7)), [])], null)))];
        await WithWorkspace([], async (panel, binding, current, content) =>
        {
            var train = ((Lookup<TrainView>.Found)current.FindTrain(new(7))).Value;
            panel.Open(new(binding, content.Generation, new WorldObjectId.Train(train.Id)), train, current, content);
            var ordered = Find<VBoxContainer>(panel, "OrderedVehicles");
            Require(ordered.GetChildCount() == 2 && Find<CheckBox>(panel, "Vehicle1").ButtonPressed &&
                Find<Button>(panel, "Form").Text == "保存编组" && !Find<Button>(panel, "FormOnly").Visible &&
                Find<Label>(panel, "Destination").Text.Contains("移出的车辆会停在"), "调整模式未预载车辆或缺少去向说明。");
            await new UiTestInput(TestScene).Activate(Find<Button>(ordered.GetChild(1), "Remove"));
            Require(Find<Button>(panel, "Form").Disabled && Find<Label>(panel, "Statistics").Text.Contains("1 件") &&
                Find<Label>(panel, "Reason").Text.Contains("容量不足"), "容量不足未禁用保存或清掉了统计。");
            await UiCapture.Capture(TestScene, "formation-capacity");
        }, view);
    }

    private static void Open(TrainFormationWorkspace panel, HostBinding binding,
        ICoreReadView view, IContentView content) => panel.Open(new(binding, content.Generation, new WorldObjectId.Node(new(1))), new NodeId(1), view, content);
}
