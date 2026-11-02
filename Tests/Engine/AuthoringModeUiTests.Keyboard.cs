using Chickensoft.GoDotTest;
using Godot;
using Train.App.Authoring;
using Train.Content.Contracts;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class AuthoringModeUiTests
{
    [Test]
    [Chickensoft.GoDotTest.Timeout(120000)]
    public async Task Unsaved_form_can_be_cancelled_discarded_or_saved_using_only_keyboard()
    {
        using var fixture = new RealAuthoringFixture(TestScene);
        await fixture.Start(); await fixture.Open();
        fixture.Service.OpenDefinition(new(DefinitionKind.Voxel, "engine-food"), grid: false);
        var panel = new AuthoringPanel { Name = "KeyboardAuthoring" };
        panel.Initialize(fixture.Service, fixture.Editor); TestScene.AddChild(panel); panel.Open();
        try
        {
            await fixture.Pump(() => !fixture.Service.IsBusy);
            var definition = Path.Combine(fixture.ModDirectory, "resources", "voxels", "engine-food.voxel.json");
            var original = File.ReadAllBytes(definition);
            var form = Find<AuthoringDefinitionForm>(panel, "DefinitionForm");
            LineEdit NameField() => form.FindChildren("*", "LineEdit", true, false).OfType<LineEdit>()
                .Single(field => field.TooltipText == "名称");
            var exit = Find<Button>(panel, "ExitAuthoring");
            var leave = Find<Control>(panel, "LeaveAuthoring");
            await input.Enter(NameField(), "Discarded by keyboard");
            await TabTo(exit, panel); await input.PressKey(Key.Space);
            Require(leave.IsVisibleInTree() && fixture.Service.Leaving && Find<Button>(leave, "CancelLeave").HasFocus(),
                "未保存表单退出时必须询问，并默认聚焦取消。");
            await AssertTabStaysIn(leave);
            await TabTo(Find<Button>(leave, "CancelLeave"), leave); await input.PressKey(Key.Space);
            Require(!leave.IsVisibleInTree() && panel.IsVisibleInTree() && fixture.Service.DirtyDefinition is not null
                && NameField().Text == "Discarded by keyboard" && exit.HasFocus(),
                "纯键盘取消离开必须保留未保存表单，并恢复退出按钮焦点。");

            await input.PressKey(Key.Space);
            Require(leave.IsVisibleInTree() && Find<Button>(leave, "CancelLeave").HasFocus(), "再次离开仍必须默认聚焦取消。");
            await TabTo(Find<Button>(leave, "DiscardAndLeave"), leave); await input.PressKey(Key.Space);
            Require(!panel.IsVisibleInTree() && !fixture.Service.Leaving && fixture.Service.DirtyDefinition is null
                && fixture.Service.Drafts().IsEmpty && original.AsSpan().SequenceEqual(File.ReadAllBytes(definition)),
                "纯键盘放弃必须退出，不能留下草稿或修改源文件。");

            panel.Open(); await fixture.Pump(() => !fixture.Service.IsBusy);
            fixture.Service.OpenDefinition(new(DefinitionKind.Voxel, "engine-food"), grid: false);
            await input.Enter(NameField(), "Saved by keyboard");
            await TabTo(exit, panel); await input.PressKey(Key.Space);
            Require(leave.IsVisibleInTree() && Find<Button>(leave, "CancelLeave").HasFocus(), "保存离开前必须出现可键盘操作的询问。");
            await TabTo(Find<Button>(leave, "SaveAndLeave"), leave); await input.PressKey(Key.Space);
            await fixture.Pump(() => !fixture.Service.IsBusy && !panel.IsVisibleInTree());
            Require(!fixture.Service.Leaving && fixture.Service.Drafts().Single().Payload
                is DraftPayload.Definition { Value: DefinitionDraft.Voxel { Value.Display.Name: "Saved by keyboard" } }
                && original.AsSpan().SequenceEqual(File.ReadAllBytes(definition)),
                "纯键盘保存离开必须持久保存表单草稿，源文件保持原样。");
        }
        finally { panel.QueueFree(); await input.Frames(3); }
    }

    [Test]
    public async Task Locating_a_definition_diagnostic_opens_its_form_and_closes_the_problem_dialog()
    {
        using var fixture = new RealAuthoringFixture(TestScene);
        await fixture.Start(); await fixture.Open();
        fixture.Service.NewDefinition(new DefinitionDraft.LairType(new("new-lair", new("生物聚落", ""), null,
            null, [], null, null, null, null, null, null, null)));
        var panel = new AuthoringPanel(); panel.Initialize(fixture.Service, fixture.Editor); TestScene.AddChild(panel); panel.Open();
        try
        {
            await fixture.Pump(() => !fixture.Service.IsBusy);
            fixture.Service.SaveForm(Find<AuthoringDefinitionForm>(panel, "DefinitionForm").Capture());
            await fixture.Pump(() => !fixture.Service.IsBusy);
            fixture.Service.Publish(); await fixture.Pump(() => !fixture.Service.IsBusy, ContentPreparationTimeout);
            Require(fixture.Service.PublishIssues.Any(issue => issue.Definition == new AuthoringDefinitionAddress(DefinitionKind.LairType, "new-lair")
                && issue.Diagnostic.Field == "/generation_weight"), "真实写入校验必须产生可定位到新生物聚落类型的字段诊断。");
            fixture.Service.OpenDefinition(new(DefinitionKind.Voxel, "engine-food"), grid: false);
            Require(fixture.Service.Form is DefinitionDraft.Voxel, "定位前必须先切换到另一张表单。");
            await input.Activate(Find<Button>(panel, "Problems"));
            var problems = Find<Control>(panel, "SourceProblems");
            await input.Activate(Find<Button>(problems, "LocateDiagnostic"));
            Require(!problems.IsVisibleInTree() && fixture.Service.Form is DefinitionDraft.LairType { Value.LocalId: "new-lair" }
                && Find<AuthoringDefinitionForm>(panel, "DefinitionForm").IsVisibleInTree(),
                "定位可编辑定义必须关闭问题框，切换到该定义的可见表单。");
        }
        finally { panel.QueueFree(); await input.Frames(3); }
    }

    private async Task TabTo(Control target, Control boundary)
    {
        var limit = boundary.FindChildren("*", "Control", true, false).Count + 1;
        for (var move = 0; move < limit; move++)
        {
            if (target.HasFocus()) return;
            await input.PressKey(Key.Tab);
            var focused = boundary.GetViewport().GuiGetFocusOwner();
            Require(focused is not null && (focused == boundary || boundary.IsAncestorOf(focused)),
                $"键盘焦点不能离开当前界面：{boundary.Name}。");
        }
        Require(false, $"Tab 无法到达控件：{target.Name}。");
    }

    private async Task AssertTabStaysIn(Control dialog)
    {
        var count = dialog.FindChildren("*", "BaseButton", true, false).Count + 1;
        for (var move = 0; move < count; move++)
        {
            await input.PressKey(Key.Tab);
            Require(dialog.IsAncestorOf(dialog.GetViewport().GuiGetFocusOwner()), "Tab 必须留在当前询问框内。");
        }
    }
}
