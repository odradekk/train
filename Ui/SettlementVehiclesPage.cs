using System.Collections.Immutable;
using Godot;
using Train.App;
using Train.Content.Contracts;
using Train.Core.Contracts;
using static Train.Ui.EconomyPanelControls;
using static Train.Ui.EconomyPanels;

namespace Train.Ui;

/// <summary>
/// 在聚居地查看车辆委托，选择停放车辆和购买现车。
/// Decision note: 2026-10-31-object-centric-management-screens
/// </summary>
internal sealed partial class SettlementVehiclesPage(
    Func<bool> canAct,
    Func<PanelTarget, CoreCommand, SubmissionResult> submit,
    Action<PanelTarget, NodeId, ImmutableArray<VehicleId>, ICoreReadView, IContentView> formationRequested)
{
    private PanelTarget _target = null!;
    private HostBinding _binding;
    private ICoreReadView _view = null!;
    private IContentView _content = null!;
    private SettlementView _settlement = null!;
    private VBoxContainer? _body;
    private VBoxContainer _parkedRows = null!, _saleRows = null!;
    private Label _jobs = null!, _parkedEmpty = null!, _soldOut = null!, _result = null!;
    private Label _purchaseTotal = null!, _saleReason = null!, _repairReason = null!, _dismantleReason = null!;
    private Button _buy = null!, _form = null!, _repair = null!, _sell = null!, _dismantle = null!;
    private readonly List<VehicleId> _buySelection = [], _parkedSelection = [];
    private readonly Dictionary<VehicleId, CheckBox> _saleChecks = [], _parkedChecks = [];
    private readonly Dictionary<SizeClass, Label> _capacities = [];
    private CoreRevision? _quotedRevision;
    private bool _owned;

    internal void Bind(HostBinding binding, ICoreReadView view, IContentView content, SettlementView settlement, PanelTarget target)
    { _binding = binding; _view = view; _content = content; _settlement = settlement; _target = target; }

    internal void BuildVehicles(VBoxContainer body, bool owned)
    {
        Close(); _body = body; _owned = owned;
        _jobs = Text(body, "VehicleJobs", "");
        Text(body, "ParkedTitle", "停在这里的车辆 · 未编入列车");
        _parkedRows = new() { Name = "ParkedVehicles" }; body.AddChild(_parkedRows);
        _parkedEmpty = Text(body, "ParkedEmpty", "这里没有未编组的玩家车辆。");
        _form = UiStyle.Button("FormParked", "用所选车辆编组", () => OpenFormation([.. _parkedSelection]), true);
        body.AddChild(_form);
        var actions = new HFlowContainer(); body.AddChild(actions);
        _repair = UiStyle.Button("RepairVehicles", "修理", () => Execute(_repairBatch, false), true); actions.AddChild(_repair);
        _sell = UiStyle.Button("SellVehicles", "出售", () => Confirm(_sellBatch), true); actions.AddChild(_sell);
        _dismantle = UiStyle.Button("DismantleVehicles", "拆解…", () => Confirm(_dismantleBatch), true); actions.AddChild(_dismantle);
        _sell.Visible = !owned; _dismantle.Visible = owned;
        _repairReason = Text(body, "RepairReason", "请勾选停放车辆。");
        _saleReason = Text(body, "SaleReason", ""); _saleReason.Visible = !owned;
        _dismantleReason = Text(body, "DismantleReason", ""); _dismantleReason.Visible = owned;
        var sale = new VBoxContainer { Visible = !owned }; body.AddChild(sale);
        Text(sale, "SaleTitle", "在售现车");
        _saleRows = new() { Name = "VehiclesForSale" }; sale.AddChild(_saleRows);
        _soldOut = Text(sale, "SoldOut", "现车已售罄。");
        _purchaseTotal = Text(sale, "PurchaseTotal", "请勾选要购买的车辆。");
        _buy = UiStyle.Button("BuyVehicles", "购买所选车辆", () => Confirm(_buyBatch), true); sale.AddChild(_buy);
        _result = Text(body, "VehicleResult", "");
        BuildConfirmations(body);
    }

    internal void RefreshVehicles()
    {
        if (_body is null || _settlement is null) return;
        var jobs = _settlement.CommissionJobs.Concat(_settlement.OwnedJobs).Select(_view.FindJob)
            .OfType<Lookup<JobView>.Found>().Select(item => item.Value).OfType<JobView.Construction>()
            .Select(item => item.Value).Where(job => job.Target is ConstructionTarget.NewVehicle
                or ConstructionTarget.Existing { Instance: InstanceId.Vehicle });
        var descriptions = jobs.Select(job => DescribeVehicleJob(_view, _content, job)).ToArray();
        _jobs.Text = descriptions.Length == 0
            ? _owned ? "没有进行中的修理或拆解。" : "没有进行中的委托。"
            : (_owned ? "进行中的施工\n" : "进行中的委托\n") + string.Join("\n", descriptions);
        var parked = _settlement.ParkedVehicles.Where(id => _view.FindInstance(new InstanceId.Vehicle(id)) is
            Lookup<InstanceView>.Found { Value.Ownership: SettlementRelation.PlayerOwned }).ToArray();
        SyncVehicles(_parkedRows, _parkedChecks, parked, _parkedSelection, "ParkedVehicle");
        SyncVehicles(_saleRows, _saleChecks, _settlement.VehiclesForSale, _buySelection, "BuyVehicle");
        _parkedEmpty.Visible = parked.Length == 0;
        _soldOut.Visible = _settlement.VehiclesForSale.IsEmpty;
        foreach (var size in _capacities.Keys.Where(size => !_settlement.VehiclePurchaseCapacities.Any(item => item.Size == size)).ToArray())
        { RemoveControl(_capacities[size]); _capacities.Remove(size); }
        foreach (var capacity in _settlement.VehiclePurchaseCapacities)
        {
            if (!_capacities.TryGetValue(capacity.Size, out var label))
                _capacities.Add(capacity.Size, label = Text(_saleRows.GetParent(), "PurchaseCapacity", ""));
            label.Text = $"{SizeName(capacity.Size)} 收购名额：{capacity.Available} · 待售 {capacity.ForSale} / 上限 {capacity.Limit}";
        }
        UpdateVehicleActions();
    }

    private void SyncVehicles(VBoxContainer parent, Dictionary<VehicleId, CheckBox> checks,
        IEnumerable<VehicleId> available, List<VehicleId> selection, string prefix)
    {
        var ids = available.Concat(selection).Distinct().ToArray();
        foreach (var id in checks.Keys.Except(ids).ToArray()) { RemoveControl(checks[id]); checks.Remove(id); }
        foreach (var id in ids)
        {
            if (!checks.TryGetValue(id, out var check))
            {
                check = new() { Name = prefix + id.Value, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                    Alignment = HorizontalAlignment.Left, AutowrapMode = TextServer.AutowrapMode.WordSmart };
                parent.AddChild(check); checks.Add(id, check);
                check.Toggled += selected =>
                {
                    if (selected) selection.Add(id); else selection.Remove(id);
                    _quotedRevision = null; _result.Text = ""; UpdateVehicleActions();
                };
            }
            check.Text = _view.FindInstance(new InstanceId.Vehicle(id)) is Lookup<InstanceView>.Found instance
                ? $"{VehicleName(id)} · 结构耐久 {instance.Value.Structure.EffectiveCurrent}/{instance.Value.Structure.Maximum}" : "车辆已不存在";
            check.SetPressedNoSignal(selection.Contains(id));
        }
    }

    private string VehicleName(VehicleId id) =>
        _view.FindInstance(new InstanceId.Vehicle(id)) is Lookup<InstanceView>.Found instance &&
        _content.FindDesign(instance.Value.Design) is ContentLookup<DesignEvaluation>.Found { Value: DesignEvaluation.Complete complete }
            ? complete.Definition.Display.Name : "设计内容缺失的车辆";

    internal void UpdateVehicleActions()
    {
        if (_body is null || !_body.IsVisibleInTree() || _settlement is null) return;
        if (_quotedRevision != _view.Revision)
        { _quotedRevision = _view.Revision; ReadQuotes(); }
        var enabled = canAct();
        foreach (var check in _saleChecks.Values.Concat(_parkedChecks.Values)) check.Disabled = !enabled;
        _form.Disabled = !enabled || _parkedSelection.Count == 0;
        _form.Text = $"用所选 {_parkedSelection.Count} 辆编组列车";
        _buy.Disabled = !enabled || _buyBatch.IsEmpty;
        _sell.Disabled = !enabled || _sellBatch.IsEmpty;
        _repair.Disabled = !enabled || _repairBatch.IsEmpty;
        _dismantle.Disabled = !enabled || _dismantleBatch.IsEmpty;
    }

    private void OpenFormation(ImmutableArray<VehicleId> vehicles)
    {
        if (canAct() && !vehicles.IsEmpty)
            formationRequested(new(_binding, _content.Generation, new WorldObjectId.Node(_settlement.Node)),
                _settlement.Node, vehicles, _view, _content);
    }

    internal void Close()
    {
        _body = null; _quotedRevision = null;
        _buySelection.Clear(); _parkedSelection.Clear(); _saleChecks.Clear(); _parkedChecks.Clear(); _capacities.Clear();
        _buyBatch = _sellBatch = _repairBatch = _dismantleBatch = [];
        _pending = [];
    }
}
