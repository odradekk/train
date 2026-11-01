using System.Collections.Immutable;
using Godot;
using Train.App;
using Train.Content.Contracts;
using Train.Core.Contracts;
using static Train.Ui.EconomyPanelControls;
using static Train.Ui.EconomyPanels;

namespace Train.Ui;

internal sealed class EconomyReportsPanel
{
    private readonly HostCoordinator _host;
    internal EconomyReportsPanel(HostCoordinator host) => _host = host;
    private PanelContainer? _reportsPanel;
    private HostBinding _reportsBinding;
    private ContentGeneration _reportsGeneration;
    private CoreRevision? _reportsRevision;
    private VBoxContainer _reportsBody = null!;
    private Label _reportsState = null!;
    private Label _reportsText = null!;

    internal void Open(PanelContainer panel, VBoxContainer body, HostBinding binding, ContentGeneration generation)
    {
        _reportsBinding = binding; _reportsGeneration = generation; _reportsRevision = null;
        _reportsPanel = panel; _reportsBody = body;
        _reportsState = Text(_reportsBody, "State", "");
        _reportsText = Text(List(_reportsBody, "Reports", 570), "ReportsText", "");
    }

    internal void Close() { RemoveWindow(_reportsPanel); _reportsPanel = null; }

    internal void Refresh(HostBinding binding, ICoreReadView view, IContentView content)
    {
        if (_reportsPanel?.Visible != true) return;
        if (binding != _reportsBinding || !_host.PanelGenerationCurrent(_reportsBinding, _reportsGeneration))
        { Invalidate(_reportsBody, _reportsState); return; }
        if (_reportsRevision == view.Revision) return;
        _reportsRevision = view.Revision;
        var lines = new List<string> { $"资金：{view.Economy.Wallet.Value}", $"未归属服务支出：{view.Economy.UnassignedServicePayments.Value}", "最近运行报告：" };
        foreach (var report in view.Economy.RecentRunReports) lines.Add(ReportText(report, content));
        if (view.Economy.RecentRunReports.IsEmpty) lines.Add("暂无已结束的运行报告。");
        lines.Add("玩家自有聚居地最近人口结算：");
        var settlements = view.World.Nodes.Select(node => node.Occupant).OfType<NodeOccupant.Settlement>()
            .Where(item => item.Relation == SettlementRelation.PlayerOwned);
        var any = false;
        foreach (var item in settlements)
        {
            if (view.FindSettlement(item.Id) is not Lookup<SettlementView>.Found found) continue;
            any = true;
            lines.Add($"聚居地 {item.Id.Value}\n" + (found.Value.Population.LastSettlement is { } last ? PopulationReportText(last) : "尚未结算"));
        }
        if (!any) lines.Add("没有玩家自有聚居地。");
        _reportsText.Text = string.Join("\n\n", lines);
    }

}
