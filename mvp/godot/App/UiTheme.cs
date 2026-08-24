using Godot;

namespace TrainMvp.App;

public static class UiTheme
{
    public static readonly Color Paper = new(0.965f, 0.945f, 0.910f);
    public static readonly Color Ink = new(0.141f, 0.110f, 0.078f);
    public static readonly Color Accent = new(0.541f, 0.294f, 0.122f);
    public static readonly Color Muted = new(0.361f, 0.325f, 0.282f);
    public static readonly Color Card = new(1f, 0.992f, 0.973f);
    public static readonly Color Warn = new(0.992f, 0.910f, 0.910f);
    public static readonly Color Ok = new(0.906f, 0.957f, 0.918f);

    public static Theme Create()
    {
        var font = new SystemFont();
        font.FontNames = ["Microsoft YaHei UI", "Microsoft YaHei", "Segoe UI"];
        var theme = new Theme();
        theme.DefaultFont = font;
        theme.DefaultFontSize = 16;
        return theme;
    }

    public static Label MakeLabel(string text, int size = 16, bool bold = false, bool wrap = true)
    {
        var label = new Label
        {
            Text = text,
            AutowrapMode = wrap ? TextServer.AutowrapMode.WordSmart : TextServer.AutowrapMode.Off
        };
        label.AddThemeFontSizeOverride("font_size", size);
        label.AddThemeColorOverride("font_color", Ink);
        if (bold)
        {
            label.AddThemeColorOverride("font_color", Accent);
        }

        return label;
    }

    public static Button MakeButton(string text, bool primary = true)
    {
        var button = new Button
        {
            Text = text,
            ClipText = false,
            CustomMinimumSize = new Vector2(Math.Max(108, 18 + text.Length * 16), 36),
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin
        };
        button.AddThemeFontSizeOverride("font_size", 16);
        var style = new StyleBoxFlat
        {
            BgColor = primary ? Accent : new Color(0.361f, 0.325f, 0.282f),
            ContentMarginLeft = 12,
            ContentMarginRight = 12,
            ContentMarginTop = 6,
            ContentMarginBottom = 6
        };
        button.AddThemeStyleboxOverride("normal", style);
        button.AddThemeColorOverride("font_color", Colors.White);
        return button;
    }

    public static PanelContainer MakeCard()
    {
        var panel = new PanelContainer();
        var style = new StyleBoxFlat
        {
            BgColor = Card,
            BorderColor = new Color(0.847f, 0.812f, 0.761f),
            BorderWidthBottom = 1,
            BorderWidthLeft = 1,
            BorderWidthRight = 1,
            BorderWidthTop = 1,
            ContentMarginLeft = 10,
            ContentMarginRight = 10,
            ContentMarginTop = 8,
            ContentMarginBottom = 8
        };
        panel.AddThemeStyleboxOverride("panel", style);
        return panel;
    }
}
