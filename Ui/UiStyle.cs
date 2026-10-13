using Godot;

namespace Train.Ui;

internal static class UiStyle
{
    internal static readonly Color Text = new("e6e9ec");
    internal static readonly Color Muted = new("9aa3ad");
    internal static readonly Color Accent = new("e0a22e");
    internal static readonly Color Error = new("c9423a");

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
