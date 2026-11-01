using Godot;
using Train.App;
using Train.Content.Contracts;
using Train.Core.Contracts;

namespace Train.Ui;

internal enum WorldListKind { Nodes, Edges, Fleet, Combats }

public partial class WorldPanels : Control
{
    private HostCoordinator _host = null!;
    private Func<PanelTarget?, CoreCommand, CommandDecision.Accepted?> _submit = null!;
    private Action<string> _reportError = null!;
    private HostBinding _binding;
    private ICoreReadView _view = null!;
    private IContentView _content = null!;
    private readonly Dictionary<WorldListKind, WorldListPanel> _lists = [];
    private DetailPanelControls? _detail;
    private ConfirmationDialog _disband = null!;
    private PanelTarget? _disbandTarget;
    private TrainId _disbandTrain;

    internal event Action<PanelTarget, TrainView, ICoreReadView, IContentView>? PlanRequested;
    internal event Action<PanelTarget, NodeId, ICoreReadView, IContentView>? FormationRequested;
    internal event Action<PanelTarget, SettlementId>? SettlementRequested;
    internal event Action<PanelTarget, NodeId>? FoundingRequested;
    internal event Action<PanelTarget, EdgeId>? RailwayWorkRequested;
    internal event Action<PanelTarget, TrainId>? InventoryRequested;
    internal event Action<InstanceId>? RefitRequested;
    internal event Action<InstanceId>? VehicleServiceRequested;
    internal event Action<TrainId>? TrainCombatRequested;
    internal event Action<SettlementId>? SettlementCombatRequested;
    internal event Action<WorldObjectId>? SelectionRequested;
    internal event Action<Control>? ListOpened;
    internal event Action<WorldObjectId, Control>? InspectorOpened;

    internal void Initialize(HostCoordinator host,
        Func<PanelTarget?, CoreCommand, CommandDecision.Accepted?> submit, Action<string> reportError)
    { _host = host; _submit = submit; _reportError = reportError; }

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
        _disband = new ConfirmationDialog { Name = "DisbandConfirmation", OkButtonText = "解编", CancelButtonText = "取消" };
        _disband.Confirmed += () => _submit(_disbandTarget, new CoreCommand.DisbandTrain(_disbandTrain));
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
        foreach (var entry in _lists.Values) entry.Refresh(view, content, NameOf);
        if (_detail is { } detail)
        {
            var valid = detail.Target.IsAvailable(binding, view, _host.PanelGenerationCurrent(detail.Target.Binding, detail.Target.Generation));
            detail.Refresh(view, content, valid ? NameOf(detail.Target.Object, view, content) : "", valid, _host.IsBusy);
        }
        if (_disbandTarget is { } target)
        {
            var valid = target.IsAvailable(binding, view, _host.PanelGenerationCurrent(target.Binding, target.Generation));
            _disband.GetOkButton().Disabled = !valid || _host.IsBusy;
            _disband.DialogText = valid ? "解编后车辆停放在所在聚居地。" : "已不存在：对象或所属对局已失效。";
        }
    }

    internal void OpenList(WorldListKind kind)
    {
        if (_view is null) return;
        if (_lists.TryGetValue(kind, out var existing))
        { existing.Panel.Show(); ListOpened?.Invoke(existing.Panel); return; }
        PanelContainer panel = null!;
        var title = kind switch { WorldListKind.Nodes => "聚居地", WorldListKind.Edges => "线路",
            WorldListKind.Combats => "战斗", _ => "车队" };
        panel = UiStyle.Panel(this, kind + "List", title, new(24, 68), 360, () => panel.Hide(), out var body);
        var binding = _binding;
        var generation = _content.Generation;
        var list = new WorldListPanel(panel, body, kind, id =>
        {
            var status = _host.GetStatus();
            if (status is null || !_host.PanelGenerationCurrent(binding, generation)) return;
            SelectionRequested?.Invoke(id);
        }, () => Refresh(_binding, _view, _content));
        _lists.Add(kind, list);
        Refresh(_binding, _view, _content);
        ListOpened?.Invoke(panel);
    }

    internal void Invalidate()
    {
        Refresh(default, _view, _content);
        CloseDetail();
        _detail = null;
        _disband.Hide();
    }

    internal bool IsObjectOpen(WorldObjectId target) =>
        _detail is { } detail && detail.Target.Object == target && detail.Panel.IsVisibleInTree();

    internal IEnumerable<InstanceId> OpenInstances() => _detail is { } detail && detail.Panel.IsVisibleInTree() &&
        detail.Target.Object is WorldObjectId.Instance instance ? [instance.Id] : [];

    internal void OpenObject(WorldObjectId target)
    {
        var bound = new PanelTarget(_binding, _content.Generation, target);
        if (_detail is { } existing && existing.Target == bound)
        { existing.Panel.Show(); InspectorOpened?.Invoke(target, existing.Panel); return; }
        CloseDetail();
        _detail = null;
        PanelContainer panel = null!;
        panel = UiStyle.Panel(this, "Details", NameOf(target, _view, _content), new(408, 68), 420,
            () => panel.Hide(), out var body);
        Action<InstanceId> refit = id => RefitRequested?.Invoke(id);
        Action<InstanceId> service = id => VehicleServiceRequested?.Invoke(id);
        var detail = target switch
        {
            WorldObjectId.Node { Id: var nodeId } => new NodeDetailPanel(panel, body, bound, nodeId, () => Available(bound),
                (source, id) => FormationRequested?.Invoke(source, id, _view, _content),
                (source, id) => SettlementRequested?.Invoke(source, id),
                id => SettlementCombatRequested?.Invoke(id),
                (source, id) => FoundingRequested?.Invoke(source, id),
                refit, service, NameOf).Controls,
            WorldObjectId.Edge { Id: var edgeId } => new EdgeDetailPanel(panel, body, bound, edgeId, () => Available(bound),
                (source, id) => RailwayWorkRequested?.Invoke(source, id)).Controls,
            WorldObjectId.Train { Id: var trainId } => new TrainDetailPanel(panel, body, bound, trainId, () => Available(bound),
                (source, id) => InventoryRequested?.Invoke(source, id), id => TrainCombatRequested?.Invoke(id),
                (source, id) =>
                {
                    if (_view.FindTrain(id) is Lookup<TrainView>.Found found)
                        PlanRequested?.Invoke(source, found.Value, _view, _content);
                }, _submit, ConfirmDisband, refit, service).Controls,
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
        if (_detail is not { } detail) return;
        detail.Panel.GetParent()?.RemoveChild(detail.Panel);
        detail.Panel.QueueFree();
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

    private void ConfirmDisband(PanelTarget target, TrainId train)
    {
        if (!Available(target)) return;
        _disbandTarget = target; _disbandTrain = train;
        _disband.Title = $"解编列车 {train.Value}？";
        Refresh(_binding, _view, _content);
        _disband.PopupCentered(new(480, 180));
    }

    private static string NameOf(WorldObjectId id, ICoreReadView view, IContentView content) => id switch
    {
        WorldObjectId.Node node => NodeName(node.Id, view, content),
        WorldObjectId.Edge edge when view.FindEdge(edge.Id) is Lookup<EdgeView>.Found found =>
            $"线路 {edge.Id.Value} · {NodeName(found.Value.Map.Start, view, content)}—{NodeName(found.Value.Map.End, view, content)}",
        WorldObjectId.Train train => $"列车 {train.Id.Value} · {Activity(view.World.Trains.First(item => item.Id == train.Id).Activity)}",
        WorldObjectId.Instance { Id: InstanceId.Vehicle vehicle } => $"车辆 {vehicle.Id.Value}",
        WorldObjectId.Instance { Id: InstanceId.Building building } => $"建筑 {building.Id.Value}",
        WorldObjectId.Combat combat when view.World.Combats.FirstOrDefault(item => item.Id == combat.Id) is { } item =>
            $"战斗 · {BattleReportLog.Place(item.Location)}",
        _ => id.ToString()
    };

    internal static string NodeName(NodeId id, ICoreReadView view, IContentView content)
    {
        var node = view.World.Nodes.FirstOrDefault(item => item.Id == id);
        if (node?.Occupant is NodeOccupant.Settlement settlement &&
            content.FindSettlementLayout(settlement.Id) is ContentLookup<SettlementLayoutProjection>.Found layout)
            return $"{layout.Value.Display.Name} · {id.Value}";
        return $"节点 {id.Value}";
    }

    internal static string GoodName(GoodId good, IContentView content) => content.FindGood(good) is ContentLookup<GoodPresentation>.Found found
        ? found.Value.Display.Text.Name : good switch
        { GoodId.Voxel voxel => voxel.Definition.Value, GoodId.Composite composite => composite.Definition.Value,
            GoodId.Assembly assembly => assembly.Composition.Value, _ => "未知货物" };
    internal static string LocationText(WorldLocation location, ICoreReadView view, IContentView content) => location switch
    { WorldLocation.Node node => NodeName(node.Id, view, content),
        WorldLocation.Edge edge => $"线路 {edge.Id.Value} · {edge.DistanceMeters:0.##} m", _ => "未知地点" };
    internal static string Relation(SettlementRelation relation) => relation switch
    { SettlementRelation.PlayerOwned => "自有", SettlementRelation.NonHostile => "非敌对", _ => "敌对" };
    internal static string Activity(TrainActivity activity) => activity switch
    { TrainActivity.Running => "运行", TrainActivity.Servicing => "装卸", TrainActivity.Waiting => "等待",
        TrainActivity.Stopped => "等待 · 已停车", TrainActivity.NoRoute => "受阻", TrainActivity.Combat => "战斗",
        TrainActivity.Retreating => "撤退", _ => activity.ToString() };
}
