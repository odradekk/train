using Godot;

namespace Train.Ui;

internal static class UiStyle
{
    internal static readonly Color Foreground = new("e6e9ec");
    internal static readonly Color Error = new("c9423a");
    internal static readonly Color Warning = new("d8962a");
    internal static readonly Color Success = new("2f9e5c");

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

    internal static Label Label(string text, bool title = false) => new()
    {
        Text = text, ThemeTypeVariation = title ? "Title" : "",
        MouseFilter = Control.MouseFilterEnum.Ignore
    };

    internal static PanelContainer Panel(Control parent, string name, string title, Vector2 position,
        float width, Action close, out VBoxContainer body)
    {
        var panel = new PanelContainer { Name = name, Position = position, CustomMinimumSize = new(width, 0),
            MouseFilter = Control.MouseFilterEnum.Stop, MouseForcePassScrollEvents = false };
        var box = new VBoxContainer { Name = "Layout" };
        box.AddThemeConstantOverride("separation", 12);
        var heading = new HBoxContainer();
        var label = Label(title, true);
        label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        heading.AddChild(label);
        heading.AddChild(Button("Close", "×", close));
        box.AddChild(heading);
        box.AddChild(new HSeparator());
        var scroll = new ScrollContainer { Name = "Content", HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto };
        body = new VBoxContainer { Name = "Body", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        body.AddThemeConstantOverride("separation", 10);
        scroll.AddChild(body);
        box.AddChild(scroll);
        panel.AddChild(box);
        GameUi? ui = null;
        var content = body;
        var queued = false;
        void Fit()
        {
            queued = false;
            if (!GodotObject.IsInstanceValid(panel) || !panel.IsVisibleInTree() || parent.Size.X <= 0 || parent.Size.Y <= 0) return;
            var area = ui?.PanelArea ?? parent.GetGlobalRect().Grow(-8);
            area.Position -= parent.GlobalPosition;
            var frameHeight = panel.GetThemeStylebox("panel").GetMinimumSize().Y +
                box.GetCombinedMinimumSize().Y - scroll.GetCombinedMinimumSize().Y;
            var top = Math.Clamp(position.Y, area.Position.Y, Math.Max(area.Position.Y, area.End.Y - frameHeight));
            scroll.CustomMinimumSize = new(0, Math.Min(content.GetCombinedMinimumSize().Y, Math.Max(0, area.End.Y - top - frameHeight)));
            panel.Size = panel.GetCombinedMinimumSize();
            panel.Position = new(Math.Clamp(position.X, area.Position.X, Math.Max(area.Position.X, area.End.X - panel.Size.X)),
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
        content.MinimumSizeChanged += QueueFit;
        panel.Ready += () =>
        {
            Node? ancestor = parent;
            while (ancestor is not null && ancestor is not GameUi) ancestor = ancestor.GetParent();
            ui = ancestor as GameUi;
            if (ui is not null) ui.PanelAreaChanged += QueueFit;
            QueueFit();
        };
        panel.TreeExiting += () =>
        {
            parent.Resized -= QueueFit;
            if (ui is not null) ui.PanelAreaChanged -= QueueFit;
        };
        parent.AddChild(panel);
        return panel;
    }
}
