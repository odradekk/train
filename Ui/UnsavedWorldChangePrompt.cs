using Train.Ui;
using Godot;
using Train.Domain.Voxels;

namespace Train;

public partial class UnsavedWorldChangePrompt : CanvasLayer
{
    private readonly Label _text;
    private readonly Dictionary<string, Button> _buttons = new(StringComparer.Ordinal);

    public event Action<WorldChangeChoice>? Chosen;
    public event Action? DraftDiscard;
    internal Button DraftDiscardButton => _buttons["draft_discard"];
    internal Button CancelButton => _buttons["cancel"];

    public UnsavedWorldChangePrompt()
    {
        // Keep confirmation above GameConsole (layer 128), including when quit is entered there.
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
        var drafts = new HBoxContainer();
        box.AddChild(drafts);
        AddAction(drafts, "draft_discard", "放弃草稿", () => DraftDiscard?.Invoke());
        Visible = false;
    }

    public void Open(WorldChangeKind kind, bool canSave, bool hasDraft = false, bool hasUnsavedChanges = true)
    {
        var reload = kind == WorldChangeKind.ReloadSaved;
        _text.Text = hasUnsavedChanges ? "当前世界有未保存的变化。" : "已提交世界已保存。";
        _text.Text += reload ? "重新加载请求时的已保存版本？" : "是否继续离开当前世界？";
        if (!canSave)
            _text.Text += "\n模拟发生故障，无法保存当前世界。可以放弃更改或取消。";
        if (hasDraft)
            _text.Text += "\n体素草稿未提交。保存不包含草稿；可明确放弃草稿，或取消离开返回继续编辑。";
        _buttons["save_then_switch"].Disabled = !canSave;
        _buttons["save_then_switch"].Text = reload ? "另存当前修改后重新加载" : "保存后继续";
        _buttons["discard"].Text = reload ? "放弃修改并重新加载" : "放弃更改";
        _buttons["save_then_switch"].Visible = hasUnsavedChanges;
        _buttons["discard"].Visible = !hasDraft;
        _buttons["draft_discard"].Visible = hasDraft;
        Visible = true;
    }

    public void ShowProgress(string text)
    {
        _text.Text = text;
        _buttons["save_then_switch"].Visible = false;
        _buttons["discard"].Visible = false;
        _buttons["draft_discard"].Visible = false;
        Visible = true;
    }

    public void Close() => Visible = false;

    internal void Press(string name) => _buttons[name].EmitSignal(BaseButton.SignalName.Pressed);

    private void AddChoice(Container parent, string name, string text, WorldChangeChoice choice) =>
        AddAction(parent, name, text, () => Chosen?.Invoke(choice));

    private void AddAction(Container parent, string name, string text, Action action)
    {
        var button = new Button { Name = name, Text = text, FocusMode = Control.FocusModeEnum.None };
        button.Pressed += () =>
        {
            if (Visible && button.Visible && !button.Disabled)
                action();
        };
        parent.AddChild(button);
        _buttons[name] = button;
    }
}
