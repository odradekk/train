using Chickensoft.GoDotTest;
using Godot;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public class AuthoringFeedbackRecoveryTests(Node testScene) : TestClass(testScene)
{
    [Test]
    [Chickensoft.GoDotTest.Timeout(120000)]
    public async Task Missing_reference_feedback_recovers_after_panel_edits_and_saving_a_draft()
    {
        using var fixture = new RealAuthoringFixture(TestScene);
        await fixture.Start(); await fixture.Open();
        var missingScene = fixture.PackageId.Value + ":removed-scene";
        fixture.Service.NewDefinition(new DefinitionDraft.SettlementTemplate(new("old-template", new("旧模板", "缺失引用的表单回归"),
            new("core:agricultural-town"), 1, new(missingScene),
            [new(1, PlotSize.Small, new(0, 0, 0), VoxelOrientation.PxPy, null)], [], [], [])));
        var panel = new AuthoringPanel(); panel.Initialize(fixture.Service, fixture.Editor); TestScene.AddChild(panel); panel.Open();
        var input = new UiTestInput(TestScene);
        try
        {
            await fixture.Pump(() => !fixture.Service.FormDiagnostics.IsEmpty);
            Require(fixture.Service.FormDiagnostics.Any(d => d.Field == "/scene_model"),
                "初始夹具必须报告缺失场景引用：" + string.Join("；", fixture.Service.FormDiagnostics.Select(d => d.Field + "：" + d.Message)));
            var form = Find<AuthoringDefinitionForm>(panel, "DefinitionForm");
            var name = form.FindChildren("*", "LineEdit", true, false).OfType<LineEdit>().Single(field => field.TooltipText == "名称");
            var type = form.FindChildren("*", "OptionButton", true, false).OfType<OptionButton>().Single(field => field.TooltipText == "聚居地类型 ID（可空）");
            var availableType = type.GetItemText(type.Selected);
            Require(!availableType.StartsWith("不可用", StringComparison.Ordinal), "初始真实候选必须包含模板已引用的聚居地类型。");

            await input.Enter(name, "first edit");
            Require(type.GetItemText(type.Selected) == availableType, "重新检查期间必须保留已有引用候选，不能把有效引用显示成不可用。");
            await fixture.Pump(() => fixture.Service.FormDiagnostics.Any(d => d.Field == "/scene_model"), TimeSpan.FromSeconds(5));
            Require(FieldReportsMissingScene(), "修改含旧缺失引用的模板后，真实内容诊断必须重新显示在原字段。");

            await input.Activate(Find<Button>(panel, "SaveDraft"));
            await fixture.Pump(() => !fixture.Service.IsBusy);
            Require(type.GetItemText(type.Selected) == availableType, "保存触发的新检查必须继续保留有效引用候选。");
            Require(SavedTemplate() is { Display.Name: "first edit" } first && first.SceneModel?.Value == missingScene,
                "真实面板保存必须保留最新输入和旧缺失引用。");

            await input.Enter(name, "second edit");
            await fixture.Pump(() => fixture.Service.FormDiagnostics.Any(d => d.Field == "/scene_model"), TimeSpan.FromSeconds(5));
            Require(FieldReportsMissingScene(), "保存后继续编辑必须重新获得字段诊断，不能永久停止反馈。");
            await input.Activate(Find<Button>(panel, "SaveDraft"));
            await fixture.Pump(() => !fixture.Service.IsBusy);
            Require(SavedTemplate() is { Display.Name: "second edit" } second && second.SceneModel?.Value == missingScene,
                "再次保存必须覆盖为最新输入，并继续保留旧缺失引用。");

            bool FieldReportsMissingScene() => fixture.Service.FormDiagnostics.Where(d => d.Field == "/scene_model").Any(d =>
                form.FindChildren("*", "Label", true, false).OfType<Label>().Any(label => label.IsVisibleInTree() && label.Text.Contains(d.Message, StringComparison.Ordinal)));
            SettlementTemplateDraft? SavedTemplate() => fixture.Service.Drafts().SingleOrDefault()?.Payload is
                DraftPayload.Definition { Value: DefinitionDraft.SettlementTemplate saved } ? saved.Value : null;
        }
        finally { panel.QueueFree(); await input.Frames(3); }
    }
}
