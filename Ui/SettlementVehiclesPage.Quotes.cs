using System.Collections.Immutable;
using Train.App;
using Train.Content.Contracts;
using Train.Core.Contracts;
using static Train.Ui.EconomyPanels;

namespace Train.Ui;

internal sealed partial class SettlementVehiclesPage
{
    private sealed record QuotedVehicle(VehicleId Vehicle, string Name, CoreCommand Command, long Amount);
    private ImmutableArray<QuotedVehicle> _buyBatch = [], _sellBatch = [], _repairBatch = [], _dismantleBatch = [];

    private void ReadQuotes()
    {
        _buyBatch = _owned ? [] : TradeQuotes(_buySelection, VehicleTradeAction.Buy, _purchaseTotal);
        _sellBatch = _owned ? [] : TradeQuotes(_parkedSelection, VehicleTradeAction.Sell, _saleReason);
        _dismantleBatch = _owned ? WorkQuotes(true, _dismantleReason) : [];
        _repairBatch = WorkQuotes(false, _repairReason);
        _buy.Text = _buyBatch.IsEmpty ? "购买所选车辆" : $"购买所选车辆 · {_buyBatch.Sum(item => item.Amount)}";
        _sell.Text = _sellBatch.IsEmpty ? "出售" : $"出售 · {_sellBatch.Sum(item => item.Amount)}";
        _repair.Text = _owned ? "修理 · 用库存材料" : _repairBatch.IsEmpty ? "修理" : $"修理 · {_repairBatch.Sum(item => item.Amount)}";
    }

    private ImmutableArray<QuotedVehicle> TradeQuotes(List<VehicleId> selection, VehicleTradeAction action, Godot.Label description)
    {
        if (selection.Count == 0) { description.Text = action == VehicleTradeAction.Buy ? "请勾选要购买的车辆。" : ""; return []; }
        var quoted = ImmutableArray.CreateBuilder<QuotedVehicle>();
        var reasons = new List<string>();
        foreach (var id in selection)
        {
            switch (_view.QuoteVehicle(new(_settlement.Id, id, action)))
            {
                case CoreResult<VehicleTradeQuote>.Success { Value: var quote }:
                    if (!quote.Blockers.IsEmpty) reasons.Add($"{VehicleName(id)}：{Describe(quote.Blockers)}");
                    if (quote.Total is { } total)
                        quoted.Add(new(id, VehicleName(id), action == VehicleTradeAction.Buy
                            ? new CoreCommand.BuyVehicle(_settlement.Id, id, total)
                            : new CoreCommand.SellVehicle(_settlement.Id, id, total), total.Value));
                    else if (quote.Blockers.IsEmpty) reasons.Add("暂时无法获取车辆金额。");
                    break;
                case CoreResult<VehicleTradeQuote>.Failure failure: reasons.Add(Describe(failure.Problems)); break;
                case CoreResult<VehicleTradeQuote>.Faulted fault: reasons.Add(CommandDiagnostics.Describe(fault.Fault)); break;
            }
        }
        var amount = checked(quoted.Sum(item => item.Amount));
        if (action == VehicleTradeAction.Buy && amount > _view.Economy.Wallet.Value) reasons.Add("资金不足，不能购买全部所选车辆。");
        description.Text = action == VehicleTradeAction.Buy
            ? $"已选 {selection.Count} 辆 · 合计 {amount}\n交易后资金：{_view.Economy.Wallet.Value - amount}" : "";
        if (reasons.Count > 0) description.Text += (description.Text.Length == 0 ? "" : "\n") + string.Join("\n", reasons);
        return reasons.Count == 0 && quoted.Count == selection.Count ? quoted.ToImmutable() : [];
    }

    private ImmutableArray<QuotedVehicle> WorkQuotes(bool dismantle, Godot.Label description)
    {
        description.Text = "";
        if (_parkedSelection.Count == 0) { if (!dismantle) description.Text = "请勾选停放车辆。"; return []; }
        var facilities = _settlement.Plots.Select(plot => plot.Occupant).OfType<PlotOccupant.Building>()
            .Where(building => _view.FindInstance(new InstanceId.Building(building.Id)) is Lookup<InstanceView>.Found instance &&
                _content.FindDesign(instance.Value.Design) is ContentLookup<DesignEvaluation>.Found
                    { Value: DesignEvaluation.Complete { Core: { } input } } &&
                input.Design.Parts.Any(part => part.Capabilities.Any(capability => capability is PartCapability.Construction)))
            .Select(building => new FacilityKey(building.Id, CapabilityKind.Construction)).ToArray();
        var used = new HashSet<FacilityKey>();
        if (_owned)
            foreach (var job in _settlement.OwnedJobs.Select(_view.FindJob).OfType<Lookup<JobView>.Found>()
                .Select(item => item.Value).OfType<JobView.Construction>().Select(item => item.Value)
                .Where(job => job.State is not ConstructionState.Completed and not ConstructionState.Failed)) used.Add(job.Facility);
        var free = facilities.Count(facility => !used.Contains(facility));
        if (_owned && free < _parkedSelection.Count)
        { description.Text = $"只有 {free} 个空闲的施工设施"; return []; }
        var quoted = ImmutableArray.CreateBuilder<QuotedVehicle>();
        foreach (var id in _parkedSelection)
        {
            JobQuote? selected = null;
            var reason = "这里没有可用的施工设施。";
            foreach (var facility in facilities.Where(facility => !_owned || !used.Contains(facility)))
            {
                JobRequest request = dismantle ? new JobRequest.Dismantle(facility, new InstanceId.Vehicle(id), 0)
                    : new JobRequest.Repair(facility, new InstanceId.Vehicle(id), _owned ? [_settlement.Inventory] : [], 0);
                switch (_view.GetJobQuote(request, _owned ? JobFunding.OwnInventory : JobFunding.NpcCommission))
                {
                    case CoreResult<JobQuote>.Success { Value: var quote }:
                        reason = quote.Blockers.IsEmpty ? "暂时无法获取修理金额。" : Describe(quote.Blockers);
                        if (quote.Blockers.IsEmpty && (_owned || quote.TotalPayment is not null)) selected = quote;
                        break;
                    case CoreResult<JobQuote>.Failure failure: reason = Describe(failure.Problems); break;
                    case CoreResult<JobQuote>.Faulted fault: reason = CommandDiagnostics.Describe(fault.Fault); break;
                }
                if (selected is null) continue;
                if (_owned) used.Add(facility);
                break;
            }
            if (selected is null) { description.Text = $"{VehicleName(id)}：{reason}"; return []; }
            quoted.Add(new(id, VehicleName(id), _owned ? new CoreCommand.StartOwnedJob(selected.Request)
                : new CoreCommand.CommissionVehicleWork(selected.Request, selected.TotalPayment!.Value),
                _owned ? 0 : selected.TotalPayment!.Value.Value));
        }
        var total = checked(quoted.Sum(item => item.Amount));
        if (!_owned && total > _view.Economy.Wallet.Value) { description.Text = "资金不足，不能修理全部所选车辆。"; return []; }
        if (!dismantle) description.Text = _owned ? "材料只取本聚居地库存。" : "材料全部在当地购买，不使用列车和租用仓库中的货物。";
        return quoted.ToImmutable();
    }

    private string Describe(ImmutableArray<CoreProblem> problems) =>
        string.Join("\n", problems.Select(problem => CommandDiagnostics.Describe(problem with { Subject = null }, _content)));
}
