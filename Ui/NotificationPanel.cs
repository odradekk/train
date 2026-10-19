using Godot;
using Train.App.Notifications;

namespace Train.Ui;

public partial class NotificationPanel : Control
{
    private readonly record struct Toast(PanelContainer Panel, double Remaining);
    private readonly record struct Row(NotificationNotice Notice, BaseButton Button);
    private readonly List<Row> _rows = [];
    private readonly List<Toast> _toasts = [];
    private readonly bool[] _shown = [true, true, true, true];
    private VBoxContainer _toastsBox = null!;
    private VBoxContainer _logBody = null!;
    private PanelContainer _log = null!;
    private Guid? _session;

    internal event Action<NotificationNotice>? Chosen;

    internal void Clear()
    {
        _session = null;
        if (IsNodeReady())
        {
            foreach (var row in _rows)
            {
                row.Button.GetParent()?.RemoveChild(row.Button);
                row.Button.QueueFree();
            }
            foreach (var toast in _toasts)
            {
                toast.Panel.GetParent()?.RemoveChild(toast.Panel);
                toast.Panel.QueueFree();
            }
        }
        _rows.Clear();
        _toasts.Clear();
    }

    internal void Begin(Guid session)
    {
        if (_session == session) return;
        Clear();
        _session = session;
    }

    internal void Add(NotificationNotice notice)
    {
        if (!IsNodeReady()) return;
        var row = CreateRow(notice);
        row.Visible = _shown[(int)notice.Level];
        _rows.Add(new(notice, row));
        _logBody.AddChild(row);
        var toastRow = CreateRow(notice);
        var panel = new PanelContainer { MouseFilter = MouseFilterEnum.Stop };
        var box = new HBoxContainer();
        box.AddChild(new ColorRect { Color = ColorOf(notice.Level), CustomMinimumSize = new(4, 34),
            MouseFilter = MouseFilterEnum.Ignore });
        box.AddChild(toastRow);
        panel.AddChild(box);
        _toastsBox.AddChild(panel);
        _toasts.Add(new(panel, 6));
    }

    internal void ShowLog()
    {
        _log.Show();
        _log.MoveToFront();
    }

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
        _toastsBox = new VBoxContainer { Name = "Toasts", MouseFilter = MouseFilterEnum.Ignore };
        _toastsBox.AddThemeConstantOverride("separation", 8);
        AddChild(_toastsBox);
        _log = UiStyle.Panel(this, "NoticeLog", "通知", new(24, 68), 460, () => _log.Hide(), out _logBody);
        var filters = new HFlowContainer();
        AddFilter(filters, NotificationLevel.Information, "FilterInformation", "信息");
        AddFilter(filters, NotificationLevel.Completion, "FilterCompletion", "完成");
        AddFilter(filters, NotificationLevel.Warning, "FilterWarning", "警告");
        AddFilter(filters, NotificationLevel.Severe, "FilterSevere", "严重");
        _logBody.AddChild(filters);
        _logBody.MoveChild(filters, 0);
        _log.Hide();
    }

    public override void _Process(double delta)
    {
        // 提示按真实时间消失，不跟模拟步。
        _toastsBox.Position = new(Math.Max(8, Size.X - 480), 68);
        for (var index = _toasts.Count - 1; index >= 0; index--)
        {
            var toast = _toasts[index];
            var remaining = toast.Remaining - delta;
            if (remaining > 0) { _toasts[index] = toast with { Remaining = remaining }; continue; }
            toast.Panel.QueueFree();
            _toasts.RemoveAt(index);
        }
    }

    private void AddFilter(HFlowContainer filters, NotificationLevel level, string name, string text)
    {
        var box = new CheckBox { Name = name, Text = text, ButtonPressed = true };
        box.Toggled += shown =>
        {
            _shown[(int)level] = shown;
            foreach (var row in _rows)
                if (row.Notice.Level == level) row.Button.Visible = shown;
        };
        filters.AddChild(box);
    }

    private Button CreateRow(NotificationNotice notice)
    {
        var button = new Button { Name = "Notice", Text = notice.Text, Alignment = HorizontalAlignment.Left,
            CustomMinimumSize = new(360, 34), AutowrapMode = TextServer.AutowrapMode.WordSmart };
        button.AddThemeColorOverride("font_color", ColorOf(notice.Level));
        button.Pressed += () => Chosen?.Invoke(notice);
        return button;
    }

    private static Color ColorOf(NotificationLevel level) => level switch
    {
        NotificationLevel.Information => UiStyle.Info,
        NotificationLevel.Completion => UiStyle.Success,
        NotificationLevel.Warning => UiStyle.Warning,
        _ => UiStyle.Error
    };
}
