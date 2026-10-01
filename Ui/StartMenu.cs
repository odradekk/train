using Godot;
using Train.Domain.WorldEditor;
using Train.Domain.Voxels;

namespace Train;

public partial class StartMenu : Control
{
    private readonly Dictionary<string, Button> _worldEditorButtons = new(StringComparer.Ordinal);

    public override void _Ready()
    {
        GetNode<Button>("%StartGame").Pressed += OnStartPressed;
        GetNode<Button>("%StartSlice2").Pressed += OnSlice2Pressed;
        GetNode<Button>("%StartSlice3").Pressed += OnSlice3Pressed;
        GetNode<Button>("%StartSlice4").Pressed += OnSlice4Pressed;
        GetNode<Button>("%QuitGame").Pressed += OnQuitPressed;
        AddWorldEditorButtons();
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
        else if (Main.IsWorldEditorAutomationEnabled())
        {
            var id = Main.ReadRequestedWorldEditorConfiguration();
            GD.Print($"WORLD_EDITOR_SMOKE_MENU_ENTER config={id}");
            Callable.From(() =>
            {
                if (_worldEditorButtons.TryGetValue(id, out var button))
                    button.EmitSignal(BaseButton.SignalName.Pressed);
                else
                    GetNode<Main>("/root/Main").EnterWorldEditor(id);
            }).CallDeferred();
        }
    }

    private void AddWorldEditorButtons()
    {
        var quit = GetNode<Button>("%QuitGame");
        var buttons = quit.GetParent();
        foreach (var configuration in WorldEditorConfigurations.All)
        {
            var id = configuration.Id;
            var button = new Button
            {
                Name = "WorldEditor_" + id.Replace('-', '_'),
                Text = $"世界编辑器 · {configuration.Title}",
                CustomMinimumSize = new Vector2(280, 48)
            };
            button.Pressed += () => GetNode<Main>("/root/Main").EnterWorldEditor(id);
            buttons.AddChild(button);
            buttons.MoveChild(button, quit.GetIndex());
            _worldEditorButtons[id] = button;
        }
    }

    private void OnStartPressed() =>
        GetNode<Main>("/root/Main").EnterVoxelDevelopmentScene(VoxelDevelopmentSlice.Slice1);

    private void OnSlice2Pressed() =>
        GetNode<Main>("/root/Main").EnterVoxelDevelopmentScene(VoxelDevelopmentSlice.Slice2);

    private void OnSlice3Pressed() =>
        GetNode<Main>("/root/Main").EnterVoxelDevelopmentScene(VoxelDevelopmentSlice.Slice3);

    private void OnSlice4Pressed() =>
        GetNode<Main>("/root/Main").EnterVoxelDevelopmentScene(VoxelDevelopmentSlice.Slice4);

    private void OnQuitPressed() => GetTree().Quit();
}
