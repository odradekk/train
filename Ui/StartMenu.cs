using Godot;
using Train.Domain.Voxels;

namespace Train;

public partial class StartMenu : Control
{
    internal Button WorldEditorButton { get; private set; } = null!;

    public override void _Ready()
    {
        GetNode<Button>("%StartGame").Pressed += OnStartPressed;
        GetNode<Button>("%StartSlice2").Pressed += OnSlice2Pressed;
        GetNode<Button>("%StartSlice3").Pressed += OnSlice3Pressed;
        GetNode<Button>("%StartSlice4").Pressed += OnSlice4Pressed;
        GetNode<Button>("%QuitGame").Pressed += OnQuitPressed;
        AddWorldEditorButton();
        if (string.Equals(System.Environment.GetEnvironmentVariable("TRAIN_VOXEL_SMOKE"), "1", StringComparison.Ordinal) &&
            string.Equals(System.Environment.GetEnvironmentVariable("TRAIN_VOXEL_SMOKE_REENTER"), "1", StringComparison.Ordinal))
        {
            GD.Print("VOXEL_SMOKE_RETURNED_TO_MENU");
            var start = Main.ReadRequestedSlice() switch
            {
                VoxelDevelopmentSlice.Slice4 => GetNode<Button>("%StartSlice4"),
                VoxelDevelopmentSlice.Slice3 => GetNode<Button>("%StartSlice3"),
                VoxelDevelopmentSlice.Slice2 => GetNode<Button>("%StartSlice2"),
                _ => GetNode<Button>("%StartGame")
            };
            start.EmitSignal(BaseButton.SignalName.Pressed);
        }
        else if (WorldEditorWorkspaceSmoke.Requested())
            Callable.From(() => WorldEditorWorkspaceSmoke.FromMenu(this)).CallDeferred();
        else if (Main.IsFixedWorldEditorAutomationEnabled())
        {
            var id = Main.ReadRequestedWorldEditorConfiguration();
            GD.Print($"WORLD_EDITOR_SMOKE_MENU_ENTER config={id}");
            Callable.From(() =>
            {
                GetNode<Main>("/root/Main").EnterWorldEditor(id);
            }).CallDeferred();
        }
    }

    private void AddWorldEditorButton()
    {
        var quit = GetNode<Button>("%QuitGame");
        var buttons = quit.GetParent();
        WorldEditorButton = new Button
        {
            Text = "世界编辑器",
            CustomMinimumSize = new Vector2(280, 48)
        };
        WorldEditorButton.Pressed += () => GetNode<Main>("/root/Main").EnterWorldEditorWorkspace();
        buttons.AddChild(WorldEditorButton);
        buttons.MoveChild(WorldEditorButton, quit.GetIndex());
    }

    private void OnStartPressed() =>
        GetNode<Main>("/root/Main").EnterVoxelDevelopmentScene(VoxelDevelopmentSlice.Slice1);

    private void OnSlice2Pressed() =>
        GetNode<Main>("/root/Main").EnterVoxelDevelopmentScene(VoxelDevelopmentSlice.Slice2);

    private void OnSlice3Pressed() =>
        GetNode<Main>("/root/Main").EnterVoxelDevelopmentScene(VoxelDevelopmentSlice.Slice3);

    private void OnSlice4Pressed() =>
        GetNode<Main>("/root/Main").EnterVoxelDevelopmentScene(VoxelDevelopmentSlice.Slice4);

    private void OnQuitPressed() => GetNode<Main>("/root/Main").RequestQuit();
}
