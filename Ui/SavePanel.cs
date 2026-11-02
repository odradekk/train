using Godot;
using Train.App.Saves;

namespace Train.Ui;

// Decision note: 2026-10-31-menus-saves-and-settings
public partial class SavePanel : Control
{
    private SaveService? _service;
    private readonly PanelContainer _panel = new() { Name = "Panel" };
    private readonly Label _title = UiStyle.Label("读取存档", true);
    private readonly Label _message = UiStyle.Label("");
    private readonly Label _listMessage = UiStyle.Label("");
    private readonly LineEdit _search = new() { Name = "Search", PlaceholderText = "搜索存档或世界", TooltipText = "搜索存档名称或世界" };
    private readonly ItemList _list = new() { Name = "Saves", SizeFlagsVertical = SizeFlags.ExpandFill };
    private readonly HFlowContainer _current = new() { Name = "CurrentGame" };
    private readonly LineEdit _name = new() { Name = "SaveName", CustomMinimumSize = new(240, 0), SizeFlagsHorizontal = SizeFlags.ExpandFill };
    private readonly Label _currentState = UiStyle.Label("");
    private readonly Label _detailTitle = UiStyle.Label("选择存档", true);
    private readonly Label _timestamps = UiStyle.Label("");
    private readonly Label _details = UiStyle.Label("");
    private readonly SaveMapPreview _map = new() { Name = "MapPreview", CustomMinimumSize = new(0, 180), MouseFilter = MouseFilterEnum.Ignore };
    private readonly BoxContainer _columns = new() { SizeFlagsVertical = SizeFlags.ExpandFill };
    private readonly List<Button> _entryActions = [];
    private readonly FileDialog _import = new() { Title = "导入存档", Access = FileDialog.AccessEnum.Filesystem,
        FileMode = FileDialog.FileModeEnum.OpenFile, Filters = ["*.json ; 存档文件"] };
    private readonly FileDialog _export = new() { Title = "导出存档", Access = FileDialog.AccessEnum.Filesystem,
        FileMode = FileDialog.FileModeEnum.SaveFile, OverwriteWarningEnabled = false, Filters = ["*.json ; 存档文件"] };
    private Button _save = null!, _importButton = null!, _cancel = null!;
    private SaveEntry? _selected, _exportEntry;
    private SaveIdentity? _shownIdentity;
    private IReadOnlyList<SaveEntry> _entries = [];
    private readonly Dictionary<int, SaveEntry> _rows = [];
    private Control? _previousFocus;
    private bool _rebuilding;
    private bool _detailsDeferred;

    internal IReadOnlyList<SaveEntry> Entries => _entries;

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        MouseForcePassScrollEvents = false;
        var shade = new ColorRect { Color = new(0, 0, 0, .6f), MouseFilter = MouseFilterEnum.Ignore };
        shade.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(shade);
        AddChild(_panel);
        var margin = new MarginContainer();
        foreach (var side in new[] { "left", "top", "right", "bottom" }) margin.AddThemeConstantOverride("margin_" + side, 20);
        _panel.AddChild(margin);
        var layout = new VBoxContainer();
        layout.AddThemeConstantOverride("separation", 12);
        margin.AddChild(layout);
        var heading = new HBoxContainer();
        _title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        heading.AddChild(_title);
        _importButton = UiStyle.Button("Import", "导入存档文件…", () => { _service!.CancelDetails(); _import.PopupCentered(new(800, 560)); });
        heading.AddChild(_importButton);
        heading.AddChild(UiStyle.Button("Close", "关闭（Esc）", Hide));
        layout.AddChild(heading);
        _current.AddChild(UiStyle.Label("保存当前对局"));
        _name.TooltipText = "名称不变时覆盖当前存档，改名后另存为";
        _current.AddChild(_name);
        _save = UiStyle.Button("Save", "保存", SaveCurrent, true);
        _current.AddChild(_save);
        _current.AddChild(_currentState);
        layout.AddChild(_current);
        layout.AddChild(_columns);
        _columns.AddThemeConstantOverride("separation", 24);
        var left = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsStretchRatio = .8f };
        left.AddChild(UiStyle.Label("存档", true));
        left.AddChild(_search);
        left.AddChild(_list);
        left.AddChild(_listMessage);
        left.AddChild(UiStyle.Label("双击存档直接读取。"));
        _columns.AddChild(left);
        var right = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _columns.AddChild(right);
        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, FollowFocus = true };
        right.AddChild(scroll);
        var body = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        scroll.AddChild(body);
        body.AddChild(_map);
        foreach (var label in new[] { _detailTitle, _timestamps, _details })
        { label.AutowrapMode = TextServer.AutowrapMode.WordSmart; body.AddChild(label); }
        var actions = new HFlowContainer();
        right.AddChild(actions);
        foreach (var (name, text, action) in new (string, string, Action)[]
        {
            ("Load", "读取", LoadSelected), ("Rename", "重命名", RenameSelected),
            ("Export", "导出文件", ExportSelected), ("Delete", "删除", DeleteSelected)
        })
        {
            var button = UiStyle.Button(name, text, action, name == "Load");
            actions.AddChild(button); _entryActions.Add(button);
        }
        _message.AutowrapMode = _listMessage.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        layout.AddChild(_message);
        _cancel = UiStyle.Button("CancelOperation", "取消当前操作", () => _service!.Cancel());
        layout.AddChild(_cancel);
        BuildPrompt();
        foreach (var dialog in new[] { _import, _export }) { dialog.Transient = true; dialog.Exclusive = true; AddChild(dialog); }
        _import.FileSelected += path => ShowFailure(_service!.Import(path));
        _export.FileSelected += ExportTo;
        _search.TextChanged += _ => RebuildList();
        _list.ItemSelected += _ => { if (!_rebuilding) SelectRow(); };
        _list.ItemActivated += row => { if (_rows.TryGetValue((int)row, out var entry)) ShowFailure(_service!.Load(entry)); };
        Resized += Arrange;
        VisibilityChanged += VisibilityUpdated;
        Arrange();
        Hide();
    }

    internal void Configure(SaveService service)
    {
        _service = service;
        service.Changed += Refresh;
        service.DetailsChanged += RenderDetails;
    }

    internal void Open()
    {
        if (!Visible)
        {
            _previousFocus = GetViewport().GuiGetFocusOwner();
            if (_service?.Status is { } status) { _name.Text = status.Identity.Name; _shownIdentity = status.Identity; }
        }
        Show(); MoveToFront();
        Refresh();
        _search.GrabFocus();
    }

    private void VisibilityUpdated()
    {
        _service?.SetOverlayOpen(Visible);
        if (Visible) return;
        _service?.CancelDetails();
        if (_service?.Leaving is not null) _service.ChooseLeave(LeaveChoice.Cancel);
        if (_service?.Missing is not null) _service.Cancel();
        ClosePrompt();
        if (GodotObject.IsInstanceValid(_previousFocus) && _previousFocus!.IsVisibleInTree()) _previousFocus.GrabFocus();
    }

    public override void _Input(InputEvent input)
    {
        if (input is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape } && HandleEscape())
            GetViewport().SetInputAsHandled();
    }

    internal bool HandleEscape()
    {
        if (!IsVisibleInTree() || _import.Visible || _export.Visible) return false;
        if (_promptMask.Visible) CancelPrompt();
        else Hide();
        return true;
    }

    public override void _Process(double delta)
    {
        if (_service is null) return;
        if (Visible && _detailsDeferred && !_service.IsBusy) SelectRow();
        var status = _service.Status;
        _current.Visible = status is not null;
        _title.Text = _service.IsReplay ? "读取存档（只读重放，无法保存）" : status is null ? "读取存档" : "存档与读档";
        if (status is not null && _shownIdentity != status.Identity && !_name.HasFocus())
        { _name.Text = status.Identity.Name; _shownIdentity = status.Identity; }
        _currentState.Text = status is { Frozen: true } ? "当前对局无法保存" : status?.Unsaved == true ? "有未保存的进度" : "已保存";
        _name.Editable = !_service.IsBusy && status is { Frozen: false };
        _save.Disabled = !_name.Editable;
        foreach (var button in _entryActions) button.Disabled = _service.IsBusy || _selected is null;
        _importButton.Disabled = _service.IsBusy;
        _cancel.Visible = _service.HasPendingWork;
        UpdatePrompts();
    }

    private void SaveCurrent()
    {
        var name = _name.Text.Trim();
        _service!.CancelDetails();
        ShowFailure(name == _service.Status?.Identity.Name ? _service.Save() : _service.SaveAs(name));
    }

    private void LoadSelected() { if (_selected is { } selected) ShowFailure(_service!.Load(selected)); }

    private void Refresh()
    {
        if (!IsInsideTree() || _service is null) return;
        _message.Text = _service.PlayerMessage ?? "";
        if (!Visible) return;
        _entries = _service.List();
        RebuildList();
    }

    private void RebuildList()
    {
        var selectedPath = _selected?.Path;
        var focus = _list.HasFocus();
        if (focus) _list.ReleaseFocus();
        _rebuilding = true;
        _list.Clear(); _rows.Clear();
        var search = _search.Text.Trim();
        // 存档列表已按保存时间降序，分组保留这个顺序。
        foreach (var group in _entries.GroupBy(entry => entry.Header.World))
        {
            var title = _service!.WorldTitle(group.Key);
            var entries = group.Where(entry => title.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                entry.Header.Name.Contains(search, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (entries.Length == 0) continue;
            var heading = _list.AddItem(title);
            _list.SetItemSelectable(heading, false);
            foreach (var entry in entries)
            {
                var row = _list.AddItem($"{entry.Header.Name}{(entry.Automatic ? " · 自动" : "")} · {UiInput.FormatTime(entry.Header.Position.CompletedSteps)} · {MainMenu.SavedAt(entry.Header.SavedAt)}");
                _rows[row] = entry;
                if (entry.Path == selectedPath) _list.Select(row);
            }
        }
        if (_list.GetSelectedItems().Length == 0 && _rows.Count > 0) _list.Select(_rows.Keys.Min());
        _rebuilding = false;
        _listMessage.Text = _service?.ListMessage is not null
            ? "部分存档无法读取，请检查文件和访问权限。"
            : _rows.Count == 0 ? _entries.Count == 0 ? "还没有存档。" : "没有符合条件的存档。" : "";
        SelectRow();
        if (focus) _list.GrabFocus();
    }

    private void SelectRow()
    {
        var rows = _list.GetSelectedItems();
        var selected = rows.Length > 0 && _rows.TryGetValue(rows[0], out var entry) ? entry : null;
        if (_selected?.Path != selected?.Path || _selected?.Header.SavedAt != selected?.Header.SavedAt)
            _service?.CancelDetails();
        _selected = selected;
        _detailsDeferred = selected is not null && _service?.IsBusy == true;
        if (selected is not null && _service is { IsBusy: false, Details: null, DetailsMessage: null })
            _service.Inspect(selected);
        RenderDetails();
    }

    private void RenderDetails()
    {
        if (!IsInsideTree()) return;
        _detailTitle.Text = _selected?.Header.Name ?? "选择存档";
        _timestamps.Text = _selected is null ? "" : $"游戏时间　{UiInput.FormatTime(_selected.Header.Position.CompletedSteps)}\n保存时间　{MainMenu.SavedAt(_selected.Header.SavedAt)}";
        var details = _service?.Details;
        _map.World = details?.World;
        _map.Visible = details is not null;
        _details.Text = details is null ? _service?.DetailsMessage ?? "" :
            $"资金　{details.Funds:N0}\n列车　{details.World.Trains.Length} 列\n自有聚居地　{details.OwnedSettlements} 座\n内容包　" +
            (details.Packages.IsEmpty ? "未记录" : string.Join("、", details.Packages.Select(package => $"{package.Name} {package.Version}"))) +
            (details.Warnings.IsEmpty ? "" : "\n\n" + string.Join("\n", details.Warnings));
    }

    internal void ShowFailure(SaveFailure? failure)
    {
        if (failure is null) return;
        if (!Visible) Open();
        _message.Text = SaveService.DescribeFailure(failure);
    }

    private void Arrange()
    {
        if (!IsNodeReady()) return;
        _panel.Size = new(Math.Min(1180, Math.Max(0, Size.X - 32)), Math.Min(760, Math.Max(0, Size.Y - 32)));
        _panel.Position = (Size - _panel.Size) / 2;
        _columns.Vertical = Size.X < 900;
        _list.CustomMinimumSize = new(0, _columns.Vertical ? 100 : 0);
        ArrangePrompt();
    }

    public override void _ExitTree()
    {
        if (_service is null) return;
        _service.Changed -= Refresh;
        _service.DetailsChanged -= RenderDetails;
        _service.SetOverlayOpen(false);
        _service.CancelDetails();
    }
}
