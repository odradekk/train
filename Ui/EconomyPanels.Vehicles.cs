using Godot;
using Train.Core.Contracts;

namespace Train.Ui;

internal partial class EconomyPanels
{
    private VBoxContainer _vehicleRows = null!;
    private VehicleId? _vehicle;
    private VehicleTradeAction _vehicleAction;
    private Label _vehicleDetails = null!;
    private Label _vehicleDescription = null!;
    private Button _vehicleQuoteButton = null!;
    private Button _vehicleConfirm = null!;
    private Button _formPurchased = null!;
    private VehicleTradeQuote? _vehicleQuote;
    private bool _vehicleAvailable;

    private void BuildVehicles(VBoxContainer body)
    {
        _vehicle = null; _vehicleQuote = null; _vehicleAvailable = false;
        _vehicleRows = List(body, "Vehicles", 270);
        _vehicleDetails = Text(body, "VehicleDetails", "请选择实际车辆。");
        _vehicleDescription = Text(body, "VehicleQuote", "报价不锁定。成交保留原车辆、位置和损伤，不自动编组。");
        var actions = new HBoxContainer(); body.AddChild(actions);
        _vehicleQuoteButton = UiStyle.Button("QuoteVehicle", "获取报价", QuoteVehicle);
        _vehicleConfirm = UiStyle.Button("ConfirmVehicle", "确认买卖", ConfirmVehicle, true);
        _formPurchased = UiStyle.Button("FormPurchased", "编组…", () =>
        {
            if (CanAct) FormationRequested?.Invoke(new(_binding, _content.Generation, new WorldObjectId.Node(_settlement!.Node)), _settlement.Node, _view, _content);
        });
        actions.AddChild(_vehicleQuoteButton); actions.AddChild(_vehicleConfirm); actions.AddChild(_formPurchased);
    }

    private void RefreshVehicles()
    {
        Clear(_vehicleRows);
        if (_settlement!.VehiclesForSale.IsEmpty) Text(_vehicleRows, "SoldOut", "现车已售罄。");
        foreach (var capacity in _settlement.VehiclePurchaseCapacities)
        {
            var size = capacity.Size switch
            { SizeClass.Small => "小型", SizeClass.Medium => "中型", SizeClass.Large => "大型", SizeClass.ExtraLarge => "超大型", SizeClass.Titan => "巨型", _ => capacity.Size.ToString() };
            var label = Text(_vehicleRows, "PurchaseCapacity", $"{size} 收购名额：{capacity.Available} · 待售 {capacity.ForSale} / 上限 {capacity.Limit}");
            if (capacity.Available == 0) label.AddThemeColorOverride("font_color", UiStyle.Warning);
        }
        foreach (var id in _settlement.VehiclesForSale) VehicleRow(id, VehicleTradeAction.Buy);
        foreach (var id in _settlement.ParkedVehicles)
            if (_view.FindInstance(new InstanceId.Vehicle(id)) is Lookup<InstanceView>.Found { Value.Ownership: SettlementRelation.PlayerOwned })
                VehicleRow(id, VehicleTradeAction.Sell);
        ShowVehicleDetails();
        if (_vehicleQuote is not null) RequestVehicleQuote();
    }

    private void VehicleRow(VehicleId id, VehicleTradeAction action)
    {
        if (_view.FindInstance(new InstanceId.Vehicle(id)) is not Lookup<InstanceView>.Found found) return;
        var value = found.Value;
        var row = new HBoxContainer(); _vehicleRows.AddChild(row);
        Text(row, "Vehicle", $"车辆 {id.Value} · 结构耐久 {value.Structure.EffectiveCurrent}/{value.Structure.Maximum} · {action switch { VehicleTradeAction.Buy => "待售", _ => "玩家所有" }}");
        row.AddChild(UiStyle.Button(action == VehicleTradeAction.Buy ? "SelectBuyVehicle" : "SelectSellVehicle",
            action == VehicleTradeAction.Buy ? "买入…" : "卖出…", () =>
            {
                _vehicle = id; _vehicleAction = action; _vehicleQuote = null;
                _vehicleDescription.Text = "报价不锁定。请选择获取报价，再确认买卖。";
                ShowVehicleDetails(); UpdateVehicleActions();
            }));
    }

    private void ShowVehicleDetails()
    {
        _vehicleAvailable = false;
        if (_vehicle is not { } id) return;
        if (_view.FindInstance(new InstanceId.Vehicle(id)) is not Lookup<InstanceView>.Found found)
        { _vehicleDetails.Text = "已不存在：所选车辆"; return; }
        var value = found.Value;
        _vehicleAvailable = _vehicleAction == VehicleTradeAction.Buy ? _settlement!.VehiclesForSale.Contains(id)
            : _settlement!.ParkedVehicles.Contains(id) && value.Ownership == SettlementRelation.PlayerOwned;
        var location = value.Location switch
        { WorldLocation.Node node => WorldPanels.NodeName(node.Id, _view, _content),
            WorldLocation.Edge edge => $"线路 {edge.Id.Value} · {edge.DistanceMeters:0.##} m", _ => "未知地点" };
        var ownership = value.Ownership switch
        { SettlementRelation.PlayerOwned => "玩家所有", SettlementRelation.NonHostile => "非敌对", _ => "敌对" };
        var locks = new List<string>();
        if (value.Lock.HasFlag(InstanceLock.UnderRefit)) locks.Add("改装中");
        if (value.Lock.HasFlag(InstanceLock.UnderRepair)) locks.Add("修理中");
        if (value.Lock.HasFlag(InstanceLock.UnderDeconstruction)) locks.Add("拆解中");
        if (value.Lock.HasFlag(InstanceLock.Combat)) locks.Add("战斗中");
        _vehicleDetails.Text = $"车辆 {id.Value} · {(_vehicleAction == VehicleTradeAction.Buy ? "买入" : "卖出")}\n所在地：{location}\n归属：{ownership} · {(locks.Count == 0 ? "未锁定" : string.Join("、", locks))}\n结构耐久：{value.Structure.EffectiveCurrent}/{value.Structure.Maximum}" +
            string.Concat(value.Parts.Select(part => $"\n部件 {part.Part.Value}：{part.Durability.EffectiveCurrent}/{part.Durability.Maximum}" +
                (part.Destroyed ? "（已毁）" : part.DefinitionMissing ? "（定义缺失）" : ""))) +
            (value.MissingContent.IsEmpty ? "" : "\n" + Problems(value.MissingContent)) +
            (_vehicleAvailable ? "" : "\n所选车辆已不在这份买卖清单中，请重新选择。已购车辆可在本站编组。");
    }

    private void QuoteVehicle()
    {
        if (!CanAct || !_vehicleAvailable) return;
        RequestVehicleQuote();
        if (_queries.MissingEntry is { } entry) Unavailable(_settlementBody, _state, entry);
        else UpdateVehicleActions();
    }

    private void RequestVehicleQuote()
    {
        _vehicleQuote = null;
        if (_vehicle is not { } id || !_vehicleAvailable) return;
        var request = new VehicleTradeQuoteRequest(_settlementId, id, _vehicleAction);
        if (!_queries.TryRead(nameof(ICoreReadView.QuoteVehicle), () => _view.QuoteVehicle(request), out var result)) return;
        _vehicleQuote = QuoteValue(result, _vehicleDescription);
        if (_vehicleQuote is not { } quote) return;
        _vehicleDescription.Text = $"报价不锁定\n整车总额：{Amount(quote.Total)}" + (quote.Blockers.IsEmpty ? "" : "\n受阻：\n" + Problems(quote.Blockers));
        _vehicleDescription.AddThemeColorOverride("font_color", quote.Blockers.IsEmpty ? Colors.White : UiStyle.Error);
    }

    private void ConfirmVehicle()
    {
        if (_vehicleConfirm.Disabled || _vehicleQuote is not { Total: { } total } quote) return;
        CoreCommand command = quote.Request.Action == VehicleTradeAction.Buy
            ? new CoreCommand.BuyVehicle(quote.Request.Settlement, quote.Request.Vehicle, total)
            : new CoreCommand.SellVehicle(quote.Request.Settlement, quote.Request.Vehicle, total);
        Submit(_settlementTarget!, command);
    }

    private void UpdateVehicleActions()
    {
        _vehicleQuoteButton.Disabled = !CanAct || !_vehicleAvailable;
        _vehicleConfirm.Disabled = !CanAct || !_vehicleAvailable || _vehicleQuote is not { Total: not null, Blockers.IsEmpty: true };
        _formPurchased.Disabled = !CanAct;
    }
}
