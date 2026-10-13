using Godot;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

internal sealed class UiTestInput(Node scene)
{
    internal async Task Frames(int count = 1)
    {
        for (var frame = 0; frame < count; frame++)
            await scene.ToSignal(scene.GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    internal async Task WaitUntil(Func<bool> completed)
    {
        for (var frame = 0; frame < 180 && !completed(); frame++) await Frames();
        Require(completed(), "真实帧未完成预期的界面状态变化。");
    }

    internal async Task PressKey(Key key, bool control = false, uint unicode = 0)
    {
        using var press = new InputEventKey { Keycode = key, Pressed = true, CtrlPressed = control, Unicode = unicode };
        Input.ParseInputEvent(press); await Frames();
        using var release = new InputEventKey { Keycode = key, Pressed = false, CtrlPressed = control, Unicode = unicode };
        Input.ParseInputEvent(release); await Frames();
    }

    internal async Task MouseButton(Vector2 position, Godot.MouseButton button, bool pressed)
        => await SendMouseButton(position, button, pressed, false);

    private async Task SendMouseButton(Vector2 position, Godot.MouseButton button, bool pressed, bool control)
    {
        // ParseInputEvent 接收窗口坐标，需计入项目的 canvas_items 缩放。
        position = scene.GetViewport().GetScreenTransform() * position;
        using var input = new InputEventMouseButton { Position = position, GlobalPosition = position,
            ButtonIndex = button, Pressed = pressed, CtrlPressed = control,
            ButtonMask = !pressed ? 0 : button switch
            {
                Godot.MouseButton.Middle => MouseButtonMask.Middle,
                Godot.MouseButton.Left => MouseButtonMask.Left,
                _ => 0
            } };
        Input.ParseInputEvent(input); await Frames();
    }

    internal async Task Click(Vector2 position, bool control = false)
    {
        await SendMouseButton(position, Godot.MouseButton.Left, true, control);
        await SendMouseButton(position, Godot.MouseButton.Left, false, control);
    }

    internal async Task Drag(Vector2 start, Vector2 delta)
    {
        await MouseButton(start, Godot.MouseButton.Middle, true);
        var transform = scene.GetViewport().GetScreenTransform();
        var end = transform * (start + delta);
        using var motion = new InputEventMouseMotion { Position = end, GlobalPosition = end,
            Relative = end - transform * start, ButtonMask = MouseButtonMask.Middle };
        Input.ParseInputEvent(motion); await Frames();
        await MouseButton(start + delta, Godot.MouseButton.Middle, false);
    }

    internal async Task ClickItem(ItemList list, int index, bool control = false)
    {
        // ItemList 的行矩形由绘制更新，建立或填充后需等待缓存坐标有效。
        await WaitUntil(() => list.IsVisibleInTree() && list.Size.X > 0 && list.Size.Y > 0 &&
            list.GetItemRect(index).Size.Y > 0 &&
            list.GetItemAtPosition(list.GetItemRect(index).GetCenter(), true) == index);
        await Click(list.GlobalPosition + list.GetItemRect(index).GetCenter(), control);
    }
}
