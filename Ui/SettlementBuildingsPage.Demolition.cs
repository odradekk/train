using Godot;
using Train.Content.Contracts;
using Train.Core.Contracts;

namespace Train.Ui;

internal sealed partial class SettlementBuildingsPage
{
    private ConfirmationDialog _dismantle = null!;
    private BuildingId? _demolition;
    private JobQuote? _demolitionQuote;

    private void BuildDemolition(VBoxContainer body)
    {
        _dismantle = new ConfirmationDialog { Name = "DismantleConfirmation", Title = "确认拆除建筑",
            OkButtonText = "拆除", CancelButtonText = "取消", Exclusive = true };
        body.AddChild(_dismantle);
        _dismantle.Confirmed += () =>
        {
            if (!canAct() || _demolition is not { } id) return;
            RefreshDemolition();
            if (_demolitionQuote is not { Blockers.IsEmpty: true } quote || !_buildings.TryGetValue(id, out var row)) return;
            var result = submit(new CoreCommand.StartOwnedJob(quote.Request));
            row.Result.Text = Result(result, "拆除已开始，进度可在工地中查看。");
            _demolition = null; _demolitionQuote = null;
        };
        _dismantle.Canceled += () => { _demolition = null; _demolitionQuote = null; };
    }

    private void OpenDemolition(BuildingId id)
    {
        if (!canAct()) return;
        _demolition = id;
        RefreshDemolition();
        _dismantle.PopupCentered(new(480, 320));
    }

    private void RefreshDemolition()
    {
        _demolitionQuote = null;
        if (_demolition is not { } id) return;
        if (!_buildings.ContainsKey(id)) { _dismantle.Hide(); _demolition = null; return; }
        var reason = "这里没有能执行拆除的施工建筑。";
        foreach (var building in _settlement.Plots.Select(plot => plot.Occupant).OfType<PlotOccupant.Building>().Where(building => building.Id != id))
        {
            if (_view.FindInstance(new InstanceId.Building(building.Id)) is not Lookup<InstanceView>.Found instance ||
                _content.FindDesign(instance.Value.Design) is not ContentLookup<DesignEvaluation>.Found
                    { Value: DesignEvaluation.Complete { Core: { } input } } ||
                !input.Design.Parts.Any(part => part.Capabilities.Any(capability => capability is PartCapability.Construction))) continue;
            var request = new JobRequest.Dismantle(new(building.Id, CapabilityKind.Construction), new InstanceId.Building(id), 0);
            switch (_view.GetJobQuote(request, JobFunding.OwnInventory))
            {
                case CoreResult<JobQuote>.Success success:
                    _demolitionQuote ??= success.Value;
                    if (success.Value.Blockers.IsEmpty) _demolitionQuote = success.Value;
                    break;
                case CoreResult<JobQuote>.Failure failure: reason = Describe(failure.Problems); break;
                case CoreResult<JobQuote>.Faulted: reason = "无法查询拆除条件，请查看错误报告。"; break;
            }
            if (_demolitionQuote is { Blockers.IsEmpty: true }) break;
        }
        var description = _demolitionQuote is { } quote
            ? $"\n由{BuildingName(_view, _content, quote.Request switch { JobRequest.Dismantle dismantle => dismantle.Facility.Building, _ => id })}施工。" +
                $"\n总工作量 {quote.TotalWork}。" + (quote.Blockers.IsEmpty ? "" : "\n" + Describe(quote.Blockers))
            : "\n" + reason;
        _dismantle.Title = $"拆除{BuildingName(_view, _content, id)}？";
        _dismantle.DialogText = "拆除不能撤销。建筑将被拆成材料，回收材料进入聚居地库存。\n开工后可在工地中暂停或恢复。" +
            $"\n目标：{BuildingName(_view, _content, id)}{description}";
        _dismantle.GetOkButton().Disabled = !canAct() || _demolitionQuote is not { Blockers.IsEmpty: true };
    }
}
