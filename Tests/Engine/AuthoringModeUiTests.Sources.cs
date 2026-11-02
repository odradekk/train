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
    public async Task Cancelling_source_change_keeps_the_selected_package_and_unsaved_form()
    {
        using var fixture = new RealAuthoringFixture(TestScene);
        var otherId = Guid.Parse("6bd09acd-8d40-4a54-a017-e6ba9a630a56");
        ContentFixture.WriteMod(Path.Combine(fixture.Mods, otherId.ToString("D")), otherId);
        await fixture.Start(); await fixture.Open();
        fixture.Service.OpenDefinition(new(DefinitionKind.Voxel, "engine-food"), grid: false);
        var panel = new AuthoringPanel { Name = "SourceAuthoring" };
        panel.Initialize(fixture.Service, fixture.Editor); TestScene.AddChild(panel); panel.Open();
        try
        {
            var source = Find<OptionButton>(panel, "SourcePackage");
            await fixture.Pump(() => fixture.Service.Sources.Any(item => item.Package?.Value == otherId.ToString("D"))
                && Enumerable.Range(0, source.ItemCount).Any(index => source.GetItemTooltip(index).Contains(otherId.ToString("D"), StringComparison.Ordinal)));
            var originalTarget = fixture.Service.Target;
            var originalIndex = source.Selected;
            var originalText = source.GetItemText(originalIndex);
            var originalItemId = source.GetItemId(originalIndex);
            Require(originalTarget?.Package == fixture.PackageId
                && source.GetItemTooltip(originalIndex).Contains(fixture.PackageId.Value, StringComparison.Ordinal),
                "选择器必须从当前真实本地包开始。");
            var form = Find<AuthoringDefinitionForm>(panel, "DefinitionForm");
            var name = form.FindChildren("*", "LineEdit", true, false).OfType<LineEdit>()
                .Single(field => field.TooltipText == "名称");
            await input.Enter(name, "Keep unsaved source edit");
            Require(fixture.Service.DirtyDefinition == new AuthoringDefinitionAddress(DefinitionKind.Voxel, "engine-food"),
                "切换源包前必须通过真实表单输入产生未保存修改。");

            var otherIndex = Enumerable.Range(0, source.ItemCount).Single(index =>
                source.GetItemTooltip(index).Contains(otherId.ToString("D"), StringComparison.Ordinal));
            await input.Activate(source);
            var popup = source.GetPopup();
            await input.WaitUntil(() => popup.Visible && popup.GetFocusedItem() >= 0);
            var focused = popup.GetFocusedItem();
            var direction = focused < otherIndex ? Key.Down : Key.Up;
            for (var move = 0; move < Math.Abs(otherIndex - focused); move++) await input.PressKey(direction);
            Require(popup.GetFocusedItem() == otherIndex, "真实下拉输入必须定位到另一个临时源包。");
            await input.PressKey(Key.Enter);
            var leave = Find<Control>(panel, "LeaveAuthoring");
            Require(leave.IsVisibleInTree() && fixture.Service.Leaving && fixture.Service.Target == originalTarget,
                "切换源包必须进入真实未保存询问，确认前仍编辑原包。");
            await TabTo(Find<Button>(leave, "CancelLeave"), leave);
            await input.PressKey(Key.Space);
            await input.Frames(2);

            Require(!leave.IsVisibleInTree() && !fixture.Service.Leaving && fixture.Service.Target == originalTarget
                && fixture.Service.DirtyDefinition == new AuthoringDefinitionAddress(DefinitionKind.Voxel, "engine-food")
                && name.Text == "Keep unsaved source edit",
                "取消切换必须留在原包并保留表单中的未保存内容。");
            Require(source.Selected == originalIndex && source.GetSelectedId() == originalItemId
                && source.GetItemText(source.Selected) == originalText
                && source.GetItemTooltip(source.Selected).Contains(fixture.PackageId.Value, StringComparison.Ordinal),
                "取消切换后，源包选择器的实际选中项必须仍指向正在编辑的原包。");
        }
        finally { panel.QueueFree(); await input.Frames(3); }
    }
}
