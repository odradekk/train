using Godot;

namespace Train;

public partial class StartMenu : Control
{
    public override void _Ready()
    {
        GetNode<Button>("%StartGame").Pressed += OnStartPressed;
        GetNode<Button>("%QuitGame").Pressed += OnQuitPressed;
    }

    private void OnStartPressed()
    {
        var logger = GetNode<Main>("/root/Main").Logger;
        logger?.Info("application", "start_game_pressed", "Start game is a placeholder.");
    }

    private void OnQuitPressed() => GetTree().Quit();
}
