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
        GetNode<Main>("/root/Main").EnterVoxelDevelopmentScene();
    }

    private void OnQuitPressed() => GetTree().Quit();
}
