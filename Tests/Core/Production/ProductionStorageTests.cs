using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.Tests.World;
using static Train.Core.Tests.Production.ProductionFixture;

namespace Train.Core.Tests.Production;

[TestClass]
public sealed class ProductionStorageTests
{
    [TestMethod]
    [DataRow(0L)]
    [DataRow(3L)]
    public void Residents_wait_for_food_to_leave_output_buffer_and_for_the_next_step(long freeCapacity)
    {
        var food = new GoodId.Voxel(new("fixture:food"));
        var content = Content(stock: freeCapacity == 0 ? [new(Input, 2), new(OtherProduct, 3)] : [new(Input, 2)],
            work: 1, capacity: 3, outputs: [new(food, 3)]);
        content = content with { PopulationRules = [WorldFixture.PopulationRule() with { StatisticsPeriodTicks = 2 }] };
        using var candidate = Prepare(content);
        using var session = candidate.Activate();
        var produced = Step(session, 2);
        Assert.AreEqual(0, produced.Events.Select(e => e.Event).OfType<CoreEvent.PopulationSettled>().Single().Report.BasicSatisfaction);
        Assert.AreEqual(freeCapacity, Stock(session, food));
        Assert.AreEqual(3 - freeCapacity, Read(session).OutputBuffer.Sum(g => g.Quantity));
        var later = Step(session, 2);
        Assert.AreEqual(freeCapacity == 0 ? 0 : .5,
            later.Events.Select(e => e.Event).OfType<CoreEvent.PopulationSettled>().Single().Report.BasicSatisfaction);
        Assert.AreEqual(freeCapacity == 0 ? 0 : 2, Stock(session, food));
        Assert.AreEqual(3 - freeCapacity, Read(session).OutputBuffer.Sum(g => g.Quantity));
    }

    [TestMethod]
    [DataRow(0, false)]
    [DataRow(1, true)]
    public void Shared_storage_fills_by_priority_then_building_id(int secondPriority, bool firstWins)
    {
        var content = TwoFacilities(Content(stock: [new(Input, 4)], work: 1, capacity: 3),
            [new(Input, 2)], secondPriority);
        using var candidate = Prepare(content);
        using var session = candidate.Activate();

        Step(session);
        Assert.AreEqual(0L, Stock(session, Input));
        Assert.IsNotNull(Read(session).Batch);
        Assert.IsNotNull(ReadSecond(session).Batch);
        Step(session);

        Assert.AreEqual(firstWins ? 3L : 0L, Stock(session, Product));
        Assert.AreEqual(firstWins ? 0L : 3L, Stock(session, OtherProduct));
        Assert.IsNull(Read(session).Batch);
        Assert.IsNull(ReadSecond(session).Batch);
        CollectionAssert.AreEqual(firstWins ? Array.Empty<GoodsQuantity>() : new[] { new GoodsQuantity(Product, 3) },
            Read(session).OutputBuffer.ToArray());
        CollectionAssert.AreEqual(firstWins ? new[] { new GoodsQuantity(OtherProduct, 3) } : Array.Empty<GoodsQuantity>(),
            ReadSecond(session).OutputBuffer.ToArray());
    }

    [TestMethod]
    public void Completed_or_drained_output_cannot_start_another_facility_in_the_same_step()
    {
        var content = TwoFacilities(Content(stock: [new(Input, 2)], work: 1, capacity: 1), [new(Product, 3)], 1);
        using var candidate = Prepare(content);
        using var session = candidate.Activate();

        Step(session);
        Assert.IsNotNull(Read(session).Batch);
        Assert.IsNull(ReadSecond(session).Batch);
        Step(session);

        Assert.AreEqual(1L, Stock(session, Product));
        Assert.AreEqual(new GoodsQuantity(Product, 2), Read(session).OutputBuffer.Single());
        Assert.AreEqual(0L, Stock(session, OtherProduct));
        Assert.IsNull(ReadSecond(session).Batch);
        var expanded = Changed(content) with { Designs = content.Designs.Select(d => d.Design != WorldFixture.Station ? d : d with
        { Parts = d.Parts.Select(p => p with { Capabilities = p.Capabilities.Select(c => c is PartCapability.Storage
            ? new PartCapability.Storage(3) : c).ToImmutableArray() }).ToImmutableArray() }).ToImmutableArray() };
        Publish(session, expanded);
        var drained = Step(session);
        Assert.AreEqual(3L, Stock(session, Product));
        Assert.IsTrue(Read(session).OutputBuffer.IsEmpty);
        Assert.IsNull(ReadSecond(session).Batch);
        var ready = drained.Events.Select(e => e.Event).OfType<CoreEvent.ProductionStateChanged>()
            .Single(e => e.Facility.Building == new BuildingId(3));
        Assert.AreEqual(ProductionState.WaitingForInputs, ready.Before);
        Assert.AreEqual(ProductionState.Running, ready.After);
        Assert.IsTrue(ready.Reasons.IsEmpty);
        Step(session);
        Assert.AreEqual(0L, Stock(session, Product));
        Assert.AreEqual(0d, ReadSecond(session).Batch!.ProgressWork);
        Step(session);
        Assert.AreEqual(3L, Stock(session, OtherProduct));
    }

    [TestMethod]
    public void Acquired_lease_leaves_only_uncommitted_capacity_for_production_output()
    {
        var content = Content(stock: [new(Input, 2)], work: 1, capacity: 5);
        content = content with { MarketRules = [content.MarketRules[0] with
        { RentableCapacityRatio = new(10000), LeaseDurationTicks = 10 }] };
        using var candidate = Prepare(content);
        using var session = candidate.Activate();
        var rent = (SubmissionResult.Completed)session.Submit(
            new CoreCommand.AcquireLease(new LeaseRequest.Rent(Settlement, 3, 10), new(1000)),
            CommandSource.AutomationTest);
        Assert.IsInstanceOfType<CommandDecision.Accepted>(rent.Receipt.Decision, rent.Diagnostic?.Message);
        var lease = rent.Events.Select(e => e.Event).OfType<CoreEvent.LeaseAcquired>().Single().Lease;
        Assert.AreEqual(3L, ((Lookup<InventoryView>.Found)session.Read().FindInventory(lease.Inventory)).Value.Capacity);
        Assert.AreEqual(2L, ReadInventory(session).Capacity);

        Step(session, 2);

        Assert.AreEqual(0L, Stock(session, Input));
        Assert.AreEqual(2L, Stock(session, Product));
        Assert.AreEqual(new GoodsQuantity(Product, 1), Read(session).OutputBuffer.Single());
        Assert.AreEqual(ProductionState.OutputBlocked, Read(session).State);
        Step(session);
        Assert.AreEqual(2L, Stock(session, Product));
        Assert.AreEqual(new GoodsQuantity(Product, 1), Read(session).OutputBuffer.Single());
    }

    private static FacilityView ReadSecond(ICoreSession session)
        => ((Lookup<FacilityView>.Found)session.Read().FindFacility(new(new(3), CapabilityKind.Production))).Value;

    private static CoreContentProjection TwoFacilities(CoreContentProjection content,
        ImmutableArray<GoodsQuantity> secondInputs, int secondPriority)
    {
        ProcessRef secondProcess = new ProcessRef.Production(new("fixture:second-production"));
        return content with
        {
            Processes = [content.Processes[0], content.Processes[0] with
            { Details = new ProcessDetails.Production(new("fixture:second-production"), secondInputs, [new(OtherProduct, 3)]) }],
            SettlementTemplates = [content.SettlementTemplates[0] with
            {
                Plots = [.. content.SettlementTemplates[0].Plots, new(3, PlotSize.Small, WorldFixture.FactoryBuilding)],
                ProductionPlans = [new(3, CapabilityKind.Production, secondProcess, true, secondPriority),
                    content.SettlementTemplates[0].ProductionPlans[0] with { Priority = 1 }]
            }]
        };
    }
}
