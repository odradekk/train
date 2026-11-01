using Godot;
using Train.Core.Contracts;

namespace Train.Ui;

internal static class UiStyle
{
    internal static readonly Color Foreground = new("e6e9ec");
    internal static readonly Color Error = new("c9423a");
    internal static readonly Color Warning = new("d8962a");
    internal static readonly Color Success = new("2f9e5c");
    internal static readonly Color Info = new("7f8fa3");
    internal static readonly Color Servicing = new("2aa3b3");
    internal static readonly Color RelationOwned = new("2b77d0");
    internal static readonly Color RelationNeutral = new("7f8893");

    internal static Color TrainActivityColor(TrainActivity activity) => activity switch
    {
        TrainActivity.Running => Success,
        TrainActivity.Servicing => Servicing,
        TrainActivity.Stopped or TrainActivity.Waiting or TrainActivity.Retreating => Warning,
        _ => Error
    };

    private static readonly Dictionary<TrainActivity, Texture2D> Symbols = [];

    internal static Texture2D TrainSymbol(TrainActivity activity)
    {
        if (activity == TrainActivity.Stopped) activity = TrainActivity.Waiting;
        if (Symbols.TryGetValue(activity, out var symbol)) return symbol;
        var path = activity switch
        {
            TrainActivity.Running => "res://Ui/Symbols/train-running.png",
            TrainActivity.Servicing => "res://Ui/Symbols/train-servicing.png",
            TrainActivity.Waiting => "res://Ui/Symbols/train-waiting.png",
            TrainActivity.NoRoute => "res://Ui/Symbols/warning.png",
            TrainActivity.Combat => "res://Ui/Symbols/train-combat.png",
            TrainActivity.Retreating => "res://Ui/Symbols/train-retreating.png",
            _ => "res://Ui/Symbols/warning.png"
        };
        Symbols[activity] = symbol = GD.Load<Texture2D>(path);
        return symbol;
    }

    internal static Color RelationColor(SettlementRelation relation) => relation switch
    {
        SettlementRelation.PlayerOwned => RelationOwned,
        SettlementRelation.Hostile => Error,
        _ => RelationNeutral
    };

    private static FontVariation? _regularNumbers, _boldNumbers;
    internal static FontVariation NumericFont(bool bold = false) => bold
        ? _boldNumbers ??= CreateNumericFont("res://Ui/Fonts/NotoSansSC-Bold.otf")
        : _regularNumbers ??= CreateNumericFont("res://Ui/Fonts/NotoSansSC-Regular.otf");

    private static FontVariation CreateNumericFont(string path) => new()
    {
        BaseFont = GD.Load<Font>(path),
        OpentypeFeatures = new() { ["tnum"] = 1 }
    };

    internal static Button Button(string name, string text, Action clicked, bool primary = false)
    {
        var button = new Button { Name = name, Text = text, CustomMinimumSize = new(0, 34),
            ThemeTypeVariation = primary ? "PrimaryButton" : "" };
        button.Pressed += clicked;
        return button;
    }

    internal static void CloseOnEscape(Window window, InputEvent input)
    {
        if (!window.Visible || input is not InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape }) return;
        window.Hide(); window.SetInputAsHandled();
    }

    internal static Label Label(string text, bool title = false) => new()
    {
        Text = text, ThemeTypeVariation = title ? "Title" : "",
        MouseFilter = Control.MouseFilterEnum.Ignore
    };

    internal static PanelContainer Panel(Control parent, string name, string title, Vector2 position,
        float width, Action close, out VBoxContainer body, bool centered = false, bool fill = false)
    {
        var panel = new PanelContainer { Name = name, Position = position, CustomMinimumSize = new(width, 0),
            MouseFilter = Control.MouseFilterEnum.Stop, MouseForcePassScrollEvents = false };
        var box = new VBoxContainer { Name = "Layout" };
        box.AddThemeConstantOverride("separation", 12);
        var heading = new HBoxContainer { Name = "Heading" };
        var label = Label(title, true);
        label.Name = "Title";
        label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        heading.AddChild(label);
        heading.AddChild(Button("Close", "×", close));
        box.AddChild(heading);
        box.AddChild(new HSeparator());
        var scroll = new ScrollContainer { Name = "Content", HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto };
        body = new VBoxContainer { Name = "Body", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        body.AddThemeConstantOverride("separation", 10);
        scroll.AddChild(body);
        box.AddChild(scroll);
        panel.AddChild(box);
        GameUi? ui = null;
        var content = body;
        var queued = false;
        var subscribed = true;
        var shown = panel.Visible;
        void Fit()
        {
            queued = false;
            if (!GodotObject.IsInstanceValid(panel) || !panel.IsVisibleInTree() || parent.Size.X <= 0 || parent.Size.Y <= 0) return;
            if (GameShell.DockArea(panel) is { } dock)
            {
                panel.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
                panel.CustomMinimumSize = Vector2.Zero;
                label.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
                label.ClipText = true;
                scroll.HorizontalScrollMode = ScrollContainer.ScrollMode.Auto;
                var frame = panel.GetThemeStylebox("panel").GetMinimumSize().Y +
                    box.GetCombinedMinimumSize().Y - scroll.GetCombinedMinimumSize().Y;
                // 容器按整像素测量；向下取整避免半像素高度让最小尺寸反复增减。
                scroll.CustomMinimumSize = new(0, Math.Max(0, Mathf.Floor(dock.Size.Y - frame)));
                panel.Position = dock.Position - panel.GetParent<Control>().GlobalPosition;
                panel.Size = dock.Size;
                return;
            }
            panel.CustomMinimumSize = new(width, 0);
            scroll.HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;
            label.ClipText = false;
            label.TextOverrunBehavior = TextServer.OverrunBehavior.NoTrimming;
            var area = ui?.PanelArea ?? parent.GetGlobalRect().Grow(-8);
            area.Position -= parent.GlobalPosition;
            var frameHeight = panel.GetThemeStylebox("panel").GetMinimumSize().Y +
                box.GetCombinedMinimumSize().Y - scroll.GetCombinedMinimumSize().Y;
            var contentHeight = content.GetCombinedMinimumSize().Y;
            var preferred = centered ? (parent.Size - new Vector2(panel.GetCombinedMinimumSize().X,
                frameHeight + contentHeight)) / 2 : position;
            var top = Math.Clamp(preferred.Y, area.Position.Y, Math.Max(area.Position.Y, area.End.Y - frameHeight));
            var room = Math.Max(0, area.End.Y - top - frameHeight);
            scroll.CustomMinimumSize = new(0, fill ? room : Math.Min(contentHeight, room));
            panel.Size = panel.GetCombinedMinimumSize();
            panel.Position = new(Math.Clamp(preferred.X, area.Position.X, Math.Max(area.Position.X, area.End.X - panel.Size.X)),
                top);
        }
        void QueueFit()
        {
            if (queued || !panel.IsInsideTree() || !panel.IsVisibleInTree()) return;
            // 绑定多条规则时合并布局，避免逐个控件重新测量整个面板。
            queued = true;
            Callable.From(Fit).CallDeferred();
        }
        parent.Resized += QueueFit;
        panel.Resized += QueueFit;
        panel.VisibilityChanged += QueueFit;
        panel.VisibilityChanged += () =>
        {
            // 只在面板自己被打开时移到最前；祖先显隐变化不改变面板之间的先后。
            if (panel.Visible && !shown) BringToFront(panel);
            shown = panel.Visible;
        };
        panel.GuiInput += input => { if (input is InputEventMouseButton { Pressed: true }) BringToFront(panel); };
        content.MinimumSizeChanged += QueueFit;
        panel.Ready += () =>
        {
            ui = FindGameUi(parent);
            if (ui is not null) ui.PanelAreaChanged += QueueFit;
            QueueFit();
            // 按需创建的面板（例如对象详情）加入时就是打开的；启动时创建后立即隐藏的面板不移动。
            shown = panel.Visible;
            Callable.From(() => { if (GodotObject.IsInstanceValid(panel) && panel.Visible) BringToFront(panel); }).CallDeferred();
        };
        // Reparent 也会退出场景树；停靠后仍须继续响应外壳的尺寸变化。
        panel.TreeExiting += () => Callable.From(() =>
        {
            if (!subscribed || GodotObject.IsInstanceValid(panel) && panel.IsInsideTree()) return;
            subscribed = false;
            if (GodotObject.IsInstanceValid(parent)) parent.Resized -= QueueFit;
            if (ui is not null && GodotObject.IsInstanceValid(ui)) ui.PanelAreaChanged -= QueueFit;
        }).CallDeferred();
        parent.AddChild(panel);
        return panel;
    }

    internal static void BringToFront(Control control)
    {
        if (!GodotObject.IsInstanceValid(control) || !control.IsInsideTree()) return;
        if (GameShell.DockArea(control) is not null) return;
        control.MoveToFront();
        // 地图层的业务面板分属不同的所有者控件，所有者也要移到地图层最前才能盖住别的面板。
        // 只在地图层内移动，所以 Root 下的通知提示、设置和错误等界面仍在业务面板之上。
        if (FindGameUi(control)?.World is not { } world || !world.IsAncestorOf(control)) return;
        for (var owner = control.GetParent<Control>(); owner != world; owner = owner.GetParent<Control>()) owner.MoveToFront();
    }

    private static GameUi? FindGameUi(Node node)
    {
        Node? ancestor = node;
        while (ancestor is not null && ancestor is not GameUi) ancestor = ancestor.GetParent();
        return ancestor as GameUi;
    }
}
