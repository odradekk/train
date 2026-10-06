using Godot;
using Train.Content;
using Train.Domain.WorldEditor;
using Train.Domain.Voxels;
using Train.Core.Logging;

namespace Train;

public partial class GameConsole : CanvasLayer
{
    private RingBufferLogSink _buffer = null!;
    private ConsoleCommandDispatcher _dispatcher = null!;
    private TrainLogger _logger = null!;
    private ContentReloader? _reloader;
    private VoxelDevelopmentSession? _voxelSession;
    private WorldEditorObserver? _observer;
    private PanelContainer _panel = null!;
    private RichTextLabel _output = null!;
    private LineEdit _input = null!;
    public bool IsOpen => _open;

    private bool _open;
    private int _renderedCount = -1;
    private long _renderedHead;
    private long _renderedTail;

    public void Initialize(TrainLogger logger, RingBufferLogSink buffer, ContentReloader? reloader)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(buffer);
        _buffer = buffer;
        _logger = logger;
        _reloader = reloader;
        ConfigureDispatcher();
    }

    public void SetVoxelDevelopmentSession(VoxelDevelopmentSession? session)
    {
        _voxelSession = session;
        if (session is null)
            _observer = null;
        ConfigureDispatcher();
    }

    public void SetWorldEditorObserver(WorldEditorObserver? observer)
    {
        _observer = observer;
        ConfigureDispatcher();
    }

    private void ConfigureDispatcher()
    {
        _dispatcher = new ConsoleCommandDispatcher(
            _logger,
            _buffer.Clear,
            () => GetNode<Main>("/root/Main").RequestQuit(),
            () => _reloader?.Reload(),
            OS.IsDebugBuild(),
            _voxelSession is null ? null : new VoxelConsoleCommands(_voxelSession, ChangeWorld),
            _voxelSession is null ? null : new CompositeConsoleCommands(_voxelSession, ChangeWorld),
            _voxelSession is null ? null : new RailConsoleCommands(_voxelSession),
            _voxelSession is null || _observer is null ? null : new ObserveConsoleCommands(_voxelSession, _observer));
    }

    private WorldChangeResult ChangeWorld(WorldChangeRequest request) =>
        GetTree().CurrentScene is WorldEditorScene editor
            ? editor.RequestWorldChange(request) : _voxelSession!.TryChangeWorld(request);

    public override void _Ready()
    {
        Layer = 128;
        ProcessMode = ProcessModeEnum.Always;

        _panel = new PanelContainer
        {
            Visible = false,
            MouseFilter = Control.MouseFilterEnum.Stop
        };
        _panel.AnchorLeft = 0;
        _panel.AnchorRight = 1;
        _panel.AnchorTop = 1;
        _panel.AnchorBottom = 1;
        _panel.OffsetTop = -360;
        _panel.OffsetLeft = 0;
        _panel.OffsetRight = 0;
        _panel.OffsetBottom = 0;
        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.08f, 0.09f, 0.11f, 0.92f),
            ContentMarginLeft = 10,
            ContentMarginRight = 10,
            ContentMarginTop = 8,
            ContentMarginBottom = 8
        };
        _panel.AddThemeStyleboxOverride("panel", style);

        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 8);
        _output = new RichTextLabel
        {
            BbcodeEnabled = true,
            ScrollFollowing = true,
            SelectionEnabled = true,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            FocusMode = Control.FocusModeEnum.None
        };
        _output.AddThemeColorOverride("default_color", new Color(0.85f, 0.87f, 0.9f));
        _input = new LineEdit { PlaceholderText = "help" };
        _input.TextSubmitted += OnSubmit;
        box.AddChild(_output);
        box.AddChild(_input);
        _panel.AddChild(box);
        AddChild(_panel);
    }

    public override void _Process(double delta)
    {
        if (_open)
            RefreshOutput();
    }

    public override void _Input(InputEvent @event)
    {
        if (@event is not InputEventKey key || !key.Pressed || key.Echo)
            return;

        if (IsToggleKey(key))
        {
            GetViewport().SetInputAsHandled();
            SetOpen(!_open);
            return;
        }

        if (_open && key.Keycode == Key.Escape)
        {
            GetViewport().SetInputAsHandled();
            SetOpen(false);
        }
    }

    private void SetOpen(bool open)
    {
        _open = open;
        _panel.Visible = open;
        if (open)
        {
            RefreshOutput();
            Callable.From(FocusInput).CallDeferred();
        }
        else
            _input.ReleaseFocus();
    }

    private void FocusInput()
    {
        if (!_open)
            return;
        _input.GrabFocus();
        StripToggleCharacter();
        _input.CaretColumn = _input.Text.Length;
    }

    private void StripToggleCharacter()
    {
        var text = _input.Text;
        if (text.Length > 0 && text[^1] is '`' or '~')
            _input.Text = text[..^1];
    }

    /// <summary>Automation entry that types one line and submits it through the console's own input field.</summary>
    internal void SubmitLine(string text)
    {
        _input.Text = text;
        _input.EmitSignal(LineEdit.SignalName.TextSubmitted, text);
    }

    private void OnSubmit(string text)
    {
        _input.Clear();
        _dispatcher.Execute(text);
        RefreshOutput();
        _input.GrabFocus();
    }

    private void RefreshOutput()
    {
        var snapshot = _buffer.Snapshot();
        var head = snapshot.Count == 0 ? 0 : snapshot[0].Sequence;
        var tail = snapshot.Count == 0 ? 0 : snapshot[^1].Sequence;
        if (snapshot.Count == _renderedCount && head == _renderedHead && tail == _renderedTail)
            return;

        _renderedCount = snapshot.Count;
        _renderedHead = head;
        _renderedTail = tail;
        _output.Clear();
        foreach (var logEvent in snapshot)
            _output.AppendText(LogLevelText.FormatConsole(logEvent) + "\n");
    }

    private static bool IsToggleKey(InputEventKey key) =>
        key.Keycode is Key.Quoteleft or Key.Asciitilde
        || key.PhysicalKeycode is Key.Quoteleft or Key.Asciitilde
        || key.Unicode is '`' or '~';
}
