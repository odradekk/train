using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.Sessions;
using Train.Core.Tests.Inventory;
using static Train.Core.Tests.Inventory.InventoryFixture;

namespace Train.Core.Tests.Trading;

public sealed partial class AutomaticTradingTests
{
    [TestMethod]
    [DataRow(55L, 2L, "insufficient_money")]
    [DataRow(1000L, 1L, "insufficient_goods")]
    public void Same_step_trains_trade_by_identity_and_reports_filter_by_train(long money, long stock, string blocker)
    {
        var content = InventoryTradeTests.Content();
        using var original = InventoryFixture.Session(content, trains: 2);
        var state = original.State;
        var settlement = state.World.Settlements.Single();
        using var session = new CoreSession(state with
        {
            Content = content with { MarketRules = [] },
            World = state.World with
            {
                Money = new(money),
                Nodes = state.World.Nodes.Select(n => n.Id == settlement.Node ? n with { Platforms = 2 } : n).ToImmutableArray(),
                Trains = state.World.Trains.Reverse().ToImmutableArray(),
                Inventories = state.World.Inventories.Select(i => i with
                    { Goods = i.Id == settlement.Inventory ? [new(Voxel, stock)] : [] }).ToImmutableArray()
            }
        });
        foreach (var id in new TrainId[] { new(2), new(1) })
        {
            Accept(session, new CoreCommand.ReplaceRunPlan(id, Plan(session,
                [Rule(session, new AutomaticGoodsAction.Buy(null), Voxel, 1)])));
            Accept(session, new CoreCommand.StartTrain(id));
        }
        Accept(session, new CoreCommand.PublishContent(Changed(content, 'F')));
        var evaluations = Step(session).Events.Select(e => e.Event).OfType<CoreEvent.AutomaticTradeEvaluated>().ToArray();
        CollectionAssert.AreEqual(new TrainId[] { new(1), new(2) }, evaluations.Select(e => e.Train).ToArray());
        Assert.AreEqual(1L, evaluations[0].Results.Single().CompletedQuantity);
        Assert.AreEqual(0L, evaluations[1].Results.Single().CompletedQuantity);
        Assert.AreEqual(blocker, evaluations[1].Results.Single().IncompleteReasons.Single().Code);
        foreach (var id in new TrainId[] { new(2), new(1) })
            Accept(session, new CoreCommand.ReplaceRunPlan(id, Plan(session, [])));
        var reports = session.Read().Economy.RecentRunReports;
        CollectionAssert.AreEqual(new TrainId[] { new(2), new(1) }, reports.Select(r => r.Train).ToArray());
        foreach (var id in new TrainId[] { new(1), new(2) })
        {
            var train = ((Lookup<TrainView>.Found)session.Read().FindTrain(id)).Value;
            AssertReports(reports.Where(r => r.Train == id).ToImmutableArray(), train.RecentReports);
        }
    }

}
