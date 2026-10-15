using System.Collections.Immutable;
using Train.Core.Contracts;
using Train.Core.World;

namespace Train.Core.Settlements;

internal sealed record PopulationStep(PopulationState State, double AvailableLabor,
    PopulationConsumption? Consumption, ImmutableArray<CoreEvent> Events);

internal static class PopulationOperations
{
    internal static PopulationStep Begin(WorldState world, CoreContentProjection content, SettlementId id)
    {
        var settlement = world.Settlements.Single(s => s.Id == id);
        var state = settlement.PopulationState;
        var current = content.PopulationRules.FirstOrDefault(r => r.Id == world.Source.Configuration.Rules.Population);
        var problems = Problems(world, content, settlement, current);
        var available = problems.IsEmpty;
        ImmutableArray<CoreEvent> events = state.Available == available ? []
            : [new CoreEvent.PopulationAvailabilityChanged(id, available, problems)];
        state = state with { Available = available };
        if (!available) return new(state, 0, null, events);

        var period = state.Period ?? new PopulationPeriodState(WorldInputs.NormalizePopulationRule(current!),
            0, [0, 0, 0, 0, 0, 0], [0, 0, 0, 0, 0, 0]);
        var elapsed = checked(period.ElapsedSteps + 1);
        period = period with
        {
            ElapsedSteps = elapsed,
            Required = period.Rule.Needs.Select(n => Finite(settlement.Population * n.AmountPerPersonSecond
                * SimulationTime.StepSeconds * elapsed)).ToImmutableArray()
        };
        state = state with { Period = period };
        var consumption = elapsed % period.Rule.ConsumptionIntervalTicks == 0
            ? new PopulationConsumption(period.Rule, settlement.Population,
                world.Inventories.Single(i => i.Id == settlement.Inventory).Goods, state.ConsumptionRemaining) : null;
        return new(state, Finite(settlement.Population * current!.LaborRatio), consumption, events);
    }

    internal static (WorldState World, ImmutableArray<CoreEvent> Events) Complete(WorldState world,
        SettlementId id, PopulationStep step, ImmutableArray<long> improvementGranted, SimulationPosition position)
    {
        var settlement = world.Settlements.Single(s => s.Id == id);
        var state = step.State;
        var residents = settlement.Population;
        var events = step.Events.ToBuilder();
        if (step.Consumption is { } consumption)
        {
            consumption.Complete(improvementGranted);
            var period = state.Period!;
            var satisfied = period.Satisfied.ToBuilder();
            var shortages = state.Shortages.ToBuilder();
            for (var i = 0; i < period.Rule.Needs.Length; i++)
            {
                var previousRequired = residents * period.Rule.Needs[i].AmountPerPersonSecond * SimulationTime.StepSeconds
                    * (period.ElapsedSteps - period.Rule.ConsumptionIntervalTicks);
                // Full intervals stay exactly full; multiplying conversions must not create a rounding shortage.
                satisfied[i] = period.Satisfied[i] == previousRequired && consumption.Satisfied[i] == consumption.Required[i]
                    ? period.Required[i] : Math.Min(period.Required[i], period.Satisfied[i] + consumption.Satisfied[i]);
                var shortage = consumption.Satisfied[i] < consumption.Required[i];
                if (shortages[i] != shortage)
                    events.Add(new CoreEvent.PopulationShortageChanged(id, period.Rule.Needs[i].Need, shortage));
                shortages[i] = shortage;
            }
            period = period with { Satisfied = satisfied.ToImmutable() };
            state = state with { Period = period, ConsumptionRemaining = consumption.Remaining(), Shortages = shortages.ToImmutable() };
            if (period.ElapsedSteps == period.Rule.StatisticsPeriodTicks)
            {
                var basic = Satisfaction(period, PopulationNeedClass.Basic);
                var improvement = Satisfaction(period, PopulationNeedClass.Improvement);
                var rate = basic < 1 ? -period.Rule.MaximumLossPerSecond * (1 - basic)
                    : period.Rule.BaseGrowthPerSecond + period.Rule.ImprovementGrowthPerSecond * improvement;
                var change = Finite(residents * rate * SimulationTime.StepSeconds * period.Rule.StatisticsPeriodTicks + state.ChangeRemaining);
                var whole = change <= 1d - residents ? 0 : checked((long)Math.Truncate(change));
                var after = change <= 1d - residents ? 1 : checked(residents + whole);
                var remainder = after == 1 && change < 0 ? 0 : change - whole;
                var reason = rate < 0 ? after == 1 ? PopulationSettlementReason.MinimumReached : PopulationSettlementReason.ShortageLoss
                    : rate > 0 ? PopulationSettlementReason.Growth : PopulationSettlementReason.Stable;
                var report = new PopulationSettlementReport(position, period.Rule.Revision, residents, after,
                    after - residents, basic, improvement, reason);
                events.Add(new CoreEvent.PopulationSettled(id, report));
                if (residents > 1 && after == 1) events.Add(new CoreEvent.PopulationMinimumReached(id, after));
                residents = after;
                state = state with { Period = null, ChangeRemaining = remainder, LastSettlement = report };
            }
        }
        return (world with
        {
            Settlements = world.Settlements.Select(s => s.Id == id ? s with
                { Population = residents, PopulationState = state } : s).ToImmutableArray()
        }, events.ToImmutable());
    }

    private static ImmutableArray<CoreProblem> Problems(WorldState world, CoreContentProjection content,
        SettlementState settlement, PopulationRuleProjection? current)
    {
        var problems = ImmutableArray.CreateBuilder<CoreProblem>();
        var selected = world.Source.Configuration.Rules.Population;
        if (current is null || content.MissingReferences.Any(m => m.Reference == new CoreDefinitionReference.Rule(selected)))
            problems.Add(new("content_missing", "Configuration.Rules.Population", new WorldObjectId.Settlement(settlement.Id), [selected.Value]));
        var execution = settlement.PopulationState.Period?.Rule ?? current;
        if (execution is not null)
            foreach (var good in execution.ConsumptionGoods)
                if (WorldGoods.DefinitionMissing(world, content, good.Good))
                    problems.Add(new("content_missing", $"World.Settlements[{settlement.Id.Value}].PopulationState.Period.Rule.ConsumptionGoods",
                        new WorldObjectId.Settlement(settlement.Id), [WorldReferences.GoodKey(good.Good)]));
        return problems.ToImmutable();
    }

    private static double Satisfaction(PopulationPeriodState period, PopulationNeedClass category)
    {
        var maximumWeight = period.Rule.Needs.Where(n => n.Class == category).Max(n => n.Weight);
        double sum = 0, weights = 0;
        for (var i = 0; i < period.Rule.Needs.Length; i++)
        {
            var need = period.Rule.Needs[i];
            if (need.Class != category) continue;
            var weight = need.Weight / maximumWeight;
            sum += weight * (period.Required[i] == 0 ? 1 : Math.Min(1, period.Satisfied[i] / period.Required[i]));
            weights += weight;
        }
        var result = Math.Clamp(sum / weights, 0, 1);
        // A rounded weighted average must not turn an observed shortage into the full-supply growth branch.
        return result == 1 && period.Rule.Needs.Where(n => n.Class == category)
            .Any(n => period.Satisfied[(int)n.Need] < period.Required[(int)n.Need]) ? Math.BitDecrement(1) : result;
    }

    private static double Finite(double value) => double.IsFinite(value) ? value
        : throw new InvalidOperationException("人口计算产生非有限数值。");
}

internal sealed class PopulationConsumption
{
    private readonly PopulationRuleProjection _rule;
    private readonly Dictionary<GoodId, double> _remaining;
    private readonly ConsumptionGoodProjection[] _basic;
    private readonly ConsumptionGoodProjection[] _improvement;
    internal double[] Required { get; }
    internal double[] Satisfied { get; } = new double[6];
    internal ImmutableArray<GoodsQuantity> BasicRequests { get; }

    internal PopulationConsumption(PopulationRuleProjection rule, long residents, ImmutableArray<GoodsQuantity> inventory,
        ImmutableArray<PopulationConsumptionRemainder> remainders)
    {
        _rule = rule;
        _remaining = remainders.ToDictionary(r => r.Good, r => r.Units);
        Required = rule.Needs.Select(n => residents * n.AmountPerPersonSecond
            * SimulationTime.StepSeconds * rule.ConsumptionIntervalTicks).ToArray();
        _basic = rule.ConsumptionGoods.Where(g => g.Satisfies.Any(c => c.Need is PopulationNeed.Food or PopulationNeed.Clothing))
            .OrderBy(g => g.SelectionOrder).ToArray();
        _improvement = rule.ConsumptionGoods.Where(g => g.Satisfies.Any(c => c.Need is not (PopulationNeed.Food or PopulationNeed.Clothing)))
            .OrderBy(g => g.SelectionOrder).ToArray();
        var requests = ImmutableArray.CreateBuilder<GoodsQuantity>();
        var projected = new double[6];
        foreach (var good in _basic)
        {
            var units = UnitsNeeded(good, PopulationNeedClass.Basic, projected);
            var request = Request(good.Good, units);
            requests.Add(request);
            // Basic consumption is first: its stock is fixed at this boundary, before any facility request.
            var granted = Math.Min(request.Quantity, inventory.Where(g => g.Good == good.Good).Sum(g => g.Quantity));
            Apply(good, Math.Min(units, _remaining.GetValueOrDefault(good.Good) + granted), projected);
        }
        BasicRequests = requests.ToImmutable();
    }

    internal ImmutableArray<GoodsQuantity> PrepareImprovement(ImmutableArray<long> basicGranted)
    {
        for (var i = 0; i < _basic.Length; i++) Consume(_basic[i], basicGranted[i], PopulationNeedClass.Basic);
        return _improvement.Select(g => Request(g.Good, UnitsNeeded(g, PopulationNeedClass.Improvement, Satisfied))).ToImmutableArray();
    }

    internal void Complete(ImmutableArray<long> improvementGranted)
    {
        for (var i = 0; i < _improvement.Length; i++) Consume(_improvement[i], improvementGranted[i], PopulationNeedClass.Improvement);
    }

    internal ImmutableArray<PopulationConsumptionRemainder> Remaining() => _remaining.Where(r => r.Value > 0)
        .OrderBy(r => WorldReferences.GoodKey(r.Key), StringComparer.Ordinal).Select(r => new PopulationConsumptionRemainder(r.Key, r.Value)).ToImmutableArray();

    private GoodsQuantity Request(GoodId good, double units)
    {
        var quantity = Math.Ceiling(Math.Max(0, units - _remaining.GetValueOrDefault(good)));
        // Converting long.MaxValue to double rounds up to 2^63, which cannot be cast back to long.
        return new(good, quantity >= (double)long.MaxValue ? long.MaxValue : checked((long)quantity));
    }

    private double UnitsNeeded(ConsumptionGoodProjection good, PopulationNeedClass category, double[] satisfied)
        => good.Satisfies.Where(c => _rule.Needs[(int)c.Need].Class == category)
            .Max(c => Math.Max(0, Required[(int)c.Need] - satisfied[(int)c.Need]) / c.AmountPerGoodUnit);

    private void Consume(ConsumptionGoodProjection good, long granted, PopulationNeedClass category)
    {
        var available = _remaining.GetValueOrDefault(good.Good) + granted;
        var consumed = Math.Min(available, UnitsNeeded(good, category, Satisfied));
        Apply(good, consumed, Satisfied);
        _remaining[good.Good] = Math.Max(0, available - consumed);
    }

    private void Apply(ConsumptionGoodProjection good, double units, double[] satisfied)
    {
        foreach (var conversion in good.Satisfies)
        {
            var i = (int)conversion.Need;
            var deficit = Math.Max(0, Required[i] - satisfied[i]);
            satisfied[i] = units >= deficit / conversion.AmountPerGoodUnit ? Required[i]
                : Math.Min(Required[i], satisfied[i] + units * conversion.AmountPerGoodUnit);
        }
    }
}
