using Godot;
using Train.App.Saves;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

internal sealed class UiTestInput(Node scene)
{
    internal async Task Activate(BaseButton button, Window? window = null)
    {
        Require(button.IsVisibleInTree() && !button.Disabled, $"真实输入按钮不可用：{button.Name}。");
        button.GrabFocus();
        await PressKey(Key.Space, window: window);
    }

    internal async Task Choose(OptionButton choice, int index, Action? afterOpen = null)
    {
        Require(choice.IsVisibleInTree(), "下拉选择不可见。");
        choice.GrabFocus();
        await PressKey(Key.Space);
        var popup = choice.GetPopup();
        await WaitUntil(() => popup.Visible && popup.GetFocusedItem() >= 0);
        afterOpen?.Invoke();
        await Frames();
        Require(popup.Visible, "修订变化关闭了正在操作的下拉框。");
        var focused = popup.GetFocusedItem();
        var direction = focused < index ? Key.Down : Key.Up;
        for (var move = 0; move < Math.Abs(index - focused); move++) await PressKey(direction);
        var beforeEnter = popup.GetFocusedItem();
        await PressKey(Key.Enter);
        Require(choice.Selected == index,
            $"实际输入未选择目标选项：popupVisible={popup.Visible}，initialFocus={focused}，beforeEnterFocus={beforeEnter}，currentFocus={popup.GetFocusedItem()}，selected={choice.Selected}，target={index}。");
    }

    internal async Task Enter(LineEdit edit, string value)
    {
        Require(edit.IsVisibleInTree(), "输入框不可见。");
        edit.GrabFocus(); await PressKey(Key.A, true);
        foreach (var digit in value) await PressKey((Key)((long)Key.Key0 + digit - '0'), unicode: digit);
    }

    internal async Task ClickAcrossRevision(Button button, Action advance)
    {
        Require(button.IsVisibleInTree() && !button.Disabled, "鼠标目标按钮不可用。");
        for (var parent = button.GetParent(); parent is not null; parent = parent.GetParent())
            if (parent is ScrollContainer scroll) scroll.EnsureControlVisible(button);
        await Frames(2);
        var position = button.GetGlobalRect().GetCenter();
        await MouseButton(position, Godot.MouseButton.Left, true);
        advance();
        await Frames();
        await MouseButton(position, Godot.MouseButton.Left, false);
    }

    internal static Button SaveButton(SavePanel panel, string text) =>
        panel.FindChildren("*", "Button", true, false).Cast<Button>()
            .Single(button => button.Text == text && button.GetWindow() == panel);

    internal static void DeleteSaves(IEnumerable<Guid> owned)
    {
        var store = new FileSaveStore(OS.GetUserDataDir());
        foreach (var id in owned)
        {
            var directory = Path.GetFullPath(store.DirectoryFor(id));
            Require(Path.GetDirectoryName(directory) == Path.Combine(store.Root, FileSaveStore.SavesFolder),
                "测试清理路径不在本次存档目录内。");
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

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

    internal async Task PressKey(Key key, bool control = false, uint unicode = 0, Window? window = null)
    {
        using var press = new InputEventKey { Keycode = key, Pressed = true, CtrlPressed = control, Unicode = unicode };
        if (window is null) Input.ParseInputEvent(press); else window.PushInput(press);
        await Frames();
        using var release = new InputEventKey { Keycode = key, Pressed = false, CtrlPressed = control, Unicode = unicode };
        if (window is null) Input.ParseInputEvent(release); else window.PushInput(release);
        await Frames();
    }

    internal async Task MouseButton(Vector2 position, Godot.MouseButton button, bool pressed)
        => await SendMouseButton(position, button, pressed, false);

    private async Task SendMouseButton(Vector2 position, Godot.MouseButton button, bool pressed, bool control,
        Window? window = null)
    {
        // ParseInputEvent 接收窗口坐标，需计入项目的 canvas_items 缩放。
        if (window is null) position = scene.GetViewport().GetScreenTransform() * position;
        using var input = new InputEventMouseButton { Position = position, GlobalPosition = position,
            ButtonIndex = button, Pressed = pressed, CtrlPressed = control,
            ButtonMask = !pressed ? 0 : button switch
            {
                Godot.MouseButton.Middle => MouseButtonMask.Middle,
                Godot.MouseButton.Left => MouseButtonMask.Left,
                _ => 0
            } };
        if (window is null) Input.ParseInputEvent(input); else window.PushInput(input, true);
        await Frames();
    }

    internal async Task Click(Vector2 position, bool control = false, Window? window = null)
    {
        await SendMouseButton(position, Godot.MouseButton.Left, true, control, window);
        await SendMouseButton(position, Godot.MouseButton.Left, false, control, window);
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

    internal async Task ClickItem(ItemList list, int index, bool control = false, Window? window = null)
    {
        // ItemList 的行矩形由绘制更新，建立或填充后需等待缓存坐标有效。
        await WaitUntil(() => list.IsVisibleInTree() && list.Size.X > 0 && list.Size.Y > 0 &&
            list.GetItemRect(index).Size.Y > 0 &&
            list.GetItemAtPosition(list.GetItemRect(index).GetCenter(), true) == index);
        await Click(list.GlobalPosition + list.GetItemRect(index).GetCenter(), control, window);
    }
}
