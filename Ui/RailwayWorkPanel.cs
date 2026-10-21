using System.Collections.Immutable;
using System.Globalization;
using Godot;
using Train.App;
using Train.Content.Contracts;
using Train.Core.Contracts;

namespace Train.Ui;

internal sealed partial class RailwayWorkPanel : Control
{
    private HostCoordinator _host = null!;
    private Func<PanelTarget?, CoreCommand, CommandDecision.Accepted?> _submit = null!;
    private PanelContainer _panel = null!;
    private Label _summary = null!;
    private ItemList _breaks = null!;
    private Label _works = null!;
    private OptionButton _sources = null!;
    private Button _expand = null!, _repair = null!, _enable = null!, _disable = null!;
    private readonly List<BreakId> _breakIds = [];
    private readonly List<SettlementId> _sourceIds = [];
    private PanelTarget? _target;
    private EdgeId _edge;
    private bool _open;
    private string? _breakSignature, _sourceSignature;

    internal void Initialize(HostCoordinator host, Func<PanelTarget?, CoreCommand, CommandDecision.Accepted?> submit)
    { _host = host; _submit = submit; }

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
        _panel = UiStyle.Panel(this, "RailwayWork", "铁路施工", new(640, 120), 480, Close, out var body);
        _summary = UiStyle.Label("");
        _summary.Name = "Summary";
        _summary.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        body.AddChild(_summary);
        _breaks = new ItemList { Name = "Breaks", AutoHeight = true };
        body.AddChild(_breaks);
        _works = UiStyle.Label("");
        _works.Name = "Works";
        _works.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        body.AddChild(_works);
        _sources = new OptionButton { Name = "MaterialSource" };
        body.AddChild(_sources);
        _expand = UiStyle.Button("ExpandRail", "加轨", Expand, true);
        _repair = UiStyle.Button("RepairRail", "修复所选断点", Repair, true);
        _enable = UiStyle.Button("EnableAutomaticRepair", "设为自动修复来源", EnableAutoRepair);
        _disable = UiStyle.Button("DisableAutomaticRepair", "关闭自动修复", DisableAutoRepair);
        body.AddChild(_expand);
        body.AddChild(_repair);
        body.AddChild(_enable);
        body.AddChild(_disable);
        var help = UiStyle.Label("提交时由核心扣除材料并判断是否允许。件数和是否允许在受理或拒绝后由核心给出。关闭本面板不会提交命令。");
        help.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        body.AddChild(help);
        _panel.Hide();
    }

    internal void Open(PanelTarget target, EdgeId edge)
    {
        _target = target;
        _edge = edge;
        _open = true;
        _breakSignature = _sourceSignature = null;
        _panel.Show();
        UiStyle.BringToFront(_panel);
        if (_host.GetStatus() is { } status && _host.Content is { } content)
            Refresh(status.Binding, status.View, content);
    }

    internal void Close()
    {
        _open = false;
        _target = null;
        if (_panel is not null) _panel.Hide();
    }

    internal void Refresh(HostBinding binding, ICoreReadView view, IContentView content)
    {
        if (!_open || _summary is null) return;
        var available = _target is { } target && target.IsAvailable(binding, view,
            _host.PanelGenerationCurrent(target.Binding, target.Generation));
        if (!available || view.FindEdge(_edge) is not Lookup<EdgeView>.Found found)
        {
            ShowMissing();
            return;
        }
        var edge = found.Value;
        var rail = RailGood(view, content);
        BindBreaks(edge);
        BindSources(view, content, rail);
        _summary.Text = Summary(view, content, edge, rail);
        _works.Text = Works(view, content, edge);
        var blocked = _host.IsBusy;
        var source = SelectedSource();
        _sources.Disabled = blocked;
        _expand.Disabled = blocked || source is null;
        _repair.Disabled = blocked || source is null || SelectedBreak() is null;
        _enable.Disabled = blocked || source is null;
        _disable.Disabled = blocked;
    }

    private void ShowMissing()
    {
        _summary.Text = "已不存在";
        _works.Text = "";
        _breaks.Clear();
        _sources.Clear();
        _breakIds.Clear();
        _sourceIds.Clear();
        _breakSignature = _sourceSignature = null;
        _expand.Disabled = _repair.Disabled = _enable.Disabled = _disable.Disabled = _sources.Disabled = true;
    }

    private string Summary(ICoreReadView view, IContentView content, EdgeView edge, GoodId? rail)
    {
        var map = edge.Map;
        var lines = new List<string>
        {
            $"线路 {map.Id.Value}",
            $"{WorldPanels.NodeName(map.Start, view, content)} — {WorldPanels.NodeName(map.End, view, content)}",
            $"长度 {map.LengthMeters.ToString("0.##", CultureInfo.InvariantCulture)} m",
            $"铁轨 {map.Tracks} / 上限 {map.TrackLimit}",
            "自动修复：" + (edge.AutoRepairMaterialSource is { } source ? SettlementText(view, content, source) : "关闭")
        };
        if (rail is null) lines.Add("铁路规则不可用");
        if (_sourceIds.Count == 0) lines.Add("没有自有聚居地");
        if (map.Breaks.IsDefaultOrEmpty) lines.Add("没有断点");
        return string.Join('\n', lines);
    }

    private string Works(ICoreReadView view, IContentView content, EdgeView edge)
    {
        if (edge.Map.Works.IsDefaultOrEmpty) return "没有施工";
        var blocks = new List<string>();
        foreach (var work in edge.Map.Works)
        {
            var kind = work.Kind == RailwayWorkKind.AddTrack ? "加轨" : "修复断点";
            var repairing = work.RepairingBreak is { } repair ? $" · 断点 {repair.Value}" : "";
            var lines = new List<string>
            {
                $"{kind} · 任务 {work.Id.Value}{repairing} · {work.Track} 号轨 · {SettlementText(view, content, work.MaterialSource)}",
                work.InvestedGoods.IsDefaultOrEmpty ? "已投入：尚未投入"
                    : "已投入：" + string.Join("、", work.InvestedGoods.Select(good => $"{WorldPanels.GoodName(good.Good, content)} × {good.Quantity}")),
                $"进度 {(work.Progress * 100).ToString("0.##", CultureInfo.InvariantCulture)}%"
            };
            if (!work.Blockers.IsDefaultOrEmpty)
                foreach (var blocker in work.Blockers) lines.Add("阻塞：" + CommandDiagnostics.Describe(blocker, content));
            blocks.Add(string.Join('\n', lines));
        }
        return string.Join("\n\n", blocks);
    }

    private void BindBreaks(EdgeView edge)
    {
        ImmutableArray<TrackBreakView> breaks = edge.Map.Breaks.IsDefault ? [] : edge.Map.Breaks;
        var signature = _edge.Value + ":" + string.Join(",", breaks.Select(item => item.Id.Value));
        if (signature == _breakSignature) return;
        var selected = SelectedBreak();
        _breakSignature = signature;
        _breaks.Clear();
        _breakIds.Clear();
        foreach (var item in breaks)
        {
            _breakIds.Add(item.Id);
            _breaks.AddItem($"断点 {item.Id.Value} · {item.Track} 号轨 · {item.DistanceMeters.ToString("0.##", CultureInfo.InvariantCulture)} m");
        }
        var index = selected is { } id ? _breakIds.IndexOf(id) : -1;
        if (index >= 0) _breaks.Select(index);
    }

    private void BindSources(ICoreReadView view, IContentView content, GoodId? rail)
    {
        var owned = view.World.Nodes.Select(node => node.Occupant).OfType<NodeOccupant.Settlement>()
            .Where(item => item.Relation == SettlementRelation.PlayerOwned).Select(item => item.Id)
            .Distinct().OrderBy(id => id.Value).ToArray();
        var signature = _edge.Value + ":" + (rail?.ToString() ?? "-") + ":" + string.Join(",",
            owned.Select(id => id.Value + ":" + Stock(view, id, rail)));
        if (signature == _sourceSignature) return;
        var selected = SelectedSource();
        _sourceSignature = signature;
        _sources.Clear();
        _sourceIds.Clear();
        foreach (var id in owned)
        {
            _sourceIds.Add(id);
            var stock = rail is null ? "铁路规则不可用" : Stock(view, id, rail) is { } quantity
                ? $"{WorldPanels.GoodName(rail, content)} × {quantity}" : "库存不可用";
            _sources.AddItem($"{SettlementText(view, content, id)} · {stock}");
        }
        var index = selected is { } source ? _sourceIds.IndexOf(source) : _sourceIds.Count == 0 ? -1 : 0;
        if (index >= 0) _sources.Select(index);
    }

    private static GoodId? RailGood(ICoreReadView view, IContentView content)
    {
        if (content.Core.RailwayRules.IsDefaultOrEmpty) return null;
        var rule = content.Core.RailwayRules.FirstOrDefault(item => item.Id == view.World.RailwayRule);
        return rule is null ? null : new GoodId.Voxel(rule.RailGood);
    }

    private static string SettlementText(ICoreReadView view, IContentView content, SettlementId id) =>
        view.FindSettlement(id) is Lookup<SettlementView>.Found settlement
            ? $"{WorldPanels.NodeName(settlement.Value.Node, view, content)} · 聚居地 {id.Value}"
            : $"聚居地 {id.Value}";

    private static long? Stock(ICoreReadView view, SettlementId id, GoodId? rail)
    {
        if (rail is null || view.FindSettlement(id) is not Lookup<SettlementView>.Found settlement
            || view.FindInventory(settlement.Value.Inventory) is not Lookup<InventoryView>.Found inventory)
            return null;
        var stacks = inventory.Value.Stacks.IsDefault ? [] : inventory.Value.Stacks;
        return stacks.Where(stack => stack.Good == rail).Sum(stack => stack.Quantity);
    }

    private SettlementId? SelectedSource() =>
        _sources.Selected is >= 0 and var index && index < _sourceIds.Count ? _sourceIds[index] : null;

    private BreakId? SelectedBreak()
    {
        var rows = _breaks.GetSelectedItems();
        return rows.Length > 0 && rows[0] < _breakIds.Count ? _breakIds[rows[0]] : null;
    }

    private void Expand()
    {
        if (_target is { } target && SelectedSource() is { } source)
            _submit(target, new CoreCommand.ExpandRail(_edge, source));
    }

    private void Repair()
    {
        if (_target is { } target && SelectedSource() is { } source && SelectedBreak() is { } breakId)
            _submit(target, new CoreCommand.RepairRail(_edge, breakId, source));
    }

    private void EnableAutoRepair()
    {
        if (_target is { } target && SelectedSource() is { } source)
            _submit(target, new CoreCommand.SetAutomaticRailRepair(_edge, source));
    }

    private void DisableAutoRepair()
    {
        if (_target is { } target) _submit(target, new CoreCommand.SetAutomaticRailRepair(_edge, null));
    }
}
