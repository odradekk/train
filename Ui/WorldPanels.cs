using System.Text;
using Godot;
using Train.App;
using Train.App.Logging;
using Train.Content.Contracts;
using Train.Core.Contracts;

namespace Train.Ui;

public partial class WorldPanels : Control
{
    private HostCoordinator _host = null!;
    private TrainLogger _logger = null!;
    private Action<string> _showError = null!;
    private HostBinding _binding;
    private ICoreReadView _view = null!;
    private IContentView _content = null!;
    private readonly Dictionary<string, (PanelContainer Panel, LineEdit Search, ItemList List, List<WorldObjectId> Ids)> _lists = [];
    private readonly List<Detail> _details = [];
    private (PanelTarget Target, PanelContainer Panel, Button Confirm, Label Reason)? _disband;

    internal event Action<PanelTarget, TrainView, ICoreReadView, IContentView>? PlanRequested;
    internal event Action<PanelTarget, NodeId, ICoreReadView, IContentView>? FormationRequested;
    internal ICoreReadView? View => _view;

    internal void Initialize(HostCoordinator host, TrainLogger logger, Action<string> showError)
    { _host = host; _logger = logger; _showError = showError; }

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
    }

    internal void Refresh(HostBinding binding, ICoreReadView view, IContentView content)
    {
        if (_view is not null && (_binding != binding || _content.Generation != content.Generation))
        {
            foreach (var entry in _lists.Values) { RemoveChild(entry.Panel); entry.Panel.QueueFree(); }
            _lists.Clear();
        }
        _binding = binding; _view = view; _content = content;
        foreach (var (kind, entry) in _lists)
        {
            var items = Items(kind).Where(id => NameOf(id).Contains(entry.Search.Text, StringComparison.OrdinalIgnoreCase)).ToList();
            if (!entry.Ids.SequenceEqual(items))
            {
                entry.List.Clear(); entry.Ids.Clear(); entry.Ids.AddRange(items);
                foreach (var item in items) entry.List.AddItem(NameOf(item));
            }
            else
                for (var i = 0; i < items.Count; i++)
                {
                    var name = NameOf(items[i]);
                    if (entry.List.GetItemText(i) != name) entry.List.SetItemText(i, name);
                }
        }
        foreach (var detail in _details)
        {
            var valid = detail.Target.IsAvailable(binding, content.Generation, view);
            detail.Text.Text = valid ? Describe(detail.Target.Object) : "已不存在\n\n对象或所属对局已失效。请从当前列表重新打开。";
            foreach (var button in detail.Buttons) button.Disabled = !valid || _host.IsBusy;
        }
        if (_disband is { } disband)
        {
            var valid = disband.Target.IsAvailable(binding, content.Generation, view);
            disband.Confirm.Disabled = !valid || _host.IsBusy;
            disband.Reason.Text = valid ? "解编后车辆停放在所在聚居地。" : "已不存在：对象或所属对局已失效。";
        }
    }

    internal void OpenList(string kind)
    {
        if (_view is null) return;
        if (_lists.TryGetValue(kind, out var existing)) { existing.Panel.Show(); existing.Panel.MoveToFront(); return; }
        PanelContainer panel = null!;
        panel = UiStyle.Panel(this, kind + "List", kind, new(24, 68), 360, () => panel.Hide(), out var body);
        var search = new LineEdit { Name = "Search", PlaceholderText = "搜索名称或编号" };
        body.AddChild(search);
        var list = new ItemList { Name = "Objects", CustomMinimumSize = new(328, 340),
            AllowReselect = true, MouseForcePassScrollEvents = false };
        body.AddChild(list);
        body.AddChild(UiStyle.Label("点列表打开详情，不改变当前视角。"));
        var ids = new List<WorldObjectId>();
        _lists.Add(kind, (panel, search, list, ids));
        var binding = _binding;
        var generation = _content.Generation;
        list.ItemSelected += index =>
        {
            var status = _host.GetStatus();
            if (status is null || status.Binding != binding || _host.Content?.Generation != generation ||
                index < 0 || index >= ids.Count) return;
            OpenObject(ids[(int)index]);
        };
        search.TextChanged += _ => Refresh(_binding, _view, _content);
        Refresh(_binding, _view, _content);
    }

    internal void OpenObject(WorldObjectId target)
    {
        var bound = new PanelTarget(_binding, _content.Generation, target);
        var existing = _details.FirstOrDefault(detail => detail.Target == bound);
        if (existing is not null) { existing.Panel.Show(); existing.Panel.MoveToFront(); return; }
        PanelContainer panel = null!;
        panel = UiStyle.Panel(this, "Details", NameOf(target), new(408, 68), 420,
            () => { _details.RemoveAll(detail => detail.Panel == panel); panel.QueueFree(); }, out var body);
        var scroll = new ScrollContainer { CustomMinimumSize = new(390, 340),
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, MouseForcePassScrollEvents = false };
        var text = UiStyle.Label("");
        text.Name = "Description";
        text.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        text.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        scroll.AddChild(text); body.AddChild(scroll);
        List<Button> buttons = [];
        if (target is WorldObjectId.Node node)
            buttons.Add(UiStyle.Button("Form", "编组", () =>
            {
                if (Available(bound)) FormationRequested?.Invoke(bound, node.Id, _view, _content);
            }, true));
        if (target is WorldObjectId.Train train)
        {
            buttons.Add(UiStyle.Button("EditPlan", "编辑计划", () =>
            {
                if (Available(bound) && _view.FindTrain(train.Id) is Lookup<TrainView>.Found found)
                    PlanRequested?.Invoke(bound, found.Value, _view, _content);
            }, true));
            buttons.Add(UiStyle.Button("Start", "启动", () => Submit(bound, new CoreCommand.StartTrain(train.Id)), true));
            buttons.Add(UiStyle.Button("Stop", "停车", () => Submit(bound, new CoreCommand.StopTrain(train.Id))));
            buttons.Add(UiStyle.Button("Disband", "解编…", () => ConfirmDisband(bound, train.Id)));
        }
        foreach (var button in buttons) body.AddChild(button);
        _details.Add(new(bound, panel, text, buttons));
        Refresh(_binding, _view, _content);
    }

    private bool Available(PanelTarget target)
    {
        var status = _host.GetStatus();
        if (status is not null && _host.Content is { } content &&
            target.IsAvailable(status.Binding, content.Generation, status.View))
        { _binding = status.Binding; _view = status.View; _content = content; return true; }
        var message = "对象已不存在或所属对局已更换，操作未提交。";
        _logger.Warning("ui", "ui_target_unavailable", message);
        _showError(message);
        return false;
    }

    private void Submit(PanelTarget target, CoreCommand command)
    {
        if (!Available(target)) return;
        var result = _host.Submit(target.Binding, command, CommandSource.UserInterface);
        if (CommandDiagnostics.Report(_logger, result) is { } problem) _showError(problem);
    }

    private void ConfirmDisband(PanelTarget target, TrainId train)
    {
        if (!Available(target)) return;
        CloseDisband();
        PanelContainer confirm = null!;
        confirm = UiStyle.Panel(this, "DisbandConfirmation", $"解编列车 {train.Value}？", new(600, 270), 480,
            CloseDisband, out var body);
        var reason = UiStyle.Label("解编后车辆停放在所在聚居地。");
        body.AddChild(reason);
        body.AddChild(UiStyle.Button("Cancel", "取消", CloseDisband));
        var button = UiStyle.Button("Confirm", "解编", () =>
        { Submit(target, new CoreCommand.DisbandTrain(train)); CloseDisband(); }, true);
        body.AddChild(button);
        _disband = (target, confirm, button, reason);
    }

    private void CloseDisband()
    {
        if (_disband is not { } disband) return;
        RemoveChild(disband.Panel); disband.Panel.QueueFree(); _disband = null;
    }

    private IEnumerable<WorldObjectId> Items(string kind) => kind switch
    {
        "节点" => _view.World.Nodes.Select(item => new WorldObjectId.Node(item.Id)),
        "线路" => _view.World.Edges.Select(item => new WorldObjectId.Edge(item.Id)),
        _ => _view.World.Trains.Select(item => new WorldObjectId.Train(item.Id))
    };

    private string NameOf(WorldObjectId id) => id switch
    {
        WorldObjectId.Node node => NodeName(node.Id),
        WorldObjectId.Edge edge when _view.FindEdge(edge.Id) is Lookup<EdgeView>.Found found =>
            $"线路 {edge.Id.Value} · {NodeName(found.Value.Map.Start)}—{NodeName(found.Value.Map.End)}",
        WorldObjectId.Train train => $"列车 {train.Id.Value} · {Activity(_view.World.Trains.First(item => item.Id == train.Id).Activity)}",
        WorldObjectId.Instance instance => $"{instance.Id}",
        _ => id.ToString()
    };

    private string NodeName(NodeId id)
    {
        var node = _view.World.Nodes.FirstOrDefault(item => item.Id == id);
        if (node?.Occupant is NodeOccupant.Settlement settlement &&
            _content.FindSettlementLayout(settlement.Id) is ContentLookup<SettlementLayoutProjection>.Found layout)
            return $"{layout.Value.Display.Name} · {id.Value}";
        return $"节点 {id.Value}";
    }

    private string Describe(WorldObjectId target)
    {
        var text = new StringBuilder();
        if (target is WorldObjectId.Node node && _view.FindNode(node.Id) is Lookup<NodeView>.Found foundNode)
        {
            var map = foundNode.Value.Map;
            var occupant = map.Occupant switch { NodeOccupant.Settlement owned => $"聚居地 · {Relation(owned.Relation)}",
                NodeOccupant.Lair => "生物聚落", _ => "空节点" };
            text.AppendLine($"占据者：{occupant}\n位置：{map.Position.X:0.##} m，{map.Position.Y:0.##} m\n地貌：{map.Terrain.TypeId.Value}\n站台：{map.OccupiedPlatforms} / {map.Platforms}\n");
            text.AppendLine("节点资源");
            foreach (var resource in map.Resources) text.AppendLine($"{GoodName(resource.Good)}：{resource.Quantity}");
            text.AppendLine("\n地点车辆");
            if (_view.FindLocation(new WorldLocation.Node(node.Id)) is Lookup<LocationView>.Found location)
                foreach (var instance in location.Value.Instances.OfType<InstanceId.Vehicle>())
                    if (_view.FindInstance(instance) is Lookup<InstanceView>.Found vehicle)
                        text.AppendLine($"车辆 {instance.Id.Value} · {(vehicle.Value.Details is InstanceDetails.Vehicle details && details.Train is { } trainId ? $"列车 {trainId.Value}" : "未编组")} · {vehicle.Value.Ownership switch { SettlementRelation.PlayerOwned => "自有", SettlementRelation.NonHostile => "非敌对", _ => "敌对" }}");
            text.AppendLine("\n相邻线路");
            foreach (var edge in foundNode.Value.AdjacentEdges) text.AppendLine(NameOf(new WorldObjectId.Edge(edge)));
        }
        else if (target is WorldObjectId.Edge edge && _view.FindEdge(edge.Id) is Lookup<EdgeView>.Found foundEdge)
        {
            var map = foundEdge.Value.Map;
            text.AppendLine($"起点 / 终点：{NodeName(map.Start)} / {NodeName(map.End)}\n线路长度：{map.LengthMeters:0.##} m\n铁轨：{map.Tracks} / 上限 {map.TrackLimit}\n地貌：{map.Terrain.TypeId.Value}\n");
            foreach (var track in foundEdge.Value.Tracks)
                text.AppendLine($"{track.Number} 号轨：{string.Join("、", track.Trains.Select(id => $"列车 {id.Value}"))}");
        }
        else if (target is WorldObjectId.Train train && _view.FindTrain(train.Id) is Lookup<TrainView>.Found foundTrain)
        {
            var value = foundTrain.Value;
            text.AppendLine($"状态：{Activity(value.Activity)}\n位置：{PositionText(value.Position)}\n速度：{value.SpeedMetersPerSecond:0.00} m/s\n编组：{string.Join(" → ", value.Vehicles.Select(id => $"车辆 {id.Value}"))}\n总长：{value.LengthMeters:0.##} m\n总质量：{value.MassTons:0.##} t\n牵引：{value.TractionKilonewtons:0.##} kN\n加速度：{value.Acceleration:0.##} m/s²\n");
            foreach (var problem in value.Blockers) text.AppendLine(CommandDiagnostics.Describe(problem));
            text.AppendLine("\n运行计划");
            if (value.Plan is { } plan)
            {
                text.AppendLine(plan.Loop ? "循环" : "单次");
                foreach (var stop in plan.Stops) text.AppendLine(stop switch
                {
                    PlanStop.Station station => $"{NodeName(station.Node)} · 停靠 {station.DwellSteps / 60m:0.###} 秒",
                    PlanStop.Waypoint waypoint => $"{NodeName(waypoint.Node)} · 途经",
                    _ => ""
                });
            }
            else text.AppendLine("未设置计划");
        }
        else return "已不存在";
        return text.ToString();
    }

    private string GoodName(GoodId good) => _content.FindGood(good) is ContentLookup<GoodPresentation>.Found found
        ? found.Value.Display.Text.Name : good switch
        { GoodId.Voxel voxel => voxel.Definition.Value, GoodId.Composite composite => composite.Definition.Value,
            GoodId.Assembly assembly => assembly.Composition.Value, _ => "未知货物" };
    private string PositionText(TrainPosition position) => position switch
    { TrainPosition.Node node => NodeName(node.Id), TrainPosition.Edge edge => $"线路 {edge.Id.Value} · {edge.DistanceMeters:0.##} m", _ => "" };
    private static string Relation(SettlementRelation relation) => relation switch
    { SettlementRelation.PlayerOwned => "自有", SettlementRelation.NonHostile => "非敌对", _ => "敌对" };
    private static string Activity(TrainActivity activity) => activity switch
    { TrainActivity.Running => "运行", TrainActivity.Servicing => "装卸", TrainActivity.Waiting => "等待",
        TrainActivity.Stopped => "等待 · 已停车", TrainActivity.NoRoute => "受阻", _ => activity.ToString() };
    private sealed record Detail(PanelTarget Target, PanelContainer Panel, Label Text, List<Button> Buttons);
}
