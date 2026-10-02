using Godot;

namespace Train;

/// <summary>Real pointer input shared by Godot smoke drivers.</summary>
internal static class GodotSmokeInput
{
    internal static void Click(Vector2 point, bool ctrl = false, bool shift = false)
    {
        Input.ParseInputEvent(new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Left, Pressed = true, Position = point, GlobalPosition = point,
            CtrlPressed = ctrl, ShiftPressed = shift
        });
        Input.ParseInputEvent(new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Left, Pressed = false, Position = point, GlobalPosition = point,
            CtrlPressed = ctrl, ShiftPressed = shift
        });
        Input.FlushBufferedEvents();
    }

    internal static void Drag(Vector2 start, Vector2 end)
    {
        Input.ParseInputEvent(new InputEventMouseButton
            { ButtonIndex = MouseButton.Left, Pressed = true, Position = start, GlobalPosition = start });
        Input.ParseInputEvent(new InputEventMouseMotion
            { Position = end, GlobalPosition = end, Relative = end - start, ButtonMask = MouseButtonMask.Left });
        Input.ParseInputEvent(new InputEventMouseButton
            { ButtonIndex = MouseButton.Left, Pressed = false, Position = end, GlobalPosition = end });
        Input.FlushBufferedEvents();
    }
}
