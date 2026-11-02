using Godot;
using Train.App.Authoring;
using Train.App.Saves;
using Train.Content.Contracts;

namespace Train.Ui;

internal partial class AuthoringPanel
{
    private Control _confirm = null!, _writeDialog = null!, _problemDialog = null!, _leave = null!, _newMod = null!, _newDefinition = null!, _delete = null!;
    private VBoxContainer _confirmItems = null!, _writeItems = null!, _problemItems = null!;
    private Label _deleteText = null!, _modIdentity = null!;
    private LineEdit _modName = null!, _modAuthor = null!, _newId = null!;
    private OptionButton _newKind = null!;
    private PackageId _newPackageId;
    private AuthoringDefinitionAddress? _deleteAddress;
    private bool _showWrite;

    private void BuildDialogs()
    {
        _confirm = Modal("ConfirmPublish", "写入包并重载？", out _confirmItems, out var confirmActions);
        confirmActions.AddChild(UiStyle.Button("CancelWrite", "取消", () => HideModal(_confirm)));
        confirmActions.AddChild(UiStyle.Button("ConfirmWrite", "写入并重载", () =>
        {
            _confirm.Hide(); _showWrite = true; ShowModal(_writeDialog); _service.Publish();
        }, true));
        _writeDialog = Modal("WriteProgress", "写入包并重载", out _writeItems, out var writeActions);
        writeActions.AddChild(UiStyle.Button("CloseWriteResult", "关闭", () =>
        { if (!_service.IsBusy) { _showWrite = false; HideModal(_writeDialog); } }));
        _problemDialog = Modal("SourceProblems", "源包问题", out _problemItems, out var problemActions);
        problemActions.AddChild(UiStyle.Button("CloseProblems", "关闭", () => HideModal(_problemDialog)));
        _leave = Modal("LeaveAuthoring", "有未保存的修改", out var leave, out var leaveActions);
        Text(leave, "先保存草稿，还是放弃本次未保存的修改？已经保存的草稿会保留。");
        leaveActions.AddChild(UiStyle.Button("CancelLeave", "取消", () => _service.ResolveClose(LeaveChoice.Cancel, null)));
        leaveActions.AddChild(UiStyle.Button("DiscardAndLeave", "放弃本次修改", () => _service.ResolveClose(LeaveChoice.Discard, null)));
        leaveActions.AddChild(UiStyle.Button("SaveAndLeave", "保存草稿并继续", () => Capture(value => _service.ResolveClose(LeaveChoice.Save, value)), true));
        _newMod = Modal("NewLocalMod", "新建本地模组", out var mod, out var modActions);
        _modIdentity = Text(mod, "");
        Text(mod, "模组名称"); _modName = new LineEdit { Name = "ModName" }; mod.AddChild(_modName);
        Text(mod, "作者"); _modAuthor = new LineEdit { Name = "ModAuthor" }; mod.AddChild(_modAuthor);
        Text(mod, "版本 0.1.0。确认后创建新目录和 mod.json，不覆盖已有目录。");
        modActions.AddChild(UiStyle.Button("CancelNewMod", "取消", () => HideModal(_newMod)));
        modActions.AddChild(UiStyle.Button("CreateMod", "创建", () =>
        {
            var metadata = new PackageMetadata(_newPackageId, _modName.Text, "0.1.0", [_modAuthor.Text], []);
            HideModal(_newMod); _service.CreateMod(metadata);
        }, true));
        _newDefinition = Modal("NewDefinitionDialog", "新建定义", out var definition, out var definitionActions);
        Text(definition, "定义种类"); _newKind = new OptionButton { Name = "NewDefinitionKind" }; definition.AddChild(_newKind);
        foreach (var kind in NewDefinitions) _newKind.AddItem(kind.Name);
        Text(definition, "本地 ID"); _newId = new LineEdit { Name = "NewDefinitionId" }; definition.AddChild(_newId);
        definitionActions.AddChild(UiStyle.Button("CancelNewDefinition", "取消", () => HideModal(_newDefinition)));
        definitionActions.AddChild(UiStyle.Button("CreateDefinition", "创建", () =>
        {
            HideModal(_newDefinition); _service.NewDefinition(NewDefinitions[_newKind.Selected].Create(_newId.Text));
        }, true));
        _delete = Modal("DeleteDefinitionDialog", "删除定义？", out var deletion, out var deleteActions);
        _deleteText = Text(deletion, "");
        deleteActions.AddChild(UiStyle.Button("CancelDelete", "取消", () => HideModal(_delete)));
        deleteActions.AddChild(UiStyle.Button("ConfirmDelete", "标记删除", () =>
        { HideModal(_delete); if (_deleteAddress is { } address) _service.DeleteDefinition(address); }));
    }

    private Control Modal(string name, string title, out VBoxContainer body, out HFlowContainer actions)
    {
        var modal = new Control { Name = name, MouseFilter = MouseFilterEnum.Stop };
        modal.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect); AddChild(modal);
        var shade = new ColorRect { Color = new(0, 0, 0, .75f), MouseFilter = MouseFilterEnum.Stop };
        shade.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect); modal.AddChild(shade);
        var center = new CenterContainer(); center.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect); modal.AddChild(center);
        var panel = new PanelContainer(); center.AddChild(panel);
        var box = new VBoxContainer(); panel.AddChild(box); box.AddChild(UiStyle.Label(title, true));
        var scroll = new ScrollContainer { FollowFocus = true, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled }; box.AddChild(scroll);
        body = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; scroll.AddChild(body);
        actions = new HFlowContainer(); box.AddChild(actions);
        void Fit() => scroll.CustomMinimumSize = new(Math.Min(620, Math.Max(240, Size.X - 48)), Math.Min(360, Math.Max(100, Size.Y - 180)));
        Resized += Fit; Fit(); modal.Hide(); return modal;
    }

    private static Label Text(Node parent, string text)
    {
        var label = new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        parent.AddChild(label); return label;
    }

    private void ShowModal(Control modal) { modal.Show(); modal.MoveToFront(); UpdateDialogFocus(); }
    private void HideModal(Control modal) { modal.Hide(); UpdateDialogFocus(); }
    private void UpdateDialogFocus()
    {
        _mode.FocusBehaviorRecursive = new[] { _confirm, _writeDialog, _problemDialog, _leave, _newMod, _newDefinition, _delete }
            .Any(dialog => dialog.Visible) ? FocusBehaviorRecursiveEnum.Disabled : FocusBehaviorRecursiveEnum.Inherited;
    }

    private void PrepareNewMod()
    {
        _newPackageId = new(Guid.NewGuid().ToString("D"));
        _modIdentity.Text = "包 ID 与目录名：" + _newPackageId.Value; _modName.Clear(); _modAuthor.Clear(); ShowModal(_newMod);
        _modName.GrabFocus();
    }

    private void ConfirmDelete()
    {
        _deleteAddress = _loaded is { } value ? AuthoringService.Address(value) : null;
        if (_deleteAddress is null) { _service.Report("请先选择要删除的定义。"); return; }
        _deleteText.Text = $"将 {_deleteAddress.LocalId} 标记为删除？写入前仍需完整校验。"; ShowModal(_delete);
    }

    private void ConfirmPublish()
    {
        if (_service.PendingChanges.IsEmpty || _service.DirtyDefinition is not null) return;
        Clear(_confirmItems); Text(_confirmItems, "要写入的源包：" + _service.Target?.Package.Value);
        foreach (var edit in _service.PendingChanges) Text(_confirmItems, EditName(edit));
        Text(_confirmItems, "写入期间不要用别的工具改这个包。写入前会复核源文件是否被外部修改。");
        ShowModal(_confirm);
    }

    private static string EditName(PackageEdit edit) => edit switch
    {
        PackageEdit.WriteDefinition definition => KindName(AuthoringService.Address(definition.Draft).Kind) + " · " + AuthoringService.Address(definition.Draft).LocalId,
        PackageEdit.DeleteDefinition deletion => "删除 " + KindName(deletion.Definition.Kind) + " · " + deletion.Definition.LocalId,
        PackageEdit.WriteAsset asset => "资产 · " + asset.PackageRelativePath,
        PackageEdit.DeleteAsset asset => "删除资产 · " + asset.PackageRelativePath,
        PackageEdit.Manifest => "包信息", _ => throw new ArgumentOutOfRangeException(nameof(edit))
    };

    private void RefreshDialogs()
    {
        _leave.Visible = _service.Leaving;
        if (_showWrite)
        {
            Clear(_writeItems);
            var names = new[] { "校验草稿", "载入并校验候选包", "复核源文件没被外部修改", "写入文件", "重新载入内容" };
            for (var index = 0; index < names.Length; index++)
            {
                var stage = index + 1;
                var marker = stage < (int)_service.WriteStage || _service.LastPublishSucceeded == true ? "完成" : stage == (int)_service.WriteStage
                    ? _service.IsBusy ? "进行中" : "已停止" : "未执行";
                Text(_writeItems, $"{stage}. {names[index]} · {marker}");
            }
            if (!_service.IsBusy)
            {
                Text(_writeItems, _service.Message ?? "");
                if (_service.LastWrite is { } result)
                {
                    foreach (var file in result.Completed) Text(_writeItems, "已完成：" + file.RelativePath);
                    if (result.Failed is { } failure) Text(_writeItems, "失败：" + failure.RelativePath);
                    foreach (var file in result.Remaining) Text(_writeItems, "未执行：" + file.RelativePath);
                    if (result.CleanupMessage is { } cleanup) Text(_writeItems, "临时文件清理失败：" + cleanup);
                }
                AddIssues(_writeItems, _service.Issues);
            }
        }
        UpdateDialogFocus();
    }

    private void ShowProblems(IEnumerable<AuthoringIssue> issues)
    {
        Clear(_problemItems); var list = issues.ToArray();
        if (list.Length == 0) Text(_problemItems, "当前没有错误或警告。");
        AddIssues(_problemItems, list); ShowModal(_problemDialog);
    }

    private void AddIssues(Node parent, IEnumerable<AuthoringIssue> issues)
    {
        foreach (var issue in issues)
        {
            var row = new VBoxContainer(); parent.AddChild(row);
            var diagnostic = issue.Diagnostic;
            Text(row, $"{diagnostic.Code} · {diagnostic.Message}");
            Text(row, (diagnostic.Source is { } source ? source.Package.Value + ": " + source.Path + " " : "") + diagnostic.Field);
            row.AddChild(UiStyle.Button("LocateDiagnostic", "定位", () =>
            {
                if (issue.Definition is { } address && _service.Package is not null && _service.Definitions.Contains(address))
                {
                    _showWrite = false; _writeDialog.Hide(); _problemDialog.Hide(); UpdateDialogFocus();
                    _service.OpenDefinition(address, false); _showDefinitions = false; _showPreview = false; Arrange();
                }
                else
                {
                    _showWrite = false; _writeDialog.Hide(); ShowProblems([issue]);
                    Text(_problemItems, "此诊断没有对应的可编辑定义，请按上面的文件与字段信息直接检查源文件。");
                }
            }));
        }
    }

    private void BuildAssets(VBoxContainer body)
    {
        var relative = new LineEdit { Name = "AssetRelativePath", PlaceholderText = "资产包相对路径，如 textures/metal.png" }; body.AddChild(relative);
        var asset = new FileDialog { Title = "选择资产文件", Access = FileDialog.AccessEnum.Filesystem, FileMode = FileDialog.FileModeEnum.OpenFile };
        AddChild(asset); asset.FileSelected += source => _service.AddAsset(relative.Text, source);
        body.AddChild(UiStyle.Button("AddAsset", "选择并加入资产", () => asset.PopupCentered(new(900, 650))));
    }
}
