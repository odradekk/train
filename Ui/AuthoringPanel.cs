using Godot;
using Train.App.Authoring;
using Train.App.Editing;
using Train.App.Saves;
using Train.Content.Contracts;
using Train.Core.Contracts;

namespace Train.Ui;

internal partial class AuthoringPanel : Control
{
    private static readonly (string Name, Func<string, DefinitionDraft> Create)[] NewDefinitions =
    [
        ("车辆", id => Blueprint(id, new DesignSpecification.Vehicle(SizeClass.Small))),
        ("建筑", id => Blueprint(id, new DesignSpecification.Building(PlotSize.Small, BuildingKind.Production, []))),
        ("组装体设备", id => Blueprint(id, new DesignSpecification.Assembly(null))),
        ("功能复合体", id => Blueprint(id, new DesignSpecification.FunctionalComposite([], null))),
        ("场景模型", id => Blueprint(id, new DesignSpecification.SceneModel(null))),
        ("生物", id => Blueprint(id, new DesignSpecification.Creature(null, null))),
        ("体素", id => new DefinitionDraft.Voxel(new(id, new(id, ""), null, null, AllowedVoxelOrientations.None, new VoxelCollision.None(), null, new(false, null)))),
        ("聚居地类型", id => new DefinitionDraft.SettlementType(new(id, new(id, ""), null, false, null, [], null, null))),
        ("生物聚落类型", id => new DefinitionDraft.LairType(new(id, new(id, ""), null, null, [], null, null, null, null, null, null, null))),
        ("聚居地模板", id => new DefinitionDraft.SettlementTemplate(new(id, null, null, null, [], [], [], [])))
    ];
    private static DefinitionDraft.Blueprint Blueprint(string id, DesignSpecification specification) =>
        new(id, new(new(id, ""), specification, new([], [], [])));
    private AuthoringService _service = null!;
    private EditorService _editor = null!;
    private PanelContainer _window = null!;
    private PanelContainer _leave = null!;
    private VBoxContainer _definitions = null!;
    private VBoxContainer _drafts = null!;
    private AuthoringDefinitionForm _form = null!;
    private DefinitionDraft? _loaded;
    private Label _message = null!;
    private Button _core = null!;
    private LineEdit _packageId = null!;
    private LineEdit _relativeAsset = null!;
    private LineEdit _newId = null!;
    private OptionButton _newKind = null!;
    private FileDialog _directory = null!;
    private FileDialog _asset = null!;
    private bool _opened;
    private IAuthoringPackage? _listedPackage;
    private AuthoringDefinitionAddress[] _listedDefinitions = [];
    private bool _wasBusy;
    private bool _refreshLists = true;
    internal void Initialize(AuthoringService service, EditorService editor)
    {
        _service = service; _editor = editor;
        service.Changed += Refresh; editor.Changed += Refresh;
    }
    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect); MouseFilter = MouseFilterEnum.Ignore;
        _window = UiStyle.Panel(this, "Authoring", "内容包创作", new(30, 70), 520, Close, out var body);
        var scroll = new ScrollContainer { CustomMinimumSize = new(480, 650) }; body.AddChild(scroll);
        var fields = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; scroll.AddChild(fields);
        _core = Add(fields, "OpenCore", "打开开发源码 core 包", _service.OpenCore);
        fields.AddChild(new Label { Text = "Mod 包 ID" }); _packageId = new LineEdit { Name = "PackageId" }; fields.AddChild(_packageId);
        Add(fields, "OpenMod", "选择本地 Mod 目录", () => { _directory.CurrentDir = _service.ModsDirectory; _directory.PopupCentered(new(900, 650)); });
        Add(fields, "ReloadSource", "重新载入当前源包", () => { if (_service.Target is { } target) _service.Open(target.Directory, target.Package); });
        fields.AddChild(new Label { Text = "定义" }); _definitions = new VBoxContainer(); fields.AddChild(_definitions);
        fields.AddChild(new Label { Text = "已保存包草稿" }); _drafts = new VBoxContainer(); fields.AddChild(_drafts);
        _newId = new LineEdit { PlaceholderText = "新定义本地 ID", Name = "NewDefinitionId" }; fields.AddChild(_newId);
        _newKind = new OptionButton { Name = "NewDefinitionKind" };
        foreach (var kind in NewDefinitions) _newKind.AddItem(kind.Name);
        fields.AddChild(_newKind); Add(fields, "NewDefinition", "新建定义", NewDefinition);
        _form = new AuthoringDefinitionForm { Name = "DefinitionForm" }; fields.AddChild(_form);
        _form.Edited += _service.MarkFormDirty;
        _form.PreviewRequested += () => Capture(value => _service.PreviewTemplate(value));
        _form.GridRequested += () =>
        {
            try
            {
                var address = AuthoringService.Address(_form.Capture());
                _service.OpenDefinition(address);
            }
            catch (Exception error) { _service.Report(error.Message); }
        };
        Add(fields, "SaveDraft", "保存草稿", () => Capture(value => _service.SaveForm(value)));
        _relativeAsset = new LineEdit { Name = "AssetRelativePath", PlaceholderText = "资产包相对路径，如 textures/metal.png" }; fields.AddChild(_relativeAsset);
        Add(fields, "AddAsset", "选择并加入资产", () => _asset.PopupCentered(new(900, 650)));
        fields.AddChild(new Label { Text = "最终源基准复核到写入完成期间，请确保只有此工具写入源包。", AutowrapMode = TextServer.AutowrapMode.WordSmart });
        Add(fields, "Publish", "写入包并普通重载", _service.Publish);
        _message = new Label { Name = "AuthoringResult", AutowrapMode = TextServer.AutowrapMode.WordSmart }; fields.AddChild(_message);
        _directory = new FileDialog { Title = "选择 user://mods 下的本地包", Access = FileDialog.AccessEnum.Filesystem, FileMode = FileDialog.FileModeEnum.OpenDir }; AddChild(_directory);
        _directory.DirSelected += directory => _service.Open(directory, new(_packageId.Text));
        _asset = new FileDialog { Title = "选择资产文件", Access = FileDialog.AccessEnum.Filesystem, FileMode = FileDialog.FileModeEnum.OpenFile }; AddChild(_asset);
        _asset.FileSelected += source => _service.AddAsset(_relativeAsset.Text, source);
        _leave = UiStyle.Panel(this, "LeaveAuthoring", "表单有未保存修改", new(440, 200), 420, () => _service.ResolveClose(LeaveChoice.Cancel, null), out var leave);
        Add(leave, "SaveAndLeave", "保存草稿并继续", () => Capture(value => _service.ResolveClose(LeaveChoice.Save, value)));
        Add(leave, "DiscardAndLeave", "放弃本次修改", () => _service.ResolveClose(LeaveChoice.Discard, null));
        Add(leave, "CancelLeave", "取消", () => _service.ResolveClose(LeaveChoice.Cancel, null));
        Refresh();
    }
    internal void Open() { if (!_service.CanUse) return; _opened = true; _refreshLists = true; Refresh(); }
    internal void SelectPlot(int index) => _form.SelectPlot(index);
    private void Close()
    {
        if (_editor.RequestClose(() => { _opened = false; Refresh(); }) is { } failure) _service.Report(failure.Message);
    }
    private static Button Add(VBoxContainer body, string name, string text, Action action)
    {
        var button = UiStyle.Button(name, text, action); body.AddChild(button); return button;
    }
    private static void Clear(VBoxContainer box)
    {
        foreach (var child in box.GetChildren()) { box.RemoveChild(child); child.QueueFree(); }
    }
    private void Refresh()
    {
        if (_window is null) return;
        Visible = _service.CanUse && (_opened || _service.Leaving);
        _window.Visible = _opened && !_editor.IsOpen;
        _leave.Visible = _service.Leaving;
        _core.Visible = _service.CanWriteCore; _core.Disabled = !_service.CanWriteCore || _service.IsBusy;
        if (!ReferenceEquals(_loaded, _service.Form))
        {
            _loaded = _service.Form;
            if (_loaded is not null) { _form.Load(_loaded); }
        }
        _form.Visible = _loaded is not null;
        var definitions = _service.Definitions;
        var busy = _service.IsBusy || _editor.IsBusy;
        var refreshLists = _refreshLists || !ReferenceEquals(_listedPackage, _service.Package)
            || !_listedDefinitions.SequenceEqual(definitions) || (_wasBusy && !busy);
        _wasBusy = busy;
        if (refreshLists)
        {
            _refreshLists = false;
            _listedPackage = _service.Package;
            _listedDefinitions = definitions.ToArray();
            RefreshLists();
        }
        _message.Text = _service.Message ?? "";
        if (_service.LastWrite is { } written)
            _message.Text += "\n已完成：" + string.Join("、", written.Completed.Select(x => x.RelativePath)) + "\n失败：" + (written.Failed?.RelativePath ?? "无") + "\n未执行：" + string.Join("、", written.Remaining.Select(x => x.RelativePath))
                + (written.CleanupMessage is { } cleanup ? "\n临时文件清理失败：" + cleanup : "");
    }
    private void RefreshLists()
    {
        Clear(_definitions); Clear(_drafts);
        if (_listedPackage is not null)
        {
            foreach (var address in _listedDefinitions)
            {
                _definitions.AddChild(new Label { Text = $"{address.Kind}: {address.LocalId}" });
                Add(_definitions, "EditGrid", "网格编辑", () => _service.OpenDefinition(address));
                Add(_definitions, "EditForm", "编辑表单", () => _service.OpenDefinition(address, false));
                Add(_definitions, "DeleteDefinition", "删除定义…", () =>
                {
                    var confirm = new ConfirmationDialog { Title = "删除定义", DialogText = $"将 {address.LocalId} 标记为删除？写入前仍需完整校验。" };
                    AddChild(confirm); confirm.Confirmed += () => { _service.DeleteDefinition(address); confirm.QueueFree(); }; confirm.Canceled += confirm.QueueFree; confirm.PopupCentered();
                });
            }
            foreach (var draft in _service.Drafts())
            {
                if (draft.Target is DraftTarget.PackageDefinition target && target.SourceVersion != _listedPackage.SourceVersion)
                {
                    _drafts.AddChild(new Label { Text = $"草稿 {draft.Id.Value} 的来源已变化。请手动处理修改；工具不会自动合并。", AutowrapMode = TextServer.AutowrapMode.WordSmart });
                    Add(_drafts, "OpenDraftOnCurrentSource", "在当前源包上打开", () => _service.OpenDraft(draft, true));
                }
                else Add(_drafts, "OpenDraft", $"打开草稿 {draft.Id.Value}", () => _service.OpenDraft(draft));
            }
        }
    }
    private void NewDefinition() => _service.NewDefinition(NewDefinitions[_newKind.Selected].Create(_newId.Text));
    private void Capture(Action<DefinitionDraft> action)
    {
        try { action(_form.Capture()); }
        catch (Exception error) { _service.Report(error.Message); }
    }
    public override void _ExitTree()
    {
        _service.Changed -= Refresh; _editor.Changed -= Refresh;
    }
}
