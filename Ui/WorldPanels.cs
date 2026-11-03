using Godot;
using Train.App;
using Train.Content.Contracts;
using Train.Core.Contracts;

namespace Train.Ui;

internal enum WorldListKind { Nodes, Edges, Fleet, Combats }

public partial class WorldPanels : Control
{
    private HostCoordinator _host = null!;
    private Action<string> _reportError = null!;
    private HostBinding _binding;
    private ICoreReadView _view = null!;
    private IContentView _content = null!;
    private readonly Dictionary<WorldListKind, WorldListDrawer> _lists = [];
    private DetailPanelControls? _detail;
    private TrainInspector? _trainInspector;
    private LineInspector? _lineInspector;
    private Func<PanelTarget?, CoreCommand, SubmissionResult> _submitInline = null!;
    private ConfirmationDialog _disband = null!;
    private PanelTarget? _disbandTarget;

    internal event Action<PanelTarget, TrainView, ICoreReadView, IContentView>? PlanRequested;
    internal event Action<PanelTarget, TrainView, ICoreReadView, IContentView>? TrainFormationRequested;
    internal event Action<PanelTarget, NodeId, ICoreReadView, IContentView>? FormationRequested;
    internal event Action<PanelTarget, NodeId>? FoundingRequested;
    internal event Action<InstanceId>? RefitRequested;
    internal event Action<InstanceId>? VehicleServiceRequested;
    internal event Action<TrainId>? TrainCombatRequested;
    internal event Action<WorldObjectId>? SelectionRequested;
    internal event Action<Control>? ListOpened;
    internal event Action<WorldObjectId, Control>? InspectorOpened;

    internal void Initialize(HostCoordinator host,
        Action<string> reportError,
        Func<PanelTarget?, CoreCommand, SubmissionResult> submitInline)
    { _host = host; _reportError = reportError; _submitInline = submitInline; }

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
        _disband = new ConfirmationDialog { Name = "DisbandConfirmation", OkButtonText = "解编", CancelButtonText = "取消" };
        _disband.Confirmed += () => { if (_trainInspector?.Target == _disbandTarget) _trainInspector?.Disband(); };
        AddChild(_disband);
    }

    internal void Refresh(HostBinding binding, ICoreReadView view, IContentView content)
    {
        if (_view is not null && (_binding != binding || !_host.PanelGenerationCurrent(_binding, _content.Generation)))
        {
            foreach (var entry in _lists.Values) { entry.Panel.GetParent()?.RemoveChild(entry.Panel); entry.Panel.QueueFree(); }
            _lists.Clear();
        }
        _binding = binding; _view = view; _content = content;
        foreach (var entry in _lists.Values) entry.Refresh(view, content);
        _trainInspector?.Refresh(binding, view, content);
        _lineInspector?.Refresh(binding, view, content);
        if (_detail is { } detail)
        {
            var valid = detail.Target.IsAvailable(binding, view, _host.PanelGenerationCurrent(detail.Target.Binding, detail.Target.Generation));
            detail.Refresh(view, content, valid ? NameOf(detail.Target.Object, view, content) : "", valid, _host.IsBusy);
        }
        if (_disbandTarget is { } target)
        {
            var valid = target.IsAvailable(binding, view, _host.PanelGenerationCurrent(target.Binding, target.Generation));
            var train = valid ? (view.FindTrain(((WorldObjectId.Train)target.Object).Id) as Lookup<TrainView>.Found)?.Value : null;
            var reason = train is null ? "已不存在：对象或所属对局已失效。" : TrainInspector.DisbandReason(train, view);
            _disband.GetOkButton().Disabled = reason is not null || _host.IsBusy;
            _disband.DialogText = reason ?? $"解编后 {train!.Vehicles.Length} 辆车停放在{NodeName(((TrainPosition.Node)train.Position).Id, view, content)}。\n车上无货物，运行计划会删除。";
        }
    }

    internal void OpenList(WorldListKind kind)
    {
        if (_view is null) return;
        if (_lists.TryGetValue(kind, out var existing))
        { existing.Panel.Show(); ListOpened?.Invoke(existing.Panel); existing.Refresh(_view, _content); return; }
        PanelContainer panel = null!;
        var title = kind switch { WorldListKind.Nodes => "聚居地", WorldListKind.Edges => "线路",
            WorldListKind.Combats => "战斗", _ => "车队" };
        panel = UiStyle.Panel(this, kind + "List", title, new(24, 68), 360, () => panel.Hide(), out var body);
        var binding = _binding;
        var generation = _content.Generation;
        var list = new WorldListDrawer(panel, body, kind, id =>
        {
            var status = _host.GetStatus();
            if (status is null || !_host.PanelGenerationCurrent(binding, generation)) return;
            SelectionRequested?.Invoke(id);
        }, () => Refresh(_binding, _view, _content));
        _lists.Add(kind, list);
        ListOpened?.Invoke(panel);
        Refresh(_binding, _view, _content);
    }

    internal void Invalidate()
    {
        Refresh(default, _view, _content);
        CloseDetail();
        _detail = null;
        _disband.Hide();
    }

    internal bool IsObjectOpen(WorldObjectId target) =>
        _detail is { } detail && detail.Target.Object == target && detail.Panel.IsVisibleInTree() ||
        _trainInspector is { } train && train.Target.Object == target && train.Panel.IsVisibleInTree() ||
        _lineInspector is { } line && line.Target.Object == target && line.Panel.IsVisibleInTree();

    internal void OpenObject(WorldObjectId target)
    {
        var bound = new PanelTarget(_binding, _content.Generation, target);
        if (_trainInspector is { } opened && opened.Target == bound)
        { opened.Panel.Show(); InspectorOpened?.Invoke(target, opened.Panel); return; }
        if (_lineInspector is { } line && line.Target == bound)
        { line.Panel.Show(); InspectorOpened?.Invoke(target, line.Panel); return; }
        if (_detail is { } existing && existing.Target == bound)
        { existing.Panel.Show(); InspectorOpened?.Invoke(target, existing.Panel); return; }
        CloseDetail();
        _detail = null;
        if (target is WorldObjectId.Edge)
        {
            _lineInspector = new(this, _host, bound, _submitInline, id => SelectionRequested?.Invoke(id));
            Refresh(_binding, _view, _content);
            InspectorOpened?.Invoke(target, _lineInspector.Panel);
            return;
        }
        if (target is WorldObjectId.Train trainTarget)
        {
            _trainInspector = new(this, _host, bound, _submitInline,
                train => { if (Available(bound)) PlanRequested?.Invoke(bound, train, _view, _content); },
                train => { if (Available(bound)) TrainFormationRequested?.Invoke(bound, train, _view, _content); },
                () => TrainCombatRequested?.Invoke(trainTarget.Id), () => ConfirmDisband(bound),
                id => RefitRequested?.Invoke(id), id => VehicleServiceRequested?.Invoke(id));
            Refresh(_binding, _view, _content);
            InspectorOpened?.Invoke(target, _trainInspector.Panel);
            return;
        }
        PanelContainer panel = null!;
        panel = UiStyle.Panel(this, "Details", NameOf(target, _view, _content), new(408, 68), 420,
            () => panel.Hide(), out var body);
        Action<InstanceId> refit = id => RefitRequested?.Invoke(id);
        Action<InstanceId> service = id => VehicleServiceRequested?.Invoke(id);
        var detail = target switch
        {
            WorldObjectId.Node { Id: var nodeId } => new NodeDetailPanel(panel, body, bound, nodeId, () => Available(bound),
                (source, id) => FormationRequested?.Invoke(source, id, _view, _content),
                (source, id) => FoundingRequested?.Invoke(source, id),
                refit, service, NameOf).Controls,
            WorldObjectId.Instance { Id: var instanceId } => new InstanceDetailPanel(panel, body, bound, instanceId, () => Available(bound),
                refit, service).Controls,
            _ => new DetailPanelControls(panel, body, bound, (_, _) => "已不存在", _ => [])
        };
        detail.BuildActions(_view);
        _detail = detail;
        Refresh(_binding, _view, _content);
        InspectorOpened?.Invoke(target, panel);
    }

    private void CloseDetail()
    {
        var panel = _lineInspector?.Panel ?? _trainInspector?.Panel ?? _detail?.Panel;
        if (panel is null) return;
        panel.GetParent()?.RemoveChild(panel);
        panel.QueueFree();
        _lineInspector = null; _trainInspector = null; _detail = null;
        _disband.Hide(); _disbandTarget = null;
    }

    private bool Available(PanelTarget target)
    {
        var status = _host.GetStatus();
        if (status is not null && _host.Content is { } content &&
            target.IsAvailable(status.Binding, status.View, _host.PanelGenerationCurrent(target.Binding, target.Generation)))
        { _binding = status.Binding; _view = status.View; _content = content; return true; }
        _reportError("对象已不存在或所属对局已更换，操作未提交。");
        return false;
    }

    private void ConfirmDisband(PanelTarget target)
    {
        if (!Available(target)) return;
        _disbandTarget = target;
        _disband.Title = $"解编列车 {((WorldObjectId.Train)target.Object).Id.Value}？";
        Refresh(_binding, _view, _content);
        _disband.PopupCentered(new(480, 180));
    }

    private static string NameOf(WorldObjectId id, ICoreReadView view, IContentView content) => id switch
    {
        WorldObjectId.Node node => NodeName(node.Id, view, content),
        WorldObjectId.Edge edge when view.FindEdge(edge.Id) is Lookup<EdgeView>.Found found =>
            $"{NodeName(found.Value.Map.Start, view, content)}—{NodeName(found.Value.Map.End, view, content)}",
        WorldObjectId.Train train => $"列车 {train.Id.Value} · {Activity(view.World.Trains.First(item => item.Id == train.Id).Activity)}",
        WorldObjectId.Instance { Id: InstanceId.Vehicle vehicle } => $"车辆 {vehicle.Id.Value}",
        WorldObjectId.Instance { Id: InstanceId.Building building } => $"建筑 {building.Id.Value}",
        WorldObjectId.Combat combat when view.World.Combats.FirstOrDefault(item => item.Id == combat.Id) is { } item =>
            $"战斗 · {LocationText(item.Location, view, content)}",
        _ => id.ToString()
    };

    internal static string NodeName(NodeId id, ICoreReadView view, IContentView content)
    {
        var node = view.World.Nodes.FirstOrDefault(item => item.Id == id);
        if (node is null) return "地点已不存在";
        if (node.Occupant is NodeOccupant.Settlement settlement)
            return content.FindSettlementLayout(settlement.Id) is ContentLookup<SettlementLayoutProjection>.Found layout
                ? layout.Value.Display.Name : "聚居地";
        var name = node.Occupant is NodeOccupant.Lair ? "生物聚落" : "空节点";
        if (node.Occupant is NodeOccupant.Lair lair &&
            view.FindLair(lair.Id) is Lookup<LairView>.Found found &&
            content.FindLair(found.Value.Type) is ContentLookup<LairAppearanceProjection>.Found appearance)
            return name + " · " + appearance.Value.Display.Text.Name;
        if (content.FindDefinitionDisplay(new CoreDefinitionReference.Terrain(node.Terrain.TypeId)) is ContentLookup<ContentDisplay>.Found display)
            name += " · " + display.Value.Text.Name;
        return name;
    }

    internal static string GoodName(GoodId good, IContentView content) => content.FindGood(good) is ContentLookup<GoodPresentation>.Found found
        ? found.Value.Display.Text.Name : good switch
        { GoodId.Voxel voxel => voxel.Definition.Value, GoodId.Composite composite => composite.Definition.Value,
            GoodId.Assembly assembly => assembly.Composition.Value, _ => "未知货物" };
    internal static string LocationText(WorldLocation location, ICoreReadView view, IContentView content) => location switch
    { WorldLocation.Node node => NodeName(node.Id, view, content),
        WorldLocation.Edge edge when view.World.Edges.FirstOrDefault(item => item.Id == edge.Id) is { } map =>
            $"{NodeName(map.Start, view, content)}—{NodeName(map.End, view, content)} · {edge.DistanceMeters:0.##} 米", _ => "未知地点" };
    internal static string Relation(SettlementRelation relation) => relation switch
    { SettlementRelation.PlayerOwned => "自有", SettlementRelation.NonHostile => "非敌对", _ => "敌对" };
    internal static string Activity(TrainActivity activity) => activity switch
    { TrainActivity.Running => "运行", TrainActivity.Servicing => "装卸", TrainActivity.Waiting => "等待",
        TrainActivity.Stopped => "等待 · 已停车", TrainActivity.NoRoute => "受阻", TrainActivity.Combat => "战斗",
        TrainActivity.Retreating => "撤退", _ => activity.ToString() };
}
