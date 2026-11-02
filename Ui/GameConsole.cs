using Godot;
using Train.App.Console;
using Train.App.Logging;

namespace Train.Ui;

// Decision note: 2026-10-31-developer-dock
public partial class GameConsole : CanvasLayer
{
    private RingBufferLogSink _buffer = null!;
    private ConsoleCommandDispatcher _dispatcher = null!;
    private Func<bool> _canOpen = null!;
    private PanelContainer _panel = null!;
    private RichTextLabel _output = null!;
    private Label _empty = null!;
    private OptionButton _filter = null!;
    private bool _open, _dragging;
    private float _requestedHeight = 340, _dragHeight, _dragY;
    private long _renderedHead, _renderedTail;
    public bool IsOpen => _open;
    internal IReadOnlyList<LogEvent> LogEvents => _buffer.Snapshot();
    internal event Action<float>? HeightChanged;
    internal Action<string> CopyText { private get; set; } = DisplayServer.ClipboardSet;

    internal void Initialize(RingBufferLogSink buffer, ConsoleCommandDispatcher dispatcher, Func<bool> canOpen)
    { _buffer = buffer; _dispatcher = dispatcher; _canOpen = canOpen; }

    public override void _Ready()
    {
        Layer = 128;
        ProcessMode = ProcessModeEnum.Always;
        _panel = new PanelContainer { Name = "DeveloperDock", Visible = false,
            MouseFilter = Control.MouseFilterEnum.Stop, MouseForcePassScrollEvents = false };
        _panel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomWide);
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 6);
        var grip = new Control { Name = "ResizeGrip", CustomMinimumSize = new(0, 8),
            MouseDefaultCursorShape = Control.CursorShape.Vsize, TooltipText = "拖动调整开发工具底栏的高度" };
        grip.GuiInput += input =>
        {
            if (input is not InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } button) return;
            _dragging = true; _dragHeight = _panel.Size.Y; _dragY = button.GlobalPosition.Y;
            grip.AcceptEvent();
        };
        box.AddChild(grip);
        var heading = new HFlowContainer();
        var title = UiStyle.Label("控制台", true);
        title.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        heading.AddChild(title);
        heading.AddChild(UiStyle.Label("输出级别"));
        _filter = new OptionButton { Name = "OutputLevel", CustomMinimumSize = new(160, 34), TooltipText = "仅筛选显示，不改变磁盘日志" };
        foreach (var text in new[] { "全部", "警告和错误", "信息", "调试" }) _filter.AddItem(text);
        _filter.ItemSelected += _ => { ResetOutput(); RefreshOutput(); };
        heading.AddChild(_filter);
        heading.AddChild(UiStyle.Button("CopyAll", "复制全部", () => CopyText(_output.GetParsedText())));
        heading.AddChild(UiStyle.Button("ClearOutput", "清空", () => { _buffer.Clear(); ResetOutput(); RefreshOutput(); }));
        var close = UiStyle.Button("CloseDock", "×", () => SetOpen(false));
        close.TooltipText = "关闭开发工具底栏（`）";
        heading.AddChild(close);
        box.AddChild(heading);
        _output = new RichTextLabel { Name = "ConsoleOutput", BbcodeEnabled = true, ScrollFollowing = true,
            SelectionEnabled = true, CustomMinimumSize = new(0, 64), SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            FocusMode = Control.FocusModeEnum.All };
        box.AddChild(_output);
        _empty = UiStyle.Label("没有符合筛选条件的输出。");
        box.AddChild(_empty);
        BuildInput(box);
        _panel.AddChild(box);
        AddChild(_panel);
        AddChild(_suggestions);
        GetViewport().SizeChanged += Arrange;
        Arrange();
    }

    public override void _ExitTree() => GetViewport().SizeChanged -= Arrange;

    public override void _Process(double delta)
    {
        if (!_open) return;
        if (!_canOpen()) { SetOpen(false); return; }
        RefreshOutput();
        if (_guidedText != _input.Text || _guidedCaret != _input.CaretColumn) RefreshGuide();
        PositionSuggestions();
    }

    public override void _Input(InputEvent input)
    {
        if (_dragging && input is InputEventMouseMotion motion)
        { _requestedHeight = _dragHeight + _dragY - motion.Position.Y; Arrange(); GetViewport().SetInputAsHandled(); return; }
        if (_dragging && input is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false })
        { _dragging = false; GetViewport().SetInputAsHandled(); return; }
        if (input is not InputEventKey { Pressed: true, Echo: false } key) return;
        if (IsToggleKey(key) && _canOpen())
        { SetOpen(!_open); GetViewport().SetInputAsHandled(); }
        else if (_open && key.Keycode == Key.Escape)
        {
            if (_suggestions.Visible) _suggestions.Hide(); else SetOpen(false);
            GetViewport().SetInputAsHandled();
        }
    }

    internal void Open() => SetOpen(true);

    private void SetOpen(bool open)
    {
        open &= _canOpen();
        _open = open;
        _panel.Visible = open;
        if (open)
        {
            Arrange(); RefreshOutput();
            Callable.From(() => { if (_open) { _input.GrabFocus(); _input.CaretColumn = _input.Text.Length; RefreshGuide(false); } }).CallDeferred();
        }
        else
        {
            _dragging = false; _suggestions.Hide(); _input.ReleaseFocus();
            HeightChanged?.Invoke(0);
        }
    }

    private void Arrange()
    {
        if (!IsNodeReady()) return;
        // 底栏至少容得下输入与工具按钮，外壳保留四成高度供地图和侧栏使用。
        var minimum = _panel.GetCombinedMinimumSize().Y;
        var height = Math.Clamp(_requestedHeight, minimum, Math.Max(minimum, GetViewport().GetVisibleRect().Size.Y * .6f));
        _panel.OffsetTop = -height;
        _panel.OffsetBottom = 0;
        if (_open) HeightChanged?.Invoke(height);
    }

    private bool Includes(LogEvent item) => _filter.Selected switch
    { 1 => item.Level >= LogLevel.Warning, 2 => item.Level == LogLevel.Info, 3 => item.Level == LogLevel.Debug, _ => true };

    private void ResetOutput()
    { _output.Clear(); _renderedHead = 0; _renderedTail = 0; }

    private void RefreshOutput()
    {
        var snapshot = _buffer.Snapshot();
        if (snapshot.Count == 0)
        { ResetOutput(); _empty.Show(); return; }
        var head = snapshot[0].Sequence;
        var tail = snapshot[^1].Sequence;
        if (head != _renderedHead || tail < _renderedTail)
        {
            // 旧行移出环形缓冲时，选区保留到用户取消选择，再同步这次裁剪。
            if (_output.GetSelectionFrom() >= 0) return;
            ResetOutput();
        }
        foreach (var item in snapshot.Where(item => item.Sequence > _renderedTail && Includes(item)))
            _output.AppendText(LogLevelText.FormatConsole(item) + "\n");
        _renderedHead = head; _renderedTail = tail;
        _empty.Visible = _output.GetTotalCharacterCount() == 0;
    }

    private static bool IsToggleKey(InputEventKey key) => key.Keycode is Key.Quoteleft or Key.Asciitilde
        || key.PhysicalKeycode is Key.Quoteleft or Key.Asciitilde || key.Unicode is '`' or '~';
}
