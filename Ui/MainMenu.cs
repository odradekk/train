
using Godot;
using Train.App.Saves;
using Train.Content.Contracts;

namespace Train.Ui;

public partial class MainMenu : Control
{
    // Decision note: 2026-10-31-menus-saves-and-settings
    private SavePanel? _saves;
    private SaveService? _service;
    private SaveEntry? _recent;
    private IContentView? _content;
    private Button _continue = null!;
    private Button _newGame = null!;
    private Button _example = null!;
    private bool _ready;
    public event Action? QuitRequested;
    public event Action? NewGameRequested;
    public event Action? ExampleRequested;
    public event Action? SettingsRequested;

    internal void SetHostAvailable(bool available)
    {
        _newGame.Visible = available;
        _continue.Visible = available;
        _example.Visible = available && OS.IsDebugBuild();
    }
    internal void SetGameStartEnabled(bool enabled, IContentView? content, bool loading)
    {
        _newGame.Disabled = !enabled;
        _example.Disabled = !enabled;
        _continue.Disabled = !enabled || _recent is null;
        GetNode<ProgressBar>("%LoadingProgress").Visible = loading && content is null;
        GetNode<Label>("%LoadingStatus").Text = content is null
            ? loading ? "正在加载内容包，加载完成后可以开始游戏。" : "内容未能加载，请检查内容包并重新启动游戏。" : "";
        if (_content is null || !ReferenceEquals(_content, content))
        {
            _content = content;
            var packages = content?.Packages.Where(package => package.State == PackageState.Available)
                .Select(package => $"{package.Metadata!.Name} {package.Metadata.Version}");
            GetNode<Label>("%VersionPackages").Text = $"版本 {ProjectSettings.GetSetting("application/config/version").AsString()} · 内容包：" +
                (packages is null ? loading ? "加载中" : "未能加载" : string.Join("、", packages));
        }
        var ready = enabled && IsVisibleInTree();
        if (ready && !_ready)
        {
            RefreshRecent();
            if (GetViewport().GuiGetFocusOwner() is not { } focus || IsAncestorOf(focus))
                (_recent is null ? _newGame : _continue).GrabFocus();
        }
        _ready = ready;
    }

    private void RefreshRecent()
    {
        _recent = _service?.List().FirstOrDefault();
        GetNode<Label>("%RecentSave").Text = _recent is null ? "暂无存档" :
            $"{_recent.Header.Name} · {UiInput.FormatTime(_recent.Header.Position.CompletedSteps)}\n{SavedAt(_recent.Header.SavedAt)}保存";
        _continue.Disabled = _newGame.Disabled || _recent is null;
    }

    private static string SavedAt(DateTimeOffset savedAt)
    {
        var local = savedAt.LocalDateTime;
        var today = DateTime.Today;
        return local.Date == today ? $"今天 {local:HH:mm}" : local.Year == today.Year
            ? $"{local:M 月 d 日 HH:mm}" : $"{local:yyyy 年 M 月 d 日 HH:mm}";
    }

    internal void ShowSaveFailure(SaveFailure? failure) => _saves?.ShowFailure(failure);

    internal void OpenSaves() => _saves?.Open();
    internal void CloseSaves() => _saves?.Hide();

    internal void FitSaves(Rect2 area) => _saves?.Fit(area);

    internal void ConfigureSaves(SaveService service)
    {
        _service = service;
        _saves = new SavePanel();
        AddChild(_saves);
        _saves.Configure(service);
        var button = GetNode<Button>("%Saves");
        button.Visible = true;
        button.Pressed += OpenSaves;
        service.Changed += () => { if (IsVisibleInTree() && !_newGame.Disabled) RefreshRecent(); };
    }

    public override void _Ready()
    {
        var quit = GetNode<Button>("%QuitGame");
        quit.Pressed += () => QuitRequested?.Invoke();
        _continue = GetNode<Button>("%Continue");
        _continue.Pressed += () =>
        {
            if (_recent is null || _service is null) return;
            if (_service.Load(_recent) is { } failure) { OpenSaves(); ShowSaveFailure(failure); }
        };
        _newGame = GetNode<Button>("%NewGame");
        _newGame.Pressed += () => NewGameRequested?.Invoke();
        _example = GetNode<Button>("%Example");
        _example.Visible = OS.IsDebugBuild();
        _example.Pressed += () => ExampleRequested?.Invoke();
        GetNode<Button>("%Settings").Pressed += () => SettingsRequested?.Invoke();
        SetGameStartEnabled(false, null, true);
    }
}
