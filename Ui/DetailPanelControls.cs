using Godot;
using Train.Content.Contracts;
using Train.Core.Contracts;

namespace Train.Ui;

internal readonly record struct DetailAction(string Name, string Text, bool Primary, Action Press);

internal sealed class DetailPanelControls
{
    private readonly Label _title;
    private readonly Label _text;
    private readonly VBoxContainer _body;
    private readonly List<Button> _buttons = [];
    private readonly Func<ICoreReadView, IContentView, string> _describe;
    private readonly Func<ICoreReadView, List<DetailAction>> _actions;
    internal PanelTarget Target { get; }
    internal PanelContainer Panel { get; }

    internal DetailPanelControls(PanelContainer panel, VBoxContainer body, PanelTarget target,
        Func<ICoreReadView, IContentView, string> describe,
        Func<ICoreReadView, List<DetailAction>> actions)
    {
        Target = target; _describe = describe; _actions = actions;
        Panel = panel;
        _body = body; _title = Panel.GetNode<Label>("Layout/Heading/Title");
        var scroll = new ScrollContainer { CustomMinimumSize = new(390, 340),
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, MouseForcePassScrollEvents = false };
        _text = UiStyle.Label("");
        _text.Name = "Description";
        _text.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _text.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        scroll.AddChild(_text); body.AddChild(scroll);
    }

    internal void BuildActions(ICoreReadView view) => ReplaceActions(_actions(view));

    internal void Refresh(ICoreReadView view, IContentView content, string title, bool valid, bool busy)
    {
        _text.Text = valid ? _describe(view, content) : "已不存在\n\n对象或所属对局已失效。请从当前列表重新打开。";
        if (valid)
        {
            _title.Text = title;
            var actions = _actions(view);
            if (!SameActions(_buttons, actions)) ReplaceActions(actions);
        }
        foreach (var button in _buttons) button.Disabled = !valid || busy;
    }

    private static bool SameActions(List<Button> buttons, List<DetailAction> actions)
    {
        if (buttons.Count != actions.Count) return false;
        for (var index = 0; index < buttons.Count; index++)
        {
            var button = buttons[index];
            // 同名按钮加入树后，引擎会改掉节点名；按请求时记下的名字比较，避免每帧重建。
            if (!GodotObject.IsInstanceValid(button) || button.GetMeta("ActionName").AsString() != actions[index].Name ||
                button.Text != actions[index].Text) return false;
        }
        return true;
    }

    private void ReplaceActions(List<DetailAction> actions)
    {
        foreach (var button in _buttons)
        {
            _body.RemoveChild(button);
            button.QueueFree();
        }
        _buttons.Clear();
        foreach (var action in actions)
        {
            var button = UiStyle.Button(action.Name, action.Text, action.Press, action.Primary);
            button.SetMeta("ActionName", action.Name);
            _body.AddChild(button);
            _buttons.Add(button);
        }
    }
}
