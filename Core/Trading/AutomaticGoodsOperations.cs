using System.Collections.Immutable;
using Train.Core.Contracts;
using Train.Core.Inventory;
using Train.Core.World;

namespace Train.Core.Trading;

internal static class AutomaticGoodsOperations
{
    internal static void Validate(AutomaticGoodsRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        WorldInputs.ValidateGood(rule.Good);
        if (rule.LocalInventory.Value == 0 || rule.TargetQuantity < 0
            || rule.Action is not (AutomaticGoodsAction.Buy or AutomaticGoodsAction.Sell or AutomaticGoodsAction.Load or AutomaticGoodsAction.Unload)
            || rule.Action is AutomaticGoodsAction.Buy { MaximumUnitPrice.Value: < 0 }
            || rule.Action is AutomaticGoodsAction.Sell { MinimumUnitPrice.Value: < 0 })
            throw new ArgumentException("自动规则须提供有效动作、非零库存身份和非负目标及限价。", nameof(rule));
    }

    internal static TrainState Advance(ref WorldState world, CoreContentProjection content, TrainState train,
        ulong step, bool elapsed, ImmutableArray<CoreEvent>.Builder events)
    {
        if (MarketPrices.Rule(world, content) is not { } marketRule) return train;
        var remaining = train.AutomaticEvaluationRemainingSteps!.Value;
        if (elapsed && remaining > 0) remaining--;
        if (remaining > 0) return train with { AutomaticEvaluationRemainingSteps = remaining };
        var index = train.NextStopIndex!.Value;
        var station = (PlanStop.Station)train.Plan!.Stops[index];
        var settlement = world.Settlements.SingleOrDefault(s => s.Node == station.Node);
        var results = ImmutableArray.CreateBuilder<AutomaticRuleResult>();
        world = world with { Trains = world.Trains.Select(t => t.Id == train.Id ? train : t).ToImmutableArray() };
        for (var ruleIndex = 0; ruleIndex < station.GoodsRules.Length; ruleIndex++)
        {
            var rule = station.GoodsRules[ruleIndex];
            var action = rule.Action switch
            {
                AutomaticGoodsAction.Buy => TradeAction.Buy,
                AutomaticGoodsAction.Sell => TradeAction.Sell,
                AutomaticGoodsAction.Load => TradeAction.Load,
                AutomaticGoodsAction.Unload => TradeAction.Unload,
                _ => throw new InvalidOperationException("自动规则动作无效。")
            };
            var held = world.Inventories.Single(i => i.Id == train.Inventory).Goods
                .FirstOrDefault(g => g.Good == rule.Good)?.Quantity ?? 0;
            var quantity = action is TradeAction.Buy or TradeAction.Load
                ? Math.Max(0, rule.TargetQuantity - held) : Math.Max(0, held - rule.TargetQuantity);
            TradeQuoteRequest request = new(train.Id, settlement?.Id ?? new SettlementId(0), rule.LocalInventory, action, rule.Good, quantity);
            var quote = InventoryCommands.Quote(world, content, step, new(0), request, automatic: rule.Action);
            var completed = quote.CurrentlyExecutableQuantity;
            var amount = quote.Total ?? new Money(0);
            if (completed > 0)
            {
                var change = InventoryCommands.Apply(world, content, request with { Quantity = completed }, amount);
                world = change.World!;
                train = world.Trains.Single(t => t.Id == train.Id);
                events.AddRange(change.Events);
            }
            results.Add(new(index, ruleIndex, action, rule.Good, quantity, completed, amount, quote.Blockers));
        }
        var evaluated = results.ToImmutable();
        events.Add(new CoreEvent.AutomaticTradeEvaluated(train.Id, station.Node, evaluated));
        if (train.CurrentRun is { } run)
        {
            var incomplete = run.IncompleteRules.Where(r => r.StopIndex != index)
                .Concat(evaluated.Where(r => !r.IncompleteReasons.IsEmpty))
                .OrderBy(r => r.StopIndex).ThenBy(r => r.RuleIndex).ToImmutableArray();
            train = train with { CurrentRun = run with { IncompleteRules = incomplete } };
        }
        return train with { AutomaticEvaluationRemainingSteps = checked((ulong)marketRule.AutomaticEvaluationIntervalTicks) };
    }
}
