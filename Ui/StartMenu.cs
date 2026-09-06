using Godot;

namespace Train;

public partial class StartMenu : Control
{
    public override void _Ready()
    {
        GetNode<Button>("%StartGame").Pressed += OnStartPressed;
        GetNode<Button>("%QuitGame").Pressed += OnQuitPressed;
        if (string.Equals(System.Environment.GetEnvironmentVariable("TRAIN_VOXEL_SMOKE"), "1", StringComparison.Ordinal) &&
            string.Equals(System.Environment.GetEnvironmentVariable("TRAIN_VOXEL_SMOKE_REENTER"), "1", StringComparison.Ordinal))
        {
            GD.Print("VOXEL_SMOKE_RETURNED_TO_MENU");
            GetNode<Button>("%StartGame").EmitSignal(BaseButton.SignalName.Pressed);
        }
    }

    private void OnStartPressed()
    {
        GetNode<Main>("/root/Main").EnterVoxelDevelopmentScene();
    }

    private void OnQuitPressed() => GetTree().Quit();
}
