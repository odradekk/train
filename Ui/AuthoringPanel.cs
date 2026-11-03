using Godot;
using Train.App.Authoring;
using Train.App.Editing;
using Train.Content.Contracts;

namespace Train.Ui;

// Decision note: 2026-10-31-full-screen-authoring-mode
internal partial class AuthoringPanel : Control
{
    private AuthoringService _service = null!;
    private EditorService _editor = null!;
    private TemplateSceneController? _templates;
    private Control _mode = null!;
    private Control _background = null!;
    private SubViewportContainer? _previewViewport;
    private Label _previewHint = null!;
    private HBoxContainer _body = null!;
    private PanelContainer _sidebar = null!, _formPanel = null!;
    private VBoxContainer _preview = null!;
    private AuthoringDefinitionForm _form = null!;
    private DefinitionDraft? _loaded;
    private Label _message = null!, _empty = null!, _counts = null!, _paused = null!;
    private OptionButton _source = null!;
    private Button _save = null!, _publish = null!, _reload = null!, _problems = null!;
    private Button _definitionsToggle = null!, _previewToggle = null!;
    private bool _opened, _sourceUpdating, _showDefinitions, _showPreview;
    private AuthoringSource[] _sourceChoices = [];
    private int _coreIndex = -1, _newModIndex;
    private IAuthoringPackage? _listedPackage;
    private AuthoringDefinitionAddress[] _listedDefinitions = [];
    private PackageEdit[] _listedEdits = [];
    private bool _wasBusy;
    internal bool IsOpen => _opened;

    internal void Initialize(AuthoringService service, EditorService editor, TemplateSceneController? templates = null)
    {
        _service = service; _editor = editor; _templates = templates;
        service.Changed += Refresh; editor.Changed += Refresh;
    }

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect); MouseFilter = MouseFilterEnum.Ignore;
        _background = new ColorRect { Name = "AuthoringBackground", Color = new("10151a"), MouseFilter = MouseFilterEnum.Stop };
        _background.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect); AddChild(_background);
        _mode = new VBoxContainer { Name = "AuthoringMode" }; AddChild(_mode);
        _mode.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var top = new PanelContainer(); _mode.AddChild(top);
        var bar = new HFlowContainer(); top.AddChild(bar);
        bar.AddChild(UiStyle.Button("ExitAuthoring", "退出", Close));
        bar.AddChild(UiStyle.Label("创作模式", true));
        _source = new OptionButton { Name = "SourcePackage", CustomMinimumSize = new(230, 34), TooltipText = "要编辑的源包" };
        bar.AddChild(_source); _source.ItemSelected += SourceSelected;
        _reload = UiStyle.Button("ReloadSource", "重新载入", () =>
        {
            if (_service.Target is { } target) _service.Open(target.Directory, target.Package);
            _service.RefreshSources();
        }); bar.AddChild(_reload);
        _paused = UiStyle.Label(""); bar.AddChild(_paused);
        _problems = UiStyle.Button("Problems", "问题 0", () => ShowProblems(_service.Issues)); bar.AddChild(_problems);
        _counts = UiStyle.Label(""); bar.AddChild(_counts);
        _save = UiStyle.Button("SaveDraft", "保存草稿", () => Capture(_service.SaveForm)); bar.AddChild(_save);
        _publish = UiStyle.Button("Publish", "写入包并重载…", ConfirmPublish, true); bar.AddChild(_publish);
        _definitionsToggle = UiStyle.Button("ToggleDefinitions", "定义", () => { _showDefinitions = !_showDefinitions; Arrange(); }); bar.AddChild(_definitionsToggle);
        _previewToggle = UiStyle.Button("TogglePreview", "预览", () => { _showPreview = !_showPreview; _showDefinitions = false; Arrange(); }); bar.AddChild(_previewToggle);
        _body = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill }; _mode.AddChild(_body);
        BuildDefinitions();
        _formPanel = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsStretchRatio = 2 }; _body.AddChild(_formPanel);
        var formScroll = new ScrollContainer { FollowFocus = true }; _formPanel.AddChild(formScroll);
        var fields = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; formScroll.AddChild(fields);
        _empty = UiStyle.Label("选择源包，再从左侧选择定义。没有表单的种类请直接修改源文件。");
        _empty.AutowrapMode = TextServer.AutowrapMode.WordSmart; fields.AddChild(_empty);
        _form = new AuthoringDefinitionForm { Name = "DefinitionForm" }; fields.AddChild(_form);
        _form.Edited += () =>
        {
            _service.MarkFormDirty();
            if (_form.TryCapture(out var value) && value is not null) _service.UpdateForm(value);
            else _service.CaptureFailed("表单存在错误，请修正后重试。");
        };
        _form.GridRequested += () => Capture(_service.OpenGrid);
        BuildAssets();
        _message = new Label { Name = "AuthoringResult", AutowrapMode = TextServer.AutowrapMode.WordSmart }; fields.AddChild(_message);
        _preview = new VBoxContainer { Name = "AuthoringPreview", SizeFlagsHorizontal = SizeFlags.ExpandFill }; _body.AddChild(_preview);
        _preview.AddChild(UiStyle.Label("预览", true));
        _previewHint = Text(_preview, "聚居地模板会自动预览。其他定义请在表单或设计编辑器中编辑。");
        if (_templates is not null)
        {
            _previewViewport = new SubViewportContainer { Stretch = true, SizeFlagsVertical = SizeFlags.ExpandFill };
            _preview.AddChild(_previewViewport);
            var viewport = new SubViewport { OwnWorld3D = true, Size = new(400, 600) };
            _previewViewport.AddChild(viewport); _templates.View.Reparent(viewport);
        }
        BuildDialogs(); Resized += Arrange; Refresh();
    }

    internal void Open()
    {
        if (!_service.CanOpen) return;
        _opened = true; _service.SetOverlayOpen(true); _service.RefreshSources(); Refresh();
        ((Control)_mode.FindChild("ExitAuthoring", true, false)).GrabFocus();
    }

    internal void SelectPlot(int index) => _form.SelectPlot(index);
    private void Close()
    {
        if (_editor.RequestClose(() =>
        {
            _opened = false; _service.SetOverlayOpen(false); Refresh();
        }) is { } failure) _service.Report(failure.Message);
    }

    internal bool HandleEscape()
    {
        if (!_opened) return false;
        if (_editor.IsOpen) return false;
        if (_assetDialog.Visible) { _assetSelected = null; _assetDialog.Hide(); }
        else if (_confirm.Visible) _confirm.Hide();
        else if (_problemDialog.Visible) _problemDialog.Hide();
        else if (_writeDialog.Visible && !_service.IsBusy) { _writeDialog.Hide(); _showWrite = false; }
        else if (_newMod.Visible) _newMod.Hide();
        else if (_newDefinition.Visible) _newDefinition.Hide();
        else if (_delete.Visible) _delete.Hide();
        else if (_leave.Visible) _service.ResolveClose(Train.App.Saves.LeaveChoice.Cancel, null);
        else if (!_service.IsBusy) Close();
        UpdateDialogFocus(); return true;
    }

    public override void _Input(InputEvent input)
    {
        if (!_opened || _editor.IsOpen) return;
        if (input is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape } && HandleEscape()) GetViewport().SetInputAsHandled();
        else if (_mode.Visible && _mode.FocusBehaviorRecursive != FocusBehaviorRecursiveEnum.Disabled
            && input.IsActionPressed(DesignEditorPanel.SaveAction) && _service.DirtyDefinition is not null)
        { Capture(_service.SaveForm); GetViewport().SetInputAsHandled(); }
    }

    private void Refresh()
    {
        if (_mode is null) return;
        Visible = _service.CanUse && (_opened || _service.Leaving);
        _mode.Visible = _opened && !_editor.IsOpen;
        _background.Visible = _mode.Visible;
        _previewHint.Visible = _service.TemplatePreview is null;
        _previewHint.Text = _service.FormDiagnostics.Any(diagnostic => diagnostic.Severity == ContentDiagnosticSeverity.Error)
            || _service.DirtyDefinition is { } dirty && _service.Issues.Any(issue => issue.Definition == dirty && issue.Diagnostic.Severity == ContentDiagnosticSeverity.Error)
            ? "表单存在错误，请先修正。" : "聚居地模板会自动预览。其他定义请在表单或设计编辑器中编辑。";
        if (_previewViewport is not null) _previewViewport.Visible = !_previewHint.Visible;
        var busy = _service.IsBusy || _editor.IsBusy;
        if (!ReferenceEquals(_loaded, _service.Form))
        {
            _loaded = _service.Form;
            if (_loaded is not null) _form.Load(_loaded, _service.Package!.Package, _service.ReferenceChoices);
        }
        _form.SetReferenceChoices(_service.ReferenceChoices);
        _form.SetDiagnostics(_service.FormDiagnostics);
        _form.Visible = _loaded is not null; _empty.Visible = _loaded is null;
        _source.Disabled = busy; _reload.Disabled = busy || _service.Target is null;
        _save.Disabled = busy || _service.DirtyDefinition is null;
        _publish.Disabled = busy || _service.Package is null || _service.PendingChanges.IsEmpty || _service.DirtyDefinition is not null;
        _publish.TooltipText = _service.DirtyDefinition is not null ? "请先保存草稿" : "校验后写入源包并重新载入内容";
        _paused.Text = _service.HasWorld ? "世界已暂停" : "未打开对局";
        _counts.Text = $"未保存 {(_service.DirtyDefinition is null ? 0 : 1)} · 待写入 {_service.PendingChanges.Length}";
        _problems.Text = $"问题 {_service.Issues.Length}";
        _message.Text = _service.Message ?? "";
        RefreshSources();
        var definitions = _service.Definitions;
        var packageChanged = !ReferenceEquals(_listedPackage, _service.Package);
        if (packageChanged || !_listedDefinitions.SequenceEqual(definitions))
        {
            _listedPackage = _service.Package; _listedDefinitions = definitions.ToArray(); RebuildTree();
        }
        var edits = _service.PendingChanges;
        if (packageChanged || !_listedEdits.SequenceEqual(edits) || _wasBusy && !busy)
        { _listedEdits = edits.ToArray(); RefreshDrafts(); }
        _wasBusy = busy; RefreshTreeMarks(); RefreshDialogs(); Arrange();
    }

    private void Arrange()
    {
        if (_body is null) return;
        var narrow = Size.X < 900;
        _definitionsToggle.Visible = narrow; _previewToggle.Visible = Size.X < 1250;
        _sidebar.Visible = !narrow || _showDefinitions;
        _sidebar.CustomMinimumSize = new(narrow ? 0 : 250, 0);
        _sidebar.SizeFlagsHorizontal = narrow ? SizeFlags.ExpandFill : SizeFlags.Fill;
        _formPanel.Visible = (!narrow || !_showDefinitions) && !(Size.X < 1250 && _showPreview);
        _preview.Visible = (!narrow || !_showDefinitions) && (Size.X >= 1250 || _showPreview);
        _preview.CustomMinimumSize = new(Size.X >= 1250 ? 360 : 0, 0);
    }

    private void Capture(Action<DefinitionDraft> action)
    {
        try { action(_form.Capture()); }
        catch (Exception error) { _service.CaptureFailed(error.Message); }
    }

    public override void _ExitTree()
    {
        _service.Changed -= Refresh; _editor.Changed -= Refresh;
        if (_opened) _service.SetOverlayOpen(false);
    }
}
