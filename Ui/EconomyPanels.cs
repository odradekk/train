using System.Collections.Immutable;
using Godot;
using Train.App;
using Train.Content.Contracts;
using Train.Core.Contracts;
using static Train.Ui.EconomyPanelControls;

namespace Train.Ui;

internal partial class EconomyPanels : Control
{
    private SettlementPanel _settlementWindow = null!;
    private EconomyReportsWorkspace _reports = null!;
    private ConstructionWorkspace _construction = null!;

    internal const string ReservedStockHint = "聚居地库存在补货目标以内的部分留给本地，自动装载和买入只取超出部分。";
    internal event Action<PanelTarget, NodeId, ImmutableArray<VehicleId>, ICoreReadView, IContentView>? FormationRequested;
    internal event Action<InstanceId>? ServiceDesignRequested;
    internal event Action<SettlementId>? SettlementCombatRequested;
    internal event Action<WorldObjectId, Control>? InspectorRequested;
    internal event Action<Control>? WorkspaceRequested;
    internal event Action<PanelTarget, TrainView, ICoreReadView, IContentView>? PlanRequested;

    internal void Initialize(HostCoordinator host, GoodsIconCache icons,
        Func<PanelTarget?, CoreCommand, IContentCandidate?, SubmissionResult> submitTarget,
        Func<PanelTarget?, CoreCommand, SubmissionResult> submitInline)
    {
        var goods = new GoodsRows(icons);
        _construction = new(); _construction.Initialize(host, submitTarget); AddChild(_construction);
        _construction.Opened += () => WorkspaceRequested?.Invoke(_construction);
        _construction.EditRequested += instance => ServiceDesignRequested?.Invoke(instance);
        _settlementWindow = new(this, host, _construction, goods, Refresh, submitInline);
        _reports = new(this, host);
        _settlementWindow.FormationRequested += (target, node, vehicles, view, content) => FormationRequested?.Invoke(target, node, vehicles, view, content);
        _settlementWindow.SettlementCombatRequested += settlement => SettlementCombatRequested?.Invoke(settlement);
        _settlementWindow.Opened += (target, panel) => InspectorRequested?.Invoke(target, panel);
        _reports.Opened += panel => WorkspaceRequested?.Invoke(panel);
        _reports.PlanRequested += (target, train, view, content) => PlanRequested?.Invoke(target, train, view, content);
    }

    public override void _Ready()
    { SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect); MouseFilter = MouseFilterEnum.Ignore; }

    internal void OpenSettlement(PanelTarget source, SettlementId id) => _settlementWindow.Open(source, id);
    internal void OpenReports() => _reports.Open();
    internal void OpenReports(PanelTarget source) => _reports.Open(source);

    internal void Close()
    { _settlementWindow.Close(); _reports.Close(); _construction.Close(); }

    internal void Refresh(HostBinding binding, ICoreReadView view, IContentView content)
    {
        _reports.Refresh(binding, view, content);
        _settlementWindow.Refresh(binding, view, content);
        _construction.Refresh(binding, view, content);
    }

    internal void OpenVehicleServices(InstanceId instance) => _construction.OpenInstance(instance);

    internal static void SyncChoices<T>(OptionButton choice, List<T?> options, IEnumerable<T> values, T? selected,
        Func<T, string> title, string? placeholder = null) where T : struct
    {
        var ids = values.Distinct().ToArray();
        var next = ids.Select(id => (T?)id).ToList();
        if (placeholder is not null) next.Insert(0, null);
        if (selected is not null && !next.Contains(selected)) next.Add(selected);
        for (var index = options.Count - 1; index >= 0; index--)
            if (!next.Contains(options[index])) { choice.RemoveItem(index); options.RemoveAt(index); }
        foreach (var id in next)
            if (!options.Contains(id)) { options.Add(id); choice.AddItem(""); }
        for (var index = 0; index < options.Count; index++)
        {
            var text = options[index] is not { } id ? placeholder! : ids.Contains(id) ? title(id) : "已不存在：所选项";
            if (choice.GetItemText(index) != text) choice.SetItemText(index, text);
        }
        var selectedIndex = options.IndexOf(selected);
        if (choice.Selected != selectedIndex) choice.Select(selectedIndex);
    }

    internal static string SizeName(SizeClass size) => size switch
    { SizeClass.Small => "小型", SizeClass.Medium => "中型", SizeClass.Large => "大型", SizeClass.ExtraLarge => "超大型", SizeClass.Titan => "泰坦", _ => size.ToString() };

}
