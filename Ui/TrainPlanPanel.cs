using System.Globalization;
using Godot;
using Train.App;
using Train.App.Logging;
using Train.Content.Contracts;
using Train.Core.Contracts;

namespace Train.Ui;

internal partial class TrainPlanPanel : Control
{
    private HostCoordinator _host = null!;
    private TrainLogger _logger = null!;
    private Action<string> _showError = null!;
    private PanelTarget? _target;
    private TrainId _train;
    private NodeId[] _nodes = [];
    private readonly List<Row> _rows = [];
    private VBoxContainer _rowsBox = null!;
    private CheckBox _loop = null!;
    private Button _apply = null!;
    private bool _protected;
    private int _nextRow;

    public override void _Ready() => SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
    private sealed record Row(HBoxContainer Box, OptionButton Node, OptionButton Kind, LineEdit Seconds,
        string InitialSeconds, ulong? InitialSteps);

    internal void Initialize(HostCoordinator host, TrainLogger logger, Action<string> showError)
    {
        _host = host; _logger = logger; _showError = showError;
        Name = "TrainPlanPanel";
        MouseFilter = MouseFilterEnum.Ignore;
        UiStyle.Panel(this, "Plan", "运行计划", new(310, 90), 660, Hide, out var body);
        _loop = new CheckBox { Name = "Loop", Text = "循环运行（未勾选为单次）" };
        body.AddChild(_loop);
        var scroll = new ScrollContainer { Name = "RowsScroll", CustomMinimumSize = new(0, 300) };
        _rowsBox = new VBoxContainer { Name = "Rows", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        scroll.AddChild(_rowsBox); body.AddChild(scroll);
        body.AddChild(UiStyle.Button("AddStop", "添加计划项", () => { if (!_protected) AddRow(null); }));
        body.AddChild(UiStyle.Label("节点从列表选择；停站秒数为非负整数。未编辑的原停留时间保留精确步数。"));
        body.AddChild(new Label { Name = "Reason", AutowrapMode = TextServer.AutowrapMode.WordSmart });
        var actions = new HBoxContainer();
        _apply = UiStyle.Button("Apply", "应用计划", Apply, true);
        actions.AddChild(_apply); actions.AddChild(UiStyle.Button("Cancel", "取消", Hide));
        body.AddChild(actions);
        Hide();
    }

    internal void Open(PanelTarget target, TrainView train, ICoreReadView view, IContentView content)
    {
        _target = target; _train = train.Id;
        _nodes = view.World.Nodes.Select(item => item.Id).ToArray();
        foreach (var row in _rows) { _rowsBox.RemoveChild(row.Box); row.Box.QueueFree(); }
        _rows.Clear();
        _nextRow = 0;
        _protected = train.Plan?.Stops.Any(stop => stop is PlanStop.Station station && !station.GoodsRules.IsEmpty) == true;
        _loop.ButtonPressed = train.Plan?.Loop ?? false;
        _loop.Disabled = _protected;
        GetNode<Button>("Plan/Layout/Body/AddStop").Disabled = _protected;
        GetNode<Label>("Plan/Layout/Body/Reason").Text = _protected
            ? "此计划含自动货物规则，当前表单不支持编辑，已禁止修改以保留规则。" : "";
        if (train.Plan is { } plan) foreach (var stop in plan.Stops) AddRow(stop);
        else AddRow(null);
        Show();
        Refresh(target.Binding, view, content);
    }

    private void AddRow(PlanStop? stop)
    {
        var box = new HBoxContainer { Name = $"Row{_nextRow++}" };
        var node = new OptionButton { Name = "Node", SizeFlagsHorizontal = SizeFlags.ExpandFill, Disabled = _protected };
        foreach (var id in _nodes) node.AddItem($"节点 {id.Value}");
        var selected = stop switch { PlanStop.Station station => station.Node, PlanStop.Waypoint waypoint => waypoint.Node, _ => default };
        var index = Array.IndexOf(_nodes, selected);
        if (index >= 0) node.Select(index);
        else if (stop is not null) { node.AddItem($"已失效节点 {selected.Value}"); node.Select(_nodes.Length); }
        var kind = new OptionButton { Name = "Kind", Disabled = _protected };
        kind.AddItem("停靠站"); kind.AddItem("途经点"); kind.Select(stop is PlanStop.Waypoint ? 1 : 0);
        var steps = (stop as PlanStop.Station)?.DwellSteps;
        var initial = steps is { } value ? (value / (decimal)SimulationTime.StepsPerSecond).ToString(CultureInfo.InvariantCulture) : "0";
        var seconds = new LineEdit { Name = "DwellSeconds", Text = initial, PlaceholderText = "停站秒数", CustomMinimumSize = new(120, 0), Editable = !_protected };
        var row = new Row(box, node, kind, seconds, initial, steps);
        kind.ItemSelected += _ => seconds.Editable = !_protected && kind.Selected == 0;
        seconds.Editable = !_protected && kind.Selected == 0;
        box.AddChild(node); box.AddChild(kind); box.AddChild(seconds);
        var up = UiStyle.Button("MoveUp", "↑", () => Move(row, -1));
        var down = UiStyle.Button("MoveDown", "↓", () => Move(row, 1));
        var remove = UiStyle.Button("Remove", "删除", () =>
        {
            if (_protected) return;
            _rows.Remove(row); _rowsBox.RemoveChild(box); box.QueueFree();
        });
        up.Disabled = down.Disabled = remove.Disabled = _protected;
        box.AddChild(up); box.AddChild(down); box.AddChild(remove);
        _rows.Add(row); _rowsBox.AddChild(box);
    }

    private void Move(Row row, int offset)
    {
        if (_protected) return;
        var index = _rows.IndexOf(row); var next = index + offset;
        if (next < 0 || next >= _rows.Count) return;
        _rows.RemoveAt(index); _rows.Insert(next, row); _rowsBox.MoveChild(row.Box, next);
    }

    internal void Refresh(HostBinding binding, ICoreReadView view, IContentView content)
    {
        var unavailable = _target is null || !_target.IsAvailable(binding, content.Generation, view);
        _apply.Disabled = _protected || _host.IsBusy || unavailable;
        if (unavailable && _target is not null)
            GetNode<Label>("Plan/Layout/Body/Reason").Text = "已不存在：草稿所属对象或对局已失效，请从当前列表重新打开。";
    }

    private void Apply()
    {
        var status = _host.GetStatus(); var content = _host.Content;
        if (_protected || status is null || content is null || _target is null ||
            !_target.IsAvailable(status.Binding, content.Generation, status.View) || _host.IsBusy)
        {
            Error("当前计划不可提交：对象已失效、含不支持的货物规则或宿主正在操作。"); return;
        }
        var inputs = _rows.Select(row => new PlanRowInput(
            row.Node.Selected >= 0 && row.Node.Selected < _nodes.Length ? _nodes[row.Node.Selected] : default,
            row.Kind.Selected == 0, row.Seconds.Text,
            row.Seconds.Text == row.InitialSeconds ? row.InitialSteps : null)).ToArray();
        if (!UiInput.TryPlan(_train, _loop.ButtonPressed, inputs, out var command, out var error)) { Error(error!); return; }
        var result = _host.Submit(_target.Binding, command!, CommandSource.UserInterface);
        if (CommandDiagnostics.Report(_logger, result) is { } problem) _showError(problem);
        else Hide();
    }

    private void Error(string message)
    {
        _logger.Log(LogLevel.Warning, "ui", "train_plan_invalid", message); _showError(message);
    }
}
