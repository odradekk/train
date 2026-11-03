using Godot;
using Train.App;
using Train.Content.Contracts;
using Train.Core.Contracts;
using static Train.Ui.EconomyPanelControls;

namespace Train.Ui;

/// <summary>
/// 在线路上直接查看材料、开工和选择列车。
/// Decision note: 2026-10-31-object-centric-management-screens
/// </summary>
internal sealed partial class LineInspector
{
    private readonly HostCoordinator _host;
    private readonly Func<PanelTarget?, CoreCommand, SubmissionResult> _submit;
    private readonly Action<WorldObjectId> _select;
    private readonly Label _summary, _works, _feedback, _noBreaks;
    private readonly VBoxContainer _body, _breaks, _trains;
    private readonly OptionButton _sources, _automaticSource;
    private readonly CheckButton _automatic;
    private readonly WorkRow _add;
    private readonly Dictionary<BreakId, WorkRow> _repairs = [];
    private readonly Dictionary<TrainId, Button> _trainRows = [];
    private readonly List<SettlementId> _sourceIds = [];
    private ICoreReadView _view = null!;
    private IContentView _content = null!;
    private EdgeView _edge = null!;
    private SettlementId? _source, _quotedSource;
    private bool _current, _choosingAutomaticSource;
    internal PanelTarget Target { get; }
    internal PanelContainer Panel { get; }

    private sealed record WorkRow(VBoxContainer Root, Label Description, Button Button)
    {
        internal bool CanStart { get; set; }
    }

    internal LineInspector(Control parent, HostCoordinator host, PanelTarget target,
        Func<PanelTarget?, CoreCommand, SubmissionResult> submit, Action<WorldObjectId> select)
    {
        _host = host; Target = target; _submit = submit; _select = select;
        PanelContainer panel = null!;
        Panel = panel = UiStyle.Panel(parent, "LineInspector", "线路", new(408, 68), 420, () => panel.Hide(), out _body);
        Panel.GetNode<ScrollContainer>("Layout/Content").FollowFocus = true;
        _summary = Text(_body, "Summary", "");
        Text(_body, "SourceHeading", "施工材料来源");
        _sources = Sources(_body, "MaterialSource");
        _sources.ItemSelected += index => { _source = _sourceIds[(int)index]; ReadCurrent(); };
        Text(_body, "BreaksHeading", "断点修复");
        _breaks = new VBoxContainer { Name = "Breaks" }; _body.AddChild(_breaks);
        _noBreaks = Text(_breaks, "NoBreaks", "没有断点。");
        _add = CreateRow(_body, "AddTrack", () => StartWork(new RailwayWorkRequest.AddTrack(_edge.Map.Id), _add!));
        _automatic = new CheckButton { Name = "AutomaticRepair", Text = "自动修复" }; _body.AddChild(_automatic);
        _automaticSource = Sources(_body, "AutomaticRepairSource");
        _automaticSource.AddItem("选择自动修复材料来源");
        Text(_body, "WorkHeading", "施工中"); _works = Text(_body, "Works", "");
        Text(_body, "TrainHeading", "线路上的列车");
        _trains = new VBoxContainer { Name = "Trains" }; _body.AddChild(_trains);
        _feedback = Text(_body, "Feedback", "");
        _automatic.Toggled += enabled =>
        {
            if (!ReadCurrent()) return;
            _choosingAutomaticSource = enabled;
            if (!enabled && _edge.AutoRepairMaterialSource is not null)
                Submit(new CoreCommand.SetAutomaticRailRepair(_edge.Map.Id, null));
            RefreshAutomaticRepair();
        };
        _automaticSource.ItemSelected += index =>
        {
            if (index == 0) return;
            var source = _sourceIds[(int)index - 1];
            if (!ReadCurrent()) return;
            _choosingAutomaticSource = false;
            if (!_sourceIds.Contains(source))
            {
                _feedback.Text = "材料来源已不存在，请重新选择。";
                RefreshAutomaticRepair();
                return;
            }
            Submit(new CoreCommand.SetAutomaticRailRepair(_edge.Map.Id, source));
        };
    }

    private static OptionButton Sources(Node parent, string name)
    {
        var control = new OptionButton { Name = name, FitToLongestItem = false,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        parent.AddChild(control); return control;
    }

    internal void Refresh(HostBinding binding, ICoreReadView view, IContentView content)
    {
        _current = Target.IsAvailable(binding, view, _host.PanelGenerationCurrent(Target.Binding, Target.Generation));
        if (!_current || view.FindEdge(((WorldObjectId.Edge)Target.Object).Id) is not Lookup<EdgeView>.Found found)
        { _current = false; Invalidate(_body, _summary); return; }
        var changed = !ReferenceEquals(_view, view) || !ReferenceEquals(_content, content);
        _view = view; _content = content; _edge = found.Value;
        if (changed) BindSources();
        if (changed || _quotedSource != _source)
        {
            _quotedSource = _source;
            var map = _edge.Map;
            Panel.GetNode<Label>("Layout/Heading/Title").Text = $"{Place(map.Start)}—{Place(map.End)}";
            _summary.Text = $"长度 {map.LengthMeters:0.##} 米 · 铁轨 {map.Tracks}/{map.TrackLimit}";
            RefreshWorks(); RefreshTrains(); RefreshBreaks();
            RefreshPreview(_add, new RailwayWorkRequest.AddTrack(map.Id), map.Tracks == 0 ? "建设线路" : "加轨");
        }
        _sources.Disabled = _host.IsBusy || _sourceIds.Count == 0;
        foreach (var row in _repairs.Values.Append(_add)) row.Button.Disabled = _host.IsBusy || !row.CanStart;
        foreach (var button in _trainRows.Values) button.Disabled = _host.IsBusy;
        RefreshAutomaticRepair();
    }

    private void BindSources()
    {
        var ids = _view.World.Nodes.Select(node => node.Occupant).OfType<NodeOccupant.Settlement>()
            .Where(item => item.Relation == SettlementRelation.PlayerOwned).Select(item => item.Id).Distinct().OrderBy(id => id.Value).ToArray();
        if (!_sourceIds.SequenceEqual(ids))
        {
            _sourceIds.Clear(); _sourceIds.AddRange(ids); _sources.Clear(); _automaticSource.Clear();
            _automaticSource.AddItem("选择自动修复材料来源");
            foreach (var id in ids) { _sources.AddItem(SettlementName(id)); _automaticSource.AddItem(SettlementName(id)); }
            if (_source is null || !_sourceIds.Contains(_source.Value)) _source = ids.Length == 0 ? null : ids[0];
        }
        for (var index = 0; index < ids.Length; index++)
        {
            _sources.SetItemText(index, SettlementName(ids[index]));
            _automaticSource.SetItemText(index + 1, SettlementName(ids[index]));
        }
        if (_source is { } source) _sources.Select(_sourceIds.IndexOf(source));
        else _sources.Text = "没有自有聚居地";
    }

    private void RefreshAutomaticRepair()
    {
        var enabled = _edge.AutoRepairMaterialSource is not null || _choosingAutomaticSource;
        _automatic.SetPressedNoSignal(enabled);
        _automatic.Disabled = _host.IsBusy || _sourceIds.Count == 0 && !enabled;
        _automaticSource.Visible = enabled;
        _automaticSource.Disabled = _host.IsBusy || _sourceIds.Count == 0;
        _automaticSource.Select(_edge.AutoRepairMaterialSource is { } source ? _sourceIds.IndexOf(source) + 1 : 0);
    }

    private bool ReadCurrent()
    {
        if (!GodotObject.IsInstanceValid(Panel) || !Panel.IsVisibleInTree() || _host.GetStatus() is not { } status || _host.Content is not { } content)
            return false;
        Refresh(status.Binding, status.View, content);
        return _current && !_host.IsBusy;
    }

    private void StartWork(RailwayWorkRequest request, WorkRow row)
    {
        if (!ReadCurrent() || row.Button.Disabled || _source is not { } source) return;
        Submit(request switch
        {
            RailwayWorkRequest.AddTrack add => new CoreCommand.ExpandRail(add.Edge, source),
            RailwayWorkRequest.RepairBreak repair => new CoreCommand.RepairRail(repair.Edge, repair.Break, source),
            _ => throw new InvalidOperationException("未知铁路施工请求。")
        });
    }

    private void Submit(CoreCommand command)
    {
        var result = _submit(Target, command);
        _feedback.Text = result switch
        {
            SubmissionResult.Completed { Receipt.Decision: CommandDecision.Accepted } => "操作已受理。",
            SubmissionResult.Completed { Receipt.Decision: CommandDecision.Rejected rejected } => string.Join("\n", rejected.Problems.Select(ProblemText)),
            SubmissionResult.NotAccepted rejected => ProblemText(rejected.Reason),
            _ => "核心模拟发生故障。"
        };
        ReadCurrent();
    }

    private string Place(NodeId node) => WorldPanels.NodeName(node, _view, _content);
    private string SettlementName(SettlementId source) => _view.FindSettlement(source) is Lookup<SettlementView>.Found found
        ? Place(found.Value.Node) : "来源聚居地已不存在";

    internal static string Duration(ulong ticks)
    {
        var seconds = ticks / (decimal)SimulationTime.StepsPerSecond;
        if (seconds < 60) return $"{seconds:0.##} 秒";
        var minutes = (ulong)decimal.Ceiling(seconds / 60);
        var parts = new List<string>();
        if (minutes / 1440 > 0) parts.Add($"{minutes / 1440} 天");
        if (minutes % 1440 / 60 > 0) parts.Add($"{minutes % 1440 / 60} 时");
        if (minutes % 60 > 0) parts.Add($"{minutes % 60} 分");
        return string.Join(" ", parts);
    }
}
