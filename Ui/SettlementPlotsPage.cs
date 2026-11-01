using Godot;
using Train.Content.Contracts;
using Train.Core.Contracts;
using static Train.Ui.EconomyPanelControls;
using static Train.Ui.EconomyPanels;

namespace Train.Ui;

internal sealed class SettlementPlotsPage
{
    private ICoreReadView _view = null!;
    private IContentView _content = null!;
    private SettlementView _settlement = null!;

    internal void Bind(ICoreReadView view, IContentView content, SettlementView settlement)
    { _view = view; _content = content; _settlement = settlement; }

    private Label _plots = null!;

    internal void BuildPlots(VBoxContainer body) => _plots = Text(body, "Plots", "");

    internal void RefreshPlots()
    {
        if (_plots is null || !GodotObject.IsInstanceValid(_plots) || _settlement is null) return;
        if (_settlement.Plots.IsEmpty) { _plots.Text = "没有地块。"; return; }
        var text = "";
        foreach (var plot in _settlement.Plots)
            text += $"地块 {plot.Id.Value} · 模板编号 {plot.TemplatePlotNumber} · {PlotSizeName(plot.Size)}\n{OccupantText(plot.Occupant)}\n";
        _plots.Text = text.TrimEnd();
    }

    private string OccupantText(PlotOccupant occupant) => occupant switch
    {
        PlotOccupant.Empty => "占用：空地",
        PlotOccupant.Building building => BuildingOccupant(building.Id),
        PlotOccupant.Construction site => ConstructionOccupant(site.Id),
        _ => "占用：未知"
    };

    private string BuildingOccupant(BuildingId id)
    {
        if (_view.FindInstance(new InstanceId.Building(id)) is not Lookup<InstanceView>.Found found)
            return $"占用：建筑 {id.Value} · 实例缺失";
        var name = _content.FindDesign(found.Value.Design) is ContentLookup<DesignEvaluation>.Found { Value: DesignEvaluation.Complete complete }
            ? complete.Definition.Display.Name : "设计缺失";
        return $"占用：{name} · 建筑 {id.Value} · {LockText(found.Value.Lock)}";
    }

    private string ConstructionOccupant(JobId id)
    {
        if (_view.FindJob(id) is not Lookup<JobView>.Found { Value: JobView.Construction job })
            return $"占用：工地 · 任务 {id.Value} · 任务已不在当前查询中";
        var value = job.Value;
        return $"占用：工地 · 任务 {value.Id.Value} · {ServiceKind(value.Kind)} · {ServiceState(value.State)}\n工作 {value.ProgressWork:0.###}/{value.FrozenTotalWork} · {value.Progress:P1}";
    }

    private static string LockText(InstanceLock value)
    {
        if (value == InstanceLock.None) return "未锁定";
        var names = new List<string>();
        if (value.HasFlag(InstanceLock.UnderRefit)) names.Add("改装中");
        if (value.HasFlag(InstanceLock.UnderRepair)) names.Add("修理中");
        if (value.HasFlag(InstanceLock.UnderDeconstruction)) names.Add("拆解中");
        if (value.HasFlag(InstanceLock.Combat)) names.Add("战斗中");
        return names.Count == 0 ? value.ToString() : string.Join("、", names);
    }
}
