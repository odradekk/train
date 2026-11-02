using Godot;
using Train.Content.Contracts;
using Train.Core.Contracts;

namespace Train.Ui;

/// <summary>
/// 导航列表的筛选、分组和对象选择。
/// Decision note: 2026-10-31-docked-game-shell
/// </summary>
internal sealed class WorldListDrawer
{
    private readonly WorldListKind _kind;
    private readonly LineEdit _search;
    private readonly OptionButton? _filter;
    private readonly ItemList _list;
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
            filters.AddChild(UiStyle.Label("筛选"));
            _filter = new OptionButton { Name = "Filter", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            foreach (var label in kind == WorldListKind.Fleet
                ? new[] { "全部", "需要处理", "运行中", "已停车" }
                : new[] { "全部", "已建成", "有断点", "未建成" }) _filter.AddItem(label);
            filters.AddChild(_filter);
            body.AddChild(filters);
            _filter.ItemSelected += _ => { _list!.GetVScrollBar().Value = 0; refresh(); };
        }
        _empty = UiStyle.Label("没有符合条件的对象。");
        _empty.Name = "Empty";
        _empty.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        body.AddChild(_empty);
        // ItemList 只在 Top 模式和固定列宽下换行；完整文本同时保留在提示中。
        _list = new ItemList { Name = "Objects", SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            AllowReselect = true, IconMode = ItemList.IconModeEnum.Top,
            MaxTextLines = kind == WorldListKind.Fleet ? 6 : kind == WorldListKind.Edges ? 4 : 2,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            MouseForcePassScrollEvents = false };
        body.AddChild(_list);
        _list.Resized += () => _list.FixedColumnWidth = Math.Max(1,
            (int)(_list.Size.X - _list.GetThemeStylebox("panel").GetMinimumSize().X - _list.GetVScrollBar().Size.X));
        _list.ItemSelected += index =>
        {
            if (index >= 0 && index < _rows.Count && _rows[(int)index].Object is { } id) selected(id);
        };
        _search.TextChanged += _ => { _list.GetVScrollBar().Value = 0; refresh(); };
        var hint = UiStyle.Label("点一行打开检查器，不改变当前视角。");
        hint.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        body.AddChild(hint);
    }

    internal void Refresh(ICoreReadView view, IContentView content)
    {
        if (_kind == WorldListKind.Fleet)
        {
            var count = view.World.Trains.Count(item => view.FindTrain(item.Id) is Lookup<TrainView>.Found found && NeedsAttention(found.Value));
            _filter!.SetItemText(1, $"需要处理 {count}");
        }
        var rows = Rows(_kind, view, content, _search.Text, _filter?.Selected ?? 0);
        _empty.Visible = rows.Count == 0;
        if (_rows.SequenceEqual(rows)) return;
        var scroll = _list.GetVScrollBar().Value;
        var selection = _list.GetSelectedItems();
        var selected = selection.Length == 0 ? null : _rows[selection[0]].Object;
        var rebuild = !_rows.Select(row => row.Object).SequenceEqual(rows.Select(row => row.Object));
        if (rebuild) _list.Clear();
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            var text = row.Text.Replace("\n", " · ");
            if (rebuild) _list.AddItem(text);
            else if (_rows[index].Text != row.Text) _list.SetItemText(index, text);
            _list.SetItemTooltip(index, row.Text);
            _list.SetItemSelectable(index, row.Object is not null);
            _list.SetItemCustomFgColor(index, row.Attention ? UiStyle.Warning :
                row.Object is null ? UiStyle.Info : UiStyle.Foreground);
            if (rebuild && selected is not null && row.Object == selected) _list.Select(index);
        }
        _rows = rows;
        Callable.From(() =>
        {
            if (GodotObject.IsInstanceValid(_list)) _list.GetVScrollBar().Value = scroll;
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
                    (edge.Tracks == 0 ? "未建成" : $"铁轨 {edge.Tracks}/{edge.TrackLimit}") +
                    $" · 长度 {edge.LengthMeters:0.##} 米" +
                    (edge.Breaks.IsEmpty ? "" : $" · {edge.Breaks.Length} 处断点"), !edge.Breaks.IsEmpty));
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
                1 => NeedsAttention(train), 2 => train.Activity == TrainActivity.Running,
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
                $"{name}{(NeedsAttention(train) ? " · 需要处理" : "")}\n{WorldPanels.Activity(train.Activity)} · {destination}\n" +
                (train.Plan is null ? "没有运行计划 · " : "") + report, NeedsAttention(train)));
        }
        return rows;
    }
}
