using System.Collections.Immutable;
using Godot;
using Train.App;
using Train.Content.Contracts;
using Train.Core.Contracts;
using static Train.Ui.EconomyPanelControls;
using static Train.Ui.EconomyPanels;

namespace Train.Ui;

/// <summary>
/// 按蓝图制造、在指定地块建造，或修理和改装指定实例。
/// Decision note: 2026-10-31-object-centric-management-screens
/// Decision note: 2026-10-31-design-editor-direct-editing
/// </summary>
internal partial class ConstructionWorkspace : Control
{
    private HostCoordinator _host = null!;
    private Func<PanelTarget?, CoreCommand, IContentCandidate?, SubmissionResult> _submit = null!;
    private PanelTarget? _target;
    private SettlementId _settlementId;
    private SettlementView _settlement = null!;
    private PlotId? _plot;
    private InstanceId? _instance;
    private BlueprintAddress? _blueprint;
    private DraftId? _draft;
    private bool _repairSelected;
    private ICoreReadView _view = null!;
    private IContentView _content = null!;
    private ICoreReadView? _shownView;
    private IContentView? _shownContent;
    private IContentCandidate? _candidate;
    private DesignInput? _design;
    private DesignChangeEvaluation? _difference;
    private JobQuote? _quote;
    private bool _refreshing, _submitted;
    private Label _title = null!, _reason = null!, _summary = null!, _result = null!, _empty = null!, _consequence = null!;
    private VBoxContainer _choices = null!, _sources = null!;
    private GridContainer _materials = null!;
    private Button _confirm = null!, _edit = null!;
    private readonly List<InventoryId> _sourceOrder = [];
    private readonly HashSet<InventoryId> _enabledSources = [];
    internal event Action? Opened;
    internal event Action<InstanceId>? EditRequested;

    internal void Initialize(HostCoordinator host, Func<PanelTarget?, CoreCommand, IContentCandidate?, SubmissionResult> submit)
    {
        _host = host; _submit = submit; Name = "ConstructionWorkspace";
        MouseFilter = MouseFilterEnum.Ignore;
        var panel = new PanelContainer(); AddChild(panel); panel.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var layout = new VBoxContainer(); panel.AddChild(layout);
        layout.AddThemeConstantOverride("separation", 10);
        var heading = new HBoxContainer(); layout.AddChild(heading);
        _title = Text(heading, "WorkTitle", ""); _title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        heading.AddChild(UiStyle.Button("CloseWork", "关闭", Close));
        layout.AddChild(new HSeparator());
        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill,
            VerticalScrollMode = ScrollContainer.ScrollMode.Disabled, FollowFocus = true }; layout.AddChild(scroll);
        var columns = new BoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill }; scroll.AddChild(columns);
        columns.AddThemeConstantOverride("separation", 16);
        var left = Column(columns, "选择", 240);
        _choices = new() { Name = "WorkChoices" }; left.AddChild(_choices);
        _empty = Text(left, "EmptyChoices", "");
        _edit = UiStyle.Button("EditWorkTarget", "继续编辑改装方案…", () =>
        { if (CanAct && _instance is { } instance) EditRequested?.Invoke(instance); }); left.AddChild(_edit);
        var right = Column(columns, "材料与费用", 480);
        var materialsScroll = new ScrollContainer { Name = "MaterialsScroll", SizeFlagsHorizontal = SizeFlags.ExpandFill,
            VerticalScrollMode = ScrollContainer.ScrollMode.Disabled, FollowFocus = true };
        right.AddChild(materialsScroll);
        _materials = new() { Name = "Materials", Columns = 5, CustomMinimumSize = new(480, 0), SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _materials.AddThemeConstantOverride("h_separation", 12); materialsScroll.AddChild(_materials);
        _sources = new() { Name = "Sources" }; right.AddChild(_sources);
        _summary = Text(right, "WorkSummary", "");
        scroll.Resized += () =>
        {
            var narrow = scroll.Size.X < 736;
            columns.Vertical = narrow;
            var leftScroll = (ScrollContainer)left.GetParent();
            var rightScroll = (ScrollContainer)right.GetParent();
            scroll.VerticalScrollMode = narrow ? ScrollContainer.ScrollMode.Auto : ScrollContainer.ScrollMode.Disabled;
            leftScroll.VerticalScrollMode = rightScroll.VerticalScrollMode = narrow
                ? ScrollContainer.ScrollMode.Disabled : ScrollContainer.ScrollMode.Auto;
            leftScroll.CustomMinimumSize = new(narrow ? 0 : 240, 0);
            rightScroll.CustomMinimumSize = new(narrow ? 0 : 480, 0);
            columns.SizeFlagsVertical = narrow ? SizeFlags.Fill : SizeFlags.ExpandFill;
        };
        _reason = Text(right, "WorkReason", "");
        _result = Text(right, "WorkResult", "");
        layout.AddChild(new HSeparator());
        var footer = new VBoxContainer(); layout.AddChild(footer);
        _consequence = Text(footer, "WorkConsequence", "委托和开工后不能取消，材料和费用立即扣除。");
        _confirm = UiStyle.Button("ConfirmWork", "请选择目标", Confirm, true); footer.AddChild(_confirm);
        _confirm.SizeFlagsHorizontal = SizeFlags.ShrinkEnd;
        Hide();
    }

    private static VBoxContainer Column(BoxContainer parent, string title, float width)
    {
        var scroll = new ScrollContainer { CustomMinimumSize = new(width, 0),
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, FollowFocus = true,
            SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        parent.AddChild(scroll);
        var body = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        body.AddThemeConstantOverride("separation", 10); scroll.AddChild(body);
        Text(body, "Title", title); return body;
    }

    internal void OpenManufacture(PanelTarget source, SettlementId settlement) => Open(source, settlement, null, null);
    internal void OpenBuilding(PanelTarget source, SettlementId settlement, PlotId plot) => Open(source, settlement, plot, null);

    internal void OpenInstance(InstanceId instance)
    {
        if (_host.GetStatus() is not { } status || _host.Content is not { } content ||
            status.View.FindInstance(instance) is not Lookup<InstanceView>.Found
            { Value.Ownership: SettlementRelation.PlayerOwned, Value.Location: WorldLocation.Node node } ||
            status.View.World.Nodes.FirstOrDefault(item => item.Id == node.Id)?.Occupant is not NodeOccupant.Settlement settlement) return;
        Open(new(status.Binding, content.Generation, new WorldObjectId.Instance(instance)), settlement.Id, null, instance);
    }

    private void Open(PanelTarget source, SettlementId settlement, PlotId? plot, InstanceId? instance)
    {
        if (_host.GetStatus() is not { } status || _host.Content is not { } content ||
            !source.IsAvailable(status.Binding, status.View, _host.PanelGenerationCurrent(source.Binding, source.Generation))) return;
        var reuseSources = _target?.Binding == source.Binding && _settlementId == settlement && _instance == instance;
        ClearDesign();
        _target = source; _settlementId = settlement; _plot = plot; _instance = instance;
        _blueprint = null; _draft = null; _repairSelected = false; _submitted = false; _result.Text = "";
        if (!reuseSources) { _sourceOrder.Clear(); _enabledSources.Clear(); Clear(_sources); _sourceRows.Clear(); }
        _shownView = null; _shownContent = null;
        Show(); Opened?.Invoke(); Refresh(status.Binding, status.View, content);
    }

    internal void Refresh(HostBinding binding, ICoreReadView view, IContentView content)
    {
        if (_refreshing || _target is null) return;
        if (!_target.IsAvailable(binding, view, _host.PanelGenerationCurrent(_target.Binding, _target.Generation)) ||
            view.FindSettlement(_settlementId) is not Lookup<SettlementView>.Found found ||
            found.Value.Relation == SettlementRelation.Hostile)
        { _confirm.Disabled = true; _reason.Text = "对象或所属对局已失效，请重新打开。"; ClearDesign(); return; }
        if (!IsVisibleInTree() || _host.IsBusy) { _confirm.Disabled = true; return; }
        if (ReferenceEquals(_shownView, view) && ReferenceEquals(_shownContent, content))
        { UpdateConfirm(); return; }
        _refreshing = true;
        try
        {
            var contentChanged = !ReferenceEquals(_content, content);
            _view = view; _content = content; _settlement = found.Value;
            _shownView = view; _shownContent = content; _submitted = false;
            if (contentChanged || _instance is { } id && _difference is { } difference &&
                view.FindInstance(id) is Lookup<InstanceView>.Found current && current.Value.Design != difference.Core.Before) ClearDesign();
            _title.Text = $"{(_instance is not null ? "修理与改装" : _plot is not null ? "建造建筑" : "制造车辆")} · {PlaceName()}";
            RefreshSources(); BuildChoices(); Evaluate();
        }
        finally { _refreshing = false; }
    }

    private bool CanAct => _target is not null && !_host.IsBusy && _view.Fault is null &&
        _host.GetStatus() is { } status && _target.IsAvailable(status.Binding, status.View,
            _host.PanelGenerationCurrent(_target.Binding, _target.Generation));

    private void Changed()
    {
        _submitted = false; _result.Text = ""; ClearDesign();
        if (_refreshing) return;
        _refreshing = true;
        try { Evaluate(); }
        finally { _refreshing = false; }
    }

    private void ClearDesign()
    { _candidate?.Dispose(); _candidate = null; _design = null; _difference = null; _quote = null; }

    internal void Close() { ClearDesign(); Hide(); }
    public override void _ExitTree() => ClearDesign();
    private string PlaceName() => WorldPanels.NodeName(_settlement.Node, _view, _content);
    private string Describe(ImmutableArray<CoreProblem> problems) => string.Join("\n",
        problems.Select(problem => CommandDiagnostics.Describe(problem with { Subject = null }, _content)));
}
