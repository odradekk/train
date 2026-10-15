using Godot;
using Train.Core.Contracts;

namespace Train.Ui;

internal partial class EconomyPanels
{
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

    private void BuildVehicles(VBoxContainer body)
    {
        _vehicle = null; _vehicleQuote = null; _vehicleAvailable = false; _vehicleItems.Clear(); _capacityItems.Clear();
        _vehicleRows = List(body, "Vehicles", 270); _soldOut = Text(_vehicleRows, "SoldOut", "现车已售罄。");
        _vehicleDetails = Text(body, "VehicleDetails", "请选择实际车辆。");
        var actions = new HBoxContainer(); body.AddChild(actions);
        _vehicleQuoteButton = UiStyle.Button("QuoteVehicle", "获取报价", () => Quote(RequestVehicleQuote));
        _vehicleConfirm = UiStyle.Button("ConfirmVehicle", "确认买卖", ConfirmVehicle, true);
        _formPurchased = UiStyle.Button("FormPurchased", "编组…", () =>
        {
            if (CanAct) FormationRequested?.Invoke(new(_binding, _content.Generation, new WorldObjectId.Node(_settlement!.Node)), _settlement.Node, _view, _content);
        });
        actions.AddChild(_vehicleQuoteButton); actions.AddChild(_vehicleConfirm); actions.AddChild(_formPurchased);
        _vehicleDescription = Text(body, "VehicleQuote", "报价不锁定。成交保留原车辆、位置和损伤，不自动编组。");
    }

    private void RefreshVehicles()
    {
        _soldOut.Visible = _settlement!.VehiclesForSale.IsEmpty;
        var capacities = _settlement.VehiclePurchaseCapacities;
        foreach (var size in _capacityItems.Keys.Where(size => !capacities.Any(item => item.Size == size)).ToArray())
        { RemoveControl(_capacityItems[size]); _capacityItems.Remove(size); }
        foreach (var capacity in capacities)
        {
            if (!_capacityItems.TryGetValue(capacity.Size, out var label))
                _capacityItems.Add(capacity.Size, label = Text(_vehicleRows, "PurchaseCapacity", ""));
            var size = capacity.Size switch
            { SizeClass.Small => "小型", SizeClass.Medium => "中型", SizeClass.Large => "大型", SizeClass.ExtraLarge => "超大型", SizeClass.Titan => "泰坦", _ => capacity.Size.ToString() };
            label.Text = $"{size} 收购名额：{capacity.Available} · 待售 {capacity.ForSale} / 上限 {capacity.Limit}";
            label.AddThemeColorOverride("font_color", capacity.Available == 0 ? UiStyle.Warning : Colors.White);
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
            row.Select.Text = action == VehicleTradeAction.Buy ? "买入…" : "卖出…"; row.Select.Disabled = !CanAct;
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
        if (!_vehicleAvailable) MissingSelection(ref _vehicleQuote, _vehicleDescription, "车辆");
        var locks = new List<string>();
        if (value.Lock.HasFlag(InstanceLock.UnderRefit)) locks.Add("改装中");
        if (value.Lock.HasFlag(InstanceLock.UnderRepair)) locks.Add("修理中");
        if (value.Lock.HasFlag(InstanceLock.UnderDeconstruction)) locks.Add("拆解中");
        if (value.Lock.HasFlag(InstanceLock.Combat)) locks.Add("战斗中");
        _vehicleDetails.Text = $"车辆 {id.Value} · {(_vehicleAction == VehicleTradeAction.Buy ? "买入" : "卖出")}\n所在地：{WorldPanels.LocationText(value.Location, _view, _content)}\n归属：{WorldPanels.Relation(value.Ownership)} · {(locks.Count == 0 ? "未锁定" : string.Join("、", locks))}\n结构耐久：{value.Structure.EffectiveCurrent}/{value.Structure.Maximum}" +
            string.Concat(value.Parts.Select(part => $"\n部件 {part.Part.Value}：{part.Durability.EffectiveCurrent}/{part.Durability.Maximum}" +
                (part.Destroyed ? "（已毁）" : part.DefinitionMissing ? "（定义缺失）" : ""))) +
            (value.MissingContent.IsEmpty ? "" : "\n" + Problems(value.MissingContent)) +
            (_vehicleAvailable ? "" : "\n所选车辆已不在这份买卖清单中，请重新选择。已购车辆可在本站编组。");
    }

    private void RequestVehicleQuote()
    {
        _vehicleQuote = null;
        if (_vehicle is not { } id || !_vehicleAvailable) return;
        var request = new VehicleTradeQuoteRequest(_settlementId, id, _vehicleAction);
        _vehicleQuote = ReadQuote(_view.QuoteVehicle(request), _vehicleDescription);
    }

    private void ConfirmVehicle()
    {
        if (_vehicleConfirm.Disabled || _vehicleQuote is not { Total: { } total } quote) return;
        CoreCommand command = quote.Request.Action == VehicleTradeAction.Buy
            ? new CoreCommand.BuyVehicle(quote.Request.Settlement, quote.Request.Vehicle, total)
            : new CoreCommand.SellVehicle(quote.Request.Settlement, quote.Request.Vehicle, total);
        Submit(_settlementTarget!, command, RequestVehicleQuote);
    }

    private void UpdateVehicleActions()
    {
        _vehicleQuoteButton.Disabled = !CanAct || !_vehicleAvailable;
        _vehicleConfirm.Disabled = !CanAct || !_vehicleAvailable || _vehicleQuote is not { Total: not null, Blockers.IsEmpty: true };
        _formPurchased.Disabled = !CanAct;
        if (_vehicleQuote is { } quote) ShowQuote(_vehicleDescription, quote.Revision, $"整车总额：{Amount(quote.Total)}", quote.Blockers,
            reason: quote.Total is null ? "报价未提供总额，不能确认买卖。" : null);
    }
}
