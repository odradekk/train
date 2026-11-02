using System.Collections.Immutable;
using Godot;
using Train.App;
using Train.Content.Contracts;
using Train.Core.Contracts;

namespace Train.Ui;

/// <summary>
/// 选择当地车辆、调整顺序，并用核心预览检查编组。
/// Decision note: 2026-10-31-object-centric-management-screens
/// </summary>
internal partial class TrainFormationWorkspace : Control
{
    internal event Action? Opened;
    internal event Action<TrainId, bool>? FormationSaved;
    private HostCoordinator _host = null!;
    private Func<PanelTarget?, CoreCommand, SubmissionResult> _submit = null!;
    private PanelTarget? _target;
    private NodeId _node;
    private TrainId? _train;
    private ICoreReadView _view = null!;
    private IContentView _content = null!;
    private CoreRevision? _revision;
    private readonly List<VehicleId> _selection = [];
    private readonly Dictionary<VehicleId, CheckBox> _checks = [];
    private readonly Dictionary<VehicleId, VBoxContainer> _rows = [];
    private VBoxContainer _available = null!, _ordered = null!;
    private Label _title = null!, _statistics = null!, _reason = null!, _destination = null!, _empty = null!;
    private Button _form = null!, _formOnly = null!;
    private TrainFormationPreview? _preview;
    private string? _previewError, _submissionError;
    private bool _current;

    public override void _Ready() => SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
    internal void Initialize(HostCoordinator host, Func<PanelTarget?, CoreCommand, SubmissionResult> submit)
    {
        _host = host; _submit = submit; Name = "TrainFormationWorkspace"; MouseFilter = MouseFilterEnum.Ignore;
        var panel = new PanelContainer { Name = "Formation" }; AddChild(panel);
        panel.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var layout = new VBoxContainer { Name = "Layout" }; panel.AddChild(layout);
        layout.AddThemeConstantOverride("separation", 10);
        var heading = new HBoxContainer(); layout.AddChild(heading);
        _title = UiStyle.Label("编组列车", true); _title.SizeFlagsHorizontal = SizeFlags.ExpandFill; heading.AddChild(_title);
        var close = UiStyle.Button("Close", "关闭", Hide); close.TooltipText = "关闭编组（Esc）"; heading.AddChild(close);
        layout.AddChild(new HSeparator());
        var body = new ScrollContainer { Name = "ColumnsScroll", VerticalScrollMode = ScrollContainer.ScrollMode.Disabled,
            SizeFlagsVertical = SizeFlags.ExpandFill }; layout.AddChild(body);
        var columns = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        columns.AddThemeConstantOverride("separation", 12); body.AddChild(columns);
        var left = Column(columns, "Candidates", "停在这里的车辆", 260);
        left.AddChild(Wrap("勾选要编入的车辆。"));
        _available = Scroll(left, "AvailableVehicles");
        _empty = Wrap("这里没有可编组的玩家车辆。"); left.AddChild(_empty);
        var right = Column(columns, "Order", "编组顺序", 320);
        right.AddChild(Wrap("顺序只影响外观，不影响牵引。"));
        _ordered = Scroll(right, "OrderedVehicles");
        _statistics = Wrap(""); _statistics.Name = "Statistics"; right.AddChild(_statistics);
        layout.AddChild(new HSeparator());
        _reason = Wrap(""); _reason.Name = "Reason"; layout.AddChild(_reason);
        _destination = Wrap(""); _destination.Name = "Destination"; layout.AddChild(_destination);
        var actions = new HFlowContainer(); layout.AddChild(actions);
        actions.AddChild(UiStyle.Button("Cancel", "取消", Hide));
        _formOnly = UiStyle.Button("FormOnly", "只编组", () => Form(false)); actions.AddChild(_formOnly);
        _form = UiStyle.Button("Form", "编组并编辑计划", () => Form(true), true); actions.AddChild(_form);
        Hide();
    }

    private static VBoxContainer Column(HBoxContainer parent, string name, string title, float width)
    {
        var column = new VBoxContainer { Name = name, CustomMinimumSize = new(width, 0),
            SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        column.AddThemeConstantOverride("separation", 8); column.AddChild(UiStyle.Label(title, true)); parent.AddChild(column);
        return column;
    }
    private static VBoxContainer Scroll(VBoxContainer parent, string name)
    {
        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        var rows = new VBoxContainer { Name = name, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        scroll.AddChild(rows); parent.AddChild(scroll); return rows;
    }
    private static Label Wrap(string text) => new() { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart,
        SizeFlagsHorizontal = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore };
    private static void Clear(Node parent)
    {
        foreach (var child in parent.GetChildren()) { parent.RemoveChild(child); child.QueueFree(); }
    }

    internal void Open(PanelTarget target, NodeId node, ICoreReadView view, IContentView content)
        => Open(target, node, null, [], view, content);
    internal void Open(PanelTarget target, TrainView train, ICoreReadView view, IContentView content)
        => Open(target, train.Position is TrainPosition.Node at ? at.Id : default, train.Id, train.Vehicles, view, content);
    private void Open(PanelTarget target, NodeId node, TrainId? train, ImmutableArray<VehicleId> vehicles,
        ICoreReadView view, IContentView content)
    {
        _target = target; _node = node; _train = train; _view = view; _content = content; _revision = null;
        _selection.Clear(); _selection.AddRange(vehicles); _checks.Clear(); _rows.Clear(); Clear(_available); Clear(_ordered);
        _submissionError = null;
        _form.Text = train is null ? "编组并编辑计划" : "保存编组"; _formOnly.Visible = train is null;
        Show(); Opened?.Invoke(); RefreshState(target.Binding, view, content);
    }

    internal void Refresh(HostBinding binding, ICoreReadView view, IContentView content)
    {
        if (!IsVisibleInTree() && (_target is null || _target.Binding == binding)) return;
        RefreshState(binding, view, content);
    }
    private void RefreshState(HostBinding binding, ICoreReadView view, IContentView content)
    {
        _current = _target is not null && _target.IsAvailable(binding, view, _host.PanelGenerationCurrent(_target.Binding, _target.Generation));
        if (!_current) { Footer(); return; }
        if (_revision != view.Revision || !ReferenceEquals(_content, content))
        {
            _view = view; _content = content; _revision = view.Revision;
            _title.Text = $"{(_train is null ? "编组列车" : "调整编组")} · {PlaceName()}";
            _destination.Text = $"移出的车辆会停在{PlaceName()}。"; _destination.Visible = _train is not null;
            Candidates(); Order(); Validate();
        }
        else Footer();
    }
    private void Candidates()
    {
        var eligible = new List<InstanceView>();
        if (_node.Value != 0 && _view.FindLocation(new WorldLocation.Node(_node)) is Lookup<LocationView>.Found found)
            foreach (var id in found.Value.Instances.OfType<InstanceId.Vehicle>().OrderBy(id => id.Id.Value))
                if (_view.FindInstance(id) is Lookup<InstanceView>.Found { Value: var instance } &&
                    instance.Ownership == SettlementRelation.PlayerOwned && instance.Details is InstanceDetails.Vehicle vehicle &&
                    (vehicle.Train is null || vehicle.Train == _train)) eligible.Add(instance);
        foreach (var id in _checks.Keys.Where(id => !eligible.Any(i => i.Id == new InstanceId.Vehicle(id))).ToArray())
        {
            var check = _checks[id];
            if (_selection.Contains(id)) { check.Text = "车辆已不可用"; check.Disabled = true; }
            else { _available.RemoveChild(check); check.QueueFree(); _checks.Remove(id); }
        }
        for (var index = 0; index < eligible.Count; index++)
        {
            var instance = eligible[index]; var id = ((InstanceId.Vehicle)instance.Id).Id;
            if (!_checks.TryGetValue(id, out var check))
            {
                check = new CheckBox { Name = $"Vehicle{id.Value}", SizeFlagsHorizontal = SizeFlags.ExpandFill,
                    TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis };
                _checks.Add(id, check); _available.AddChild(check);
                check.Toggled += selected =>
                {
                    if (selected) _selection.Add(id); else _selection.Remove(id);
                    Changed();
                };
            }
            var name = _content.FindDesign(instance.Design) is ContentLookup<DesignEvaluation>.Found { Value: DesignEvaluation.Complete complete }
                ? complete.Definition.Display.Name : "车辆";
            check.Text = $"第{index + 1}辆 · {name} · {EconomyPanels.SizeName(((InstanceDetails.Vehicle)instance.Details).Size)}";
            check.TooltipText = check.Text; check.Disabled = false; check.SetPressedNoSignal(_selection.Contains(id));
        }
        _empty.Visible = eligible.Count == 0;
    }
    private void Changed() { _submissionError = null; Order(); Validate(); }
    private void Order()
    {
        foreach (var id in _rows.Keys.Where(id => !_selection.Contains(id)).ToArray())
        { var row = _rows[id]; _ordered.RemoveChild(row); row.QueueFree(); _rows.Remove(id); }
        for (var index = 0; index < _selection.Count; index++)
        {
            var id = _selection[index];
            if (!_rows.TryGetValue(id, out var row))
            {
                row = new VBoxContainer { Name = $"Vehicle{id.Value}" }; _rows.Add(id, row); _ordered.AddChild(row);
                var label = Wrap(""); label.Name = "Name"; row.AddChild(label);
                var controls = new HFlowContainer { Name = "Controls" }; row.AddChild(controls);
                controls.AddChild(UiStyle.Button("MoveUp", "前移", () => Move(id, -1)));
                controls.AddChild(UiStyle.Button("MoveDown", "后移", () => Move(id, 1)));
                controls.AddChild(UiStyle.Button("Remove", "移出", () =>
                {
                    _selection.Remove(id); if (_checks.TryGetValue(id, out var check)) check.SetPressedNoSignal(false);
                    Changed();
                    if (check is not null) check.GrabFocus();
                }));
                row.AddChild(new HSeparator());
            }
            _ordered.MoveChild(row, index);
            row.GetNode<Label>("Name").Text = $"{index + 1}. {(_checks.TryGetValue(id, out var candidate) ? candidate.Text : "车辆已不可用")}";
            row.GetNode<Button>("Controls/MoveUp").Disabled = index == 0;
            row.GetNode<Button>("Controls/MoveDown").Disabled = index == _selection.Count - 1;
        }
    }
    private void Move(VehicleId id, int offset)
    {
        var index = _selection.IndexOf(id); var next = index + offset;
        if (next < 0 || next >= _selection.Count) return;
        _selection.RemoveAt(index); _selection.Insert(next, id); Changed();
        _rows[id].GetNode<Button>("Controls/Remove").GrabFocus();
    }
    private void Validate()
    {
        _preview = null; _previewError = null;
        switch (_view.PreviewTrainFormation(_selection.ToImmutableArray(), _train))
        {
            case CoreResult<TrainFormationPreview>.Success success: _preview = success.Value; break;
            case CoreResult<TrainFormationPreview>.Failure failure: _previewError = Problems(failure.Problems); break;
            case CoreResult<TrainFormationPreview>.Faulted failed: _previewError = CommandDiagnostics.Describe(failed.Fault); break;
        }
        _statistics.Text = _preview is { } value
            ? $"总长 {value.LengthMeters:0.##} 米\n总质量 {value.MassTons:0.##} 吨\n牵引力 {value.TractionKilonewtons:0.##} 千牛\n载量 {value.Capacity} 件"
            : "总长 —\n总质量 —\n牵引力 —\n载量 —";
        Footer();
    }
    private string Problems(IEnumerable<CoreProblem> problems) => string.Join("\n",
        problems.Select(problem => CommandDiagnostics.Describe(problem with { Subject = null }, _content)));
    private void Footer()
    {
        var unavailable = !_current ? "已不存在：编组草稿所属对象或对局已失效，请重新打开。"
            : _host.IsBusy ? "正在处理其他操作，请稍后再试。" : _view.Fault is not null ? CommandDiagnostics.DescribeFrozen(_view.Fault) : null;
        _form.Disabled = _formOnly.Disabled = unavailable is not null || _preview is null || !_preview.Blockers.IsEmpty ||
            _preview.DepartureProblem?.Code is "control_missing" or "traction_missing";
        _reason.Text = unavailable ?? _submissionError ?? (_checks.Count == 0 ? "这里没有可编组的玩家车辆。"
            : _selection.Count == 0 ? "请勾选左侧车辆。" : _previewError ??
            (_preview is { } preview ? Problems(preview.Blockers.Concat(preview.DepartureProblem is { } departure ? [departure] : [])) : ""));
        _reason.AddThemeColorOverride("font_color", _form.Disabled ? UiStyle.Error : UiStyle.Warning);
    }
    private void Form(bool editPlan)
    {
        if (_host.GetStatus() is { } status && _host.Content is { } content) RefreshState(status.Binding, status.View, content);
        if (!_current) return;
        Validate(); if (_form.Disabled) return;
        CoreCommand command = _train is { } train ? new CoreCommand.ReplaceTrainFormation(train, _selection.ToImmutableArray())
            : new CoreCommand.FormTrain(_selection.ToImmutableArray());
        var result = _submit(_target, command);
        if (result is SubmissionResult.Completed { Receipt.Decision: CommandDecision.Accepted accepted })
        {
            var saved = _train ?? accepted.Created.OfType<WorldObjectId.Train>().Single().Id;
            Hide(); FormationSaved?.Invoke(saved, _train is null && editPlan);
        }
        else
        {
            _submissionError = result switch
            {
                SubmissionResult.NotAccepted rejected => Problems([rejected.Reason]),
                SubmissionResult.Completed { Receipt.Decision: CommandDecision.Rejected rejected } => Problems(rejected.Problems),
                SubmissionResult.Completed { Receipt.Decision: CommandDecision.Faulted failed } => CommandDiagnostics.Describe(failed.Fault),
                _ => null
            };
            Footer();
        }
    }
    private string PlaceName() => _view.World.Nodes.FirstOrDefault(node => node.Id == _node)?.Occupant is NodeOccupant.Settlement settlement &&
        _content.FindSettlementLayout(settlement.Id) is ContentLookup<SettlementLayoutProjection>.Found layout ? layout.Value.Display.Name : "当前地点";
}
