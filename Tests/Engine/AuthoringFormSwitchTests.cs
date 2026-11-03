using Chickensoft.GoDotTest;
using Godot;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public class AuthoringFormSwitchTests(Node testScene) : TestClass(testScene)
{
    [Test]
    public async Task Disabling_simulation_immediately_captures_after_invalid_mass_input()
    {
        var form = new AuthoringDefinitionForm();
        TestScene.AddChild(form);
        form.Load(new DefinitionDraft.Voxel(new("voxel", new("体素", ""), null,
            new(null, 1, null, false, null, []), AllowedVoxelOrientations.None,
            new VoxelCollision.None(), null, new(false, null))), new("fixture"), []);
        try
        {
            Require(form.TryCapture(out _), "初始模拟层表单必须有效。");
            DefinitionDraft? updated = null;
            var edits = 0;
            form.Edited += () =>
            {
                edits++;
                if (form.TryCapture(out var value) && value is not null) updated = value;
            };
            var mass = form.FindChildren("*", "LineEdit", true, false).OfType<LineEdit>()
                .Single(edit => edit.TooltipText == "质量（可空）");
            mass.Text = "invalid";
            mass.EmitSignal(LineEdit.SignalName.TextChanged, mass.Text);
            Require(edits > 0 && updated is null && !form.TryCapture(out _), "启用模拟层时非法质量必须阻止捕获。");
            var beforeSwitch = edits;
            var enabled = form.FindChildren("*", "CheckBox", true, false).OfType<CheckBox>()
                .Single(control => control.Text == "启用模拟层");
            Require(enabled.ButtonPressed, "初始模拟层必须启用。");
            enabled.ButtonPressed = false;
            Require(edits > beforeSwitch, "取消启用模拟层必须发出编辑事件。");
            Require(updated is DefinitionDraft.Voxel { Value.Simulation: null },
                "取消启用模拟层的编辑事件必须立即捕获新表单，未启用分组的非法质量不能阻止捕获。");
        }
        finally { form.QueueFree(); await new UiTestInput(TestScene).Frames(2); }
    }

    [Test]
    public async Task Switching_to_no_collision_immediately_captures_after_invalid_local_box_input()
    {
        var form = new AuthoringDefinitionForm();
        TestScene.AddChild(form);
        form.Load(new DefinitionDraft.Voxel(new("voxel", new("体素", ""), null, null,
            AllowedVoxelOrientations.None, new VoxelCollision.LocalBox(new(0, 0, 0), new(1, 1, 1)),
            null, new(false, null))), new("fixture"), []);
        try
        {
            Require(form.TryCapture(out _), "初始局部碰撞表单必须有效。");
            DefinitionDraft? updated = null;
            var edits = 0;
            form.Edited += () =>
            {
                edits++;
                if (form.TryCapture(out var value) && value is not null) updated = value;
            };
            var offset = form.FindChildren("*", "LineEdit", true, false).OfType<LineEdit>()
                .Single(edit => edit.TooltipText == "碰撞偏移 X");
            Require(offset.IsVisibleInTree(), "必须编辑当前可见的局部碰撞字段。");
            offset.Text = "invalid";
            offset.EmitSignal(LineEdit.SignalName.TextChanged, offset.Text);
            Require(edits > 0 && updated is null && !form.TryCapture(out _), "非法偏移必须触发编辑并阻止捕获。");
            var beforeSwitch = edits;
            var collision = form.FindChildren("*", "OptionButton", true, false).OfType<OptionButton>()
                .Single(control => control.TooltipText == "碰撞类型");
            var none = Enumerable.Range(0, collision.ItemCount).Single(index => collision.GetItemText(index) == "无碰撞");
            collision.Select(none);
            collision.EmitSignal(OptionButton.SignalName.ItemSelected, (long)none);
            Require(edits > beforeSwitch && !offset.IsVisibleInTree(), "切换必须发出编辑事件并隐藏局部碰撞字段。");
            Require(updated is DefinitionDraft.Voxel { Value.Collision: VoxelCollision.None },
                "切换无碰撞的编辑事件必须立即捕获新表单，无需再编辑其他字段。");
        }
        finally { form.QueueFree(); await new UiTestInput(TestScene).Frames(2); }
    }
}
