using TrainMvp.App;
using TrainMvp.Domain;
using Xunit;

namespace TrainMvp.Tests;

public sealed class LoopPlaybackTests
{
    private static GameSession CreateSession()
    {
        var scenario = ApprovedMvpCatalog.Create();
        return new GameSession(new SimulationEngine(scenario), scenario);
    }

    private static LoopPlayback Play(LoopRunResult run, SimulationState before, Action<LoopPlayback> drive)
    {
        var playback = new LoopPlayback(run, before);
        drive(playback);
        return playback;
    }

    private static void AssertSameOutcome(
        LoopRunResult run,
        string resultFingerprint,
        SimulationState before,
        params LoopPlayback[] plays)
    {
        var frozenMarkets = SnapshotMarkets(before);
        foreach (var play in plays)
        {
            Assert.True(play.IsFinished);
            Assert.Same(run, play.Result);
            Assert.Same(run.StateAfter, play.Result.StateAfter);
            Assert.Same(run.Report, play.Result.Report);
            Assert.Equal(run.Events, play.AppliedEvents);
            Assert.Equal(resultFingerprint, FingerprintResult(play.Result));
            Assert.Equal(frozenMarkets, SnapshotMarkets(play.DisplayState));
            Assert.Equal(run.StateAfter.Cash, play.DisplayState.Cash);
            Assert.Equal(run.StateAfter.LocationId, play.DisplayState.LocationId);
            Assert.Equal(run.StateAfter.CargoUsed, play.DisplayState.CargoUsed);
            Assert.Equal(run.StateAfter.Holding(GoodIds.Grain), play.DisplayState.Holding(GoodIds.Grain));
            Assert.Equal(run.StateAfter.Holding(GoodIds.Coal), play.DisplayState.Holding(GoodIds.Coal));
            Assert.Equal(run.StateAfter.Holding(GoodIds.Tea), play.DisplayState.Holding(GoodIds.Tea));
            AssertReportEqual(run.Report, play.Result.Report);
        }
    }

    private static Dictionary<string, (int Supply, int Demand)> SnapshotMarkets(SimulationState state)
    {
        return state.Markets.ToDictionary(
            pair => $"{pair.Key.StationId}:{pair.Key.GoodId}",
            pair => (pair.Value.Supply, pair.Value.Demand),
            StringComparer.Ordinal);
    }

    private static string FingerprintResult(LoopRunResult run)
    {
        var markets = SnapshotMarkets(run.StateAfter)
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"{pair.Key}={pair.Value.Supply}/{pair.Value.Demand}");
        var events = run.Events.Select(EventKey);
        var cargo = run.StateAfter.Cargo
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"{pair.Key}={pair.Value}");
        return string.Join("|",
        [
            $"cash={run.StateAfter.Cash}",
            $"loc={run.StateAfter.LocationId}",
            $"cargo={string.Join(",", cargo)}",
            $"markets={string.Join(",", markets)}",
            $"events={string.Join(",", events)}",
            ReportFingerprint(run.Report)
        ]);
    }

    private static string EventKey(SimEvent item)
    {
        return item switch
        {
            TradeEvent trade => $"trade:{trade.StationId}:{trade.IsLoopStart}:{trade.Rules.Count}:{string.Join(",", trade.Rules.Select(rule => $"{rule.GoodId}:{rule.ActualQuantity}:{rule.CashDelta}"))}",
            TravelEvent travel => $"travel:{travel.FromStationId}->{travel.ToStationId}:{travel.Ticks}:{string.Join(">", travel.Path)}",
            TransitEvent transit => $"transit:{transit.StationId}",
            RepositionEvent reposition => $"reposition:{reposition.FromStationId}->{reposition.ToStationId}:{reposition.Ticks}:{string.Join(">", reposition.Path)}",
            ReturnArrivalEvent arrival => $"return:{arrival.StationId}",
            LoopFinishedEvent => "finished",
            _ => item.GetType().Name
        };
    }

    private static string ReportFingerprint(RunReport report)
    {
        var cargo = report.CargoAfter
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"{pair.Key}={pair.Value}");
        var stops = report.Stops.Select(stop =>
            $"{stop.StationId}:{stop.Traded}:{stop.TravelTicks}:{string.Join(">", stop.Path)}:{string.Join(",", stop.Rules.Select(RuleKey))}");
        var markets = report.MarketChanges.Select(change =>
            $"{change.StationId}:{change.GoodId}:{change.Before.Supply}/{change.Before.Demand}/{change.Before.Price}->{change.After.Supply}/{change.After.Demand}/{change.After.Price}");
        return string.Join("|",
        [
            $"profit={report.Profit}",
            $"cash={report.CashBefore}->{report.CashAfter}",
            $"ticks={report.LoopTicks}",
            $"sold={report.SoldQuantity}",
            $"used={report.CargoUsedAfter}/{report.Capacity}",
            $"cargo={string.Join(",", cargo)}",
            $"hint={report.NextHint}",
            $"repo={report.RepositionTicks}:{report.RepositionFromId}->{report.RepositionToId}:{string.Join(">", report.RepositionPath)}",
            $"stops={string.Join(";", stops)}",
            $"markets={string.Join(";", markets)}"
        ]);
    }

    private static string RuleKey(RuleResult rule)
    {
        return string.Join(":",
            rule.StationId,
            rule.GoodId,
            rule.Action,
            rule.Threshold,
            rule.TargetHolding,
            rule.QuotedPrice,
            rule.ExpectedQuantity,
            rule.ActualQuantity,
            rule.CashDelta,
            rule.Outcome,
            rule.Cause);
    }

    private static void AssertReportEqual(RunReport expected, RunReport actual)
    {
        Assert.Equal(ReportFingerprint(expected), ReportFingerprint(actual));
        Assert.Equal(expected.Profit, actual.Profit);
        Assert.Equal(expected.CashBefore, actual.CashBefore);
        Assert.Equal(expected.CashAfter, actual.CashAfter);
        Assert.Equal(expected.LoopTicks, actual.LoopTicks);
        Assert.Equal(expected.CargoAfter, actual.CargoAfter);
        Assert.Equal(expected.CargoUsedAfter, actual.CargoUsedAfter);
        Assert.Equal(expected.Capacity, actual.Capacity);
        Assert.Equal(expected.Stops, actual.Stops);
        Assert.Equal(expected.MarketChanges, actual.MarketChanges);
        Assert.Equal(expected.NextHint, actual.NextHint);
        Assert.Equal(expected.SoldQuantity, actual.SoldQuantity);
        Assert.Equal(expected.RepositionTicks, actual.RepositionTicks);
        Assert.Equal(expected.RepositionFromId, actual.RepositionFromId);
        Assert.Equal(expected.RepositionToId, actual.RepositionToId);
        Assert.Equal(expected.RepositionPath, actual.RepositionPath);
    }

    private static void Advance(LoopPlayback play)
    {
        play.AdvanceEvent();
    }

    private static IReadOnlyList<string> Hop(string from, string to) => [from, to];

    [Fact]
    public void Event_speed_skip_and_fast_complete_share_final_outcome()
    {
        var session = CreateSession();
        var before = session.State.Clone();
        var run = session.StartLoop();
        var fingerprint = FingerprintResult(run);
        var afterMarkets = SnapshotMarkets(run.StateAfter);

        var byEvent = Play(run, before, play =>
        {
            play.Start();
            play.Tick(120);
        });
        var x4 = Play(run, before, play =>
        {
            play.Start();
            play.SetSpeed(LoopPlayback.Speed4);
            play.Tick(30);
        });
        var x16 = Play(run, before, play =>
        {
            play.Start();
            play.SetSpeed(LoopPlayback.Speed16);
            play.Tick(8);
        });
        var skip = Play(run, before, play =>
        {
            play.Start();
            while (!play.IsFinished)
            {
                play.SkipToNextTradeStop();
            }
        });
        var fast = Play(run, before, play => play.FastComplete());

        AssertSameOutcome(run, fingerprint, before, byEvent, x4, x16, skip, fast);
        Assert.Equal(-48, run.Report.Profit);
        Assert.Equal(afterMarkets, SnapshotMarkets(run.StateAfter));
        Assert.Equal(fingerprint, FingerprintResult(run));
        Assert.NotEqual(SnapshotMarkets(before), afterMarkets);
    }

    [Fact]
    public void Pause_does_not_advance_index_or_clock()
    {
        var session = CreateSession();
        var play = new LoopPlayback(session.StartLoop(), session.State);
        play.Start();
        var index = play.AppliedEventCount;
        var clock = play.Clock;
        var cash = play.DisplayState.Cash;
        play.Pause();
        play.Tick(10);
        Assert.True(play.IsPaused);
        Assert.Equal(index, play.AppliedEventCount);
        Assert.Equal(clock, play.Clock);
        Assert.Equal(cash, play.DisplayState.Cash);
        play.Resume();
        play.Tick(10);
        Assert.True(play.IsFinished || play.AppliedEventCount > index || play.Clock > clock);
    }

    [Fact]
    public void Reposition_can_pause_and_resume_without_trading()
    {
        var session = CreateSession();
        session.ReplacePlan(new TradePlan(
            [StationIds.NorthMine, StationIds.WestBarn],
            [
                new TradeRule(StationIds.NorthMine, GoodIds.Coal, TradeAction.Buy, 32, 1),
                new TradeRule(StationIds.WestBarn, GoodIds.Grain, TradeAction.Buy, 18, 1)
            ]));
        var before = session.State.Clone();
        var run = session.StartLoop();
        Assert.True(run.Report.RepositionTicks > 0);
        var play = new LoopPlayback(run, before);
        play.Start();
        Assert.IsType<RepositionEvent>(play.CurrentEvent);
        play.Pause();
        play.Tick(4);
        Assert.IsType<RepositionEvent>(play.CurrentEvent);
        Assert.DoesNotContain(play.AppliedEvents, item => item is TradeEvent);
        play.Resume();
        play.FastComplete();
        Assert.Equal(run.StateAfter.Cash, play.DisplayState.Cash);
        Assert.Equal(run.Events, play.AppliedEvents);
    }

    [Fact]
    public void Skip_processes_transit_but_does_not_trade_early_north_post()
    {
        var session = CreateSession();
        session.ReplacePlan(new TradePlan(
            [StationIds.NorthMine, StationIds.EastTea, StationIds.NorthPost],
            [
                new TradeRule(StationIds.NorthMine, GoodIds.Coal, TradeAction.Buy, 32, 6),
                new TradeRule(StationIds.EastTea, GoodIds.Tea, TradeAction.Buy, 40, 1),
                new TradeRule(StationIds.NorthPost, GoodIds.Coal, TradeAction.Sell, 12, 0)
            ]));
        var before = session.State.Clone();
        var run = session.StartLoop();
        var outbound = session.Engine.FindPath(StationIds.NorthMine, StationIds.EastTea);
        Assert.Contains(StationIds.NorthPost, outbound.Path);

        var play = new LoopPlayback(run, before);
        play.Start();
        play.SkipToNextTradeStop();
        var firstTrade = play.AppliedEvents.OfType<TradeEvent>().Single();
        Assert.Equal(StationIds.NorthMine, firstTrade.StationId);

        play.SkipToNextTradeStop();
        Assert.Contains(play.AppliedEvents, item => item is TransitEvent transit && transit.StationId == StationIds.NorthPost);
        Assert.Equal(StationIds.EastTea, play.AppliedEvents.OfType<TradeEvent>().Last().StationId);
        Assert.DoesNotContain(play.AppliedEvents.OfType<TradeEvent>(), trade => trade.StationId == StationIds.NorthPost);

        play.SkipToNextTradeStop();
        Assert.Equal(1, play.AppliedEvents.OfType<TradeEvent>().Count(trade => trade.StationId == StationIds.NorthPost));
        play.FastComplete();
        AssertSameOutcome(run, FingerprintResult(run), before, play);
    }

    [Fact]
    public void Finished_playback_disables_controls_and_keeps_report()
    {
        var session = CreateSession();
        var run = session.StartLoop();
        var play = new LoopPlayback(run, session.State);
        play.FastComplete();
        Assert.False(play.CanControl);
        play.Pause();
        play.SetSpeed(LoopPlayback.Speed16);
        play.SkipToNextTradeStop();
        Assert.Equal(LoopPlayback.Speed1, play.Speed);
        Assert.False(play.IsPaused);
        Assert.Equal(run.Report.Profit, play.Result.Report.Profit);
    }

    [Fact]
    public void Plan_cannot_change_while_running()
    {
        var session = CreateSession();
        session.StartLoop();
        Assert.Throws<InvalidOperationException>(() =>
            session.SaveRule(new TradeRule(StationIds.SouthMarket, GoodIds.Grain, TradeAction.Sell, 12, 0)));
    }

    [Fact]
    public void Preset_travel_presents_every_hop_without_teleport_or_rewind()
    {
        var session = CreateSession();
        var run = session.StartLoop();
        var outbound = Assert.IsType<TravelEvent>(run.Events.OfType<TravelEvent>().First(travel => !travel.IsReturn));
        var inbound = Assert.IsType<TravelEvent>(run.Events.OfType<TravelEvent>().First(travel => travel.IsReturn));
        Assert.Equal([StationIds.WestBarn, StationIds.Hub, StationIds.SouthMarket], outbound.Path);
        Assert.Equal([StationIds.SouthMarket, StationIds.Hub, StationIds.WestBarn], inbound.Path);

        var play = new LoopPlayback(run, session.State);
        play.Start();
        Assert.IsType<TradeEvent>(play.CurrentEvent);
        Assert.Equal(StationIds.WestBarn, play.DisplayState.LocationId);

        var locations = new List<string> { play.DisplayState.LocationId };
        var motions = new List<IReadOnlyList<string>>();
        while (play.CurrentEvent is not TradeEvent trade || trade.StationId != StationIds.SouthMarket)
        {
            Advance(play);
            locations.Add(play.DisplayState.LocationId);
            if (play.MotionPath.Count >= 2)
            {
                motions.Add(play.MotionPath.ToArray());
            }
        }

        Assert.Equal(StationIds.SouthMarket, play.DisplayState.LocationId);
        Assert.Equal(outbound.Path, LoopPlayback.ConcatMotionHops(motions));
        Assert.Equal(
            [Hop(StationIds.WestBarn, StationIds.Hub), Hop(StationIds.Hub, StationIds.SouthMarket)],
            motions);
        Assert.Equal(
            [StationIds.WestBarn, StationIds.WestBarn, StationIds.Hub, StationIds.Hub, StationIds.SouthMarket],
            locations);
        Assert.DoesNotContain(locations.Take(locations.Count - 1), id => id == StationIds.SouthMarket);

        var returnMotions = new List<IReadOnlyList<string>>();
        while (play.CurrentEvent is not ReturnArrivalEvent && !play.IsFinished)
        {
            Advance(play);
            if (play.MotionPath.Count >= 2)
            {
                returnMotions.Add(play.MotionPath.ToArray());
            }
        }

        Assert.Equal(StationIds.WestBarn, play.DisplayState.LocationId);
        Assert.Equal(inbound.Path, LoopPlayback.ConcatMotionHops(returnMotions));
        Assert.Equal(
            [Hop(StationIds.SouthMarket, StationIds.Hub), Hop(StationIds.Hub, StationIds.WestBarn)],
            returnMotions);
        Assert.Equal(outbound.Path, LoopPlayback.ConcatMotionHops(play.PresentedMotionHops.Take(2)));
    }

    [Fact]
    public void Reposition_presents_every_hop_of_the_long_path()
    {
        var session = CreateSession();
        session.ReplacePlan(new TradePlan(
            [StationIds.EastTea, StationIds.WestBarn],
            [
                new TradeRule(StationIds.EastTea, GoodIds.Tea, TradeAction.Buy, 40, 1),
                new TradeRule(StationIds.WestBarn, GoodIds.Grain, TradeAction.Buy, 18, 1)
            ]));
        var before = session.State.Clone();
        var run = session.StartLoop();
        Assert.Equal(AppPhase.Repositioning, session.Phase);
        var reposition = Assert.IsType<RepositionEvent>(run.Events[0]);
        Assert.Equal(StationIds.WestBarn, reposition.FromStationId);
        Assert.Equal(StationIds.EastTea, reposition.ToStationId);
        Assert.True(reposition.Path.Count >= 3);

        var play = new LoopPlayback(run, before);
        play.Start();
        Assert.IsType<RepositionEvent>(play.CurrentEvent);
        Assert.Equal(StationIds.WestBarn, play.DisplayState.LocationId);
        Assert.Equal([reposition.Path[0], reposition.Path[1]], play.MotionPath);
        Assert.NotEqual(StationIds.EastTea, play.DisplayState.LocationId);

        var locations = new List<string> { play.DisplayState.LocationId };
        var motions = new List<IReadOnlyList<string>>();
        if (play.MotionPath.Count >= 2)
        {
            motions.Add(play.MotionPath.ToArray());
        }

        while (play.CurrentEvent is not TradeEvent && !play.IsFinished)
        {
            Advance(play);
            locations.Add(play.DisplayState.LocationId);
            if (play.MotionPath.Count >= 2)
            {
                motions.Add(play.MotionPath.ToArray());
            }
        }

        var firstTrade = Assert.IsType<TradeEvent>(play.CurrentEvent);
        Assert.Equal(StationIds.EastTea, firstTrade.StationId);
        Assert.Equal(StationIds.EastTea, play.DisplayState.LocationId);
        Assert.Equal(reposition.Path, LoopPlayback.ConcatMotionHops(motions));
        Assert.Equal(reposition.Path.Count - 1, motions.Count);
        Assert.DoesNotContain(locations.Take(locations.Count - 1), id => id == StationIds.EastTea);
        for (var i = 1; i < locations.Count; i++)
        {
            var previous = Array.IndexOf(reposition.Path.ToArray(), locations[i - 1]);
            var current = Array.IndexOf(reposition.Path.ToArray(), locations[i]);
            Assert.True(current >= previous, $"reposition rewound {locations[i - 1]} -> {locations[i]}");
        }
    }

    [Fact]
    public void ShowReport_is_only_allowed_from_running()
    {
        var session = CreateSession();
        session.ReplacePlan(new TradePlan(
            [StationIds.NorthMine, StationIds.WestBarn],
            [
                new TradeRule(StationIds.NorthMine, GoodIds.Coal, TradeAction.Buy, 32, 1),
                new TradeRule(StationIds.WestBarn, GoodIds.Grain, TradeAction.Buy, 18, 1)
            ]));
        var run = session.StartLoop();
        Assert.Equal(AppPhase.Repositioning, session.Phase);
        Assert.Throws<InvalidOperationException>(session.ShowReport);
        Assert.Equal(AppPhase.Repositioning, session.Phase);
        session.BeginRunning();
        session.ShowReport();
        Assert.Equal(AppPhase.Report, session.Phase);
        Assert.Equal(run.Report.Profit, session.CurrentRun!.Report.Profit);
    }
}
