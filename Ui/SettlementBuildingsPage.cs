using Godot;
using Train.App;
using Train.Content.Contracts;
using Train.Core.Contracts;
using static Train.Ui.EconomyPanelControls;

namespace Train.Ui;

// Decision note: 2026-10-31-object-centric-management-screens
internal sealed partial class SettlementBuildingsPage(Func<bool> canAct, Func<CoreCommand, SubmissionResult> submit)
{
    private ICoreReadView _view = null!;
    private IContentView _content = null!;
    private SettlementView _settlement = null!;
    private VBoxContainer _body = null!;
    private Label _empty = null!, _plots = null!;
    private BuildingId? _expanded;
    private readonly Dictionary<BuildingId, BuildingRow> _buildings = [];
    private readonly Dictionary<FacilityKey, PlanRow> _plans = [];
    private sealed record BuildingRow(VBoxContainer Root, Button Select, VBoxContainer Details,
        Label Structure, VBoxContainer Facilities, Button Demolish, Label Result);

    internal void Build(VBoxContainer body)
    {
        _body = body;
        _empty = Text(body, "Empty", "没有建筑。");
        _plots = Text(body, "OtherPlots", "");
        BuildDemolition(body);
    }

    internal void Refresh(ICoreReadView view, IContentView content, SettlementView settlement)
    {
        _view = view; _content = content; _settlement = settlement;
        var occupied = settlement.Plots.Where(plot => plot.Occupant is PlotOccupant.Building).ToArray();
        var ids = occupied.Select(plot => ((PlotOccupant.Building)plot.Occupant).Id).ToArray();
        foreach (var id in _buildings.Keys.Except(ids).ToArray())
        { RemoveControl(_buildings[id].Root); _buildings.Remove(id); }
        foreach (var key in _plans.Keys.Where(key => !settlement.Facilities.Any(item => item.Key == key) || !ids.Contains(key.Building)).ToArray())
        { RemoveControl(_plans[key].Root); _plans.Remove(key); }
        _empty.Visible = occupied.Length == 0;
        foreach (var plot in occupied)
        {
            var id = ((PlotOccupant.Building)plot.Occupant).Id;
            if (!_buildings.TryGetValue(id, out var row))
            {
                var root = new VBoxContainer { Name = $"Building_{id.Value}" }; _body.AddChild(root);
                var select = UiStyle.Button($"SelectBuilding_{id.Value}", "", () =>
                {
                    _expanded = _expanded == id ? null : id;
                    Refresh(_view, _content, _settlement);
                });
                select.AutowrapMode = TextServer.AutowrapMode.WordSmart; root.AddChild(select);
                var details = new VBoxContainer { Name = "Details", Visible = false }; root.AddChild(details);
                var structure = Text(details, "Structure", "");
                var facilities = new VBoxContainer { Name = "Facilities" }; details.AddChild(facilities);
                var demolish = UiStyle.Button("Demolish", "拆除…", () => OpenDemolition(id)); details.AddChild(demolish);
                row = new(root, select, details, structure, facilities, demolish, Text(details, "DismantleResult", ""));
                _buildings.Add(id, row);
            }
            var facilitiesHere = settlement.Facilities.Where(facility => facility.Key.Building == id).ToArray();
            row.Select.Text = BuildingName(view, content, id) + $" · {EconomyPanels.PlotSizeName(plot.Size)}地块" +
                (facilitiesHere.Length == 0 ? "" : "\n" + string.Join("；", facilitiesHere.Select(Status).Distinct()));
            row.Details.Visible = _expanded == id;
            row.Structure.Text = view.FindInstance(new InstanceId.Building(id)) is Lookup<InstanceView>.Found instance
                ? $"结构耐久 {instance.Value.Structure.EffectiveCurrent}/{instance.Value.Structure.Maximum}" : "建筑已不存在，请重新打开聚居地。";
            foreach (var facility in facilitiesHere)
            {
                if (!_plans.TryGetValue(facility.Key, out var plan)) _plans.Add(facility.Key, plan = BuildPlan(row.Facilities, facility.Key));
                RefreshPlan(plan, facility);
            }
        }
        var emptyPlots = settlement.Plots.Where(plot => plot.Occupant is PlotOccupant.Empty).ToArray();
        var otherPlots = ConstructionSites(view, content, settlement).ToList();
        if (emptyPlots.Length > 0) otherPlots.Add("空地块：" + string.Join("、", emptyPlots.GroupBy(plot => plot.Size)
            .Select(group => $"{EconomyPanels.PlotSizeName(group.Key)} {group.Count()} 块")));
        _plots.Text = string.Join("\n", otherPlots);
        _plots.Visible = _plots.Text.Length > 0;
        _body.MoveChild(_plots, _body.GetChildCount() - 1);
        UpdateActions();
        if (_dismantle.Visible) RefreshDemolition();
    }

    internal void OpenBuilding(BuildingId id)
    {
        _expanded = id; Refresh(_view, _content, _settlement);
        if (!_buildings.TryGetValue(id, out var row)) return;
        Callable.From(() =>
        {
            if (!GodotObject.IsInstanceValid(row.Select)) return;
            for (var parent = row.Select.GetParent(); parent is not null; parent = parent.GetParent())
                if (parent is ScrollContainer scroll) scroll.EnsureControlVisible(row.Select);
            row.Select.GrabFocus();
        }).CallDeferred();
    }

    internal void UpdateActions()
    {
        var available = canAct();
        foreach (var row in _buildings.Values) row.Select.Disabled = row.Demolish.Disabled = !available;
        foreach (var (key, row) in _plans)
        {
            var facility = _settlement.Facilities.FirstOrDefault(item => item.Key == key);
            row.Process.Disabled = !available;
            row.Enabled.Disabled = !available || (row.Pending?.Process ?? facility?.Process) is null;
            foreach (var button in row.Priority) button.Disabled = !available || (row.Pending?.Process ?? facility?.Process) is null;
        }
        if (_dismantle.Visible) _dismantle.GetOkButton().Disabled = !available || _demolitionQuote is not { Blockers.IsEmpty: true };
    }

    internal static string BuildingName(ICoreReadView view, IContentView content, BuildingId id) =>
        view.FindInstance(new InstanceId.Building(id)) is Lookup<InstanceView>.Found found &&
        content.FindDesign(found.Value.Design) is ContentLookup<DesignEvaluation>.Found { Value: DesignEvaluation.Complete complete }
            ? complete.Definition.Display.Name : "建筑内容缺失";

    internal static IEnumerable<string> ConstructionSites(ICoreReadView view, IContentView content, SettlementView settlement)
    {
        foreach (var plot in settlement.Plots)
        {
            if (plot.Occupant is not PlotOccupant.Construction site ||
                view.FindJob(site.Id) is not Lookup<JobView>.Found { Value: JobView.Construction construction } ||
                construction.Value.Target is not ConstructionTarget.NewBuilding target) continue;
            var job = construction.Value;
            var name = content.FindDesign(target.Design) is ContentLookup<DesignEvaluation>.Found { Value: DesignEvaluation.Complete complete }
                ? complete.Definition.Display.Name : "建筑内容缺失";
            yield return $"正在建造{name} · {EconomyPanels.PlotSizeName(plot.Size)}地块 · 进度 {job.Progress:P0} · {EconomyPanels.ServiceRemainingTime(job)}" +
                (job.State == ConstructionState.Working ? "" : $" · {EconomyPanels.ServiceState(job.State)}");
        }
    }

    internal static bool NeedsAttention(FacilityView facility) => facility.Enabled &&
        facility.State is not (ProductionState.Running or ProductionState.Disabled);

    internal static string Status(FacilityView facility) => facility.State switch
    {
        ProductionState.Disabled => facility.Process is null ? "未选择流程" : "已停用",
        ProductionState.Running => "生产中",
        ProductionState.WaitingForInputs => "等待原料",
        ProductionState.OutputBlocked => "产出无法入库",
        ProductionState.ZeroCapability => "没有可用的生产能力",
        ProductionState.ConditionsNotMet => "流程条件不满足",
        ProductionState.Locked => "建筑正在改装、修理或拆除",
        ProductionState.ProcessMissing => "流程内容缺失",
        _ => "生产状态不可用"
    };
}
