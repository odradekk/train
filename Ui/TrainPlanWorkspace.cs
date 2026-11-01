using System.Globalization;
using Godot;
using Train.App;
using Train.Content.Contracts;
using Train.Core.Contracts;

namespace Train.Ui;

/// <summary>
/// 在一份草稿里编辑停靠顺序、路线和本站交易规则。
/// Decision note: 2026-10-31-object-centric-management-screens
/// </summary>
internal partial class TrainPlanWorkspace : Control
{
    internal event Action? Opened;
    private HostCoordinator _host = null!;
    private Func<PanelTarget?, CoreCommand, Action<string>, CommandDecision.Accepted?> _submit = null!;
    private PanelTarget? _target;
    private TrainId _train;
    private ICoreReadView _view = null!;
    private IContentView _content = null!;
    private CoreRevision? _revision;
    private readonly List<Row> _rows = [];
    private Row? _selected;
    private VBoxContainer _stops = null!, _editor = null!, _searchResults = null!;
    private LineEdit _search = null!;
    private CheckBox _loop = null!;
    private Button _apply = null!, _saveOnly = null!;
    private Label _title = null!, _reason = null!;
    private TrainPlanMap _map = null!;
    private PlanEvaluation? _evaluation;
    private bool _available, _stopped, _bindingChoices;
    private string? _submissionError;

    private sealed class Row(PlanRowInput input, VBoxContainer box, Button select, Label summary, Label problems)
    {
        internal PlanRowInput Input = input;
        internal readonly string InitialSeconds = input.DwellSeconds;
        internal readonly ulong? InitialSteps = input.UnchangedDwellSteps;
        internal readonly VBoxContainer Box = box;
        internal readonly Button Select = select;
        internal readonly Label Summary = summary, Problems = problems;
        internal string? Notice;
    }

    public override void _Ready() => SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

    internal void Initialize(HostCoordinator host,
        Func<PanelTarget?, CoreCommand, Action<string>, CommandDecision.Accepted?> submit)
    {
        _host = host; _submit = submit; Name = "TrainPlanWorkspace"; MouseFilter = MouseFilterEnum.Ignore;
        var panel = new PanelContainer { Name = "Plan" }; AddChild(panel);
        panel.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var layout = new VBoxContainer { Name = "Layout" }; panel.AddChild(layout);
        layout.AddThemeConstantOverride("separation", 10);
        var header = new HBoxContainer(); layout.AddChild(header);
        _title = UiStyle.Label("运行计划", true); _title.SizeFlagsHorizontal = SizeFlags.ExpandFill; header.AddChild(_title);
        var close = UiStyle.Button("Close", "关闭", Hide); close.TooltipText = "关闭运行计划（Esc）"; header.AddChild(close);
        layout.AddChild(new HSeparator());
        var body = new ScrollContainer { Name = "ColumnsScroll", VerticalScrollMode = ScrollContainer.ScrollMode.Disabled,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Auto, SizeFlagsVertical = SizeFlags.ExpandFill };
        layout.AddChild(body);
        var columns = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        columns.AddThemeConstantOverride("separation", 12); body.AddChild(columns);
        var left = Column(columns, "StopList", 250);
        var listHeader = new HBoxContainer(); left.AddChild(listHeader);
        var caption = UiStyle.Label("停靠顺序", true); caption.SizeFlagsHorizontal = SizeFlags.ExpandFill; listHeader.AddChild(caption);
        _loop = new CheckBox { Name = "Loop", Text = "循环运行" }; listHeader.AddChild(_loop);
        _loop.Toggled += _ => Changed();
        var stopScroll = Scroll(left, "StopsScroll");
        _stops = new VBoxContainer { Name = "Stops", SizeFlagsHorizontal = SizeFlags.ExpandFill }; stopScroll.AddChild(_stops);
        left.AddChild(UiStyle.Label("搜索地名添加停靠站"));
        _search = new LineEdit { Name = "Search", PlaceholderText = "输入地名", ClearButtonEnabled = true }; left.AddChild(_search);
        _searchResults = new VBoxContainer { Name = "SearchResults" }; left.AddChild(_searchResults);
        _search.TextChanged += _ => Search();
        var middle = Column(columns, "Route", 280); middle.SizeFlagsStretchRatio = 1.6f;
        middle.AddChild(Wrap("点地点添加停靠站，按住 Shift 点地点添加途经点；黄线为计划路线，悬停可看地名，滚轮放大可显示全部地名。"));
        _map = new TrainPlanMap { Name = "RouteMap", SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        middle.AddChild(_map); _map.PlaceClicked += AddPlace;
        var right = Column(columns, "Settings", 320);
        var editorScroll = Scroll(right, "EditorScroll");
        _editor = new VBoxContainer { Name = "StopEditor", SizeFlagsHorizontal = SizeFlags.ExpandFill }; editorScroll.AddChild(_editor);
        layout.AddChild(new HSeparator());
        _reason = Wrap(""); _reason.Name = "Reason"; layout.AddChild(_reason);
        var actions = new HFlowContainer(); layout.AddChild(actions);
        actions.AddChild(UiStyle.Button("Cancel", "取消", Hide));
        _saveOnly = UiStyle.Button("SaveOnly", "只保存", () => Save(false)); actions.AddChild(_saveOnly);
        _apply = UiStyle.Button("Apply", "保存并发车", () => Save(_stopped), true); actions.AddChild(_apply);
        Hide();
    }

    private static VBoxContainer Column(HBoxContainer parent, string name, float width)
    {
        var column = new VBoxContainer { Name = name, CustomMinimumSize = new(width, 0),
            SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        column.AddThemeConstantOverride("separation", 8); parent.AddChild(column); return column;
    }
    private static ScrollContainer Scroll(VBoxContainer parent, string name)
    {
        var scroll = new ScrollContainer { Name = name, SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        parent.AddChild(scroll); return scroll;
    }
    private static Label Wrap(string text) => new() { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart,
        SizeFlagsHorizontal = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore };
    private static void Clear(Node parent)
    {
        foreach (var child in parent.GetChildren()) { parent.RemoveChild(child); child.QueueFree(); }
    }

    internal void Open(PanelTarget target, TrainView train, ICoreReadView view, IContentView content)
    {
        _target = target; _train = train.Id; _view = view; _content = content; _revision = null; _submissionError = null;
        _title.Text = $"运行计划 · 列车 {train.Id.Value}";
        _rows.Clear(); Clear(_stops); _selected = null;
        _loop.SetPressedNoSignal(train.Plan?.Loop ?? false);
        if (train.Plan is { } plan)
            foreach (var stop in plan.Stops)
            {
                var node = stop is PlanStop.Station station ? station.Node : ((PlanStop.Waypoint)stop).Node;
                var steps = (stop as PlanStop.Station)?.DwellSteps;
                AddRow(new(node, stop is PlanStop.Station,
                    steps is { } value ? (value / (decimal)SimulationTime.StepsPerSecond).ToString(CultureInfo.InvariantCulture) : "0",
                    steps, (stop as PlanStop.Station)?.GoodsRules.Select(RuleInput).ToArray()));
            }
        else if (train.Position is TrainPosition.Node at)
            AddRow(new(at.Id, TrainPlanInput.StationProblem(view.World.Nodes.FirstOrDefault(node => node.Id == at.Id)) is null, "0"));
        _search.Text = ""; Search();
        _map.SetWorld(view.World, PlaceName);
        Select(_rows.FirstOrDefault());
        Show(); Opened?.Invoke(); RefreshState(target.Binding, view, content);
    }

    private Row AddRow(PlanRowInput input)
    {
        var box = new VBoxContainer();
        var controls = new HBoxContainer { Name = "Controls" }; box.AddChild(controls);
        var select = new Button { Name = "Select", ToggleMode = true, SizeFlagsHorizontal = SizeFlags.ExpandFill,
            Alignment = HorizontalAlignment.Left, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis };
        controls.AddChild(select);
        var summary = Wrap(""); summary.Name = "Summary"; box.AddChild(summary);
        var problems = Wrap(""); problems.Name = "Problems"; problems.AddThemeColorOverride("font_color", UiStyle.Error); box.AddChild(problems);
        var row = new Row(input, box, select, summary, problems);
        select.Pressed += () => Select(row);
        controls.AddChild(UiStyle.Button("MoveUp", "↑", () => Move(row, -1)));
        controls.AddChild(UiStyle.Button("MoveDown", "↓", () => Move(row, 1)));
        controls.GetChild<Control>(1).TooltipText = "上移这一站"; controls.GetChild<Control>(2).TooltipText = "下移这一站";
        controls.AddChild(UiStyle.Button("Remove", "删除", () =>
        {
            var index = _rows.IndexOf(row); _rows.Remove(row); _stops.RemoveChild(box); box.QueueFree();
            Select(_selected == row ? _rows.ElementAtOrDefault(Math.Min(index, _rows.Count - 1)) : _selected); Changed();
        }));
        box.AddChild(new HSeparator()); _rows.Add(row); _stops.AddChild(box); return row;
    }

    private void AddPlace(NodeId node, bool waypoint)
    {
        var reason = TrainPlanInput.StationProblem(_view.World.Nodes.FirstOrDefault(item => item.Id == node));
        var row = AddRow(new(node, !waypoint && reason is null, "0"));
        if (!waypoint && reason is not null) row.Notice = reason.Replace("请改为途经点", "已添加为途经点", StringComparison.Ordinal);
        _search.Text = ""; Search(); Select(row); Changed();
    }
    private void Select(Row? row)
    {
        _selected = row;
        foreach (var item in _rows) item.Select.SetPressedNoSignal(item == row);
        RenderEditor(); Validate();
    }
    private void Move(Row row, int offset)
    {
        var index = _rows.IndexOf(row); var next = index + offset;
        if (next < 0 || next >= _rows.Count) return;
        _rows.RemoveAt(index); _rows.Insert(next, row); _stops.MoveChild(row.Box, next); Select(row); Changed();
    }
    private void Search()
    {
        var query = _search.Text.Trim();
        var matches = query.Length == 0 ? [] : _view.World.Nodes.Select(node => (node.Id, Name: PlaceName(node.Id)))
            .Where(item => item.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).Take(7).ToArray();
        var results = matches.Take(6).ToArray();
        var hint = query.Length == 0 ? null : matches.Length == 0 ? "没有匹配地点，请换个名称。"
            : matches.Length > 6 ? "还有更多地点，请继续输入名称缩小范围。" : null;
        if (_searchResults.GetChildCount() == results.Length + (hint is null ? 0 : 1) &&
            results.Select((item, index) => _searchResults.GetChild(index) is Button button &&
                button.Name == $"Add{item.Id.Value}" && button.Text == item.Name).All(same => same) &&
            (hint is null || _searchResults.GetChild(results.Length) is Label label && label.Text == hint)) return;
        Clear(_searchResults);
        foreach (var item in results)
            _searchResults.AddChild(UiStyle.Button($"Add{item.Id.Value}", item.Name, () => AddPlace(item.Id, false)));
        if (hint is not null) _searchResults.AddChild(Wrap(hint));
    }
    private void Changed() { _submissionError = null; Validate(); }

    internal void Refresh(HostBinding binding, ICoreReadView view, IContentView content)
    {
        if (!IsVisibleInTree() && (_target is null || _target.Binding == binding)) return;
        RefreshState(binding, view, content);
    }
    private void RefreshState(HostBinding binding, ICoreReadView view, IContentView content)
    {
        _available = _target is not null && _target.IsAvailable(binding, view, _host.PanelGenerationCurrent(_target.Binding, _target.Generation));
        if (!_available) { UpdateFooter(); return; }
        _stopped = view.FindTrain(_train) is Lookup<TrainView>.Found { Value.Activity: TrainActivity.Stopped };
        if (_revision != view.Revision || !ReferenceEquals(_content, content))
        {
            _view = view; _content = content; _revision = view.Revision;
            _map.SetWorld(view.World, PlaceName); RefreshChoices(); Search(); Validate();
        }
        else UpdateFooter();
    }
    private void Validate()
    {
        if (_view is null) return;
        _evaluation = TrainPlanInput.Evaluate(_train, _loop.ButtonPressed, _rows.Select(row => row.Input).ToArray(), _view, _content);
        for (var index = 0; index < _rows.Count; index++)
        {
            var row = _rows[index];
            row.Select.Text = $"{index + 1}. {PlaceName(row.Input.Node)}"; row.Select.TooltipText = row.Select.Text;
            row.Summary.Text = (row.Input.Station ? $"停留 {row.Input.DwellSeconds} 秒" : "途经点") +
                string.Concat((row.Input.GoodsRules ?? []).Select(rule => "\n" + RuleSentence(rule)));
            row.Problems.Text = string.Join("\n", _evaluation.Problems.Where(problem => problem.Stop == index).Select(problem => problem.Message));
            row.Problems.Visible = row.Problems.Text.Length > 0;
            row.Box.GetNode<Button>("Controls/MoveUp").Disabled = index == 0;
            row.Box.GetNode<Button>("Controls/MoveDown").Disabled = index == _rows.Count - 1;
        }
        UpdateRuleMessages();
        _map.SetPlan(_rows.Select(row => row.Input.Node).ToArray(), _evaluation.Legs);
        UpdateFooter();
    }
    private void UpdateFooter()
    {
        _apply.Text = _stopped ? "保存并发车" : "保存"; _saveOnly.Visible = _stopped;
        var problem = !_available ? "已不存在：草稿所属对象或对局已失效，请从当前列表重新打开。"
            : _host.IsBusy ? "正在处理其他操作，请稍后再试。" : _view.Fault is not null ? "模拟已停止，暂时不能保存运行计划。" : null;
        _apply.Disabled = _saveOnly.Disabled = problem is not null || _evaluation?.Command is null;
        var problems = _evaluation?.Problems ?? [];
        _reason.Text = problem ?? _submissionError ?? (problems.Count == 0
            ? $"{_rows.Count} 站 · {(_loop.ButtonPressed ? "循环运行" : "单次运行，到最后一站后停车")}" :
            $"{(problems[0].Stop is { } at ? $"第 {at + 1} 站：" : "")}{problems[0].Message}" + (problems.Count > 1 ? $"另有 {problems.Count - 1} 个问题。" : ""));
        _reason.TooltipText = string.Join("\n", problems.Select(item => (item.Stop is { } at ? $"第 {at + 1} 站：" : "") + item.Message));
    }
    private void Save(bool depart)
    {
        if (_host.GetStatus() is { } status && _host.Content is { } content) RefreshState(status.Binding, status.View, content);
        Validate(); if (_apply.Disabled || _evaluation?.Command is not { } command) return;
        void Error(string text) { _submissionError = CleanError(text); UpdateFooter(); }
        if (_submit(_target, command, Error) is null) return;
        if (depart && _submit(_target, new CoreCommand.StartTrain(_train), text => Error("计划已保存，发车未成功：" + text)) is null) return;
        Hide();
    }
    // 截掉 CommandDiagnostics 附加的、带内部编号的对象说明，保留玩家可读的拒绝原因。
    private static string CleanError(string text) => string.Join("\n", text.Split('\n').Select(line => line.Split("对象：", StringSplitOptions.None)[0]));

    private string PlaceName(NodeId id)
    {
        var node = _view.World.Nodes.FirstOrDefault(item => item.Id == id);
        if (node is null) return "地点已不存在";
        if (node.Occupant is NodeOccupant.Settlement settlement)
            return _content.FindSettlementLayout(settlement.Id) is ContentLookup<SettlementLayoutProjection>.Found layout
                ? layout.Value.Display.Name : "聚居地";
        var name = node.Occupant is NodeOccupant.Lair ? "生物聚落" : "空节点";
        if (node.Occupant is NodeOccupant.Lair lair &&
            _view.FindLair(lair.Id) is Lookup<LairView>.Found found &&
            _content.FindLair(found.Value.Type) is ContentLookup<LairAppearanceProjection>.Found appearance)
            return name + " · " + appearance.Value.Display.Text.Name;
        if (_content.FindDefinitionDisplay(new CoreDefinitionReference.Terrain(node.Terrain.TypeId)) is ContentLookup<ContentDisplay>.Found display)
            name += " · " + display.Value.Text.Name;
        return name;
    }
}
