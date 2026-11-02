using Godot;
using Train.Core.Contracts;
using static Train.Ui.EconomyPanelControls;
using static Train.Ui.EconomyPanels;

namespace Train.Ui;

internal sealed partial class SettlementBuildingsPage
{
    private VBoxContainer _emptyPlots = null!;
    private ConstructionJobRows _jobs = null!;
    private readonly Dictionary<PlotId, Button> _plotButtons = [];
    private readonly Dictionary<BuildingId, JobQuote> _repairQuotes = [];

    private void BuildWork(VBoxContainer body)
    {
        _emptyPlots = new() { Name = "EmptyPlots" }; body.AddChild(_emptyPlots);
        _jobs = new(canAct, submit); _jobs.Build(body);
    }

    private void RefreshWork()
    {
        var plots = _settlement.Plots.Where(plot => plot.Occupant is PlotOccupant.Empty).ToArray();
        foreach (var id in _plotButtons.Keys.Except(plots.Select(plot => plot.Id)).ToArray())
        { RemoveControl(_plotButtons[id]); _plotButtons.Remove(id); }
        foreach (var plot in plots)
        {
            if (!_plotButtons.TryGetValue(plot.Id, out var button))
            {
                var id = plot.Id;
                button = UiStyle.Button($"BuildPlot_{id.Value}", "", () => { if (canAct()) buildRequested(id); });
                _emptyPlots.AddChild(button); _plotButtons.Add(id, button);
            }
            button.Text = $"{PlotSizeName(plot.Size)}空地块 · 建造…";
        }
        _repairQuotes.Clear();
        foreach (var (id, row) in _buildings)
        {
            if (!row.Details.Visible) continue;
            if (_view.FindInstance(new InstanceId.Building(id)) is not Lookup<InstanceView>.Found instance)
            { row.RepairReason.Text = "建筑已不存在。"; continue; }
            if (!VehicleRepairs.NeedsRepair(instance.Value))
            { row.RepairReason.Text = "没有损伤，不需要修理"; continue; }
            var available = FindConstructionQuote(_view, _content,
                _settlement.Plots.Select(plot => plot.Occupant).OfType<PlotOccupant.Building>()
                    .Where(building => IsConstructionFacility(_view, _content, building.Id)).Select(building => building.Id),
                JobFunding.OwnInventory,
                facility => new JobRequest.Repair(facility, new InstanceId.Building(id), [_settlement.Inventory], 0), out var reason);
            if (available is { Blockers.IsEmpty: true }) _repairQuotes.Add(id, available);
            row.RepairReason.Text = _repairQuotes.TryGetValue(id, out var quote)
                ? $"材料只取本聚居地库存。需要：{Goods(quote.RequiredGoods)}" : reason;
        }
        _jobs.Refresh(_view, _content, _settlement, false);
    }

    private void RepairBuilding(BuildingId id)
    {
        if (!canAct()) return;
        RefreshWork();
        if (!_repairQuotes.TryGetValue(id, out var quote) || !_buildings.TryGetValue(id, out var row)) return;
        row.Result.Text = Result(submit(new CoreCommand.StartOwnedJob(quote.Request)), "修理已开始，进度可在施工任务中查看。");
    }
}
