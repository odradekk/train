
using Godot;
using Train.App.Saves;

namespace Train.Ui;

public partial class MainMenu : Control
{
    private SavePanel? _saves;
    private Button _newGame = null!;
    private Button _example = null!;
    public event Action? QuitRequested;
    public event Action? NewGameRequested;
    public event Action? ExampleRequested;
    public event Action? SettingsRequested;

    internal void SetHostAvailable(bool available)
    {
        _newGame.Visible = available;
        _example.Visible = available && OS.IsDebugBuild();
    }
    internal void SetGameStartEnabled(bool enabled)
    {
        _newGame.Disabled = !enabled;
        _example.Disabled = !enabled;
    }

    internal void ShowSaveFailure(SaveFailure? failure) => _saves?.ShowFailure(failure);

    internal void OpenSaves() => _saves?.Open();
    internal void CloseSaves() => _saves?.Hide();

    internal void FitSaves(Rect2 area) => _saves?.Fit(area);

    internal void ConfigureSaves(SaveService service)
    {
        _saves = new SavePanel();
        AddChild(_saves);
        _saves.Configure(service);
        var button = GetNode<Button>("%Saves");
        button.Visible = true;
        button.Pressed += OpenSaves;
    }

    public override void _Ready()
    {
        var quit = GetNode<Button>("%QuitGame");
        quit.Pressed += () => QuitRequested?.Invoke();
        _newGame = GetNode<Button>("%NewGame");
        _newGame.Pressed += () => NewGameRequested?.Invoke();
        _example = GetNode<Button>("%Example");
        _example.Visible = OS.IsDebugBuild();
        _example.Pressed += () => ExampleRequested?.Invoke();
        GetNode<Button>("%Settings").Pressed += () => SettingsRequested?.Invoke();
        quit.GrabFocus();
    }
}
