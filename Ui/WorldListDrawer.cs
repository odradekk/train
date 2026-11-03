using Godot;
using Train.Content.Contracts;
using Train.Core.Contracts;

namespace Train.Ui;

// Decision note: 2026-10-31-docked-game-shell
internal sealed class WorldListDrawer
{
    private readonly WorldListKind _kind;
    private readonly LineEdit _search;
    private readonly List<Button> _filters = [];
    private int _filter;
    private ICoreReadView? _lastView;
    private IContentView? _lastContent;
    private string? _lastSearch;
    private int _lastFilter;
    private readonly Tree _list;
    private readonly VScrollBar _scroll;
    private readonly List<TreeItem> _items = [];
    private bool _updating;
    private readonly Label _empty;
    private IReadOnlyList<Row> _rows = [];
    internal PanelContainer Panel { get; }
    internal sealed record Row(WorldObjectId? Object, string Text, bool Attention);

    internal WorldListDrawer(PanelContainer panel, VBoxContainer body, WorldListKind kind,
        Action<WorldObjectId> selected, Action refresh)
    {
        _kind = kind;
        Panel = panel;
        body.AddChild(UiStyle.Label("搜索名称"));
        _search = new LineEdit { Name = "Search", PlaceholderText = "输入名称" };
        body.AddChild(_search);
        if (kind is WorldListKind.Fleet or WorldListKind.Edges)
        {
            var filters = new HBoxContainer();
            var group = new ButtonGroup();
            foreach (var label in kind == WorldListKind.Fleet
                ? new[] { "全部", "需要处理", "运行中", "已停车" }
                : new[] { "全部", "已建成", "有断点", "未建成" })
            {
                var index = _filters.Count;
                var button = new Button { Name = "Filter" + index, Text = label, ToggleMode = true,
                    ButtonGroup = group, ButtonPressed = index == 0, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
                button.Pressed += () => { _filter = index; _scroll!.Value = 0; refresh(); };
                _filters.Add(button);
                filters.AddChild(button);
            }
            body.AddChild(filters);
        }
        _empty = UiStyle.Label("没有符合条件的对象。");
        _empty.Name = "Empty";
        _empty.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        body.AddChild(_empty);
        _list = new Tree { Name = "Objects", SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            HideRoot = true, HideFolding = true, AllowReselect = true,
            ScrollHorizontalEnabled = false, MouseForcePassScrollEvents = false };
        _list.SetColumnClipContent(0, true);
        body.AddChild(_list);
        // Tree 不公开滚动条访问器，取内部滚动条来保留刷新前的位置。
        _scroll = _list.GetChildren(true).OfType<VScrollBar>().Single();
        _list.ItemSelected += () =>
        {
            if (!_updating && _list.GetSelected() is { } item &&
                _rows[item.GetMetadata(0).AsInt32()].Object is { } id) selected(id);
        };
        _search.TextChanged += _ => { _scroll.Value = 0; refresh(); };
        var hint = UiStyle.Label("点一行打开检查器，不改变当前视角。");
        hint.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        body.AddChild(hint);
    }

    internal void Refresh(ICoreReadView view, IContentView content)
    {
        if (!Panel.IsVisibleInTree() || ReferenceEquals(view, _lastView) && ReferenceEquals(content, _lastContent) &&
            _search.Text == _lastSearch && _filter == _lastFilter) return;
        _lastView = view;
        _lastContent = content;
        _lastSearch = _search.Text;
        _lastFilter = _filter;
        if (_kind == WorldListKind.Fleet)
        {
            var count = view.World.Trains.Count(item => view.FindTrain(item.Id) is Lookup<TrainView>.Found found && NeedsAttention(found.Value));
            _filters[1].Text = $"需要处理 {count}";
        }
        var rows = Rows(_kind, view, content, _search.Text, _filter);
        _empty.Visible = rows.Count == 0;
        if (_rows.SequenceEqual(rows)) return;
        var scroll = _scroll.Value;
        var selected = _list.GetSelected() is { } current ? _rows[current.GetMetadata(0).AsInt32()].Object : null;
        var rebuild = !_rows.Select(row => row.Object).SequenceEqual(rows.Select(row => row.Object));
        _updating = true;
        TreeItem? root = null;
        TreeItem? group = null;
        if (rebuild)
        {
            _list.Clear();
            _items.Clear();
            root = _list.CreateItem();
        }
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            TreeItem item;
            if (rebuild)
            {
                item = _list.CreateItem(row.Object is null ? root : group ?? root);
                if (row.Object is null) group = item;
                item.DisableFolding = true;
                item.SetMetadata(0, index);
                item.SetSelectable(0, row.Object is not null);
                item.SetTextAlignment(0, HorizontalAlignment.Left);
                item.SetTextOverrunBehavior(0, TextServer.OverrunBehavior.TrimEllipsis);
                _items.Add(item);
            }
            else item = _items[index];
            item.SetText(0, row.Text);
            item.SetTooltipText(0, row.Text);
            item.SetCustomColor(0, row.Attention ? UiStyle.Warning : row.Object is null ? UiStyle.Info : UiStyle.Foreground);
            if (rebuild && selected is not null && row.Object == selected) item.Select(0);
        }
        _updating = false;
        _rows = rows;
        Callable.From(() =>
        {
            if (GodotObject.IsInstanceValid(_scroll)) _scroll.Value = scroll;
        }).CallDeferred();
    }

    internal static bool NeedsAttention(TrainView train) =>
        train.Plan is null || train.Activity is TrainActivity.NoRoute or TrainActivity.Stopped;

    internal static IReadOnlyList<Row> Rows(WorldListKind kind, ICoreReadView view, IContentView content,
        string search, int filter)
    {
        var rows = new List<Row>();
        bool Matches(string name) => name.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase);
        if (kind == WorldListKind.Nodes)
        {
            string[] titles = ["自有聚居地", "非敌对聚居地", "敌对聚居地", "空节点", "生物聚落"];
            var groups = view.World.Nodes.Select(node => (Node: node, Name: WorldPanels.NodeName(node.Id, view, content)))
                .Where(item => Matches(item.Name)).GroupBy(item => item.Node.Occupant switch
                {
                    NodeOccupant.Settlement { Relation: SettlementRelation.PlayerOwned } => 0,
                    NodeOccupant.Settlement { Relation: SettlementRelation.NonHostile } => 1,
                    NodeOccupant.Settlement => 2, NodeOccupant.Empty => 3, _ => 4
                }).OrderBy(group => group.Key);
            foreach (var group in groups)
            {
                rows.Add(new(null, $"{titles[group.Key]} · {group.Count()}", false));
                rows.AddRange(group.Select(item => new Row(new WorldObjectId.Node(item.Node.Id), item.Name, false)));
            }
            return rows;
        }
        if (kind == WorldListKind.Combats)
        {
            foreach (var combat in view.World.Combats)
            {
                var name = "战斗 · " + WorldPanels.LocationText(combat.Location, view, content);
                if (Matches(name)) rows.Add(new(new WorldObjectId.Combat(combat.Id), name, false));
            }
            return rows;
        }
        if (kind == WorldListKind.Edges)
        {
            foreach (var edge in view.World.Edges)
            {
                var name = WorldPanels.NodeName(edge.Start, view, content) + "—" + WorldPanels.NodeName(edge.End, view, content);
                if (!Matches(name) || !(filter switch
                {
                    1 => edge.Tracks > 0, 2 => !edge.Breaks.IsEmpty, 3 => edge.Tracks == 0, _ => true
                })) continue;
                rows.Add(new(new WorldObjectId.Edge(edge.Id), $"{name}\n" +
                    EdgeStatus(edge), !edge.Breaks.IsEmpty));
            }
            return rows;
        }
        foreach (var item in view.World.Trains)
        {
            var name = $"列车 {item.Id.Value}";
            if (!Matches(name) || view.FindTrain(item.Id) is not Lookup<TrainView>.Found found) continue;
            var train = found.Value;
            if (!(filter switch
            {
                1 => NeedsAttention(train), 2 => train.Activity != TrainActivity.Stopped,
                3 => train.Activity == TrainActivity.Stopped, _ => true
            })) continue;
            var destination = "暂无去向";
            if (train.Activity == TrainActivity.Retreating) destination = "撤退中";
            else if (train.Plan is { } plan && train.NextStopIndex is { } next)
                destination = plan.Stops[next] switch
                {
                    PlanStop.Station station => "停靠：" + WorldPanels.NodeName(station.Node, view, content),
                    PlanStop.Waypoint waypoint => "途经：" + WorldPanels.NodeName(waypoint.Node, view, content),
                    _ => destination
                };
            var report = train.RecentReports.IsEmpty ? "暂无运行报告"
                : $"最近报告净现金流 {train.RecentReports[^1].Totals.NetCashFlow:+0;-0;0}";
            rows.Add(new(new WorldObjectId.Train(train.Id),
                $"{name} · {WorldPanels.Activity(train.Activity)}{(NeedsAttention(train) ? " · 需要处理" : "")}\n{destination}\n" +
                (train.Plan is null ? "没有运行计划\n" : "") + report, NeedsAttention(train)));
        }
        return rows;
    }

    internal static string EdgeStatus(EdgeMapItem edge) =>
        (edge.Tracks == 0 ? "未建成" : $"铁轨 {edge.Tracks}/{edge.TrackLimit}") +
        $" · 长度 {edge.LengthMeters:0.##} 米" +
        (edge.Breaks.IsEmpty ? "" : $" · {edge.Breaks.Length} 处断点");
}
