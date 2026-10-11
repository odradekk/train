// 属于 Godot 界面模块。
// 提供控制台输入、开关与日志显示，命令动作由宿主注入的基础分发器处理。

using Godot;
using Train.App.Console;
using Train.App.Logging;

namespace Train.Ui;

/// <summary>将 Godot 文本输入与内存日志缓冲连接到基础控制台分发器的覆盖界面。</summary>
/// <remarks>宿主须在加入场景树前调用 Initialize。界面不持有世界会话、观察器或内容加载器，也不决定退出和重载流程；核心模拟暂停不影响控制台输入。</remarks>
/// <example>输入 help 显示基础命令列表；分发契约见 <c>ConsoleCommandDispatchTests.Commands_echo_through_logger_as_console_category</c>。</example>
public partial class GameConsole : CanvasLayer
{
    private RingBufferLogSink _buffer = null!;
    private ConsoleCommandDispatcher _dispatcher = null!;
    private PanelContainer _panel = null!;
    private RichTextLabel _output = null!;
    private LineEdit _input = null!;
    /// <summary>获取控制台面板是否打开；关闭面板不会停止日志收集。</summary>
    public bool IsOpen => _open;

    private bool _open;
    private int _renderedCount = -1;
    private long _renderedHead;
    private long _renderedTail;

    /// <summary>连接显示缓冲与命令分发器；宿主负责它们的创建、回调和释放。</summary>
    /// <param name="buffer">非 null 的控制台日志缓冲；应与分发器日志及 clear 回调使用同一缓冲。</param>
    /// <param name="dispatcher">非 null 的基础分发器；宿主已绑定退出、清屏、重载和构建可用性。</param>
    /// <exception cref="ArgumentNullException">任一参数为 null。</exception>
    /// <remarks>在加入场景树前调用；此方法不创建控件，也不修改宿主的日志过滤等级。</remarks>
    public void Initialize(RingBufferLogSink buffer, ConsoleCommandDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(dispatcher);
        _buffer = buffer;
        _dispatcher = dispatcher;
    }

    /// <summary>创建底部日志面板与文本输入框，绑定提交事件；初始状态为关闭。</summary>
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
        _input = new LineEdit
        {
            PlaceholderText = "help",
            // Godot 默认在提交后退出编辑模式；保持编辑，才能在焦点不变时连续输入命令。
            KeepEditingOnTextSubmit = true
        };
        _input.TextSubmitted += OnSubmit;
        box.AddChild(_output);
        box.AddChild(_input);
        _panel.AddChild(box);
        AddChild(_panel);
    }

    /// <summary>面板打开时按当前日志快照刷新显示；关闭时不更新控件。</summary>
    /// <param name="delta">Godot 提供的帧间隔，单位为秒；此界面不使用它推进状态。</param>
    public override void _Process(double delta)
    {
        if (_open)
            RefreshOutput();
    }

    /// <summary>响应反引号或波浪号开关，以及面板打开时的 Escape 关闭，并阻止场景继续处理这些按键。</summary>
    /// <param name="event">Godot 输入事件；只处理按下且非重复的键盘事件。</param>
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

    /// <summary>切换面板可见性和输入焦点；打开时先刷新日志，再延迟取得焦点。</summary>
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

    /// <summary>面板仍打开时聚焦输入框，清除可能随开关按键进入的字符，并将光标移到末尾。</summary>
    private void FocusInput()
    {
        if (!_open)
            return;
        _input.GrabFocus();
        StripToggleCharacter();
        _input.CaretColumn = _input.Text.Length;
    }

    /// <summary>去除输入末尾的一枚反引号或波浪号，避免打开控制台的按键进入下一条命令。</summary>
    private void StripToggleCharacter()
    {
        var text = _input.Text;
        if (text.Length > 0 && text[^1] is '`' or '~')
            _input.Text = text[..^1];
    }

    /// <summary>自动化入口：经输入框自身的提交信号执行一行命令，不绕过界面的提交处理。</summary>
    /// <param name="text">要输入并提交的一行文本；控件须已在 Ready 中创建。</param>
    internal void SubmitLine(string text)
    {
        _input.Text = text;
        _input.EmitSignal(LineEdit.SignalName.TextSubmitted, text);
    }

    /// <summary>清空输入框，执行基础命令，刷新日志并重新聚焦；分发异常原样传播。</summary>
    private void OnSubmit(string text)
    {
        _input.Clear();
        _dispatcher.Execute(text);
        RefreshOutput();
        _input.GrabFocus();
    }

    /// <summary>日志数量或首尾序号变化时重绘快照，使缓冲覆盖和清屏都能更新显示。</summary>
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

    /// <summary>按逻辑键、物理键或 Unicode 字符识别反引号与波浪号开关键。</summary>
    private static bool IsToggleKey(InputEventKey key) =>
        key.Keycode is Key.Quoteleft or Key.Asciitilde
        || key.PhysicalKeycode is Key.Quoteleft or Key.Asciitilde
        || key.Unicode is '`' or '~';
}
