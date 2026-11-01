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
    private TrainInventoryPanel _trainInventory = null!;
    private EconomyReportsPanel _reports = null!;

    internal const string ReservedStockHint = "聚居地库存在补货目标以内的部分留给本地，自动装载和买入只取超出部分。";
    internal event Action<PanelTarget, NodeId, ICoreReadView, IContentView>? FormationRequested;
    internal event Action<InstanceId>? ServiceDesignRequested;
    internal event Action<WorldObjectId, Control>? InspectorRequested;
    internal event Action<Control>? WorkspaceRequested;

    internal void Initialize(HostCoordinator host,
        Func<PanelTarget?, CoreCommand, CommandDecision.Accepted?> submit, GoodsIconCache icons,
        Func<PanelTarget?, CoreCommand, IContentCandidate?, CommandDecision.Accepted?> submitTarget)
    {
        var goods = new GoodsRows(icons);
        _settlementWindow = new(this, host, submit, submitTarget, goods, Refresh, () => _trainInventory.InvalidateRevision());
        _trainInventory = new(this, host, goods, Refresh);
        _reports = new(this, host);
        _settlementWindow.FormationRequested += (target, node, view, content) => FormationRequested?.Invoke(target, node, view, content);
        _settlementWindow.ServiceDesignRequested += instance => ServiceDesignRequested?.Invoke(instance);
        _settlementWindow.Opened += (target, panel) => InspectorRequested?.Invoke(target, panel);
        _trainInventory.Opened += (target, panel) => InspectorRequested?.Invoke(target, panel);
        _reports.Opened += panel => WorkspaceRequested?.Invoke(panel);
    }

    public override void _Ready()
    { SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect); MouseFilter = MouseFilterEnum.Ignore; }

    internal void OpenSettlement(PanelTarget source, SettlementId id) => _settlementWindow.Open(source, id);
    internal void OpenTrainInventory(PanelTarget target, TrainId id) => _trainInventory.Open(target, id);
    internal void OpenReports() => _reports.Open();

    internal void Close()
    { _settlementWindow.Close(); _trainInventory.Close(); _reports.Close(); }

    internal void Refresh(HostBinding binding, ICoreReadView view, IContentView content)
    {
        _reports.Refresh(binding, view, content);
        _settlementWindow.Refresh(binding, view, content);
        _trainInventory.Refresh(binding, view, content, _settlementWindow.Settlement);
    }

    internal void Observe(HostBinding binding, ICoreReadView view, ImmutableArray<EventEnvelope> events)
        => _settlementWindow.Observe(binding, view, events);
    internal void OpenVehicleServices(InstanceId instance) => _settlementWindow.OpenVehicleServices(instance);
    public override void _ExitTree() => _settlementWindow.ReleaseResources();

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
