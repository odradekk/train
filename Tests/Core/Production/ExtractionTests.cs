using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.Sessions;
using Train.Core.Tests.World;
using static Train.Core.Tests.Production.ExtractionFixture;
using static Train.Core.Tests.Production.ProductionFixture;

namespace Train.Core.Tests.Production;

[TestClass]
public sealed class ExtractionTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Recovered_or_regenerated_reserves_only_start_batches_on_the_next_step(bool renewable)
    {
        var input = WorldFixture.Input with { Configuration = WorldFixture.Input.Configuration with
        {
            Resources = [new(WorldFixture.Material, [WorldFixture.Plain], 1, 2, 2,
                renewable, 120, 1d / 60, 1)]
        } };
        using var candidate = WorldFixture.Prepare(input, ExtractionFixture.Content());
        using var original = (CoreSession)candidate.Activate();
        var state = original.State;
        var node = state.World.Nodes.Single();
        using var session = new CoreSession(state with { World = state.World with { Nodes =
            [node with { Resources = renewable ? [node.Resources.Single() with { Quantity = 0 }] : [] }] } });
        var replenished = Step(session);
        Assert.IsNull(ExtractionFixture.Read(session).Batch);
        Assert.AreEqual(2L, Resources(session).Single().Quantity);
        Assert.AreEqual(0L, Stock(session, Input));
        Assert.HasCount(1, replenished.Events.Where(e => renewable
            ? e.Event is CoreEvent.ResourceRecovered : e.Event is CoreEvent.ResourceRegenerated));
        Step(session);
        Assert.AreEqual(0L, Resources(session).Sum(r => r.Quantity));
        Assert.AreEqual(0d, ExtractionFixture.Read(session).Batch!.ProgressWork);
        Assert.AreEqual(new GoodsQuantity(Input, 2), ExtractionFixture.Read(session).Batch!.FrozenOutputs.Single());
    }

    [TestMethod]
    public void Exhaustion_precedes_recovery_in_the_same_step_and_accounts_for_the_recovered_quantity()
    {
        var input = WorldFixture.Input with { Configuration = WorldFixture.Input.Configuration with
        { Resources = [new(WorldFixture.Material, [WorldFixture.Plain], 1, 3, 3, true, 60, 1000, 0)] } };
        using var candidate = WorldFixture.Prepare(input, ExtractionFixture.Content());
        using var original = (CoreSession)candidate.Activate();
        var state = original.State;
        var node = state.World.Nodes.Single();
        using var session = new CoreSession(state with { World = state.World with { Nodes =
            [node with { Resources = [node.Resources.Single() with { Quantity = 2 }] }] } });
        var events = Step(session).Events.Select(e => e.Event)
            .Where(e => e is CoreEvent.ResourceExhausted or CoreEvent.ResourceRecovered).ToArray();
        CollectionAssert.AreEqual(new CoreEvent[]
        {
            new CoreEvent.ResourceExhausted(node.Id, Input, true),
            new CoreEvent.ResourceRecovered(node.Id, Input, 1)
        }, events);
        var recovered = events.OfType<CoreEvent.ResourceRecovered>().Sum(e => e.Quantity);
        var facility = ExtractionFixture.Read(session);
        Assert.AreEqual(2L + recovered - Resources(session).Single().Quantity,
            Stock(session, Input) + facility.OutputBuffer.Sum(g => g.Quantity)
                + facility.Batch!.FrozenOutputs.Sum(g => g.Quantity));
        Assert.AreEqual(1L, Resources(session).Single().Quantity);
    }

    [TestMethod]
    public void A_resource_depleted_on_the_check_step_is_not_a_candidate_until_the_next_check()
    {
        var input = WorldFixture.Input with { Configuration = WorldFixture.Input.Configuration with
        { Resources = [new(WorldFixture.Material, [WorldFixture.Plain], 1, 4, 4, false, 0, 1d / 60, 1)] } };
        using var candidate = WorldFixture.Prepare(input, ExtractionFixture.Content());
        using var original = (CoreSession)candidate.Activate();
        var state = original.State;
        var node = state.World.Nodes.Single();
        using var session = new CoreSession(state with { World = state.World with { Nodes =
            [node with { Resources = [node.Resources.Single() with { Quantity = 2 }] }] } });
        var depleted = Step(session);
        Assert.IsEmpty(Resources(session));
        Assert.HasCount(1, depleted.Events.Where(e => e.Event is CoreEvent.ResourceExhausted));
        Assert.IsEmpty(depleted.Events.Where(e => e.Event is CoreEvent.ResourceRegenerated));
        var regenerated = Step(session).Events.Select(e => e.Event).OfType<CoreEvent.ResourceRegenerated>().Single();
        Assert.AreEqual(4L, regenerated.Quantity);
        Assert.AreEqual(4L, Resources(session).Single().Quantity);
        Assert.IsNull(ExtractionFixture.Read(session).Batch);
        Step(session);
        Assert.AreEqual(2L, Resources(session).Single().Quantity);
        Assert.IsNotNull(ExtractionFixture.Read(session).Batch);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void One_building_runs_both_plans_with_shared_power_or_labor(bool laborLimited)
    {
        var content = ExtractionFixture.Content(dual: true, work: 100,
            powerPerWork: laborLimited ? 0 : 1, laborPerWork: laborLimited ? 1 : 0);
        content = content with { PopulationRules = [content.PopulationRules[0] with { LaborRatio = .5 }] };
        using var candidate = ExtractionFixture.Prepare(content);
        using var session = candidate.Activate();
        Step(session);
        Assert.IsNotNull(ProductionFixture.Read(session).Batch);
        Assert.IsNotNull(ExtractionFixture.Read(session).Batch);
        Step(session);
        var facilities = new[] { ProductionFixture.Read(session), ExtractionFixture.Read(session) };
        Assert.AreEqual(laborLimited ? 5d : 60d, facilities.Sum(f => f.ActualWorkPerSecond), 1e-12);
        Assert.IsTrue(facilities.Any(f => f.Limits.Contains(laborLimited ? WorkLimit.Labor : WorkLimit.Power)));
        AssertConserved(session, 6);
    }

    [TestMethod]
    public void Both_plans_share_output_capacity_without_losing_buffered_goods()
    {
        using var candidate = ExtractionFixture.Prepare(ExtractionFixture.Content(dual: true, capacity: 1));
        using var session = candidate.Activate();
        Step(session, 2);
        var facilities = new[] { ProductionFixture.Read(session), ExtractionFixture.Read(session) };
        Assert.IsTrue(facilities.All(f => f.Batch is null));
        Assert.AreEqual(1L, ProductionFixture.ReadInventory(session).Used);
        Assert.AreEqual(4L, facilities.Sum(f => f.OutputBuffer.Sum(g => g.Quantity)));
        Step(session, 4);
        Assert.AreEqual(4L, Resources(session).Single().Quantity);
        AssertConserved(session, 6);
    }

    [TestMethod]
    public void Starting_consumes_full_reserve_once_and_work_and_output_follow_on_the_next_step()
    {
        using var candidate = ExtractionFixture.Prepare();
        using var session = candidate.Activate();
        Step(session);
        Assert.AreEqual(4L, Resources(session).Single().Quantity);
        Assert.AreEqual(0d, ExtractionFixture.Read(session).Batch!.ProgressWork);
        Assert.AreEqual(0L, Stock(session, Input));
        AssertConserved(session, 6);
        Step(session);
        Assert.IsNull(ExtractionFixture.Read(session).Batch);
        Assert.AreEqual(2L, Stock(session, Input));
        Assert.AreEqual(4L, Resources(session).Single().Quantity);
        AssertConserved(session, 6);
    }

    [TestMethod]
    public void Insufficient_reserve_does_not_start_or_consume_a_partial_batch()
    {
        using var candidate = ExtractionFixture.Prepare(reserve: 1);
        using var session = candidate.Activate();
        Step(session, 4);
        Assert.AreEqual(ProductionState.WaitingForInputs, ExtractionFixture.Read(session).State);
        Assert.IsNull(ExtractionFixture.Read(session).Batch);
        Assert.AreEqual(1L, Resources(session).Single().Quantity);
        AssertConserved(session, 1);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Exhaustion_reports_once_and_only_renewable_entries_remain(bool renewable)
    {
        using var candidate = ExtractionFixture.Prepare(reserve: 2, renewable: renewable);
        using var session = candidate.Activate();
        var exhausted = Step(session).Events.Select(e => e.Event).OfType<CoreEvent.ResourceExhausted>().Single();
        Assert.AreEqual(Input, exhausted.Good);
        Assert.AreEqual(renewable, exhausted.Renewable);
        Assert.AreEqual(renewable ? 1 : 0, Resources(session).Length);
        Assert.IsTrue(Resources(session).All(r => r.Quantity == 0));
        AssertConserved(session, 2);
        Assert.IsFalse(Step(session, 4).Events.Any(e => e.Event is CoreEvent.ResourceExhausted));
        Assert.AreEqual(2L, Stock(session, Input));
        Assert.AreEqual(ProductionState.WaitingForInputs, ExtractionFixture.Read(session).State);
        AssertConserved(session, 2);
    }

    [TestMethod]
    public void Blocked_output_retains_goods_without_starting_or_consuming_again()
    {
        using var candidate = ExtractionFixture.Prepare(ExtractionFixture.Content(capacity: 1));
        using var session = candidate.Activate();
        Step(session, 2);
        Assert.AreEqual(ProductionState.OutputBlocked, ExtractionFixture.Read(session).State);
        Assert.AreEqual(1L, Stock(session, Input));
        Assert.AreEqual(new GoodsQuantity(Input, 1), ExtractionFixture.Read(session).OutputBuffer.Single());
        Step(session, 5);
        Assert.AreEqual(4L, Resources(session).Single().Quantity);
        AssertConserved(session, 6);
    }

    [TestMethod]
    public void Two_facilities_compete_for_the_same_reserve_by_priority()
    {
        var content = ExtractionFixture.Content();
        var template = content.SettlementTemplates[0];
        content = content with { SettlementTemplates = [template with
        {
            Plots = [..template.Plots, new(3, PlotSize.Small, WorldFixture.FactoryBuilding)],
            ProductionPlans = [template.ProductionPlans[0] with { Priority = 1 },
                new(3, CapabilityKind.Extraction, ExtractionFixture.Process, true, 0)]
        }] };
        using var candidate = ExtractionFixture.Prepare(content, reserve: 2);
        using var session = candidate.Activate();
        Step(session);
        Assert.IsNull(ExtractionFixture.Read(session).Batch);
        Assert.IsNotNull(ExtractionFixture.Read(session, new(new(3), CapabilityKind.Extraction)).Batch);
        AssertConserved(session, 2);
        Step(session, 3);
        Assert.AreEqual(2L, Stock(session, Input));
        AssertConserved(session, 2);
    }

    [TestMethod]
    [DataRow("terrain")]
    [DataRow("tag")]
    [DataRow("capability")]
    public void Unmet_conditions_do_not_consume_reserve(string condition)
    {
        var content = ExtractionFixture.Content();
        if (condition == "capability") content = content with { Designs = content.Designs.Select(d =>
            d.Design != WorldFixture.FactoryBuilding ? d : d with { Parts = d.Parts.Select(p => p with
            { Capabilities = [new PartCapability.Production(60)] }).ToImmutableArray() }).ToImmutableArray() };
        else content = content with { Processes = content.Processes.Select(p => p.Details is not ProcessDetails.Extraction ? p
            : condition == "terrain" ? p with { AllowedTerrainTypes = [WorldFixture.Hills] }
            : p with { RequiredFacilityTag = FacilityTag.Assembly }).ToImmutableArray() };
        using var candidate = ExtractionFixture.Prepare(content);
        using var session = candidate.Activate();
        Step(session, 3);
        Assert.AreEqual(condition == "capability" ? ProductionState.ZeroCapability : ProductionState.ConditionsNotMet,
            ExtractionFixture.Read(session).State);
        Assert.IsNull(ExtractionFixture.Read(session).Batch);
        AssertConserved(session, 6);
    }

    [TestMethod]
    public void Publication_keeps_the_active_extraction_batch_frozen()
    {
        var content = ExtractionFixture.Content(work: 2);
        using var candidate = ExtractionFixture.Prepare(content);
        using var session = candidate.Activate();
        Step(session);
        Publish(session, Changed(content) with { Processes = content.Processes.Select(p =>
            p.Details is not ProcessDetails.Extraction ? p : p with { Revision = new(new string('A', 64)), WorkRequired = 1,
                Details = new ProcessDetails.Extraction(new("fixture:extraction"), new(Input, 3)) }).ToImmutableArray() });
        Assert.AreEqual(2L, ExtractionFixture.Read(session).Batch!.FrozenOutputs.Single().Quantity);
        Assert.IsTrue(ExtractionFixture.Read(session).Batch!.CurrentSpecificationDiffers);
        Step(session, 2);
        Assert.AreEqual(2L, Stock(session, Input));
        AssertConserved(session, 6);
        Step(session);
        Assert.AreEqual(1L, Resources(session).Single().Quantity);
        Assert.AreEqual(3L, ExtractionFixture.Read(session).Batch!.FrozenOutputs.Single().Quantity);
        AssertConserved(session, 6);
    }
}
