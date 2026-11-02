using Godot;
using Train.Core.Contracts;

namespace Train.Ui;

internal sealed partial class TrainInspector
{
    private VBoxContainer _vehicleList = null!;
    private Label _statistics = null!, _repairReason = null!, _formationReason = null!, _disbandReason = null!;
    private Button _repair = null!, _reform = null!, _disbandButton = null!;
    private VehicleRepairQuote? _repairQuote;
    private readonly Dictionary<VehicleId, (VBoxContainer Row, Label Durability, Button Refit, Button Service)> _vehicles = [];

    private void BuildVehicles()
    {
        _vehicleList = new VBoxContainer { Name = "Vehicles" }; _vehiclesPage.AddChild(_vehicleList);
        _statistics = Text(_vehiclesPage, "VehicleStats", "");
        _repair = UiStyle.Button("RepairAll", "全部修理", RepairAll); _vehiclesPage.AddChild(_repair);
        _repairReason = Text(_vehiclesPage, "RepairReason", "");
        _reform = UiStyle.Button("Reform", "调整编组…", () => { if (ReadCurrent() && FormationReason() is null) _formation(_train); });
        _vehiclesPage.AddChild(_reform); _formationReason = Text(_vehiclesPage, "FormationReason", "");
        _vehiclesPage.AddChild(new HSeparator());
        _disbandButton = UiStyle.Button("Disband", "解编…", () => { if (ReadCurrent() && DisbandReason(_train, _view) is null) _disband(); });
        _vehiclesPage.AddChild(_disbandButton); _disbandReason = Text(_vehiclesPage, "DisbandReason", "");
    }

    private void RefreshVehicles()
    {
        foreach (var id in _vehicles.Keys.Except(_train.Vehicles).ToArray())
        { EconomyPanelControls.RemoveControl(_vehicles[id].Row); _vehicles.Remove(id); }
        for (var index = 0; index < _train.Vehicles.Length; index++)
        {
            var id = _train.Vehicles[index];
            if (!_vehicles.TryGetValue(id, out var row))
            {
                var box = new VBoxContainer(); _vehicleList.AddChild(box);
                var durability = Text(box, "VehicleDurability", "");
                var actions = new HFlowContainer(); box.AddChild(actions);
                var refit = UiStyle.Button("RefitVehicle", "改装…", () => { if (ReadCurrent() && _train.Vehicles.Contains(id)) _refit(new InstanceId.Vehicle(id)); });
                var service = UiStyle.Button("VehicleServices", "车辆服务…", () => { if (ReadCurrent() && _train.Vehicles.Contains(id)) _service(new InstanceId.Vehicle(id)); });
                actions.AddChild(refit); actions.AddChild(service);
                _vehicles.Add(id, row = (box, durability, refit, service));
            }
            _vehicleList.MoveChild(row.Row, index);
            row.Durability.Text = VehicleName(id);
            if (_view.FindInstance(new InstanceId.Vehicle(id)) is Lookup<InstanceView>.Found found)
            {
                var instance = found.Value;
                row.Durability.Text += $"\n结构耐久：{instance.Structure.EffectiveCurrent}/{instance.Structure.Maximum}";
                if (!instance.Parts.IsEmpty) row.Durability.Text += "\n部件耐久：" + string.Join("、",
                    instance.Parts.Select(part => $"{part.Durability.EffectiveCurrent}/{part.Durability.Maximum}"));
            }
            else row.Durability.Text += "\n车辆已不存在。";
        }
        _statistics.Text = $"总长：{_train.LengthMeters:0.##} 米\n总质量：{_train.MassTons:0.##} 吨\n牵引力：{_train.TractionKilonewtons:0.##} 千牛\n载量：{(_inventory is { } inventory ? inventory.Capacity.ToString() : "—")}";
    }

    private void UpdateVehicleActions()
    {
        var busy = _host.IsBusy;
        foreach (var row in _vehicles.Values)
        { row.Refit.Disabled = row.Service.Disabled = busy; row.Service.Visible = _train.Position is TrainPosition.Node; }
        _repair.Visible = _repairReason.Visible = LocalSettlement() is { Relation: SettlementRelation.NonHostile };
        _repair.Text = _repairQuote?.Total is { } total ? $"全部修理 · {total.Value}" : "全部修理";
        _repairReason.Text = busy ? "当前操作尚未结束。" : _train.Activity != TrainActivity.Stopped ? "先停车才能修理" : _repairQuote?.Reason ?? "";
        _repair.Disabled = busy || _repairQuote is not { Reason: null, Total: not null, Jobs.IsEmpty: false };
        _formationReason.Text = busy ? "当前操作尚未结束。" : FormationReason() ?? "";
        _formationReason.Visible = _formationReason.Text.Length > 0;
        _reform.Disabled = busy || FormationReason() is not null;
        _disbandReason.Text = busy ? "当前操作尚未结束。" : DisbandReason(_train, _view) ?? "";
        _disbandReason.Visible = _disbandReason.Text.Length > 0;
        _disbandButton.Disabled = busy || DisbandReason(_train, _view) is not null;
    }

    private string VehicleName(VehicleId id) => EconomyPanels.TrainVehicleName(_view, id) ?? $"第 {_train.Vehicles.IndexOf(id) + 1} 辆车";
    private SettlementView? LocalSettlement() => _train.Position is TrainPosition.Node node &&
        _view.World.Nodes.First(item => item.Id == node.Id).Occupant is NodeOccupant.Settlement occupant &&
        _view.FindSettlement(occupant.Id) is Lookup<SettlementView>.Found found ? found.Value : null;
    private string? FormationReason() => _train.Activity != TrainActivity.Stopped ? "先停车才能调整编组" :
        LocalSettlement() is not { Relation: not SettlementRelation.Hostile } ? "请先停在自有或非敌对聚居地。" : null;

    internal static string? DisbandReason(TrainView train, ICoreReadView view)
    {
        if (train.Activity != TrainActivity.Stopped) return "先停车才能解编";
        if (train.Position is not TrainPosition.Node node || train.SpeedMetersPerSecond != 0 ||
            view.World.Nodes.First(item => item.Id == node.Id).Occupant is not NodeOccupant.Settlement { Relation: not SettlementRelation.Hostile })
            return "请先停在自有或非敌对聚居地。";
        if (view.FindInventory(train.Inventory) is not Lookup<InventoryView>.Found inventory) return "列车库存已不存在。";
        return !inventory.Value.Stacks.IsEmpty || !inventory.Value.Reservations.IsEmpty
            ? "车上还有货物，先卖出或卸下才能解编" : null;
    }

    internal void Disband()
    {
        if (ReadCurrent() && DisbandReason(_train, _view) is null) Submit(new CoreCommand.DisbandTrain(_train.Id));
    }

    private void RepairAll()
    {
        if (!ReadCurrent() || _repair.Disabled || _repairQuote is not { } quote) return;
        var commissioned = new List<string>();
        _result.Text = "";
        foreach (var job in quote.Jobs)
        {
            var id = ((InstanceId.Vehicle)((JobRequest.Repair)job.Request).Instance).Id;
            var name = VehicleName(id);
            var decision = _submit(Target, new CoreCommand.CommissionVehicleWork(job.Request, job.TotalPayment!.Value));
            if (FailureReason(decision) is { } reason)
            { _result.Text = $"{name}未委托：{reason}"; break; }
            commissioned.Add(name);
        }
        if (commissioned.Count > 0) _result.Text = $"已委托修理：{string.Join("、", commissioned)}。\n" + _result.Text;
        ReadCurrent();
    }
}
