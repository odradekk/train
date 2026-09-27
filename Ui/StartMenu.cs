using Godot;
using Train.Domain.Playground;
using Train.Domain.Voxels;

namespace Train;

public partial class StartMenu : Control
{
    private readonly Dictionary<string, Button> _playgroundButtons = new(StringComparer.Ordinal);

    public override void _Ready()
    {
        GetNode<Button>("%StartGame").Pressed += OnStartPressed;
        GetNode<Button>("%StartSlice2").Pressed += OnSlice2Pressed;
        GetNode<Button>("%StartSlice3").Pressed += OnSlice3Pressed;
        GetNode<Button>("%StartSlice4").Pressed += OnSlice4Pressed;
        GetNode<Button>("%QuitGame").Pressed += OnQuitPressed;
        AddPlaygroundButtons();
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
        else if (Main.IsPlaygroundAutomationEnabled())
        {
            var id = Main.ReadRequestedPlaygroundConfiguration();
            GD.Print($"PLAYGROUND_SMOKE_MENU_ENTER config={id}");
            Callable.From(() =>
            {
                if (_playgroundButtons.TryGetValue(id, out var button))
                    button.EmitSignal(BaseButton.SignalName.Pressed);
                else
                    GetNode<Main>("/root/Main").EnterPlayground(id);
            }).CallDeferred();
        }
    }

    private void AddPlaygroundButtons()
    {
        var quit = GetNode<Button>("%QuitGame");
        var buttons = quit.GetParent();
        foreach (var configuration in PlaygroundConfigurations.All)
        {
            var id = configuration.Id;
            var button = new Button
            {
                Name = "Playground_" + id.Replace('-', '_'),
                Text = $"Playground · {configuration.Title}",
                CustomMinimumSize = new Vector2(280, 48)
            };
            button.Pressed += () => GetNode<Main>("/root/Main").EnterPlayground(id);
            buttons.AddChild(button);
            buttons.MoveChild(button, quit.GetIndex());
            _playgroundButtons[id] = button;
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
