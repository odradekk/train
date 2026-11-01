using System.Collections.Immutable;
using Godot;
using Train.App;
using Train.Content.Contracts;
using Train.Core.Contracts;
using static Train.Ui.EconomyPanelControls;

namespace Train.Ui;

internal partial class EconomyPanels
{
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
                $" {WorldPanels.GoodName(rule.Good, content)} · {rule.CompletedQuantity}/{rule.RequestedQuantity} · 实际金额 {rule.ActualAmount.Value}\n" + Problems(rule.IncompleteReasons, content) +
                (rule.Action is TradeAction.Buy or TradeAction.Load &&
                    rule.IncompleteReasons.Any(problem => problem.Code == "insufficient_goods") ? "\n" + ReservedStockHint : ""));
        return string.Join("\n", lines);
    }

    internal static string ReportText(RunReport report, IContentView content) =>
        $"列车 {report.Train.Value} · 第 {report.EndedAt.CompletedSteps} 步结束 · " +
        (report.Reason switch { RunEndReason.PlanCompleted => "计划完成", RunEndReason.LoopCompleted => "一圈完成", RunEndReason.PlanReplaced => "计划已替换", RunEndReason.TrainDisbanded => "列车已解散", RunEndReason.AllVehiclesDestroyed => "全部车辆损毁", _ => report.Reason.ToString() }) + "\n" + RunText(report.Totals, content);

    internal static string PopulationReportText(PopulationSettlementReport report) =>
        $"第 {report.Position.CompletedSteps} 步 · 执行修订 {report.ExecutionRevision.Value}\n人口 {report.Before} → {report.After}（{report.Change:+0;-0;0}） · 基本满足率 {report.BasicSatisfaction:P1} · 改善满足率 {report.ImprovementSatisfaction:P1}\n原因：" +
        (report.Reason switch { PopulationSettlementReason.Growth => "增长", PopulationSettlementReason.ShortageLoss => "短缺流失", PopulationSettlementReason.Stable => "稳定", PopulationSettlementReason.MinimumReached => "达到最低人口", _ => report.Reason.ToString() });
}
