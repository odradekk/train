namespace TrainMvp.Domain;

public static class MarketPrice
{
    public static int Compute(GoodSpec good, MarketSlot slot)
    {
        double supplyRatio = slot.SupplyCap == 0 ? 0.0 : (double)slot.Supply / slot.SupplyCap;
        double demandRatio = slot.DemandCap == 0 ? 0.0 : (double)slot.Demand / slot.DemandCap;
        double raw = good.BasePrice * (1.0 + good.Sensitivity * (demandRatio - supplyRatio));
        int rounded = (int)Math.Round(raw, MidpointRounding.AwayFromZero);
        int clamped = Math.Clamp(rounded, good.MinPrice, good.MaxPrice);
        return Math.Max(1, clamped);
    }
}

public sealed class SimulationEngine
{
    private readonly ScenarioData _scenario;
    private readonly Dictionary<string, List<(string To, int Ticks)>> _adjacency;

    public SimulationEngine(ScenarioData scenario)
    {
        _scenario = scenario;
        _adjacency = BuildAdjacency(scenario);
    }

    public SimulationState CreateInitialState()
    {
        return SimulationState.FromScenario(_scenario);
    }

    public int QuotePrice(SimulationState state, string stationId, string goodId)
    {
        return MarketPrice.Compute(_scenario.Goods[goodId], state.GetMarket(stationId, goodId));
    }

    public ShortestPathResult FindPath(string fromStationId, string toStationId)
    {
        if (fromStationId == toStationId)
        {
            return new ShortestPathResult([fromStationId], 0);
        }

        var dist = _scenario.StationOrder.ToDictionary(id => id, _ => int.MaxValue, StringComparer.Ordinal);
        var prev = new Dictionary<string, string?>(StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.Ordinal);
        dist[fromStationId] = 0;

        while (true)
        {
            string? current = null;
            foreach (var id in _scenario.StationOrder)
            {
                if (used.Contains(id) || dist[id] == int.MaxValue)
                {
                    continue;
                }

                if (current is null
                    || dist[id] < dist[current]
                    || (dist[id] == dist[current] && StringComparer.Ordinal.Compare(id, current) < 0))
                {
                    current = id;
                }
            }

            if (current is null)
            {
                break;
            }

            used.Add(current);
            foreach (var edge in _adjacency[current])
            {
                var nextDist = dist[current] + edge.Ticks;
                if (nextDist < dist[edge.To])
                {
                    dist[edge.To] = nextDist;
                    prev[edge.To] = current;
                }
            }
        }

        if (dist[toStationId] == int.MaxValue)
        {
            throw new InvalidOperationException($"No rail path from {fromStationId} to {toStationId}.");
        }

        var path = new List<string>();
        for (string? cursor = toStationId; cursor is not null; cursor = prev.GetValueOrDefault(cursor))
        {
            path.Add(cursor);
            if (cursor == fromStationId)
            {
                break;
            }
        }

        path.Reverse();
        return new ShortestPathResult(path, dist[toStationId]);
    }

    public LoopGeometry DescribeLoop(TradePlan plan)
    {
        if (plan.Stops.Count < 2)
        {
            throw new InvalidOperationException("A trade loop needs at least two trade stops.");
        }

        if (plan.Stops.Distinct(StringComparer.Ordinal).Count() != plan.Stops.Count)
        {
            throw new InvalidOperationException("Trade stops cannot be repeated.");
        }

        var segments = new List<LoopSegment>();
        var total = 0;
        for (var i = 0; i < plan.Stops.Count; i++)
        {
            var from = plan.Stops[i];
            var to = plan.Stops[(i + 1) % plan.Stops.Count];
            var path = FindPath(from, to);
            var transits = path.Path.Skip(1).Take(Math.Max(0, path.Path.Count - 2)).ToArray();
            var isReturn = i == plan.Stops.Count - 1;
            segments.Add(new LoopSegment(from, to, path.Path, path.Ticks, transits, isReturn));
            total += path.Ticks;
        }

        return new LoopGeometry(segments, total);
    }

    public LoopRunResult RunLoop(SimulationState input)
    {
        var state = input.Clone();
        var geometry = DescribeLoop(state.Plan);
        var events = new List<SimEvent>();
        var stops = new List<StopResult>();
        var marketChanges = new List<MarketChange>();
        var cashBefore = state.Cash;

        var startId = state.Plan.Stops[0];
        var startTrade = ExecuteStop(state, startId, 0, [startId], marketChanges);
        stops.Add(startTrade);
        events.Add(new TradeEvent(startId, startTrade.Rules, IsLoopStart: true));
        state.LocationId = startId;

        foreach (var segment in geometry.Segments)
        {
            RestoreMarkets(state, segment.Ticks);
            events.Add(new TravelEvent(
                segment.From,
                segment.To,
                segment.Path,
                segment.Ticks,
                segment.Transits,
                segment.IsReturn));

            foreach (var transit in segment.Transits)
            {
                events.Add(new TransitEvent(transit));
            }

            state.LocationId = segment.To;
            if (segment.IsReturn)
            {
                stops.Add(new StopResult(segment.To, false, segment.Ticks, segment.Path, []));
                events.Add(new ReturnArrivalEvent(segment.To));
            }
            else
            {
                var traded = ExecuteStop(state, segment.To, segment.Ticks, segment.Path, marketChanges);
                stops.Add(traded);
                events.Add(new TradeEvent(segment.To, traded.Rules, IsLoopStart: false));
            }
        }

        events.Add(new LoopFinishedEvent());
        var sold = stops.SelectMany(stop => stop.Rules)
            .Where(rule => rule.Action == TradeAction.Sell)
            .Sum(rule => rule.ActualQuantity);
        var report = new RunReport
        {
            Profit = state.Cash - cashBefore,
            CashBefore = cashBefore,
            CashAfter = state.Cash,
            LoopTicks = geometry.TotalTicks,
            CargoAfter = state.Cargo.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            CargoUsedAfter = state.CargoUsed,
            Capacity = state.Capacity,
            Stops = stops,
            MarketChanges = marketChanges,
            NextHint = RunReport.BuildNextHint(_scenario, stops),
            SoldQuantity = sold
        };
        return new LoopRunResult(state, report, events);
    }

    private StopResult ExecuteStop(
        SimulationState state,
        string stationId,
        int travelTicks,
        IReadOnlyList<string> path,
        List<MarketChange> marketChanges)
    {
        var results = new List<RuleResult>();
        foreach (var rule in state.Plan.RulesAt(stationId))
        {
            var before = Snapshot(state, stationId, rule.GoodId);
            var result = ApplyRule(state, rule);
            var after = Snapshot(state, stationId, rule.GoodId);
            results.Add(result);
            marketChanges.Add(new MarketChange(stationId, rule.GoodId, before, after));
        }

        return new StopResult(stationId, true, travelTicks, path, results);
    }

    private MarketSnapshot Snapshot(SimulationState state, string stationId, string goodId)
    {
        var slot = state.GetMarket(stationId, goodId);
        return new MarketSnapshot(slot.Supply, slot.Demand, QuotePrice(state, stationId, goodId));
    }

    private RuleResult ApplyRule(SimulationState state, TradeRule rule)
    {
        var price = QuotePrice(state, rule.StationId, rule.GoodId);
        var slot = state.GetMarket(rule.StationId, rule.GoodId);
        var holding = state.Holding(rule.GoodId);
        var expected = rule.Action == TradeAction.Buy
            ? Math.Max(0, rule.TargetHolding - holding)
            : Math.Max(0, holding - rule.TargetHolding);

        if (rule.Action == TradeAction.Buy)
        {
            if (price > rule.Threshold)
            {
                return Unfilled(rule, price, expected, RuleFailureCause.PriceConditionNotMet);
            }

            if (expected <= 0)
            {
                return Unfilled(rule, price, expected, RuleFailureCause.TargetAlreadyMet);
            }

            var afford = price <= 0 ? 0 : state.Cash / price;
            var space = state.RemainingCapacity;
            var actual = Math.Min(expected, Math.Min(slot.Supply, Math.Min(space, afford)));
            if (actual <= 0)
            {
                var cause = slot.Supply <= 0
                    ? RuleFailureCause.InsufficientSupply
                    : space <= 0
                        ? RuleFailureCause.InsufficientCargoSpace
                        : RuleFailureCause.InsufficientFunds;
                return Unfilled(rule, price, expected, cause);
            }

            state.Cash -= actual * price;
            state.Cargo[rule.GoodId] = holding + actual;
            slot.Supply -= actual;
            return FilledOrPartial(rule, price, expected, actual, -(actual * price), BuyPartialCause(actual, slot.Supply + actual, space, afford));
        }

        if (price < rule.Threshold)
        {
            return Unfilled(rule, price, expected, RuleFailureCause.PriceConditionNotMet);
        }

        if (expected <= 0)
        {
            return Unfilled(rule, price, expected, RuleFailureCause.TargetAlreadyMet);
        }

        var sold = Math.Min(expected, slot.Demand);
        if (sold <= 0)
        {
            return Unfilled(rule, price, expected, RuleFailureCause.InsufficientDemand);
        }

        state.Cash += sold * price;
        state.Cargo[rule.GoodId] = holding - sold;
        slot.Demand -= sold;
        return FilledOrPartial(rule, price, expected, sold, sold * price, RuleFailureCause.InsufficientDemand);
    }

    private static RuleFailureCause BuyPartialCause(int actual, int supplyBefore, int space, int afford)
    {
        if (actual == supplyBefore)
        {
            return RuleFailureCause.InsufficientSupply;
        }

        if (actual == space)
        {
            return RuleFailureCause.InsufficientCargoSpace;
        }

        return RuleFailureCause.InsufficientFunds;
    }

    private static RuleResult Unfilled(TradeRule rule, int price, int expected, RuleFailureCause cause)
    {
        return new RuleResult(
            rule.StationId,
            rule.GoodId,
            rule.Action,
            rule.Threshold,
            rule.TargetHolding,
            price,
            expected,
            0,
            0,
            RuleOutcomeKind.None,
            cause);
    }

    private static RuleResult FilledOrPartial(
        TradeRule rule,
        int price,
        int expected,
        int actual,
        int cashDelta,
        RuleFailureCause partialCause)
    {
        if (actual == expected)
        {
            return new RuleResult(
                rule.StationId,
                rule.GoodId,
                rule.Action,
                rule.Threshold,
                rule.TargetHolding,
                price,
                expected,
                actual,
                cashDelta,
                RuleOutcomeKind.Filled,
                RuleFailureCause.None);
        }

        return new RuleResult(
            rule.StationId,
            rule.GoodId,
            rule.Action,
            rule.Threshold,
            rule.TargetHolding,
            price,
            expected,
            actual,
            cashDelta,
            RuleOutcomeKind.Partial,
            partialCause);
    }

    private static void RestoreMarkets(SimulationState state, int ticks)
    {
        if (ticks <= 0)
        {
            return;
        }

        foreach (var slot in state.Markets.Values)
        {
            slot.Supply = Math.Min(slot.SupplyCap, slot.Supply + slot.SupplyRestore * ticks);
            slot.Demand = Math.Min(slot.DemandCap, slot.Demand + slot.DemandRestore * ticks);
        }
    }

    private static Dictionary<string, List<(string To, int Ticks)>> BuildAdjacency(ScenarioData scenario)
    {
        var adj = scenario.StationOrder.ToDictionary(
            id => id,
            _ => new List<(string To, int Ticks)>(),
            StringComparer.Ordinal);
        foreach (var edge in scenario.Edges)
        {
            adj[edge.A].Add((edge.B, edge.Ticks));
            adj[edge.B].Add((edge.A, edge.Ticks));
        }

        foreach (var list in adj.Values)
        {
            list.Sort((left, right) => StringComparer.Ordinal.Compare(left.To, right.To));
        }

        return adj;
    }
}
