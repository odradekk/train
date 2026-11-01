using Godot;
using Train.App;
using Train.Content.Contracts;
using Train.Core.Contracts;
using static Train.Ui.EconomyPanelControls;
using static Train.Ui.EconomyPanels;

namespace Train.Ui;

internal sealed class SettlementVehiclesPage
{
    private PanelTarget _settlementTarget = null!;
    private readonly Func<bool> _canAct;
    private ICoreReadView _view = null!;
    private IContentView _content = null!;
    private SettlementView _settlement = null!;
    private HostBinding _binding;
    private readonly Action<Action> _quote;
    private readonly Action<PanelTarget, CoreCommand, Action> _submit;
    private readonly Action<PanelTarget, NodeId, ICoreReadView, IContentView> _formationRequested;

    internal SettlementVehiclesPage(Func<bool> canAct, Action<Action> quote, Action<PanelTarget, CoreCommand, Action> submit, Action<PanelTarget, NodeId, ICoreReadView, IContentView> formationRequested)
    { _canAct = canAct; _quote = quote; _submit = submit; _formationRequested = formationRequested; }

    internal void Bind(HostBinding binding, ICoreReadView view, IContentView content, SettlementView settlement, PanelTarget target)
    { _binding = binding; _view = view; _content = content; _settlement = settlement; _settlementTarget = target; }

    private VBoxContainer _vehicleRows = null!;
    private Label _soldOut = null!;
    private readonly Dictionary<SizeClass, Label> _capacityItems = [];
    private readonly Dictionary<VehicleId, VehicleRow> _vehicleItems = [];
    private VehicleId? _vehicle;
    private VehicleTradeAction _vehicleAction;
    private Label _vehicleDetails = null!;
    private Label _vehicleDescription = null!;
    private Button _vehicleQuoteButton = null!;
    private Button _vehicleConfirm = null!;
    private Button _formPurchased = null!;
    private VehicleTradeQuote? _vehicleQuote;
    private bool _vehicleAvailable;
    private sealed record VehicleRow(HBoxContainer Control, Label Description, Button Select)
    {
        internal VehicleTradeAction Action;
    }

    internal void BuildVehicles(VBoxContainer body)
    {
        _vehicle = null; _vehicleQuote = null; _vehicleAvailable = false; _vehicleItems.Clear(); _capacityItems.Clear();
        _vehicleRows = List(body, "Vehicles", 270); _soldOut = Text(_vehicleRows, "SoldOut", "现车已售罄。");
        _vehicleDetails = Text(body, "VehicleDetails", "请选择实际车辆。");
        var actions = Footer(body);
        _vehicleQuoteButton = UiStyle.Button("QuoteVehicle", "获取报价", () => _quote(RequestVehicleQuote));
        _vehicleConfirm = UiStyle.Button("ConfirmVehicle", "确认买卖", ConfirmVehicle, true);
        _formPurchased = UiStyle.Button("FormPurchased", "编组…", () =>
        {
            if (_canAct()) _formationRequested?.Invoke(new(_binding, _content.Generation, new WorldObjectId.Node(_settlement!.Node)), _settlement.Node, _view, _content);
        });
        actions.AddChild(_vehicleQuoteButton); actions.AddChild(_vehicleConfirm); actions.AddChild(_formPurchased);
        _vehicleDescription = Text(body, "VehicleQuote", "报价不锁定。成交保留原车辆、位置和损伤，不自动编组。");
    }

    internal void RefreshVehicles()
    {
        _soldOut.Visible = _settlement!.VehiclesForSale.IsEmpty;
        var capacities = _settlement.VehiclePurchaseCapacities;
        foreach (var size in _capacityItems.Keys.Where(size => !capacities.Any(item => item.Size == size)).ToArray())
        { RemoveControl(_capacityItems[size]); _capacityItems.Remove(size); }
        foreach (var capacity in capacities)
        {
            if (!_capacityItems.TryGetValue(capacity.Size, out var label))
                _capacityItems.Add(capacity.Size, label = Text(_vehicleRows, "PurchaseCapacity", ""));
            label.Text = $"{SizeName(capacity.Size)} 收购名额：{capacity.Available} · 待售 {capacity.ForSale} / 上限 {capacity.Limit}";
            label.AddThemeColorOverride("font_color", capacity.Available == 0 ? UiStyle.Warning : UiStyle.Foreground);
        }
        var vehicles = _settlement.VehiclesForSale.Select(id => (Id: id, Action: VehicleTradeAction.Buy))
            .Concat(_settlement.ParkedVehicles.Where(id => _view.FindInstance(new InstanceId.Vehicle(id)) is
                Lookup<InstanceView>.Found { Value.Ownership: SettlementRelation.PlayerOwned }).Select(id => (Id: id, Action: VehicleTradeAction.Sell)))
            .ToArray();
        foreach (var id in _vehicleItems.Keys.Where(id => !vehicles.Any(item => item.Id == id)).ToArray())
        { RemoveControl(_vehicleItems[id].Control); _vehicleItems.Remove(id); }
        foreach (var (id, action) in vehicles)
        {
            if (_view.FindInstance(new InstanceId.Vehicle(id)) is not Lookup<InstanceView>.Found found) continue;
            if (!_vehicleItems.TryGetValue(id, out var row))
            {
                var box = new HBoxContainer(); _vehicleRows.AddChild(box);
                var label = Text(box, "Vehicle", "");
                VehicleRow created = null!;
                var select = UiStyle.Button("SelectVehicle", "", () =>
                {
                    _vehicle = id; _vehicleAction = created.Action;
                    ResetQuote(ref _vehicleQuote, _vehicleDescription, "请选择获取报价，再确认买卖。");
                    ShowVehicleDetails(); UpdateVehicleActions();
                });
                box.AddChild(select); row = created = new(box, label, select); _vehicleItems.Add(id, row);
            }
            row.Action = action; row.Select.Name = action == VehicleTradeAction.Buy ? "SelectBuyVehicle" : "SelectSellVehicle";
            row.Select.Text = action == VehicleTradeAction.Buy ? "买入…" : "卖出…"; row.Select.Disabled = !_canAct();
            row.Description.Text = $"车辆 {id.Value} · 结构耐久 {found.Value.Structure.EffectiveCurrent}/{found.Value.Structure.Maximum} · {(action == VehicleTradeAction.Buy ? "待售" : WorldPanels.Relation(found.Value.Ownership))}";
        }
        ShowVehicleDetails();
    }

    private void ShowVehicleDetails()
    {
        _vehicleAvailable = false;
        if (_vehicle is not { } id) return;
        if (_view.FindInstance(new InstanceId.Vehicle(id)) is not Lookup<InstanceView>.Found found)
        { _vehicleDetails.Text = "已不存在：所选车辆"; MissingSelection(ref _vehicleQuote, _vehicleDescription, "车辆"); return; }
        var value = found.Value;
        _vehicleAvailable = _vehicleAction == VehicleTradeAction.Buy ? _settlement!.VehiclesForSale.Contains(id)
            : _settlement!.ParkedVehicles.Contains(id) && value.Ownership == SettlementRelation.PlayerOwned;
        // 成交后车辆仍在清单里，只是买卖方向反过来了。
        var traded = !_vehicleAvailable && _vehicleItems.TryGetValue(id, out var listed) && listed.Action != _vehicleAction;
        var result = _vehicleAction == VehicleTradeAction.Buy ? "已买入，可在本站编组" : "已卖出，现为本站待售";
        if (traded) ResetQuote(ref _vehicleQuote, _vehicleDescription, $"车辆 {id.Value} {result}。");
        else if (!_vehicleAvailable) MissingSelection(ref _vehicleQuote, _vehicleDescription, "车辆");
        var locks = new List<string>();
        if (value.Lock.HasFlag(InstanceLock.UnderRefit)) locks.Add("改装中");
        if (value.Lock.HasFlag(InstanceLock.UnderRepair)) locks.Add("修理中");
        if (value.Lock.HasFlag(InstanceLock.UnderDeconstruction)) locks.Add("拆解中");
        if (value.Lock.HasFlag(InstanceLock.Combat)) locks.Add("战斗中");
        _vehicleDetails.Text = $"车辆 {id.Value} · {(traded ? result : _vehicleAction == VehicleTradeAction.Buy ? "买入" : "卖出")}\n所在地：{WorldPanels.LocationText(value.Location, _view, _content)}\n归属：{WorldPanels.Relation(value.Ownership)} · {(locks.Count == 0 ? "未锁定" : string.Join("、", locks))}\n结构耐久：{value.Structure.EffectiveCurrent}/{value.Structure.Maximum}" +
            string.Concat(value.Parts.Select(part => $"\n部件 {part.Part.Value}：{part.Durability.EffectiveCurrent}/{part.Durability.Maximum}" +
                (part.Destroyed ? "（已毁）" : part.DefinitionMissing ? "（定义缺失）" : ""))) +
            (value.MissingContent.IsEmpty ? "" : "\n" + Problems(value.MissingContent, _content)) +
            (_vehicleAvailable || traded ? "" : "\n所选车辆已不在这份买卖清单中，请重新选择。");
    }

    private void RequestVehicleQuote()
    {
        _vehicleQuote = null;
        if (_vehicle is not { } id || !_vehicleAvailable) return;
        var request = new VehicleTradeQuoteRequest(_settlement.Id, id, _vehicleAction);
        _vehicleQuote = ReadQuote(_view.QuoteVehicle(request), _vehicleDescription, _content);
    }

    private void ConfirmVehicle()
    {
        if (_vehicleConfirm.Disabled || _vehicleQuote is not { Total: { } total } quote) return;
        CoreCommand command = quote.Request.Action == VehicleTradeAction.Buy
            ? new CoreCommand.BuyVehicle(quote.Request.Settlement, quote.Request.Vehicle, total)
            : new CoreCommand.SellVehicle(quote.Request.Settlement, quote.Request.Vehicle, total);
        _submit(_settlementTarget!, command, RequestVehicleQuote);
    }

    internal void UpdateVehicleActions()
    {
        _vehicleQuoteButton.Disabled = !_canAct() || !_vehicleAvailable;
        _vehicleConfirm.Disabled = !_canAct() || !_vehicleAvailable || _vehicleQuote is not { Total: not null, Blockers.IsEmpty: true };
        _formPurchased.Disabled = !_canAct();
        if (_vehicleQuote is { } quote) ShowQuote(_view, _content, _vehicleDescription, quote.Revision, $"整车总额：{Amount(quote.Total)}", quote.Blockers,
            reason: quote.Total is null ? "报价未提供总额，不能确认买卖。" : null);
    }
    internal void Close() { _vehicleItems.Clear(); _capacityItems.Clear(); _vehicleQuote = null; }
}
