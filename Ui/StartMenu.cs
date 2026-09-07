using Godot;
using Train.Domain.Voxels;

namespace Train;

public partial class StartMenu : Control
{
    public override void _Ready()
    {
        GetNode<Button>("%StartGame").Pressed += OnStartPressed;
        GetNode<Button>("%StartSlice2").Pressed += OnSlice2Pressed;
        GetNode<Button>("%QuitGame").Pressed += OnQuitPressed;
        if (string.Equals(System.Environment.GetEnvironmentVariable("TRAIN_VOXEL_SMOKE"), "1", StringComparison.Ordinal) &&
            string.Equals(System.Environment.GetEnvironmentVariable("TRAIN_VOXEL_SMOKE_REENTER"), "1", StringComparison.Ordinal))
        {
            GD.Print("VOXEL_SMOKE_RETURNED_TO_MENU");
            if (Main.ReadRequestedSlice() == VoxelDevelopmentSlice.Slice2)
                GetNode<Button>("%StartSlice2").EmitSignal(BaseButton.SignalName.Pressed);
            else
                GetNode<Button>("%StartGame").EmitSignal(BaseButton.SignalName.Pressed);
        }
    }

    private void OnStartPressed() =>
        GetNode<Main>("/root/Main").EnterVoxelDevelopmentScene(VoxelDevelopmentSlice.Slice1);

    private void OnSlice2Pressed() =>
        GetNode<Main>("/root/Main").EnterVoxelDevelopmentScene(VoxelDevelopmentSlice.Slice2);

    private void OnQuitPressed() => GetTree().Quit();
}
