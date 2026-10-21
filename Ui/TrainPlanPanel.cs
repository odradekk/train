using System.Globalization;
using Godot;
using Train.App;
using Train.Content.Contracts;
using Train.Core.Contracts;

namespace Train.Ui;

internal partial class TrainPlanPanel : Control
{
    private HostCoordinator _host = null!;
    private Func<PanelTarget?, CoreCommand, CommandDecision.Accepted?> _submit = null!;
    private Action<string> _reportError = null!;
    private PanelTarget? _target;
    private TrainId _train;
    private NodeId[] _nodes = [];
    private string[] _nodeNames = [];
    private readonly List<Row> _rows = [];
    private VBoxContainer _rowsBox = null!;
    private CheckBox _loop = null!;
    private Button _apply = null!;
    private Label _reason = null!;
    private ICoreReadView _view = null!;
    private IContentView _content = null!;
    private CoreRevision? _revision;
    private int _nextRow;

    public override void _Ready() => SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
    private sealed record Row(VBoxContainer Box, OptionButton Node, OptionButton Kind, LineEdit Seconds,
        VBoxContainer Rules, Button AddRule)
    {
        internal string InitialSeconds = "0";
        internal ulong? InitialSteps;
        internal readonly List<NodeId> Nodes = [];
        internal readonly List<RuleRow> Goods = [];
    }

    internal void Initialize(HostCoordinator host,
        Func<PanelTarget?, CoreCommand, CommandDecision.Accepted?> submit, Action<string> reportError)
    {
        _host = host; _submit = submit; _reportError = reportError;
        Name = "TrainPlanPanel";
        MouseFilter = MouseFilterEnum.Ignore;
        UiStyle.Panel(this, "Plan", "运行计划", new(310, 90), 960, Hide, out var body);
        _loop = new CheckBox { Name = "Loop", Text = "循环运行（未勾选为单次）" };
        body.AddChild(_loop);
        var scroll = new ScrollContainer { Name = "RowsScroll", VerticalScrollMode = ScrollContainer.ScrollMode.Disabled,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _rowsBox = new VBoxContainer { Name = "Rows", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        scroll.AddChild(_rowsBox); body.AddChild(scroll);
        body.AddChild(UiStyle.Button("AddStop", "添加计划项", AddRow));
        body.AddChild(UiStyle.Label("节点从列表选择；停站秒数为非负整数。未编辑的原停留时间保留精确步数。"));
        _reason = new Label { Name = "Reason", AutowrapMode = TextServer.AutowrapMode.WordSmart };
        body.AddChild(_reason);
        var actions = new HBoxContainer();
        _apply = UiStyle.Button("Apply", "应用计划", Apply, true);
        actions.AddChild(_apply); actions.AddChild(UiStyle.Button("Cancel", "取消", Hide));
        body.AddChild(actions);
        Hide();
    }

    internal void Open(PanelTarget target, TrainView train, ICoreReadView view, IContentView content)
    {
        _target = target; _train = train.Id;
        _view = view; _content = content; _revision = null;
        _nodes = view.World.Nodes.Select(item => item.Id).ToArray();
        _nodeNames = _nodes.Select(id => WorldPanels.NodeName(id, view, content)).ToArray();
        _loop.ButtonPressed = train.Plan?.Loop ?? false;
        _reason.Text = "目标数量指列车持有量：买入／装载补到目标，卖出／卸载保留目标；规则从上到下执行。含规则的停靠站须先删除规则才能改为途经点。";
        var count = train.Plan?.Stops.Length ?? 1;
        for (var index = 0; index < count; index++)
            BindRow(index < _rows.Count ? _rows[index] : CreateRow(), train.Plan?.Stops[index]);
        while (_rows.Count > count)
        {
            var row = _rows[^1];
            _rows.RemoveAt(_rows.Count - 1); _rowsBox.RemoveChild(row.Box); row.Box.QueueFree();
        }
        Show();
        UiStyle.BringToFront(this);
        RefreshState(target.Binding, view, content);
    }

    private void AddRow() => BindRow(CreateRow(), null);

    private Row CreateRow()
    {
        var box = new VBoxContainer { Name = $"Row{_nextRow++}", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        var controls = new HBoxContainer { Name = "Stop" }; box.AddChild(controls);
        var node = new OptionButton { Name = "Node", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        var kind = new OptionButton { Name = "Kind" };
        kind.AddItem("停靠站"); kind.AddItem("途经点");
        var seconds = new LineEdit { Name = "DwellSeconds", PlaceholderText = "停站秒数", CustomMinimumSize = new(120, 0) };
        var rules = new VBoxContainer { Name = "Rules" }; box.AddChild(rules);
        var add = new Button { Name = "AddRule", Text = "添加货物规则", CustomMinimumSize = new(0, 34) }; box.AddChild(add);
        var row = new Row(box, node, kind, seconds, rules, add);
        add.Pressed += () => AddRule(row);
        kind.ItemSelected += _ => UpdateStopKind(row);
        node.ItemSelected += _ => RefreshInventories([row]);
        controls.AddChild(node); controls.AddChild(kind); controls.AddChild(seconds);
        var up = UiStyle.Button("MoveUp", "↑", () => Move(row, -1));
        var down = UiStyle.Button("MoveDown", "↓", () => Move(row, 1));
        var remove = UiStyle.Button("Remove", "删除", () =>
        {
            _rows.Remove(row); _rowsBox.RemoveChild(box); box.QueueFree();
        });
        controls.AddChild(up); controls.AddChild(down); controls.AddChild(remove);
        box.AddChild(new HSeparator());
        _rows.Add(row); _rowsBox.AddChild(box);
        return row;
    }

    private void BindRow(Row row, PlanStop? stop)
    {
        var selected = stop switch { PlanStop.Station station => station.Node, PlanStop.Waypoint waypoint => waypoint.Node, _ => default };
        var index = Array.IndexOf(_nodes, selected);
        var nodes = _nodes;
        var names = _nodeNames;
        if (index < 0 && stop is not null)
        {
            index = nodes.Length;
            nodes = [.. nodes, selected]; names = [.. names, $"已失效节点 {selected.Value}"];
        }
        BindChoices(row.Node, row.Nodes, nodes, names);
        row.Node.Select(index < 0 && nodes.Length > 0 ? 0 : index);
        row.Kind.SetItemDisabled(1, false);
        row.Kind.Select(stop is PlanStop.Waypoint ? 1 : 0);
        row.InitialSteps = (stop as PlanStop.Station)?.DwellSteps;
        row.InitialSeconds = row.InitialSteps is { } value
            ? (value / (decimal)SimulationTime.StepsPerSecond).ToString(CultureInfo.InvariantCulture) : "0";
        row.Seconds.Text = row.InitialSeconds;
        var goods = stop is PlanStop.Station planned ? planned.GoodsRules : [];
        for (var ruleIndex = 0; ruleIndex < goods.Length; ruleIndex++)
            BindRule(ruleIndex < row.Goods.Count ? row.Goods[ruleIndex] : CreateRule(row), goods[ruleIndex]);
        while (row.Goods.Count > goods.Length)
        {
            var rule = row.Goods[^1];
            row.Goods.RemoveAt(row.Goods.Count - 1); row.Rules.RemoveChild(rule.Box); rule.Box.QueueFree();
        }
        UpdateStopKind(row);
    }

    private static void BindChoices<T>(OptionButton choice, List<T> current, T[] next, string[] labels)
    {
        if (!current.SequenceEqual(next))
        {
            choice.Clear(); current.Clear(); current.AddRange(next);
            foreach (var label in labels) choice.AddItem(label);
        }
        else
            for (var index = 0; index < labels.Length; index++)
                if (choice.GetItemText(index) != labels[index]) choice.SetItemText(index, labels[index]);
    }

    private void Move(Row row, int offset)
    {
        var index = _rows.IndexOf(row); var next = index + offset;
        if (next < 0 || next >= _rows.Count) return;
        _rows.RemoveAt(index); _rows.Insert(next, row); _rowsBox.MoveChild(row.Box, next);
    }

    internal void Refresh(HostBinding binding, ICoreReadView view, IContentView content)
    {
        if (!IsVisibleInTree()) return;
        RefreshState(binding, view, content);
    }

    private void RefreshState(HostBinding binding, ICoreReadView view, IContentView content)
    {
        var unavailable = _target is null || !_target.IsAvailable(binding, view, _host.PanelGenerationCurrent(_target.Binding, _target.Generation));
        _apply.Disabled = _host.IsBusy || view.Fault is not null || unavailable;
        if (unavailable && _target is not null)
            _reason.Text = "已不存在：草稿所属对象或对局已失效，请从当前列表重新打开。";
        if (unavailable || _revision == view.Revision) return;
        _view = view; _content = content; _revision = view.Revision;
        RefreshInventories(_rows);
    }

    private void Apply()
    {
        var inputs = _rows.Select(row => new PlanRowInput(
            row.Node.Selected >= 0 && row.Node.Selected < _nodes.Length ? _nodes[row.Node.Selected] : default,
            row.Kind.Selected == 0, row.Seconds.Text,
            row.Seconds.Text == row.InitialSeconds ? row.InitialSteps : null,
            row.Goods.Select(rule => new GoodsRuleInput((TradeAction)rule.Action.GetSelectedId(),
                rule.Goods[rule.Good.Selected], rule.Inventories[rule.Inventory.Selected],
                rule.Target.Text, rule.Price.Text)).ToArray())).ToArray();
        if (!TrainPlanInput.TryPlan(_train, _loop.ButtonPressed, inputs, out var command, out var error))
        { _reportError(error!); return; }
        if (_submit(_target, command!) is not null) Hide();
    }
}
