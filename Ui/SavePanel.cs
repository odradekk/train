using Godot;
using Train.App.Saves;

namespace Train.Ui;

public partial class SavePanel : Window
{
    private SaveService? _service;
    private readonly Label _status = new();
    private readonly Label _message = new();
    private readonly Label _listMessage = new()
    {
        AutowrapMode = TextServer.AutowrapMode.WordSmart,
        MaxLinesVisible = 2,
        TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
        Visible = false
    };
    private readonly ItemList _list = new() { CustomMinimumSize = new Vector2(620, 220) };
    private readonly List<Button> _operations = [];
    private readonly List<Button> _saveButtons = [];
    private readonly AcceptDialog _nameDialog = new() { Title = "另存为" };
    private readonly LineEdit _name = new() { PlaceholderText = "存档名称", CustomMinimumSize = new Vector2(320, 40) };
    private readonly FileDialog _import = new() { Title = "导入存档", Access = FileDialog.AccessEnum.Filesystem, FileMode = FileDialog.FileModeEnum.OpenFile };
    private readonly FileDialog _export = new() { Title = "导出存档", Access = FileDialog.AccessEnum.Filesystem, FileMode = FileDialog.FileModeEnum.SaveFile };
    private readonly ConfirmationDialog _overwrite = new() { Title = "覆盖文件", DialogText = "目标文件已存在。确认覆盖？" };
    private readonly ConfirmationDialog _missing = new() { Title = "缺失内容", OkButtonText = "确认继续", CancelButtonText = "取消" };
    private readonly ConfirmationDialog _leave = new() { Title = "未保存的更改", OkButtonText = "保存", CancelButtonText = "取消" };
    private Button _discard = null!;
    private Button _cancel = null!;
    private HBoxContainer _current = null!;
    private IReadOnlyList<SaveEntry> _entries = [];
    private string? _exportPath;

    public override void _Ready()
    {
        Title = "存档";
        Size = new Vector2I(680, 460);
        Transient = true;
        Exclusive = true;
        Visible = false;
        CloseRequested += Hide;
        var margin = new MarginContainer();
        margin.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        foreach (var side in new[] { "left", "top", "right", "bottom" })
            margin.AddThemeConstantOverride("margin_" + side, 16);
        AddChild(margin);
        var column = new VBoxContainer();
        margin.AddChild(column);
        column.AddChild(_status);
        column.AddChild(_message);
        _message.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        column.AddChild(_listMessage);
        column.AddChild(_list);
        var files = new HBoxContainer();
        column.AddChild(files);
        AddButton(files, "读取选中存档", () =>
        {
            var selected = _list.GetSelectedItems();
            if (selected.Length > 0) ShowFailure(_service!.Load(_entries[selected[0]]));
        });
        AddButton(files, "读取最近存档", () => { if (_entries.Count > 0) ShowFailure(_service!.Load(_entries[0])); });
        AddButton(files, "导入文件", () => _import.PopupCentered(new Vector2I(800, 560)));
        var current = _current = new HBoxContainer();
        column.AddChild(current);
        _saveButtons.Add(AddButton(current, "保存", () => ShowFailure(_service!.Save())));
        _saveButtons.Add(AddButton(current, "另存为", () =>
        {
            _name.Text = _service!.Status?.Identity.Name ?? "";
            _nameDialog.PopupCentered();
            _name.GrabFocus();
        }));
        _saveButtons.Add(AddButton(current, "导出文件", () => _export.PopupCentered(new Vector2I(800, 560))));
        AddButton(current, "返回菜单", () => ShowFailure(_service!.ReturnToMenu()));
        _cancel = new Button { Text = "取消当前操作" };
        _cancel.Pressed += () => _service!.Cancel();
        column.AddChild(_cancel);
        _nameDialog.AddChild(_name);
        foreach (var dialog in new Window[] { _nameDialog, _import, _export, _overwrite, _missing, _leave })
        {
            AddChild(dialog);
            dialog.Transient = true;
            dialog.Exclusive = true;
        }
        _nameDialog.Confirmed += () => ShowFailure(_service!.SaveAs(_name.Text));
        _import.FileSelected += path => ShowFailure(_service!.Import(path));
        _export.FileSelected += Export;
        _overwrite.Confirmed += () => ShowFailure(_service!.Export(_exportPath!, true));
        _missing.Confirmed += () => _service!.ConfirmMissing();
        _missing.Canceled += () => _service!.Cancel();
        _leave.Confirmed += () => _service!.ChooseLeave(LeaveChoice.Save);
        _leave.Canceled += () => _service!.ChooseLeave(LeaveChoice.Cancel);
        _discard = _leave.AddButton("不保存", false, "discard");
        _leave.CustomAction += _ =>
        {
            _leave.Hide();
            _service!.ChooseLeave(LeaveChoice.Discard);
        };
    }

    internal void Configure(SaveService service)
    {
        _service = service;
        service.Changed += Refresh;
    }

    internal void Open()
    {
        PopupCentered();
        Refresh();
    }

    public override void _Process(double delta)
    {
        if (_service is null) return;
        var status = _service.Status;
        _current.Visible = status is not null;
        _status.Text = status is null ? "当前没有活动会话" :
            $"{status.Identity.Name} · {status.Identity.Id} · {(status.Unsaved ? "未保存" : "已保存")}" +
            (status.Frozen ? "（核心已冻结，不能保存）" : "");
        foreach (var button in _operations) button.Disabled = _service.IsBusy;
        foreach (var button in _saveButtons)
        {
            button.Visible = status is { Frozen: false };
            button.Disabled = _service.IsBusy;
        }
        _cancel.Visible = _service.IsBusy;
        if (_service.Leaving is { } leaving)
        {
            if (!Visible) PopupCentered();
            _leave.DialogText = leaving.Waiting ? "正在等待文件操作完成……" :
                leaving.CanSave ? "当前更改尚未保存。离开前保存？" : "核心已冻结，不能保存。仍要离开？";
            _leave.GetOkButton().Visible = leaving.CanSave;
            _leave.GetOkButton().Disabled = leaving.Waiting;
            _discard.Disabled = leaving.Waiting;
            if (!_leave.Visible) _leave.PopupCentered();
        }
        else _leave.Hide();
        if (_service.Missing is { } missing)
        {
            if (!Visible) PopupCentered();
            _missing.DialogText = string.Join("\n", missing.Details);
            if (!_missing.Visible) _missing.PopupCentered(new Vector2I(640, 360));
        }
        else _missing.Hide();
    }

    public override void _ExitTree()
    {
        if (_service is not null) _service.Changed -= Refresh;
    }

    private Button AddButton(HBoxContainer row, string text, Action action)
    {
        var button = new Button { Text = text };
        button.Pressed += action;
        row.AddChild(button);
        _operations.Add(button);
        return button;
    }

    private void Refresh()
    {
        if (_service is null) return;
        if (Visible)
        {
            _entries = _service.List();
            _listMessage.Text = _service.ListMessage is { } message ? $"存档列表：{message}" : "";
            _listMessage.TooltipText = _listMessage.Text;
            _listMessage.Visible = _service.ListMessage is not null;
            _list.Clear();
            foreach (var entry in _entries)
                _list.AddItem($"{entry.Header.Name} · {entry.Header.SavedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}{(entry.Automatic ? " · 自动" : "")}");
        }
        _message.Text = _service.LastMessage ?? "";
    }

    internal void ShowFailure(SaveFailure? failure)
    {
        if (failure is null) return;
        _message.Text = failure.Message;
        if (!Visible) PopupCentered();
    }

    private void Export(string path)
    {
        var failure = _service!.Export(path);
        if (failure?.Code == "overwrite-confirmation")
        {
            _exportPath = path;
            _overwrite.PopupCentered();
        }
        else ShowFailure(failure);
    }
}
