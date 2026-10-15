using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.Tests.World;
using static Train.Core.Tests.Settlements.PopulationFixture;

namespace Train.Core.Tests.Settlements;

[TestClass]
public sealed class PopulationQueryTests
{
    [TestMethod]
    public void Nonconsumption_steps_advance_the_period_and_consumption_reports_independent_need_totals_and_trend()
    {
        using var session = Start(Content(Rule() with
        {
            ConsumptionIntervalTicks = 2, StatisticsPeriodTicks = 6, MaximumLossPerSecond = 1
        }, Stock(food: 100, clothing: 0)));
        Advance(session, 1);
        var waiting = ((PopulationExecution.InProgress)Read(session.Read()).Execution).Period;
        Assert.AreEqual(1UL, waiting.ElapsedEffectiveSteps);
        Assert.IsNull(waiting.BasicSatisfaction);
        Assert.IsNull(waiting.ImprovementSatisfaction);
        foreach (var need in waiting.Needs)
        {
            Assert.AreEqual(0d, need.AccumulatedRequired);
            Assert.AreEqual(0d, need.AccumulatedSatisfied);
            Assert.IsNull(need.SatisfactionRatio);
        }
        Advance(session, 1);
        var consumed = (PopulationExecution.InProgress)Read(session.Read()).Execution;
        Assert.AreEqual(2UL, consumed.Period.ElapsedEffectiveSteps);
        Assert.AreEqual(PopulationTrend.Declining, consumed.Trend);
        Assert.AreEqual(.5, consumed.Period.BasicSatisfaction);
        Assert.AreEqual(0d, consumed.Period.ImprovementSatisfaction);
        // Ten residents require one third of each need over two 1/60-second steps; only food is stocked.
        foreach (var need in consumed.Period.Needs)
        {
            Assert.AreEqual(1d / 3, need.AccumulatedRequired, 1e-12);
            Assert.AreEqual(need.Kind == PopulationNeed.Food ? 1d / 3 : 0, need.AccumulatedSatisfied, 1e-12);
            Assert.AreEqual(need.Kind == PopulationNeed.Food ? 1d : 0, need.SatisfactionRatio);
        }
        Advance(session, 1);
        var idle = ((PopulationExecution.InProgress)Read(session.Read()).Execution).Period;
        Assert.AreEqual(3UL, idle.ElapsedEffectiveSteps);
        foreach (var need in idle.Needs)
        {
            Assert.AreEqual(1d / 3, need.AccumulatedRequired, 1e-12);
            Assert.AreEqual(need.Kind == PopulationNeed.Food ? 1d / 3 : 0, need.AccumulatedSatisfied, 1e-12);
        }
    }

    [TestMethod]
    public void Initial_needs_and_first_report_are_read_without_starting_or_settling_a_period()
    {
        var rule = Rule();
        using var session = Start(Content(rule, Stock()));
        var retained = session.Read();
        var initial = Read(retained);
        Assert.AreEqual(10L, initial.Residents);
        Assert.AreEqual(5d, initial.AvailableLabor);
        var notStarted = (PopulationExecution.NotStarted)initial.Execution;
        Assert.AreEqual(rule.Needs.Length, notStarted.CurrentNeeds.Length);
        foreach (var need in notStarted.CurrentNeeds)
        {
            Assert.AreEqual(10d, need.RequiredPerSecond);
            Assert.AreEqual(0d, need.AccumulatedRequired);
            Assert.AreEqual(0d, need.AccumulatedSatisfied);
            Assert.IsNull(need.SatisfactionRatio);
        }
        Assert.IsNull(initial.LastSettlement);
        Assert.IsEmpty(initial.RecentSettlements);
        Advance(session, 1);
        var period = ((PopulationExecution.InProgress)Read(session.Read()).Execution).Period;
        Assert.AreEqual(2UL, period.PeriodSteps);
        Assert.AreEqual(1UL, period.ElapsedEffectiveSteps);
        Assert.AreEqual(WorldFixture.Revision, period.ExecutionRevision);
        var report = Reports(Advance(session, 1)).Single();
        var settled = Read(session.Read());
        Assert.AreEqual(report, settled.LastSettlement);
        CollectionAssert.AreEqual(new[] { report }, settled.RecentSettlements.ToArray());
        Assert.IsInstanceOfType<PopulationExecution.NotStarted>(Read(retained).Execution);
        Assert.IsNull(Read(retained).LastSettlement);
        Assert.IsEmpty(Read(retained).RecentSettlements);
    }

    [TestMethod]
    public void Publication_changes_current_rule_and_labor_but_preserves_the_running_period()
    {
        var content = Content(Rule() with { StatisticsPeriodTicks = 4 }, Stock());
        using var session = Start(content);
        Advance(session, 1);
        var retained = session.Read();
        var replacement = content with
        {
            Identity = Identity('A'),
            PopulationRules = [content.PopulationRules[0] with
                { Revision = new(new string('F', 64)), StatisticsPeriodTicks = 2, LaborRatio = .25 }]
        };
        Publish(session, replacement);
        var current = Read(session.Read());
        Assert.AreEqual(replacement.PopulationRules[0].Revision, current.CurrentRule.PublishedRevision);
        Assert.AreEqual(2.5, current.AvailableLabor);
        Assert.AreEqual(2.5, current.IdleLabor);
        var frozen = ((PopulationExecution.InProgress)current.Execution).Period;
        Assert.AreEqual(WorldFixture.Revision, frozen.ExecutionRevision);
        Assert.AreEqual(4UL, frozen.PeriodSteps);
        Assert.AreEqual(1UL, frozen.ElapsedEffectiveSteps);
        Assert.AreEqual(WorldFixture.Revision, Read(retained).CurrentRule.PublishedRevision);
        Assert.AreEqual(5d, Read(retained).AvailableLabor);
        var report = Reports(Advance(session, 3)).Single();
        Assert.AreEqual(WorldFixture.Revision, report.ExecutionRevision);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void Missing_rule_or_food_exposes_disabled_period_and_repair_resumes_its_progress(bool missingRule)
    {
        var content = Content(Rule() with { StatisticsPeriodTicks = 4 }, Stock());
        using var original = Start(content);
        Advance(original, 1);
        var reference = missingRule ? (CoreDefinitionReference)new CoreDefinitionReference.Rule(WorldFixture.Rules.Population)
            : new CoreDefinitionReference.Voxel(((GoodId.Voxel)Food).Definition);
        var missing = content with
        {
            Identity = Identity('A'),
            PopulationRules = missingRule ? [] : content.PopulationRules,
            Goods = missingRule ? content.Goods : content.Goods.Where(g => g.Good != Food).ToImmutableArray(),
            MissingReferences = [new(reference, "fixture")]
        };
        using var candidate = WorldFixture.Success(CoreFactory.Create().PrepareRestore(Capture(original), missing, WorldFixture.Options));
        using var session = candidate.Activate();
        var retained = session.Read();
        var disabled = Read(retained);
        Assert.AreEqual(0d, disabled.AvailableLabor);
        Assert.AreEqual(0d, disabled.IdleLabor);
        var execution = (PopulationExecution.Disabled)disabled.Execution;
        Assert.IsNotNull(execution.RetainedPeriod);
        Assert.AreEqual(1UL, execution.RetainedPeriod.ElapsedEffectiveSteps);
        Assert.IsTrue(execution.Reasons.Any(p => p.Code == "content_missing"));
        if (missingRule) Assert.IsNull(disabled.CurrentRule.PublishedRevision);
        Advance(session, 8);
        Assert.AreEqual(1UL, ((PopulationExecution.Disabled)Read(session.Read()).Execution).RetainedPeriod!.ElapsedEffectiveSteps);
        Publish(session, content with { Identity = Identity('B') });
        var resumed = Read(session.Read());
        Assert.AreEqual(5d, resumed.AvailableLabor);
        Assert.AreEqual(1UL, ((PopulationExecution.InProgress)resumed.Execution).Period.ElapsedEffectiveSteps);
        var expected = Reports(Advance(original, 3)).Single();
        var actual = Reports(Advance(session, 3)).Single();
        Assert.AreEqual(expected.After, actual.After);
        Assert.AreEqual(expected.BasicSatisfaction, actual.BasicSatisfaction);
        Assert.AreEqual(expected.ExecutionRevision, actual.ExecutionRevision);
        Assert.IsInstanceOfType<PopulationExecution.Disabled>(Read(retained).Execution);
    }

    private static PopulationView Read(ICoreReadView view) => SettlementQueryTests.Find(view, new(1)).Population;
}
