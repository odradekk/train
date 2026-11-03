using System.Collections.Immutable;
using Chickensoft.GoDotTest;
using Chickensoft.GodotTestDriver;
using Godot;
using Train.App;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class VehicleServiceUiTests
{
    [Test]
    public async Task Leaving_the_refit_editor_keeps_workspace_target_and_sources_and_requotes()
    {
        var view = new ServiceView();
        var setup = new HostSimulationTests.HostSetup(view, EconomyFixture.Content());
        await Run(setup, async (ui, _) =>
        {
            var draft = new DesignDraft(new("改装", ""), new DesignSpecification.Vehicle(SizeClass.Medium), new([], [], []));
            setup.ContentFactory!.EvaluateDesignCall = (_, design, _) => new ContentResult<DesignEvaluation>.Success(
                new DesignEvaluation.Complete(new("design"), new("composition"), design, null,
                    new(null, null, null, true, null, null, [], [], [], [], []), true, []));
            var work = await OpenRepair(ui, new InstanceId.Vehicle(new(9)));
            await _input.Activate(Find<CheckBox>(work, "Source_12"));
            var quotes = view.QuoteCount;
            var editor = ui.GetParent<Train.App.App>().Editor!;
            await OpenAndLeave(ui, editor, draft, "ExitEditor");
            Require(work.IsVisibleInTree() && Find<CheckBox>(work, "Source_11").ButtonPressed &&
                !Find<CheckBox>(work, "Source_12").ButtonPressed, "退出编辑器丢失工作区或来源输入。");
            await OpenAndLeave(ui, editor, draft, "ReturnToVehicleServices");
            Require(work.IsVisibleInTree() && view.QuoteCount > quotes &&
                view.LastQuote!.Request is JobRequest.Repair repair && repair.Instance == new InstanceId.Vehicle(new(9)) &&
                repair.Sources.SequenceEqual(new InventoryId[] { new(11) }), "返回入口没有保留实例、来源或自动重新评估。");
            Require(setup.Session.SubmitCount == 0, "编辑器返回创建了施工任务。");
        });
        setup.Session.AssertComplete();
    }

    private async Task OpenAndLeave(GameUi ui, Train.App.Editing.EditorService editor, DesignDraft draft, string button)
    {
        editor.Open(draft, new DraftTarget.Instance(new InstanceId.Vehicle(new(9))));
        await _input.WaitUntil(() => editor.Session is not null || !editor.IsOpen && editor.Message is not null);
        Require(editor.Session is not null, "编辑器没有打开：" + editor.Message);
        await _input.Frames(2);
        Require(!Find<Control>(ui, "ConstructionWorkspace").IsVisibleInTree(), "编辑器打开时施工工作区仍可见。");
        await _input.Activate(Find<Button>(ui, button));
        await _input.Frames(2);
        var leave = ui.FindChildren("LeaveEditor", "PanelContainer", true, false).Cast<CanvasItem>().Single();
        if (leave.Visible) await _input.Activate(Find<Button>(ui, "DiscardAndLeave"));
        await _input.WaitUntil(() => !editor.IsOpen && Find<Control>(ui, "ConstructionWorkspace").IsVisibleInTree());
        await _input.Frames(2);
    }

    private static void HideCaptureChrome(GameUi ui)
    {
        ui.GetNode<CanvasItem>("Root/ErrorPanel").Hide();
        foreach (var detail in ui.FindChildren("Details", "PanelContainer", true, false))
            ((CanvasItem)detail).Hide();
    }

}
