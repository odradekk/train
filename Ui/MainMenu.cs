
using Godot;

namespace Train.Ui;

public partial class MainMenu : Control
{
    public event Action? QuitRequested;

    public override void _Ready()
    {
        var quit = GetNode<Button>("%QuitGame");
        quit.Pressed += () => QuitRequested?.Invoke();
        quit.GrabFocus();
    }
}
