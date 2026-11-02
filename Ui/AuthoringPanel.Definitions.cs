using Godot;
using Train.App.Authoring;
using Train.Content.Contracts;
using Train.Core.Contracts;

namespace Train.Ui;

internal partial class AuthoringPanel
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
        ("聚居地模板", id => new DefinitionDraft.SettlementTemplate(new(id, new(id, ""), null, null, null, [], [], [], [])))
    ];
    private static DefinitionDraft.Blueprint Blueprint(string id, DesignSpecification specification) =>
        new(id, new(new(id, ""), specification, new([], [], [])));
    private Tree _tree = null!;
    private LineEdit _search = null!;
    private VBoxContainer _drafts = null!;
    private readonly Dictionary<AuthoringDefinitionAddress, TreeItem> _treeItems = [];
    private bool _selectingTree;

    private void BuildDefinitions()
    {
        _sidebar = new PanelContainer(); _body.AddChild(_sidebar);
        var box = new VBoxContainer(); _sidebar.AddChild(box);
        _search = new LineEdit { Name = "DefinitionSearch", PlaceholderText = "搜索定义", TooltipText = "按定义本地 ID 搜索" };
        box.AddChild(_search); _search.TextChanged += _ => RebuildTree();
        var actions = new HFlowContainer(); box.AddChild(actions);
        actions.AddChild(UiStyle.Button("NewDefinition", "新建定义…", () => ShowModal(_newDefinition)));
        actions.AddChild(UiStyle.Button("DeleteDefinition", "删除定义…", ConfirmDelete));
        _tree = new Tree { Name = "DefinitionTree", HideRoot = true, SizeFlagsVertical = SizeFlags.ExpandFill }; box.AddChild(_tree);
        _tree.ItemSelected += () =>
        {
            if (_selectingTree || _tree.GetSelected() is not { } selected) return;
            var address = _treeItems.FirstOrDefault(pair => pair.Value == selected).Key;
            if (address is null) return;
            _service.OpenDefinition(address, false); _showDefinitions = false; Arrange();
        };
        Text(box, "只读种类请直接修改源文件，再重新载入。");
        box.AddChild(UiStyle.Label("以前保存的草稿", true));
        var scroll = new ScrollContainer { CustomMinimumSize = new(0, 120), FollowFocus = true }; box.AddChild(scroll);
        _drafts = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; scroll.AddChild(_drafts);
        RebuildTree();
    }

    private static string KindName(DefinitionKind kind) => kind switch
    {
        DefinitionKind.Voxel => "体素", DefinitionKind.Blueprint => "设计", DefinitionKind.SettlementType => "聚居地类型",
        DefinitionKind.SettlementTemplate => "聚居地模板", DefinitionKind.LairType => "生物聚落类型", DefinitionKind.Process => "流程",
        DefinitionKind.PopulationRule => "人口规则", DefinitionKind.MarketRule => "市场规则", DefinitionKind.ConstructionRule => "施工规则",
        DefinitionKind.RailwayRule => "铁路规则", DefinitionKind.CombatRule => "交战规则", DefinitionKind.GenerationProfile => "生成配置",
        DefinitionKind.TerrainType => "地貌类型", _ => kind.ToString()
    };

    private void RebuildTree()
    {
        if (_tree is null) return;
        _selectingTree = true; _tree.Clear(); _treeItems.Clear();
        var root = _tree.CreateItem();
        foreach (var kind in Enum.GetValues<DefinitionKind>())
        {
            var editable = kind is DefinitionKind.Voxel or DefinitionKind.Blueprint or DefinitionKind.SettlementType
                or DefinitionKind.SettlementTemplate or DefinitionKind.LairType;
            var name = KindName(kind);
            var addresses = _service.Definitions.Where(address => address.Kind == kind &&
                (address.LocalId.Contains(_search.Text.Trim(), StringComparison.OrdinalIgnoreCase)
                    || name.Contains(_search.Text.Trim(), StringComparison.OrdinalIgnoreCase))).OrderBy(address => address.LocalId, StringComparer.Ordinal).ToArray();
            if (editable && addresses.Length == 0) continue;
            if (!editable && _search.Text.Length > 0 && !name.Contains(_search.Text.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
            var group = _tree.CreateItem(root); group.SetText(0, editable ? name : name + "（只读）"); group.SetSelectable(0, false);
            if (!editable) group.SetTooltipText(0, "这类定义还没有表单。请直接修改包里的源文件，再重新载入。");
            foreach (var address in addresses)
            {
                var item = _tree.CreateItem(group); item.SetText(0, address.LocalId); _treeItems.Add(address, item);
            }
        }
        _selectingTree = false; RefreshTreeMarks();
    }

    private void RefreshTreeMarks()
    {
        var issues = _service.Issues;
        foreach (var (address, item) in _treeItems)
        {
            var labels = new List<string>();
            if (issues.Any(issue => issue.Definition == address)) labels.Add("有问题");
            if (_service.DirtyDefinition == address) labels.Add("未保存");
            if (_service.IsDeleted(address)) labels.Add("待删除");
            else if (_service.IsPending(address)) labels.Add("已存草稿");
            item.SetText(0, address.LocalId + (labels.Count == 0 ? "" : "  [" + string.Join(" · ", labels) + "]"));
            item.SetTooltipText(0, _service.Target?.Package.Value + ":" + address.LocalId);
            if (_service.Form is { } form && AuthoringService.Address(form) == address)
            { _selectingTree = true; item.Select(0); _selectingTree = false; }
        }
    }

    private void RefreshDrafts()
    {
        Clear(_drafts);
        var saved = _service.Drafts();
        if (saved.IsEmpty) _drafts.AddChild(UiStyle.Label("暂无以前保存的草稿。"));
        foreach (var draft in saved)
        {
            if (draft.Target is not DraftTarget.PackageDefinition target) continue;
            var stale = target.SourceVersion != _service.Package?.SourceVersion;
            var name = $"{KindName(target.Kind)} · {target.LocalId}";
            var button = UiStyle.Button(stale ? "OpenDraftOnCurrentSource" : "OpenDraft",
                stale ? name + "：在当前源包上打开" : name, () => _service.OpenDraft(draft, stale));
            button.ClipText = true; button.TooltipText = name + $"\n草稿 {draft.Id.Value}" +
                (stale ? "\n来源已变化；请手动处理修改。工具不会自动合并。" : "\n打开并重新保存后才加入本次待写入修改。");
            _drafts.AddChild(button);
        }
    }

    private void RefreshSources()
    {
        _sourceUpdating = true; _source.Clear(); _source.AddItem("选择要编辑的源包…");
        _coreIndex = -1;
        if (_service.CanWriteCore) { _coreIndex = _source.ItemCount; _source.AddItem("core（开发源码）"); }
        _sourceChoices = _service.Sources.ToArray();
        var selected = _service.Target?.IsCore == true ? _coreIndex : 0;
        foreach (var source in _sourceChoices)
        {
            if (source.Package == _service.Target?.Package) selected = _source.ItemCount;
            _source.AddItem(source.Name + (source.State == PackageState.Invalid ? "（无效）" : source.State == PackageState.Available ? "（本地模组）" : "（未载入）"));
            _source.SetItemTooltip(_source.ItemCount - 1, source.Directory + "\n" + (source.Package?.Value ?? "无法识别包 ID"));
        }
        _newModIndex = _source.ItemCount; _source.AddItem("新建本地模组…");
        _source.Select(Math.Max(0, selected)); _sourceUpdating = false;
    }

    private void SourceSelected(long selected)
    {
        if (_sourceUpdating) return;
        if (selected == _coreIndex) _service.OpenCore();
        else if (selected == _newModIndex) { PrepareNewMod(); RefreshSources(); }
        else if (selected > 0)
        {
            var source = _sourceChoices[(int)selected - (_coreIndex >= 0 ? 2 : 1)];
            if (source.Package is { } id) _service.Open(source.Directory, id);
            else { ShowProblems(source.Diagnostics.Select(item => new AuthoringIssue(item))); RefreshSources(); }
        }
    }

    private static void Clear(Node node)
    {
        foreach (var child in node.GetChildren()) { node.RemoveChild(child); child.QueueFree(); }
    }
}
