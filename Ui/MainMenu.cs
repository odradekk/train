
using Godot;

namespace Train.Ui;

public partial class MainMenu : Control
{
    public event Action? QuitRequested;
    public event Action? NewGameRequested;
    public event Action? SettingsRequested;

    internal void SetHostAvailable(bool available) => GetNode<Button>("%NewGame").Visible = available;

    public override void _Ready()
    {
        var quit = GetNode<Button>("%QuitGame");
        quit.Pressed += () => QuitRequested?.Invoke();
        GetNode<Button>("%NewGame").Pressed += () => NewGameRequested?.Invoke();
        GetNode<Button>("%Settings").Pressed += () => SettingsRequested?.Invoke();
        quit.GrabFocus();
    }
}
