using Godot;
using Train.App;
using Train.Content.Contracts;
using Train.Core.Contracts;

namespace Train.Ui;

internal partial class EconomyPanels
{
    private PanelContainer? _reportsPanel;
    private HostBinding _reportsBinding;
    private ContentGeneration _reportsGeneration;
    private CoreRevision? _reportsRevision;
    private VBoxContainer _reportsBody = null!;
    private Label _reportsState = null!;
    private Label _reportsText = null!;

    internal void OpenReports()
    {
        if (!ReadCurrent()) return;
        RemoveWindow(_reportsPanel);
        _reportsBinding = _binding; _reportsGeneration = _content.Generation; _reportsRevision = null;
        _reportsPanel = UiStyle.Panel(this, "EconomyReports", "经营报告", new(420, 70), 720,
            () => _reportsPanel!.Hide(), out _reportsBody);
        _reportsState = Text(_reportsBody, "State", "");
        _reportsText = Text(List(_reportsBody, "Reports", 570), "ReportsText", "");
        RefreshReports();
    }

    private void RefreshReports()
    {
        if (_reportsPanel?.Visible != true) return;
        if (_binding != _reportsBinding || !_host.PanelGenerationCurrent(_reportsBinding, _reportsGeneration))
        { Invalidate(_reportsBody, _reportsState); return; }
        if (_reportsRevision == _view.Revision) return;
        _reportsRevision = _view.Revision;
        var lines = new List<string> { $"资金：{_view.Economy.Wallet.Value}", $"未归属服务支出：{_view.Economy.UnassignedServicePayments.Value}", "最近运行报告：" };
        foreach (var report in _view.Economy.RecentRunReports) lines.Add(ReportText(report, _content));
        if (_view.Economy.RecentRunReports.IsEmpty) lines.Add("暂无已结束的运行报告。");
        lines.Add("玩家自有聚居地最近人口结算：");
        var settlements = _view.World.Nodes.Select(node => node.Occupant).OfType<NodeOccupant.Settlement>()
            .Where(item => item.Relation == SettlementRelation.PlayerOwned);
        var any = false;
        foreach (var item in settlements)
        {
            if (_view.FindSettlement(item.Id) is not Lookup<SettlementView>.Found found) continue;
            any = true;
            lines.Add($"聚居地 {item.Id.Value}\n" + (found.Value.Population.LastSettlement is { } last ? PopulationReportText(last) : "尚未结算"));
        }
        if (!any) lines.Add("没有玩家自有聚居地。");
        _reportsText.Text = string.Join("\n\n", lines);
    }

    internal static string RunText(RunAccumulatorView run, IContentView content)
    {
        var loadedCapacity = run.LoadedCapacityRatio is { } ratio
            ? ratio.ToString("P1") : "—（未进入线路，或进入时没有载货容量）";
        var lines = new List<string>
        {
            $"从第 {run.StartedAt.CompletedSteps} 步开始 · 销售收入 {run.SalesReceipts.Value} · 采购支出 {run.PurchasePayments.Value}",
            $"服务费用：{run.ServicePayments.Value} · 净现金流 {run.NetCashFlow} · 运输数量 {run.TransportedUnits} · 载运率 {loadedCapacity}"
        };
        foreach (var rule in run.IncompleteRules)
            lines.Add($"未完成规则：停靠站 {rule.StopIndex + 1} / 规则 {rule.RuleIndex + 1} · " +
                (rule.Action switch { TradeAction.Buy => "买入", TradeAction.Sell => "卖出", TradeAction.Load => "装载", TradeAction.Unload => "卸载", _ => rule.Action.ToString() }) +
                $" {WorldPanels.GoodName(rule.Good, content)} · {rule.CompletedQuantity}/{rule.RequestedQuantity} · 实际金额 {rule.ActualAmount.Value}\n" + Problems(rule.IncompleteReasons, content));
        return string.Join("\n", lines);
    }

    internal static string ReportText(RunReport report, IContentView content) =>
        $"列车 {report.Train.Value} · 第 {report.EndedAt.CompletedSteps} 步结束 · " +
        (report.Reason switch { RunEndReason.PlanCompleted => "计划完成", RunEndReason.LoopCompleted => "一圈完成", RunEndReason.PlanReplaced => "计划已替换", RunEndReason.TrainDisbanded => "列车已解散", RunEndReason.AllVehiclesDestroyed => "全部车辆损毁", _ => report.Reason.ToString() }) + "\n" + RunText(report.Totals, content);
}
