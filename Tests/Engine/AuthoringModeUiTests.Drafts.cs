using Chickensoft.GoDotTest;
using Godot;
using Train.Content.Contracts;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class AuthoringModeUiTests
{
    [Test]
    public async Task Older_drafts_of_a_staged_or_deleted_definition_remain_available_to_open()
    {
        using var fixture = new RealAuthoringFixture(TestScene);
        await fixture.Start(); await fixture.Open();
        var address = new AuthoringDefinitionAddress(DefinitionKind.Voxel, "engine-food");
        fixture.Service.OpenDefinition(address, grid: false);
        var panel = new AuthoringPanel(); panel.Initialize(fixture.Service, fixture.Editor);
        TestScene.AddChild(panel); panel.Open();
        try
        {
            await fixture.Pump(() => !fixture.Service.IsBusy);
            var form = Find<AuthoringDefinitionForm>(panel, "DefinitionForm");
            LineEdit NameField() => form.FindChildren("*", "LineEdit", true, false).OfType<LineEdit>()
                .Single(field => field.TooltipText == "名称");
            async Task Save(string name)
            {
                await input.Enter(NameField(), name);
                await input.Activate(Find<Button>(panel, "SaveDraft"));
                await fixture.Pump(() => !fixture.Service.IsBusy);
            }
            var drafts = Find<Tree>(panel, "DefinitionTree").GetParent().GetChildren().OfType<ScrollContainer>().Single();
            Button[] OldDraftButtons() => drafts.FindChildren("*", "Button", true, false).OfType<Button>()
                .Where(button => button.Name == "OpenDraft" || button.Name == "OpenDraftOnCurrentSource").ToArray();

            await Save("First saved version");
            var first = fixture.Service.Drafts().Single();
            Require(OldDraftButtons().Length == 0, "本次暂存的草稿不能同时作为旧草稿显示。");
            fixture.Service.OpenDefinition(address, grid: false);
            await Save("Second saved version");
            var second = fixture.Service.Drafts().Single(draft => draft.Id != first.Id);
            Require(OldDraftButtons() is [var older] && older.TooltipText.Contains(first.Id.Value.ToString("D"), StringComparison.Ordinal),
                "同一定义重新保存另一份草稿时，必须只隐藏本次暂存的那份，保留第一份旧草稿。");

            fixture.Service.DeleteDefinition(address);
            Require(fixture.Service.IsDeleted(address) && OldDraftButtons().Length == 2,
                "标记删除后，两份旧草稿都必须可见，供用户恢复定义。");
            var restore = OldDraftButtons().Single(button => button.TooltipText.Contains(first.Id.Value.ToString("D"), StringComparison.Ordinal));
            await input.Activate(restore);
            Require(NameField().Text == "First saved version", "打开旧草稿必须恢复选中版本的表单内容。");
            await Save("Restored first version");
            Require(!fixture.Service.IsDeleted(address) && fixture.Service.IsPending(address)
                && OldDraftButtons() is [var remaining] && remaining.TooltipText.Contains(second.Id.Value.ToString("D"), StringComparison.Ordinal),
                "重新保存旧草稿必须撤销待删除状态，并只隐藏这次暂存的草稿。");
        }
        finally { panel.QueueFree(); await input.Frames(3); }
    }
}
