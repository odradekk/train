
using Godot;
using Train.App.Saves;

namespace Train.Ui;

public partial class MainMenu : Control
{
    private SavePanel? _saves;
    private Button _newGame = null!;
    public event Action? QuitRequested;
    public event Action? NewGameRequested;
    public event Action? SettingsRequested;

    internal void SetHostAvailable(bool available) => _newGame.Visible = available;
    internal void SetNewGameEnabled(bool enabled) => _newGame.Disabled = !enabled;

    internal void ShowSaveFailure(SaveFailure? failure) => _saves?.ShowFailure(failure);

    internal void OpenSaves() => _saves?.Open();
    internal void CloseSaves() => _saves?.Hide();

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
        GetNode<Button>("%Settings").Pressed += () => SettingsRequested?.Invoke();
        quit.GrabFocus();
    }
}
