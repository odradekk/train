using Godot;
using Train.App.Console;

namespace Train.Ui;

public partial class GameConsole
{
    private LineEdit _input = null!;
    private Label _usage = null!;
    private readonly ItemList _suggestions = new() { Name = "Suggestions", Visible = false,
        FocusMode = Control.FocusModeEnum.None, MouseFilter = Control.MouseFilterEnum.Stop };
    private ConsoleInputGuide _guide = new(null, [], 0, 0);
    private readonly List<string> _history = [];
    private int _historyIndex, _selectedSuggestion, _guidedCaret;
    private string _draft = "", _guidedText = "";

    private void BuildInput(VBoxContainer box)
    {
        _usage = UiStyle.Label("输入 help 查看可用命令；Tab 补全，↑↓ 选择建议或翻历史。");
        _usage.Name = "CurrentUsage";
        _usage.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _usage.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        box.AddChild(_usage);
        var row = new HBoxContainer();
        row.AddChild(UiStyle.Label("命令"));
        _input = new LineEdit { Name = "ConsoleInput", PlaceholderText = "help",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            // 提交后继续编辑，才能在焦点不变时连续输入命令。
            KeepEditingOnTextSubmit = true };
        _input.TextSubmitted += OnSubmit;
        _input.GuiInput += OnInputKey;
        _input.FocusExited += () => _suggestions.Hide();
        _suggestions.ItemClicked += (index, _, button) => { if (button == (long)MouseButton.Left) Complete((int)index); };
        row.AddChild(_input);
        box.AddChild(row);
    }

    private void RefreshGuide(bool showSuggestions = true)
    {
        _guidedText = _input.Text; _guidedCaret = _input.CaretColumn;
        _guide = _dispatcher.DescribeInput(_guidedText, _guidedCaret);
        _usage.Text = _guide.Usage ?? "输入 help 查看可用命令；Tab 补全，↑↓ 选择建议或翻历史。";
        _suggestions.Clear();
        foreach (var suggestion in _guide.Suggestions)
            _suggestions.AddItem(suggestion.Text + "  ·  " + suggestion.Description);
        _selectedSuggestion = 0;
        _suggestions.Visible = showSuggestions && _open && _input.HasFocus() && _input.Text.Length > 0 && _guide.Suggestions.Count > 0;
        if (_suggestions.ItemCount > 0) _suggestions.Select(0);
        Arrange(); PositionSuggestions();
    }

    private void PositionSuggestions()
    {
        if (!_suggestions.Visible) return;
        var input = _input.GetGlobalRect();
        var bottom = _usage.GlobalPosition.Y - 4;
        var height = Math.Min(168, Math.Max(32, bottom - _output.GlobalPosition.Y));
        _suggestions.Position = new(input.Position.X, bottom - height);
        _suggestions.Size = new(input.Size.X, height);
    }

    private void OnInputKey(InputEvent input)
    {
        if (input is not InputEventKey { Pressed: true } key || key.CtrlPressed || key.AltPressed) return;
        if (key.Keycode == Key.Tab && _guide.Suggestions.Count > 0)
            Complete(_selectedSuggestion);
        else if (key.Keycode is Key.Up or Key.Down)
        {
            var direction = key.Keycode == Key.Up ? -1 : 1;
            if (_suggestions.Visible)
            {
                _selectedSuggestion = (_selectedSuggestion + direction + _guide.Suggestions.Count) % _guide.Suggestions.Count;
                _suggestions.Select(_selectedSuggestion); _suggestions.EnsureCurrentIsVisible();
            }
            else
            {
                if (_historyIndex == _history.Count) _draft = _input.Text;
                _historyIndex = Math.Clamp(_historyIndex + direction, 0, _history.Count);
                _input.Text = _historyIndex == _history.Count ? _draft : _history[_historyIndex];
                _input.CaretColumn = _input.Text.Length;
                RefreshGuide(false);
            }
        }
        else return;
        _input.AcceptEvent();
    }

    private void Complete(int index)
    {
        var text = _input.Text;
        var replacement = _guide.Suggestions[index].Text;
        var suffix = text[(_guide.Start + _guide.Length)..];
        if (suffix.Length == 0) replacement += " ";
        _input.Text = text[.._guide.Start] + replacement + suffix;
        _input.CaretColumn = _guide.Start + replacement.Length + (suffix.Length > 0 && char.IsWhiteSpace(suffix[0]) ? 1 : 0);
        _input.GrabFocus();
        RefreshGuide();
    }

    internal void SubmitLine(string text) => OnSubmit(text);

    private void OnSubmit(string text)
    {
        if (!string.IsNullOrWhiteSpace(text)) _history.Add(text.Trim());
        _historyIndex = _history.Count; _draft = "";
        _input.Clear();
        _dispatcher.Execute(text);
        RefreshOutput();
        if (_open) _input.GrabFocus();
        RefreshGuide(false);
    }
}
