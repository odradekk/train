using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.Sessions;
using Train.Core.Tests.World;

namespace Train.Core.Tests.Inventory;

internal static class InventoryFixture
{
    internal static readonly GoodId Voxel = new GoodId.Voxel(WorldFixture.Material);
    internal static readonly GoodId Composite = new GoodId.Composite(new(WorldFixture.Material.Value));
    internal static readonly GoodId Assembly = new GoodId.Assembly(new(new string('A', 64)));
    internal static readonly GoodId[] Goods = [Voxel, Composite, Assembly];

    internal static CoreContentProjection Content(ulong duration = 3, long rate = 5, long localStock = 0) => WorldFixture.Content with
    {
        Goods = WorldFixture.Content.Goods.AddRange(Goods.Where(g => g is not GoodId.Assembly && g != Voxel)
            .Select(g => WorldFixture.Content.Goods[0] with { Good = g })),
        DesignGoods = Goods.Where(g => g is GoodId.Assembly).Select(g => WorldFixture.Content.Goods[0] with { Good = g }).ToImmutableArray(),
        Designs = WorldFixture.Content.Designs.Select(d => d.Design == WorldFixture.Station ? d with
        {
            Parts = d.Parts.Select(p => p with { Capabilities = p.Capabilities.Add(new PartCapability.Storage(100)) }).ToImmutableArray()
        } : d).ToImmutableArray(),
        MarketRules = [WorldFixture.Content.MarketRules[0] with
        { RentMoneyPerCapacityHour = new(rate), RentableCapacityRatio = new(10000), LeaseDurationTicks = checked((long)duration) }],
        SettlementTemplates = [WorldFixture.Content.SettlementTemplates[0] with
        { SpecialInitialInventory = localStock == 0 ? [] : [new(Voxel, localStock)] }]
    };

    internal static CoreSession Session(CoreContentProjection? content = null, int trains = 1)
    {
        var input = WorldFixture.Input with { Start = WorldFixture.Input.Start with
        { Trains = Enumerable.Range(0, trains).Select(_ => new StartingTrainProjection([WorldFixture.Vehicle],
            Goods.Select(g => new GoodsQuantity(g, 10)).ToImmutableArray())).ToImmutableArray() } };
        using var candidate = WorldFixture.Prepare(input, content ?? Content());
        using var generated = (CoreSession)candidate.Activate();
        var state = generated.State;
        var rule = state.Content.RailwayRules.Single(r => r.Id == state.World.Source.Configuration.Rules.Railway);
        return new(state with { World = state.World with
        {
            Trains = state.World.Trains.Select(train =>
            {
                var position = (TrainPosition.Node)train.Position;
                var template = state.World.Settlements.Single(settlement => settlement.Node == position.Id).Template;
                return train with { Position = position with
                    { Phase = NodeTravelPhase.Docked, DistanceMeters = rule.ApproachLengthMeters + template.PlatformLengthMeters / 2 },
                    SpeedMetersPerSecond = 0 };
            }).ToImmutableArray()
        } });
    }

    internal static SubmissionResult.Completed Submit(ICoreSession session, CoreCommand command)
        => (SubmissionResult.Completed)session.Submit(command, CommandSource.AutomationTest);

    internal static SubmissionResult.Completed Accept(ICoreSession session, CoreCommand command)
    {
        var result = Submit(session, command);
        Assert.IsInstanceOfType<CommandDecision.Accepted>(result.Receipt.Decision, result.Diagnostic?.Message);
        return result;
    }

    internal static InventoryView ReadInventory(ICoreSession session, InventoryId id)
        => ((Lookup<InventoryView>.Found)session.Read().FindInventory(id)).Value;

    internal static LeaseView Lease(ICoreSession session, LeaseId id)
        => ((Lookup<LeaseView>.Found)session.Read().FindLease(id)).Value;

    internal static LeaseView Rent(CoreSession session, long capacity = 20, ulong duration = 3)
        => Accept(session, new CoreCommand.AcquireLease(new LeaseRequest.Rent(session.State.World.Settlements.Single().Id,
            capacity, duration), new(1000))).Events.Select(e => e.Event).OfType<CoreEvent.LeaseAcquired>().Single().Lease;

    internal static void Service(CoreSession session, ulong train = 1, ulong dwell = 100)
    {
        var node = session.State.World.Settlements.Single().Node;
        Accept(session, new CoreCommand.ReplaceRunPlan(new(train), new(false, [new PlanStop.Station(node, dwell, [])])));
        Accept(session, new CoreCommand.StartTrain(new(train)));
        Step(session);
    }

    internal static AdvanceResult Step(ICoreSession session)
    {
        var result = session.Advance(1);
        Assert.IsNull(result.Fault);
        Assert.AreEqual(1u, result.CompletedSteps);
        return result;
    }

    internal static void RejectWithoutWorldChange(CoreSession session, CoreCommand command, string? code = null, string? field = null)
    {
        var before = session.State.World;
        var result = Submit(session, command);
        var decision = (CommandDecision.Rejected)result.Receipt.Decision;
        if (code is not null) Assert.AreEqual(code, decision.Problems[0].Code);
        if (field is not null) Assert.AreEqual(field, decision.Problems[0].Field);
        Assert.IsTrue(result.Events.IsEmpty);
        Assert.AreEqual(before, session.State.World);
    }

    internal static CoreContentProjection Changed(CoreContentProjection content, char identity) => content with
    { Identity = new(new string(identity, 64)) };
}
