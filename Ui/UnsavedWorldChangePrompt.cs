using Godot;
using Train.Domain.Voxels;

namespace Train;

public partial class UnsavedWorldChangePrompt : CanvasLayer
{
    private readonly Label _text;
    private readonly Dictionary<string, Button> _buttons = new(StringComparer.Ordinal);

    public event Action<WorldChangeChoice>? Chosen;

    public UnsavedWorldChangePrompt()
    {
        Layer = 140;
        var panel = new PanelContainer { Position = new Vector2(12, 280) };
        AddChild(panel);
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 6);
        panel.AddChild(box);
        _text = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(520, 0) };
        box.AddChild(_text);
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 6);
        box.AddChild(row);
        AddChoice(row, "save_then_switch", "保存后继续", WorldChangeChoice.SaveThenSwitch);
        AddChoice(row, "discard", "放弃更改", WorldChangeChoice.Discard);
        AddChoice(row, "cancel", "取消", WorldChangeChoice.Cancel);
        Visible = false;
    }

    public void Open(WorldChangeKind kind)
    {
        var reload = kind == WorldChangeKind.ReloadSaved;
        _text.Text = reload ? "当前世界有未保存的变化。重新加载请求时的已保存版本？" : "当前世界有未保存的变化。是否继续离开当前世界？";
        _buttons["save_then_switch"].Text = reload ? "另存当前修改后重新加载" : "保存后继续";
        _buttons["discard"].Text = reload ? "放弃修改并重新加载" : "放弃更改";
        _buttons["save_then_switch"].Visible = true;
        _buttons["discard"].Visible = true;
        Visible = true;
    }

    public void ShowProgress(string text)
    {
        _text.Text = text;
        _buttons["save_then_switch"].Visible = false;
        _buttons["discard"].Visible = false;
        Visible = true;
    }

    public void Close() => Visible = false;

    internal void Press(string name) => _buttons[name].EmitSignal(BaseButton.SignalName.Pressed);

    private void AddChoice(Container parent, string name, string text, WorldChangeChoice choice)
    {
        var button = new Button { Name = name, Text = text, FocusMode = Control.FocusModeEnum.None };
        button.Pressed += () =>
        {
            if (Visible)
                Chosen?.Invoke(choice);
        };
        parent.AddChild(button);
        _buttons[name] = button;
    }
}
