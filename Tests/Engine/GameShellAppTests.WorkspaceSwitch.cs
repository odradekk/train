using Chickensoft.GoDotTest;
using Godot;
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class GameShellAppTests
{
    [Test]
    public async Task Formation_edits_are_kept_until_navigation_switch_is_confirmed()
    {
        var view = EconomyFixture.Create(vehicle: EconomyFixture.VehicleState.Owned);
        view.FormationPreviews = TrainFormationTestScenario.Vehicle9;
        var setup = new HostSimulationTests.HostSetup(view, EconomyFixture.Content(mapPresentation: true));
        await Run(async (ui, shell) =>
        {
            await OpenFormation(ui, shell);
            var formation = shell.WorkspaceContent!;
            await _input.Activate(Find<CheckBox>(formation, "Vehicle9"));
            await _input.Activate(Find<Button>(ui, "EconomyReports"));
            var dialog = SwitchConfirmation(shell);
            Require(dialog.DialogText.Contains("编组") && shell.WorkspaceContent == formation && formation.IsVisibleInTree(),
                "询问必须说明将丢弃编组编辑，并暂时保留原工作区。");
            if (DisplayServer.GetName() != "headless" &&
                !string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable("TRAIN_UI_CAPTURE_DIR")))
            {
                var window = TestScene.GetTree().Root;
                var originalSize = window.Size;
                try
                {
                    foreach (var (size, scale) in new[] { (new Vector2I(1920, 1080), 1f), (new Vector2I(1280, 720), 2f) })
                    {
                        window.Size = size; window.ContentScaleFactor = scale;
                        await _input.Frames(6);
                        dialog.PopupCentered();
                        await UiCapture.Capture(TestScene, $"workspace-switch-{size.X}x{size.Y}-scale-{scale:0}", scale, size);
                    }
                }
                finally { window.Size = originalSize; window.ContentScaleFactor = 1; dialog.PopupCentered(); }
            }
            await _input.Activate(dialog.GetCancelButton(), dialog);
            Require(shell.WorkspaceContent == formation && Find<CheckBox>(formation, "Vehicle9").ButtonPressed &&
                Find<VBoxContainer>(formation, "OrderedVehicles").GetChildCount() == 1,
                "取消切换必须保留已选车辆和编组顺序。");
            await _input.Activate(Find<Button>(ui, "EconomyReports"));
            await _input.Activate(SwitchConfirmation(shell).GetOkButton(), dialog);
            Require(shell.WorkspaceContent?.Name == "EconomyReports" && !formation.Visible && setup.Session.SubmitCount == 0,
                "只有确认后才能换到报告，切换不能提交编组命令。");
        }, setup);
    }

    [Test]
    public async Task Unedited_workspaces_switch_without_confirmation()
    {
        var view = EconomyFixture.Create(vehicle: EconomyFixture.VehicleState.Owned);
        view.FormationPreviews = TrainFormationTestScenario.Vehicle9;
        await Run(async (ui, shell) =>
        {
            await OpenFormation(ui, shell);
            await _input.Activate(Find<Button>(ui, "EconomyReports"));
            Require(shell.WorkspaceContent?.Name == "EconomyReports", "没有编辑的编组应直接切换工作区。");
            await OpenPlan(ui, shell);
            await _input.Enter(Find<LineEdit>(shell.WorkspaceContent!, "Search"), "青川");
            await _input.Activate(Find<Button>(ui, "BlueprintLibrary"));
            Require(shell.WorkspaceContent is BlueprintLibraryPanel && !Find<ConfirmationDialog>(shell, "WorkspaceSwitchConfirmation").Visible,
                "没有编辑的运行计划应直接切换，搜索地名不算编辑。");
        }, new(view, EconomyFixture.Content(mapPresentation: true)));
    }

    [Test]
    public async Task Goods_rule_edits_prompt_but_restoring_the_input_allows_switching()
    {
        var plan = new RunPlan(false, [new PlanStop.Station(new(1), 0,
            [new(new AutomaticGoodsAction.Buy(null), EconomyFixture.Food, new(10), 4)])]);
        await Run(async (ui, shell) =>
        {
            await OpenPlan(ui, shell);
            var workspace = shell.WorkspaceContent!;
            await _input.Enter(Find<LineEdit>(workspace, "TargetQuantity"), "6");
            await _input.Activate(Find<Button>(ui, "BlueprintLibrary"));
            var dialog = SwitchConfirmation(shell);
            await _input.Activate(dialog.GetCancelButton(), dialog);
            Require(shell.WorkspaceContent == workspace && Find<LineEdit>(workspace, "TargetQuantity").Text == "6",
                "只改交易规则也必须先询问，取消后应保留规则。");
            await _input.Enter(Find<LineEdit>(workspace, "TargetQuantity"), "4");
            await _input.Activate(Find<Button>(ui, "BlueprintLibrary"));
            Require(shell.WorkspaceContent is BlueprintLibraryPanel && !dialog.Visible,
                "交易规则改回原样后，数组实例虽已变化，仍应按输入内容判断为没有编辑。");
        }, new(EconomyFixture.Create(trainPlan: plan), EconomyFixture.Content(mapPresentation: true)));
    }

    [Test]
    public async Task Stop_count_and_loop_edits_each_require_confirmation()
    {
        await Run(async (ui, shell) =>
        {
            foreach (var edit in new[] { "Remove", "Loop" })
            {
                await OpenPlan(ui, shell);
                var workspace = shell.WorkspaceContent!;
                await _input.Activate(Find<Button>(workspace, edit));
                await _input.Activate(Find<Button>(ui, "EconomyReports"));
                var dialog = SwitchConfirmation(shell);
                await _input.Activate(dialog.GetCancelButton(), dialog);
                Require(shell.WorkspaceContent == workspace && (edit == "Remove"
                    ? Find<VBoxContainer>(workspace, "Stops").GetChildCount() == 0 : Find<CheckBox>(workspace, "Loop").ButtonPressed),
                    "取消切换必须保留站点增删或循环设置的编辑。");
                await _input.Activate(Find<Button>(workspace, "Close"));
            }
        });
    }

    [Test]
    public async Task Workspace_cancel_close_and_escape_discard_edits_without_prompting()
    {
        var view = EconomyFixture.Create(vehicle: EconomyFixture.VehicleState.Owned);
        view.FormationPreviews = TrainFormationTestScenario.Vehicle9;
        var setup = new HostSimulationTests.HostSetup(view, EconomyFixture.Content(mapPresentation: true));
        await Run(async (ui, shell) =>
        {
            foreach (var formation in new[] { true, false })
            foreach (var action in new[] { "Cancel", "Close", "Escape" })
            {
                if (formation)
                {
                    await OpenFormation(ui, shell);
                    await _input.Activate(Find<CheckBox>(shell.WorkspaceContent!, "Vehicle9"));
                }
                else
                {
                    await OpenPlan(ui, shell);
                    await _input.Enter(Find<LineEdit>(shell.WorkspaceContent!, "DwellSeconds"), "42");
                }
                if (action == "Escape") await _input.PressKey(Key.Escape);
                else await _input.Activate(Find<Button>(shell.WorkspaceContent!, action));
                Require(shell.WorkspaceContent is null && !Find<ConfirmationDialog>(shell, "WorkspaceSwitchConfirmation").Visible,
                    $"工作区自身 {action} 必须直接关闭，即使已有编辑：formation={formation}。");
            }
            Require(setup.Session.SubmitCount == 0, "关闭工作区不能保存草稿。");
        }, setup);
    }

    [Test]
    public async Task Pending_restore_clears_the_old_workspaces_switch_confirmation()
    {
        var view = EconomyFixture.Create();
        var setup = new HostSimulationTests.HostSetup(view, EconomyFixture.Content(mapPresentation: true));
        setup.ReplacementCandidate = new(new ScriptedCoreSession(view), HostCoreScenario.References);
        await Run(async (ui, shell) =>
        {
            await OpenPlan(ui, shell);
            await _input.Enter(Find<LineEdit>(shell.WorkspaceContent!, "DwellSeconds"), "42");
            var host = ui.GetParent<Train.App.App>().Host!;
            var binding = host.GetStatus()!.Binding;
            var prepare = setup.ContentFactory!.PrepareLoadCall!;
            using var release = new ManualResetEventSlim();
            setup.ContentFactory.PrepareLoadCall = (request, files, assets) =>
            {
                release.Wait();
                return prepare(request, files, assets);
            };
            try
            {
                var restore = host.BeginRestore("opaque core", "opaque content", host.SessionOptions!);
                Require(restore.Problem is null, "必须先开始异步读档，旧对局仍可显示。");
                await _input.Activate(Find<Button>(ui, "EconomyReports"));
                var dialog = SwitchConfirmation(shell);
                Require(host.GetStatus()!.Binding == binding && host.IsBusy, "确认框必须在旧对局仍加载期间打开。");
                release.Set();
                await _input.WaitUntil(() => host.GetStatus()!.Binding != binding);
                Require(shell.WorkspaceContent is null && !dialog.Visible,
                    "异步读档完成后必须关闭旧工作区的确认框，不能再打开旧对局里的目标。");
            }
            finally { release.Set(); }
        }, setup);
    }

    [Test]
    public async Task Saved_plan_does_not_prompt_even_when_departure_fails()
    {
        var before = EconomyFixture.Create();
        var plan = new RunPlan(false, [new PlanStop.Station(new(1), 2520, [])]);
        var after = EconomyFixture.Create(revision: 2, trainPlan: plan);
        var setup = new HostSimulationTests.HostSetup(before, EconomyFixture.Content(mapPresentation: true));
        setup.Session.QueueSubmit(new CoreCommand.ReplaceRunPlan(new(7), plan), CommandSource.UserInterface,
            Completed(1, before, after, new CommandDecision.Accepted([], [])), after);
        setup.Session.QueueSubmit(new CoreCommand.StartTrain(new(7)), CommandSource.UserInterface,
            Completed(2, after, after, new CommandDecision.Rejected([new("invalid_state", "train", null, [])])), after);
        await Run(async (ui, shell) =>
        {
            await OpenPlan(ui, shell);
            var workspace = shell.WorkspaceContent!;
            await _input.Enter(Find<LineEdit>(workspace, "DwellSeconds"), "42");
            await _input.Activate(Find<Button>(workspace, "Apply"));
            Require(workspace.IsVisibleInTree() && Find<Label>(workspace, "Reason").Text.Contains("计划已保存，发车未成功"),
                "测试必须走到计划保存成功但发车失败，草稿仍打开的状态。");
            await _input.Activate(Find<Button>(ui, "EconomyReports"));
            Require(shell.WorkspaceContent?.Name == "EconomyReports" && !Find<ConfirmationDialog>(shell, "WorkspaceSwitchConfirmation").Visible,
                "保存成功后即使发车失败，也没有未保存编辑，不应询问。");
        }, setup);
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Plan_edits_survive_escape_before_reopening_for_another_train()
    {
        var plan = new RunPlan(false, [new PlanStop.Station(new(1), 0,
            [new(new AutomaticGoodsAction.Buy(null), EconomyFixture.Food, new(10), 4)])]);
        var setup = new HostSimulationTests.HostSetup(EconomyFixture.Create(vehicle: EconomyFixture.VehicleState.Formed,
            trainPlan: plan), EconomyFixture.Content(mapPresentation: true));
        await Run(async (ui, shell) =>
        {
            await OpenPlan(ui, shell);
            var workspace = shell.WorkspaceContent!;
            await _input.Enter(Find<LineEdit>(workspace, "DwellSeconds"), "42");
            await _input.Enter(Find<LineEdit>(workspace, "TargetQuantity"), "6");
            await OpenPlan(ui, shell, 1);
            var dialog = SwitchConfirmation(shell);
            Require(dialog.DialogText.Contains("运行计划"), "计划切换询问必须说明将丢弃运行计划的编辑。");
            await _input.PressKey(Key.Escape);
            Require(!dialog.Visible && shell.WorkspaceContent == workspace &&
                Find<LineEdit>(workspace, "DwellSeconds").Text == "42" && Find<LineEdit>(workspace, "TargetQuantity").Text == "6",
                $"确认框 Esc 必须等于取消，保留原计划的时间和交易规则：dialog={dialog.Visible}，workspace={shell.WorkspaceContent == workspace}，" +
                $"seconds={Find<LineEdit>(workspace, "DwellSeconds").Text}，quantity={Find<LineEdit>(workspace, "TargetQuantity").Text}。");
            await _input.Activate(Find<Button>(shell.InspectorContent!, "EditPlan"));
            dialog = SwitchConfirmation(shell);
            await _input.Activate(dialog.GetOkButton(), dialog);
            Require(shell.WorkspaceContent == workspace && Find<LineEdit>(workspace, "DwellSeconds").Text == "0" &&
                Find<LineEdit>(workspace, "TargetQuantity").Text == "4" && workspace.FindChildren("*", "Label", true, false)
                    .OfType<Label>().Any(label => label.Text == "运行计划 · 列车 8") && setup.Session.SubmitCount == 0,
                "确认后必须重新打开所选另一列车的计划，不能保留上一列车草稿或自动保存。");
        }, setup);
    }

    private async Task OpenFormation(GameUi ui, GameShell shell)
    {
        if (shell.ListContent?.Name != "NodesList") await _input.Activate(Find<Button>(ui, "Nodes"));
        var list = Find<Tree>(shell.ListContent!, "Objects");
        await _input.ClickItem(list, FirstSelectableItem(list));
        await _input.ChooseTab(Find<TabContainer>(shell.InspectorContent!, "Pages"), 2);
        await _input.Activate(Find<Button>(shell.InspectorContent!, "FormPurchased"));
        Require(shell.WorkspaceContent is TrainFormationWorkspace, "聚居地现车入口必须打开编组工作区。");
    }

    private async Task OpenPlan(GameUi ui, GameShell shell, int row = 0)
    {
        if (shell.ListContent?.Name != "FleetList") await _input.Activate(Find<Button>(ui, "Fleet"));
        await _input.ClickItem(Find<Tree>(shell.ListContent!, "Objects"), row);
        await _input.Activate(Find<Button>(shell.InspectorContent!, "EditPlan"));
    }

    private static ConfirmationDialog SwitchConfirmation(GameShell shell)
    {
        var dialog = shell.GetChildren().OfType<ConfirmationDialog>().SingleOrDefault(window => window.Visible);
        Require(dialog is not null, "工作区有未保存编辑时必须先询问，不能直接换工作区。");
        return dialog!;
    }
}
