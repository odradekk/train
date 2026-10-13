using Godot;

namespace Train.Ui;

internal static class UiStyle
{
    internal static readonly Color Text = new("e6e9ec");
    internal static readonly Color Muted = new("9aa3ad");
    internal static readonly Color Accent = new("e0a22e");
    internal static readonly Color Error = new("c9423a");

    internal static Theme CreateTheme()
    {
        var normal = GD.Load<FontFile>("res://Ui/Fonts/NotoSansSC-Regular.otf");
        var bold = GD.Load<FontFile>("res://Ui/Fonts/NotoSansSC-Bold.otf");
        var theme = new Theme { DefaultFont = normal, DefaultFontSize = 14 };
        theme.SetFont("font", "Title", bold);
        theme.SetFontSize("font_size", "Title", 16);
        theme.SetTypeVariation("Title", "Label");
        foreach (var type in new[] { "Label", "Button", "OptionButton", "CheckButton", "CheckBox", "LineEdit", "ItemList", "PopupMenu" })
        {
            theme.SetFont("font", type, normal);
            theme.SetColor("font_color", type, Text);
        }
        theme.SetFont("normal_font", "RichTextLabel", normal);
        theme.SetFont("bold_font", "RichTextLabel", bold);
        theme.SetColor("font_disabled_color", "Button", Muted);
        theme.SetStylebox("panel", "PanelContainer", Box(new(0.094f, 0.11f, 0.13f, 0.90f)));
        theme.SetTypeVariation("TopBar", "PanelContainer");
        var bar = Box(new(0.094f, 0.11f, 0.13f, 0.92f));
        bar.ContentMarginTop = bar.ContentMarginBottom = 5;
        theme.SetStylebox("panel", "TopBar", bar);
        theme.SetTypeVariation("Speed", "OptionButton");
        theme.SetStylebox("normal", "Speed", Box(Accent));
        theme.SetColor("font_color", "Speed", new("1a1a1a"));
        foreach (var type in new[] { "Button", "OptionButton" })
        {
            theme.SetStylebox("normal", type, Box(new("181c21")));
            theme.SetStylebox("hover", type, Box(new("313942")));
            theme.SetStylebox("pressed", type, Box(new("403421")));
            theme.SetStylebox("disabled", type, Box(new("15191e")));
            theme.SetStylebox("focus", type, Box(Colors.Transparent, Accent));
        }
        theme.SetStylebox("normal", "LineEdit", Box(new("11161c")));
        theme.SetStylebox("focus", "LineEdit", Box(Colors.Transparent, Accent));
        return theme;
    }

    private static StyleBoxFlat Box(Color background, Color? border = null) => new()
    {
        BgColor = background, BorderColor = border ?? new Color("3b434d"),
        BorderWidthLeft = 1, BorderWidthTop = 1, BorderWidthRight = 1, BorderWidthBottom = 1,
        CornerRadiusTopLeft = 4, CornerRadiusTopRight = 4, CornerRadiusBottomLeft = 4, CornerRadiusBottomRight = 4,
        ContentMarginLeft = 12, ContentMarginRight = 12, ContentMarginTop = 8, ContentMarginBottom = 8
    };

    internal static Button Button(string name, string text, Action clicked, bool primary = false)
    {
        var button = new Button { Name = name, Text = text, CustomMinimumSize = new(0, 34) };
        if (primary)
        {
            button.AddThemeStyleboxOverride("normal", Box(Accent));
            button.AddThemeColorOverride("font_color", new("1a1a1a"));
        }
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
        body = new VBoxContainer { Name = "Body" };
        body.AddThemeConstantOverride("separation", 10);
        box.AddChild(body);
        panel.AddChild(box);
        parent.AddChild(panel);
        void Fit()
        {
            if (parent.Size.X <= 0 || parent.Size.Y <= 0) return;
            panel.Position = new(Math.Clamp(position.X, 8, Math.Max(8, parent.Size.X - panel.Size.X - 8)),
                Math.Clamp(position.Y, 8, Math.Max(8, parent.Size.Y - panel.Size.Y - 8)));
        }
        parent.Resized += Fit;
        panel.Resized += Fit;
        panel.TreeExiting += () => parent.Resized -= Fit;
        return panel;
    }
}
