using Godot;
using Train.App.Notifications;
using Train.Content.Contracts;
using Train.Core.Contracts;

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
    private Texture2D? _infoIcon;
    private Texture2D? _completeIcon;
    private Texture2D? _warningIcon;
    private Texture2D? _severeIcon;
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

    internal void Add(NotificationNotice notice, ICoreReadView view, IContentView content)
    {
        if (!IsNodeReady()) return;
        var message = Describe(notice, view, content);
        var row = CreateRow(notice, message);
        row.Visible = _shown[(int)notice.Level];
        _rows.Add(new(notice, row));
        _logBody.AddChild(row);
        var toastRow = CreateRow(notice, message);
        var panel = new PanelContainer { MouseFilter = MouseFilterEnum.Stop };
        var box = new HBoxContainer();
        box.AddChild(new ColorRect { Color = ColorOf(notice.Level), CustomMinimumSize = new(4, 34),
            MouseFilter = MouseFilterEnum.Ignore });
        box.AddChild(toastRow);
        panel.AddChild(box);
        _toastsBox.AddChild(panel);
        _toasts.Add(new(panel, 6));
    }

    internal Control? ToastParent { get; init; }

    internal void ShowLog()
    {
        _log.Show();
        UiStyle.BringToFront(_log);
    }

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
        _toastsBox = new VBoxContainer { Name = "Toasts", MouseFilter = MouseFilterEnum.Ignore };
        _toastsBox.AddThemeConstantOverride("separation", 8);
        (ToastParent ?? this).AddChild(_toastsBox);
        _infoIcon = GD.Load<Texture2D>("res://Ui/Symbols/notification-info.png");
        _completeIcon = GD.Load<Texture2D>("res://Ui/Symbols/notification-complete.png");
        _warningIcon = GD.Load<Texture2D>("res://Ui/Symbols/warning.png");
        _severeIcon = GD.Load<Texture2D>("res://Ui/Symbols/notification-critical.png");
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
        _toastsBox.Visible = IsVisibleInTree();
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

    internal static string Describe(NotificationNotice notice, ICoreReadView view, IContentView content)
    {
        var name = notice.Subject is { } subject ? SubjectName(subject, view, content) : null;
        if (notice.Category == NotificationCategory.RunDestroyed)
            return name is null ? notice.Text : $"{name}\n{notice.Text}";
        var verb = notice.Text.TrimEnd('。');
        if (notice.Good is { } good) verb = $"{WorldPanels.GoodName(good, content)} {verb}";
        var line = name is null || notice.Category is NotificationCategory.Encounter or NotificationCategory.Destruction
            or NotificationCategory.CombatEnded or NotificationCategory.LairCleared
            ? verb : $"{name} {verb}";
        return notice.Location is { } location ? $"{line} · {WorldPanels.LocationText(location, view, content)}" : line;
    }

    private Button CreateRow(NotificationNotice notice, string message)
    {
        var color = ColorOf(notice.Level);
        var button = new Button
        {
            Name = "Notice",
            Text = $"{LevelName(notice.Level)}  {message}",
            Icon = Icon(notice.Level),
            Alignment = HorizontalAlignment.Left,
            CustomMinimumSize = new(420, 64),
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        var background = new StyleBoxFlat
        {
            BgColor = color,
            ContentMarginLeft = 8,
            ContentMarginRight = 8,
            ContentMarginTop = 6,
            ContentMarginBottom = 6
        };
        button.AddThemeStyleboxOverride("normal", background);
        button.AddThemeStyleboxOverride("hover", background);
        button.AddThemeStyleboxOverride("pressed", background);
        button.AddThemeStyleboxOverride("focus", background);
        button.AddThemeColorOverride("font_color", Colors.White);
        button.AddThemeColorOverride("font_hover_color", Colors.White);
        button.AddThemeColorOverride("font_pressed_color", Colors.White);
        button.Pressed += () => Chosen?.Invoke(notice);
        return button;
    }

    private static string LevelName(NotificationLevel level) => level switch
    {
        NotificationLevel.Information => "信息",
        NotificationLevel.Completion => "完成",
        NotificationLevel.Warning => "警告",
        _ => "严重"
    };

    private Texture2D? Icon(NotificationLevel level) => level switch
    {
        NotificationLevel.Information => _infoIcon,
        NotificationLevel.Completion => _completeIcon,
        NotificationLevel.Warning => _warningIcon,
        _ => _severeIcon
    };

    private static string? SubjectName(WorldObjectId subject, ICoreReadView view, IContentView content) => subject switch
    {
        WorldObjectId.Train train => $"列车 {train.Id.Value}",
        WorldObjectId.Settlement settlement => content.FindSettlementLayout(settlement.Id) is
            ContentLookup<SettlementLayoutProjection>.Found layout && layout.Value.Display.Name.Length > 0
            ? layout.Value.Display.Name : $"聚居地 {settlement.Id.Value}",
        WorldObjectId.Instance instance => InstanceName(instance.Id, view),
        WorldObjectId.CreatureGroup group => $"生物群 {group.Id.Value}",
        WorldObjectId.Lair => "生物聚落",
        _ => null
    };

    private static string InstanceName(InstanceId id, ICoreReadView view)
    {
        if (view.FindInstance(id) is Lookup<InstanceView>.Found found &&
            found.Value.Details is InstanceDetails.Vehicle { Train: { } train })
            return $"列车 {train.Value}";
        return id switch
        {
            InstanceId.Vehicle vehicle => $"车辆 {vehicle.Id.Value}",
            InstanceId.Building building => $"建筑 {building.Id.Value}",
            _ => "单位"
        };
    }

    private static Color ColorOf(NotificationLevel level) => level switch
    {
        NotificationLevel.Information => UiStyle.Info,
        NotificationLevel.Completion => UiStyle.Success,
        NotificationLevel.Warning => UiStyle.Warning,
        _ => UiStyle.Error
    };
}
