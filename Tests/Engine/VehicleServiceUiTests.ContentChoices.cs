using Chickensoft.GoDotTest;
using Godot;
using Train.Content.Contracts;
using Train.Core.Contracts;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class VehicleServiceUiTests
{
    [Test]
    public async Task Blueprint_without_core_design_is_disabled_with_content_diagnostic()
    {
        var blueprint = LiveBlueprint(false, "unusable-vehicle");
        var diagnostic = new ContentDiagnostic("design_invalid", ContentDiagnosticSeverity.Error, "evaluate",
            null, null, "layout", null, "布局缺少驾驶室。");
        blueprint = blueprint with { Evaluation = ((DesignEvaluation.Complete)blueprint.Evaluation) with
            { Core = null, IsValid = false, Diagnostics = [diagnostic] } };
        var view = new ServiceView();
        var setup = new HostSimulationTests.HostSetup(view, EconomyFixture.Content(vehicleBlueprint: blueprint));
        await Run(setup, async (ui, panel) =>
        {
            var work = await OpenManufacture(ui, panel);
            var choice = Find<Button>(work, "Blueprint_0");
            Require(choice.Disabled && choice.Text.Contains("布局缺少驾驶室。"),
                "没有施工设计的蓝图应置灰，并显示内容评估的中文原因。");
            Require(view.QuoteCount == 0 && setup.Session.SubmitCount == 0,
                "不可用蓝图不得获取施工报价或提交。");
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Preparing_usable_blueprint_displays_content_failure_diagnostic()
    {
        var blueprint = LiveBlueprint(false, "prepare-failure-vehicle");
        var view = new ServiceView(expectedDesign: ((DesignEvaluation.Complete)blueprint.Evaluation).Core);
        var setup = new HostSimulationTests.HostSetup(view, EconomyFixture.Content(vehicleBlueprint: blueprint));
        var preparations = 0;
        await Run(setup, async (ui, panel) =>
        {
            setup.ContentFactory!.PrepareEditCall = (_, edit) =>
            {
                Require(edit is ContentEdit.ConstructionTarget, "应准备选中蓝图的施工目标。");
                preparations++;
                return new ContentResult<IContentCandidate>.Failure([
                    new("design_invalid", ContentDiagnosticSeverity.Error, "prepare", null, null,
                        "layout", null, "目标设计缺少承重部件。")]);
            };
            var work = await OpenManufacture(ui, panel);
            await _input.Activate(Find<Button>(work, "Blueprint_0"));
            Require(preparations == 1 && Find<Label>(work, "WorkReason").Text.Contains("目标设计缺少承重部件。") &&
                Find<Button>(work, "ConfirmWork").Disabled,
                "目标准备失败应保留具体内容诊断，并禁止确认。");
            Require(view.QuoteCount == 0 && setup.Session.SubmitCount == 0, "准备失败仍获取报价或提交。");
        });
        setup.Session.AssertComplete();
    }
}
