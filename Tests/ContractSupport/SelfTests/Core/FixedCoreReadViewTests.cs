using Train.Core.Contracts;
using Train.Core.ContractSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Train.ContractSupport.Tests.Core;

[TestClass]
public sealed class FixedCoreReadViewTests
{
    [TestMethod]
    public void ExplicitDefaultValueIsConfigured()
    {
        var view = new FixedCoreReadView { Position = new SimulationPosition(0, 0), InputIdentity = new("input") };
        Assert.AreEqual(new SimulationPosition(0, 0), view.Position);
        Assert.AreEqual(new CoreInputId("input"), view.InputIdentity);
    }

    [TestMethod]
    public void UnconfiguredPropertyAndQueryFailExplicitly()
    {
        var view = new FixedCoreReadView();
        AssertUnconfigured(() => _ = view.Position);
        AssertUnconfigured(() => _ = view.InputIdentity);
        AssertUnconfigured(() => _ = view.World);
        AssertUnconfigured(() => view.FindNode(new NodeId(1)));
        AssertUnconfigured(() => view.FindEdge(new EdgeId(1)));
        AssertUnconfigured(() => view.FindTrain(new TrainId(1)));
        AssertUnconfigured(() => view.FindInstance(new InstanceId.Vehicle(new(1))));
        AssertUnconfigured(() => view.FindCombat(new CombatId(1)));
        AssertUnconfigured(() => view.FindCreatureGroup(new CreatureGroupId(1)));
        AssertUnconfigured(() => view.FindCreature(new CreatureId(1)));
        AssertUnconfigured(() => _ = view.Revision);
        AssertUnconfigured(() => _ = view.Fault);
        AssertUnconfigured(() => view.FindLocation(new WorldLocation.Node(new(1))));
    }

    [TestMethod]
    public void WorldRetainsFixedInput()
    {
        var world = new WorldSummary(42, new("railway"), 100, 100, [], [], [], [], []);
        var view = new FixedCoreReadView { World = world };
        Assert.AreSame(world, view.World);
    }

    [TestMethod]
    public void ExplicitMissingIsRetainedAndConfigurationIsCopied()
    {
        var id = new NodeId(1);
        var missing = new Lookup<NodeView>.Missing();
        var entries = new Dictionary<NodeId, Lookup<NodeView>> { [id] = missing };
        var view = new FixedCoreReadView { Nodes = entries };
        entries.Clear();
        Assert.AreSame(missing, view.FindNode(id));
        Assert.AreSame(missing, view.FindNode(id));
        AssertUnconfigured(() => view.FindNode(new NodeId(2)));
    }

    [TestMethod]
    public void FoundQueryReturnsFixedObject()
    {
        var id = new CombatId(3);
        var combat = new CombatView(id, new WorldLocation.Node(new NodeId(1)), 4, []);
        var found = new Lookup<CombatView>.Found(combat);
        var view = new FixedCoreReadView
        {
            Combats = new Dictionary<CombatId, Lookup<CombatView>> { [id] = found }
        };
        Assert.AreSame(found, view.FindCombat(id));
        Assert.AreSame(combat, ((Lookup<CombatView>.Found)view.FindCombat(id)).Value);
        var creature = new CreatureView(new(3), new("test:beast"), 10, 10);
        var group = new CreatureGroupView(new(7), null, new WorldLocation.Node(new(1)), null, CreatureActivity.Guarding, 0, [creature]);
        var grouped = new FixedCoreReadView
        {
            CreatureGroups = new Dictionary<CreatureGroupId, Lookup<CreatureGroupView>> { [group.Id] = new Lookup<CreatureGroupView>.Found(group) },
            Creatures = new Dictionary<CreatureId, Lookup<CreatureView>> { [creature.Id] = new Lookup<CreatureView>.Found(creature) }
        };
        Assert.AreEqual(group, ((Lookup<CreatureGroupView>.Found)grouped.FindCreatureGroup(group.Id)).Value);
        Assert.AreEqual(creature, ((Lookup<CreatureView>.Found)grouped.FindCreature(creature.Id)).Value);
        AssertUnconfigured(() => grouped.FindCreatureGroup(new(8)));
        AssertUnconfigured(() => grouped.FindCreature(new(4)));
        AssertUnconfigured(() => _ = new FixedCoreReadView().Fault);
        AssertUnconfigured(() => view.PreviewSettlement(new NodeId(1)));
    }

    [TestMethod]
    public void ConfiguredEdgeTrainAndInstanceQueriesRetainExplicitMissing()
    {
        var edge = new Lookup<EdgeView>.Missing();
        var train = new Lookup<TrainView>.Missing();
        var instance = new Lookup<InstanceView>.Missing();
        var instanceId = new InstanceId.Vehicle(new(1));
        var view = new FixedCoreReadView
        {
            Edges = new Dictionary<EdgeId, Lookup<EdgeView>> { [new(1)] = edge },
            Trains = new Dictionary<TrainId, Lookup<TrainView>> { [new(1)] = train },
            Instances = new Dictionary<InstanceId, Lookup<InstanceView>> { [instanceId] = instance }
        };
        Assert.AreSame(edge, view.FindEdge(new(1)));
        Assert.AreSame(train, view.FindTrain(new(1)));
        Assert.AreSame(instance, view.FindInstance(instanceId));
    }

    [TestMethod]
    public void EconomyLookupsCopyConfigurationAndRejectUnknownInputs()
    {
        var settlement = new Lookup<SettlementView>.Missing();
        var inventory = new Lookup<InventoryView>.Missing();
        var lease = new Lookup<LeaseView>.Missing();
        var settlements = new Dictionary<SettlementId, Lookup<SettlementView>> { [new(1)] = settlement };
        var inventories = new Dictionary<InventoryId, Lookup<InventoryView>> { [new(2)] = inventory };
        var leases = new Dictionary<LeaseId, Lookup<LeaseView>> { [new(3)] = lease };
        var rule = new RuleStatusView(new("rule"), null, []);
        var economy = new EconomyView(new(42), rule, rule, rule, new(0), []);
        var view = new FixedCoreReadView { Economy = economy, Settlements = settlements, Inventories = inventories, Leases = leases };
        settlements.Clear();
        inventories.Clear();
        leases.Clear();
        Assert.AreSame(economy, view.Economy);
        Assert.AreSame(settlement, view.FindSettlement(new(1)));
        Assert.AreSame(inventory, view.FindInventory(new(2)));
        Assert.AreSame(lease, view.FindLease(new(3)));
        AssertUnconfigured(() => _ = new FixedCoreReadView().Economy);
        AssertUnconfigured(() => view.FindSettlement(new(9)));
        AssertUnconfigured(() => view.FindInventory(new(9)));
        AssertUnconfigured(() => view.FindLease(new(9)));
    }

    [TestMethod]
    public void SettlementRetainsNonemptyOwnedJobsAfterConfigurationIsCopied()
    {
        var rule = new RuleStatusView(new("rule"), null, []);
        var settlement = new SettlementView(new(1), new(1), new("town"), null,
            SettlementRelation.PlayerOwned, new(2), [], [], 0, 0, 0, [], [], [],
            new(0, 0, 0, 0), new(rule, 0, 0, 0, 0, new PopulationExecution.NotStarted([]), null, []),
            new(TargetPriority.ArmedFirst, TargetScope.All), 1, false, [], [new(3), new(7)], [],
            new(rule, new(0, 0, 0, 0, 0, 0, 0), []));
        var entries = new Dictionary<SettlementId, Lookup<SettlementView>>
            { [settlement.Id] = new Lookup<SettlementView>.Found(settlement) };
        var view = new FixedCoreReadView { Settlements = entries };
        entries.Clear();
        var found = ((Lookup<SettlementView>.Found)view.FindSettlement(settlement.Id)).Value;
        CollectionAssert.AreEqual(new[] { new JobId(3), new JobId(7) }, found.OwnedJobs.ToArray());
    }

    [TestMethod]
    public void QuotesMatchTheWholeRequestAndCopyConfiguration()
    {
        var trade = new TradeQuoteRequest(new(1), new(2), new(3), TradeAction.Buy, new GoodId.Voxel(new("good")), 4);
        var vehicle = new VehicleTradeQuoteRequest(new(2), new(3), VehicleTradeAction.Buy);
        var lease = new LeaseRequest.Rent(new(2), 4, 5);
        var tradeResult = new CoreResult<TradeQuote>.Success(new(trade, new(1), 4, new(8), [], []));
        var vehicleResult = new CoreResult<VehicleTradeQuote>.Success(new(vehicle, new(1), new(8), []));
        var leaseResult = new CoreResult<LeaseQuote>.Success(new(lease, new(1), new(8), 4, []));
        var trades = new Dictionary<TradeQuoteRequest, CoreResult<TradeQuote>> { [trade] = tradeResult };
        var vehicles = new Dictionary<VehicleTradeQuoteRequest, CoreResult<VehicleTradeQuote>> { [vehicle] = vehicleResult };
        var leases = new Dictionary<LeaseRequest, CoreResult<LeaseQuote>> { [lease] = leaseResult };
        var view = new FixedCoreReadView { TradeQuotes = trades, VehicleQuotes = vehicles, LeaseQuotes = leases };
        trades.Clear();
        vehicles.Clear();
        leases.Clear();
        Assert.AreSame(tradeResult, view.QuoteTrade(trade with { }));
        Assert.AreSame(vehicleResult, view.QuoteVehicle(vehicle with { }));
        Assert.AreSame(leaseResult, view.QuoteLease(lease with { }));
        AssertUnconfigured(() => view.QuoteTrade(trade with { Quantity = 5 }));
        AssertUnconfigured(() => view.QuoteVehicle(vehicle with { Action = VehicleTradeAction.Sell }));
        AssertUnconfigured(() => view.QuoteLease(lease with { DurationSteps = 6 }));
        var unconfigured = new FixedCoreReadView();
        AssertUnconfigured(() => unconfigured.QuoteTrade(trade));
        AssertUnconfigured(() => unconfigured.QuoteVehicle(vehicle));
        AssertUnconfigured(() => unconfigured.QuoteLease(lease));
    }

    [TestMethod]
    public void JobsAndFoundingPreviewsCopyConfigurationAndRejectUnknownKeys()
    {
        var jobId = new JobId(4);
        var job = new Lookup<JobView>.Found(new JobView.Founding(new(jobId, new(1), 180, 0, new(91), new("template"), [])));
        var node = new NodeId(1);
        var preview = new CoreResult<SettlementFoundingPreview>.Success(new(node, 40, [], [], 180, []));
        var jobs = new Dictionary<JobId, Lookup<JobView>> { [jobId] = job };
        var previews = new Dictionary<NodeId, CoreResult<SettlementFoundingPreview>> { [node] = preview };
        var view = new FixedCoreReadView { Jobs = jobs, FoundingPreviews = previews };
        jobs.Clear();
        previews.Clear();
        Assert.AreSame(job, view.FindJob(jobId));
        Assert.AreSame(preview, view.PreviewSettlement(node));
        AssertUnconfigured(() => view.FindJob(new JobId(9)));
        AssertUnconfigured(() => view.PreviewSettlement(new NodeId(9)));
        var unconfigured = new FixedCoreReadView();
        AssertUnconfigured(() => unconfigured.FindJob(jobId));
        AssertUnconfigured(() => unconfigured.PreviewSettlement(node));
    }

    private static void AssertUnconfigured(Action operation)
    {
        var error = Assert.ThrowsExactly<InvalidOperationException>(operation);
        StringAssert.StartsWith(error.Message, "UNCONFIGURED:");
    }
}
