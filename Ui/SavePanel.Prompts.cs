using Godot;
using Train.App.Saves;

namespace Train.Ui;

public partial class SavePanel
{
    private readonly Control _promptMask = new() { Name = "PromptMask", Visible = false, MouseForcePassScrollEvents = false };
    private readonly PanelContainer _prompt = new() { Name = "Prompt" };
    private readonly Label _promptTitle = new() { Name = "PromptTitle", ThemeTypeVariation = "Title" };
    private readonly Label _promptMessage = new() { Name = "PromptMessage", AutowrapMode = TextServer.AutowrapMode.WordSmart };
    private readonly LineEdit _promptName = new() { Name = "PromptName" };
    private Button _confirm = null!, _discard = null!, _cancelPrompt = null!;
    private Action? _confirmation;
    private PromptKind _promptKind;
    private LeavePrompt? _shownLeave;
    private Control? _beforePrompt;

    private enum PromptKind { None, Rename, Delete, Overwrite, Leave, Missing }

    private void BuildPrompt()
    {
        _promptMask.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(_promptMask);
        var shade = new ColorRect { Color = new(0, 0, 0, .6f) };
        shade.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _promptMask.AddChild(shade);
        _promptMask.AddChild(_prompt);
        var margin = new MarginContainer();
        foreach (var side in new[] { "left", "top", "right", "bottom" }) margin.AddThemeConstantOverride("margin_" + side, 24);
        _prompt.AddChild(margin);
        var layout = new VBoxContainer();
        layout.AddThemeConstantOverride("separation", 16);
        margin.AddChild(layout);
        layout.AddChild(_promptTitle);
        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, FollowFocus = true };
        _promptMessage.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        scroll.AddChild(_promptMessage);
        layout.AddChild(scroll);
        layout.AddChild(_promptName);
        var actions = new HFlowContainer { Name = "PromptActions" };
        layout.AddChild(actions);
        _confirm = UiStyle.Button("Confirm", "确认", () => { var action = _confirmation; ClosePrompt(); action?.Invoke(); }, true);
        _discard = UiStyle.Button("Discard", "不保存", () => { ClosePrompt(); _service!.ChooseLeave(LeaveChoice.Discard); });
        _cancelPrompt = UiStyle.Button("Cancel", "取消", CancelPrompt);
        foreach (var button in new[] { _confirm, _discard, _cancelPrompt }) actions.AddChild(button);
        _promptName.TextSubmitted += _ => { if (!_confirm.Disabled) _confirm.EmitSignal(BaseButton.SignalName.Pressed); };
    }

    private void ShowPrompt(PromptKind kind, string title, string message, string confirm, Action action)
    {
        if (!Visible) Open();
        if (!_promptMask.Visible) _beforePrompt = GetViewport().GuiGetFocusOwner();
        _promptKind = kind;
        _promptTitle.Text = title; _promptMessage.Text = message;
        _confirmation = action;
        _confirm.Text = confirm; _confirm.Visible = true; _confirm.Disabled = false;
        _discard.Visible = false; _discard.Disabled = false;
        _promptName.Visible = kind == PromptKind.Rename;
        _panel.FocusBehaviorRecursive = FocusBehaviorRecursiveEnum.Disabled;
        _promptMask.Show(); _promptMask.MoveToFront();
        ArrangePrompt();
        if (kind == PromptKind.Rename) _promptName.GrabFocus();
        else _cancelPrompt.GrabFocus();
    }

    private void ClosePrompt()
    {
        _promptMask.Hide(); _promptKind = PromptKind.None;
        _panel.FocusBehaviorRecursive = FocusBehaviorRecursiveEnum.Inherited;
        if (Visible && GodotObject.IsInstanceValid(_beforePrompt) && _beforePrompt!.IsVisibleInTree()) _beforePrompt.GrabFocus();
        _beforePrompt = null;
    }

    private void CancelPrompt()
    {
        if (_promptKind == PromptKind.Leave) _service!.ChooseLeave(LeaveChoice.Cancel);
        else if (_promptKind == PromptKind.Missing) _service!.Cancel();
        ClosePrompt();
    }

    private void UpdatePrompts()
    {
        if (_service!.Leaving is { } leaving)
        {
            if (_promptKind != PromptKind.Leave || _shownLeave != leaving)
            {
                _shownLeave = leaving;
                var operation = leaving.Action switch { LeaveAction.Load => "读取", LeaveAction.Menu => "返回主菜单", LeaveAction.Quit => "退出", _ => "继续" };
                ShowPrompt(PromptKind.Leave, "未保存的更改", leaving.Waiting ? "正在等待文件操作完成…" :
                    leaving.CanSave ? "当前有未保存的进度。请先选择如何处理。" : "当前对局无法保存。仍要离开吗？",
                    "保存后" + operation, () => _service.ChooseLeave(LeaveChoice.Save));
                _confirm.Visible = leaving.CanSave || leaving.Waiting;
                _confirm.Disabled = leaving.Waiting;
                _discard.Visible = true; _discard.Disabled = leaving.Waiting;
                _discard.Text = "不保存，直接" + operation;
            }
            return;
        }
        if (_service.Missing is { } missing)
        {
            if (_promptKind != PromptKind.Missing)
                ShowPrompt(PromptKind.Missing, "缺失内容", string.Join("\n", missing.Details), "仍然读取", () => _service.ConfirmMissing());
            return;
        }
        if (_promptKind is PromptKind.Leave or PromptKind.Missing) ClosePrompt();
    }

    private void RenameSelected()
    {
        if (_selected is not { } entry) return;
        _service!.CancelDetails();
        _promptName.Text = entry.Header.Name;
        ShowPrompt(PromptKind.Rename, "重命名存档", "手动存档和同一对局的自动存档会一起改名。", "重命名",
            () => ShowFailure(_service.Rename(entry, _promptName.Text)));
        _promptName.SelectAll();
    }

    private void DeleteSelected()
    {
        if (_selected is not { } entry) return;
        _service!.CancelDetails();
        ShowPrompt(PromptKind.Delete, "删除存档", $"永久删除「{entry.Header.Name}」这份{(entry.Automatic ? "自动" : "")}存档？此操作无法撤销。",
            "删除存档", () => ShowFailure(_service.Delete(entry)));
    }

    private void ExportSelected()
    {
        if (_selected is null) return;
        _service!.CancelDetails();
        _exportEntry = _selected;
        _export.CurrentFile = "存档.json";
        _export.PopupCentered(new(800, 560));
    }

    private void ExportTo(string path)
    {
        if (_exportEntry is not { } entry) return;
        var failure = _service!.Export(entry, path);
        if (failure?.Code == "overwrite-confirmation")
            ShowPrompt(PromptKind.Overwrite, "覆盖文件", "目标文件已存在。确认覆盖？", "覆盖文件",
                () => ShowFailure(_service.Export(entry, path, true)));
        else ShowFailure(failure);
    }

    private void ArrangePrompt()
    {
        _prompt.Size = new(Math.Min(740, Math.Max(0, Size.X - 48)), Math.Min(420, Math.Max(0, Size.Y - 48)));
        _prompt.Position = (Size - _prompt.Size) / 2;
    }
}
