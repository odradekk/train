using Chickensoft.GoDotTest;
using Godot;
using Train.Content.Contracts;
using Train.Scenes.Contracts;
using Train.Scenes.ContractSupport;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class EditorUiTests
{
    [Test]
    public async Task Hover_conflicts_show_player_text_and_only_apply_reports_raw_details()
    {
        var fixture = await Ready();
        try
        {
            var session = fixture.Factory.Session!;
            EditConflict[] conflicts =
            [
                new(new(1, 0, 2), null, new("occupancy_conflict", SceneStage.Edit, null, null, null, null,
                    "The brush intersects another object's cells.")),
                new(null, null, new("edit_locked", SceneStage.Edit, null,
                    new ContentReference.Design(new("locked-device")), new("missing-package"), null, "缺失定义禁止旋转。"))
            ];
            session.PreviewCall = operation => new SceneResult<IEditPreview>.Success(new FixedEditPreview(session.Capture().Stamp)
            {
                CanApply = false,
                Conflicts = [.. conflicts]
            });
            var panel = EditorPanel(fixture);
            var actions = panel.FindChildren("*", "OptionButton", true, false).OfType<OptionButton>().Single(choice => choice.ItemCount > 0 && choice.GetItemText(0) == "添加");
            await new UiTestInput(TestScene).Choose(actions, 1);
            await LeftMotion(TestScene.GetViewport().GetVisibleRect().GetCenter());
            foreach (var text in new[] { "(1, 0, 2)", "与其他部分的占格重叠", "缺失定义已锁定，不能进行这个操作。", "来源内容包不可用。" })
                Require(Displayed(panel, text), $"预览冲突界面缺少：{text}");
            foreach (var text in new[] { "GridPosition", "occupancy_conflict", "edit_locked", "locked-device", "missing-package", "禁止旋转", "The brush intersects" })
                Require(!Displayed(panel, text), $"预览冲突不能向玩家显示：{text}");
            Require(!fixture.Editor.HasError, "悬停冲突不能成为操作失败。");
            Require(!fixture.App.FindChildren("*", "", true, false).OfType<GameConsole>().Single().LogEvents
                .Any(log => log.EventId == "editor_operation_failed"), "悬停不能记录操作失败日志。");
            await UiCapture.Capture(TestScene, "player-editor-preview-conflict");
            session.ApplyCall = _ => new SceneResult<EditState>.Failure([.. conflicts.Select(conflict => conflict.Problem)]);
            await new UiTestInput(TestScene).Click(fixture.Controller.VisibleArea.GetCenter());
            Require(fixture.Editor.HasError, "实际提交失败必须报告错误。");
            await new UiTestInput(TestScene).PressKey(Key.Quoteleft);
            foreach (var text in new[] { "occupancy_conflict", "The brush intersects", "edit_locked", "locked-device", "missing-package", "禁止旋转" })
                Require(ConsoleShows(fixture, text), $"提交失败控制台缺少：{text}");
        }
        finally { fixture.QueueFree(); await new UiTestInput(TestScene).Frames(3); }
    }
}
