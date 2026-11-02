using Chickensoft.GoDotTest;
using Godot;
using Train.Content.Contracts;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class AuthoringModeUiTests
{
    [Test]
    [Chickensoft.GoDotTest.Timeout(120000)]
    public async Task New_definition_dialog_validates_ids_and_asset_cancel_leaves_the_form_unchanged()
    {
        using var fixture = new RealAuthoringFixture(TestScene);
        await fixture.Start(); await fixture.Open();
        fixture.Service.OpenDefinition(new(DefinitionKind.Voxel, "engine-food"), grid: false);
        var panel = new AuthoringPanel(); panel.Initialize(fixture.Service, fixture.Editor); TestScene.AddChild(panel); panel.Open();
        try
        {
            await fixture.Pump(() => !fixture.Service.IsBusy);
            await input.Frames(2);
            await input.Activate(Find<Button>(panel, "NewDefinition"));
            var dialog = Find<Control>(panel, "NewDefinitionDialog");
            var id = Find<LineEdit>(dialog, "NewDefinitionId");
            var kind = Find<OptionButton>(dialog, "NewDefinitionKind");
            var create = Find<Button>(dialog, "CreateDefinition");
            kind.Select(6); kind.EmitSignal(OptionButton.SignalName.ItemSelected, 6);
            await input.Enter(id, "bad id");
            Require(create.Disabled && Find<Label>(dialog, "NewDefinitionProblem").Text.Length > 0, "内容层拒绝的 ID 必须立即显示错误并禁用创建。");
            await input.Enter(id, "engine-food");
            Require(create.Disabled, "已有同类定义必须阻止创建。");
            await input.Enter(id, "available-voxel");
            Require(!create.Disabled && Find<Label>(dialog, "NewDefinitionFullId").Text == fixture.PackageId.Value + ":available-voxel",
                "可用 ID 必须显示完整包地址并允许创建。");
            await input.Activate(Find<Button>(dialog, "CancelNewDefinition"));
            var form = Find<AuthoringDefinitionForm>(panel, "DefinitionForm");
            var localId = form.FindChildren("*", "LineEdit", true, false).OfType<LineEdit>().Single(field => field.TooltipText == "本地 ID");
            Require(!localId.Editable, "已有定义 ID 必须只读。");
            var captured = (DefinitionDraft.Voxel)form.Capture(); var changes = fixture.Service.PendingChanges;
            var assetButton = form.FindChildren("*", "Button", true, false).OfType<Button>().First(button => button.Text == "选择文件…");
            await input.Activate(assetButton);
            var file = Find<FileDialog>(panel, "ChooseAuthoringAsset");
            Require(file.Visible, "资产字段必须打开文件选择器。");
            Require(panel.HandleEscape(), "取消资产选择必须由创作面板处理。");
            Require(!file.Visible && ((DefinitionDraft.Voxel)form.Capture()).Value.Icon == captured.Value.Icon && fixture.Service.PendingChanges.SequenceEqual(changes)
                && fixture.Service.DirtyDefinition is null, "取消文件选择不能修改字段或暂存资产。");
        }
        finally { panel.QueueFree(); await input.Frames(3); }
    }
}
