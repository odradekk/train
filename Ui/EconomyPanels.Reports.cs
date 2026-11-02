using Train.App;
using Train.Content.Contracts;
using Train.Core.Contracts;

namespace Train.Ui;

internal partial class EconomyPanels
{
    internal static string RunText(RunAccumulatorView run, IContentView content)
    {
        var loadedCapacity = run.LoadedCapacityRatio is { } ratio
            ? ratio.ToString("P1") : "—（未进入线路，或进入时没有载货容量）";
        var lines = new List<string>
        {
            $"从{UiInput.FormatTime(run.StartedAt.CompletedSteps)}开始 · 销售收入 {run.SalesReceipts.Value} · 采购支出 {run.PurchasePayments.Value}",
            $"服务费用：{run.ServicePayments.Value} · 净现金流 {run.NetCashFlow} · 运输数量 {run.TransportedUnits} · 载运率 {loadedCapacity}"
        };
        foreach (var rule in run.IncompleteRules)
            lines.Add($"未完成规则：停靠站 {rule.StopIndex + 1} / 规则 {rule.RuleIndex + 1} · " +
                TrainPlanWorkspace.ActionName(rule.Action) +
                $" {WorldPanels.GoodName(rule.Good, content)} · {rule.CompletedQuantity}/{rule.RequestedQuantity} · 实际金额 {rule.ActualAmount.Value}\n" +
                string.Join("\n", rule.IncompleteReasons.Select(problem => CommandDiagnostics.Describe(problem with { Subject = null }, content))) +
                (rule.Action is TradeAction.Buy or TradeAction.Load &&
                    rule.IncompleteReasons.Any(problem => problem.Code == "insufficient_goods") ? "\n" + ReservedStockHint : ""));
        return string.Join("\n", lines);
    }

    internal static string ReportText(RunReport report, IContentView content) =>
        $"{UiInput.FormatTime(report.EndedAt.CompletedSteps)}结束 · " +
        EndReasonText(report.Reason) + "\n" + RunText(report.Totals, content);

    internal static string EndReasonText(RunEndReason reason) => reason switch
    {
        RunEndReason.PlanCompleted => "计划完成", RunEndReason.LoopCompleted => "一圈完成",
        RunEndReason.PlanReplaced => "计划已替换", RunEndReason.TrainDisbanded => "列车已解编",
        RunEndReason.AllVehiclesDestroyed => "全部车辆损毁", _ => "运行已结束"
    };

}
