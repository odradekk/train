using Godot;
using Train.Core.Contracts;

namespace Train.Ui;

internal partial class EconomyPanels
{
    private Label _population = null!;
    private void BuildPopulation(VBoxContainer body) => _population = Text(body, "Population", "");

    private void RefreshPopulation()
    {
        var population = _settlement!.Population;
        var current = population.CurrentRule;
        var lines = new List<string>
        {
            $"居民 {population.Residents}",
            $"当前发布规则 {current.Rule.Value} · 修订 {current.PublishedRevision?.Value ?? "缺失"}"
        };
        if (!current.Problems.IsEmpty) lines.Add("当前规则问题：\n" + Problems(current.Problems));
        PopulationPeriodView? period = null;
        IEnumerable<PopulationNeedView> needs = [];
        switch (population.Execution)
        {
            case PopulationExecution.NotStarted pending:
                lines.Add("本期尚未开始 · 满足率未结算"); needs = pending.CurrentNeeds; break;
            case PopulationExecution.InProgress active:
                period = active.Period; needs = period.Needs;
                lines.Add("本期趋势：" + (active.Trend switch
                { PopulationTrend.Growing => "增长", PopulationTrend.Stable => "稳定", PopulationTrend.Declining => "下降", PopulationTrend.AtMinimum => "最低人口", _ => active.Trend.ToString() }));
                break;
            case PopulationExecution.Disabled disabled:
                lines.Add("人口消费已停用：\n" + Problems(disabled.Reasons));
                period = disabled.RetainedPeriod; needs = period?.Needs ?? [];
                lines.Add(period is null ? "没有已开始的周期。" : "保留本期统计，停用期间不推进。"); break;
        }
        if (period is not null)
        {
            lines.Add($"本期执行规则 {period.ExecutionRule.Value} · 修订 {period.ExecutionRevision.Value}\n统计进度 {period.ElapsedEffectiveSteps}/{period.PeriodSteps} 有效步\n基本满足率 {Ratio(period.BasicSatisfaction)} · 改善满足率 {Ratio(period.ImprovementSatisfaction)}");
            lines.Add("本期按开始时的消费与增长参数完成，更新从下一期生效；劳动比例使用当前有效规则。");
        }
        foreach (var kind in Enum.GetValues<PopulationNeed>())
        {
            var need = needs.FirstOrDefault(item => item.Kind == kind);
            var name = kind switch
            { PopulationNeed.Food => "主食", PopulationNeed.Clothing => "衣物", PopulationNeed.ImprovedDiet => "改善饮食", PopulationNeed.ConsumerGoods => "日用品", PopulationNeed.ConsumerElectronics => "消费电子", PopulationNeed.MedicalSupplies => "医疗用品", _ => kind.ToString() };
            lines.Add(need is null ? $"{name}：暂无消费统计" : $"{name}：需求 {need.RequiredPerSecond:0.###}/秒 · 累计应需 {need.AccumulatedRequired:0.###} · 实际满足 {need.AccumulatedSatisfied:0.###} · 满足率 {Ratio(need.SatisfactionRatio)}");
        }
        lines.Add(population.LastSettlement is { } last ? "最近结算：\n" + PopulationReportText(last) : "最近结算：尚未结算");
        _population.Text = string.Join("\n", lines);
    }

    private static string Ratio(double? value) => value is { } ratio ? $"{ratio:P1}" : "未结算（暂无消费统计）";
    private static string PopulationReportText(PopulationSettlementReport report) =>
        $"第 {report.Position.CompletedSteps} 步 · 执行修订 {report.ExecutionRevision.Value}\n人口 {report.Before} → {report.After}（{report.Change:+0;-0;0}） · 基本满足率 {report.BasicSatisfaction:P1} · 改善满足率 {report.ImprovementSatisfaction:P1}\n原因：" +
        (report.Reason switch { PopulationSettlementReason.Growth => "增长", PopulationSettlementReason.ShortageLoss => "短缺流失", PopulationSettlementReason.Stable => "稳定", PopulationSettlementReason.MinimumReached => "达到最低人口", _ => report.Reason.ToString() });
}
