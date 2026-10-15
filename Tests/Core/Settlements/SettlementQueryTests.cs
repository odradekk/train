using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.Sessions;
using Train.Core.Tests.Inventory;
using Train.Core.Tests.Production;
using Train.Core.Tests.World;

namespace Train.Core.Tests.Settlements;

[TestClass]
public sealed class SettlementQueryTests
{
    [TestMethod]
    public void Settlement_details_preserve_generated_identity_plots_and_template_copy()
    {
        using var candidate = WorldFixture.Prepare(WorldFixture.MultiInput, WorldFixture.MultiContent);
        using var session = (CoreSession)candidate.Activate();
        foreach (var expected in session.State.World.Settlements)
        {
            var actual = Find(session.Read(), expected.Id);
            Assert.AreEqual(expected.Id, actual.Id);
            Assert.AreEqual(expected.Node, actual.Node);
            Assert.AreEqual(expected.Type, actual.Type);
            Assert.AreEqual(expected.Template.Id, actual.SourceTemplateId);
            Assert.AreEqual(expected.Relation, actual.Relation);
            Assert.AreEqual(expected.Inventory, actual.Inventory);
            CollectionAssert.AreEqual(expected.ParkedVehicles.ToArray(), actual.ParkedVehicles.ToArray());
            Assert.AreEqual(expected.Plots.Length, actual.Plots.Length);
            foreach (var plot in expected.Plots)
            {
                var detail = actual.Plots.Single(p => p.Id == plot.Id);
                Assert.AreEqual(plot.TemplateNumber, detail.TemplatePlotNumber);
                Assert.AreEqual(plot.Size, detail.Size);
                if (plot.Building is { } building)
                    Assert.AreEqual(building, ((PlotOccupant.Building)detail.Occupant).Id);
                else Assert.IsInstanceOfType<PlotOccupant.Empty>(detail.Occupant);
            }
        }
        Assert.IsInstanceOfType<Lookup<SettlementView>.Missing>(session.Read().FindSettlement(new(ulong.MaxValue)));
        Assert.ThrowsExactly<ArgumentException>(() => session.Read().FindSettlement(new(0)));
    }

    [TestMethod]
    public void Lease_queries_partition_storage_and_old_views_keep_the_original_wallet_and_capacity()
    {
        using var session = InventoryFixture.Session(InventoryFixture.Content(localStock: 10));
        var id = session.State.World.Settlements.Single().Id;
        var retained = session.Read();
        var before = Find(retained, id);
        Assert.AreEqual(1, before.Platforms);
        Assert.AreEqual(0, before.OccupiedPlatforms);
        Assert.AreEqual(new Money(1000), retained.Economy.Wallet);
        Assert.AreEqual(new SettlementStorageView(100, 10, 0, 0, 0, 100, 90), before.Market.Storage);
        var lease = InventoryFixture.Rent(session);
        var current = Find(session.Read(), id);
        CollectionAssert.AreEqual(new[] { lease.Id }, current.Leases.ToArray());
        Assert.AreNotEqual(current.Inventory, lease.Inventory);
        Assert.AreEqual(new SettlementStorageView(100, 10, 0, 20, 0, 80, 70), current.Market.Storage);
        Assert.AreEqual(new Money(1000 - lease.Paid.Value), session.Read().Economy.Wallet);
        Assert.AreEqual(new Money(1000), retained.Economy.Wallet);
        Assert.IsEmpty(Find(retained, id).Leases);
        Assert.AreEqual(before.Market.Storage, Find(retained, id).Market.Storage);
        InventoryFixture.Service(session);
        Assert.AreEqual(1, Find(session.Read(), id).OccupiedPlatforms);
        Assert.AreEqual(0, before.OccupiedPlatforms);
    }

    [TestMethod]
    public void Production_power_and_labor_report_the_completed_step()
    {
        var content = ProductionFixture.Content([new(ProductionFixture.Input, 2), new(ProductionFixture.Fuel, 1)],
            work: 1, rate: 60, powerPerWork: 2, laborPerWork: 2);
        using var candidate = ProductionFixture.Prepare(content);
        using var session = candidate.Activate();
        var before = Find(session.Read(), new(1));
        ProductionFixture.Step(session, 2);
        var actual = Find(session.Read(), new(1));
        // Ten residents provide five labor; two labor and two power are required per unit of work.
        Assert.AreEqual(60d, actual.Power.GenerationCapacity);
        Assert.AreEqual(120d, actual.Power.Demand);
        Assert.AreEqual(5d, actual.Power.ActualSupply, 1e-12);
        Assert.AreEqual(1 - 5d / 60, actual.Power.FuelEnergyRemaining, 1e-12);
        Assert.AreEqual(5d, actual.Population.AvailableLabor);
        Assert.AreEqual(5d, actual.Population.AllocatedLabor);
        Assert.AreEqual(0d, actual.Population.IdleLabor);
        Assert.AreEqual(2.5, actual.Facilities.Single().ActualWorkPerSecond);
        Assert.AreEqual(0d, before.Population.AllocatedLabor);
        Assert.AreEqual(0d, before.Power.ActualSupply);
        var lowerLabor = ProductionFixture.Changed(content) with
        {
            PopulationRules = [content.PopulationRules[0] with { LaborRatio = .1 }]
        };
        ProductionFixture.Publish(session, lowerLabor);
        var updated = Find(session.Read(), new(1)).Population;
        Assert.AreEqual(1d, updated.AvailableLabor);
        Assert.AreEqual(5d, updated.AllocatedLabor);
        Assert.AreEqual(0d, updated.IdleLabor);
    }

    [TestMethod]
    public void Production_and_extraction_on_one_building_both_contribute_to_settlement_resource_totals()
    {
        var content = ExtractionFixture.Content(dual: true, work: 100, powerPerWork: 2, laborPerWork: 1);
        content = content with
        {
            Designs = content.Designs.Select(design => design.Design != WorldFixture.FactoryBuilding ? design : design with
            {
                Parts = design.Parts.Select(part => part with
                { Capabilities = [new PartCapability.Production(2), new PartCapability.Extraction(60)] }).ToImmutableArray()
            }).ToImmutableArray()
        };
        using var candidate = ExtractionFixture.Prepare(content);
        using var session = candidate.Activate();
        ProductionFixture.Step(session, 2);
        var actual = Find(session.Read(), ProductionFixture.Settlement);
        CollectionAssert.AreEquivalent(new[] { ProductionFixture.Facility, ExtractionFixture.Facility },
            actual.Facilities.Select(f => f.Key).ToArray());
        // Five labor supplies production's two work/s first and extraction's remaining three work/s.
        Assert.AreEqual(2d, actual.Facilities.Single(f => f.Key == ProductionFixture.Facility).ActualWorkPerSecond);
        Assert.AreEqual(3d, actual.Facilities.Single(f => f.Key == ExtractionFixture.Facility).ActualWorkPerSecond);
        Assert.AreEqual(60d, actual.Power.GenerationCapacity);
        Assert.AreEqual(124d, actual.Power.Demand);
        Assert.AreEqual(10d, actual.Power.ActualSupply);
        Assert.AreEqual(1 - 10d / 60, actual.Power.FuelEnergyRemaining, 1e-12);
        Assert.AreEqual(5d, actual.Population.AvailableLabor);
        Assert.AreEqual(5d, actual.Population.AllocatedLabor);
        Assert.AreEqual(0d, actual.Population.IdleLabor);
    }

    [TestMethod]
    public void Completed_batch_still_reports_the_labor_actually_allocated_in_its_last_step()
    {
        using var candidate = ProductionFixture.Prepare(ProductionFixture.Content(work: 1, laborPerWork: .1));
        using var session = candidate.Activate();
        // Opening the batch takes one step; five labor allows 50 work/s, leaving ten work/s for its final step.
        ProductionFixture.Step(session, 3);
        var actual = Find(session.Read(), new(1));
        Assert.IsNull(actual.Facilities.Single().Batch);
        Assert.AreEqual(10d, actual.Facilities.Single().ActualWorkPerSecond, 1e-10);
        Assert.AreEqual(1d, actual.Population.AllocatedLabor, 1e-10);
        Assert.AreEqual(4d, actual.Population.IdleLabor, 1e-10);
    }

    [TestMethod]
    public void Economy_reports_current_selected_rules_and_missing_publications()
    {
        using var session = PopulationFixture.Start(WorldFixture.Content);
        var retained = session.Read();
        var economy = retained.Economy;
        foreach (var status in new[] { economy.MarketRule, economy.PopulationRule, economy.ConstructionRule })
        {
            Assert.AreEqual(WorldFixture.Revision, status.PublishedRevision);
            Assert.IsEmpty(status.Problems);
        }
        Assert.AreEqual(WorldFixture.Rules.Market, economy.MarketRule.Rule);
        Assert.AreEqual(WorldFixture.Rules.Population, economy.PopulationRule.Rule);
        Assert.AreEqual(WorldFixture.Rules.Construction, economy.ConstructionRule.Rule);
        var missing = WorldFixture.Content with
        {
            Identity = PopulationFixture.Identity('A'), MarketRules = [], PopulationRules = [], ConstructionRules = [],
            MissingReferences = new[] { WorldFixture.Rules.Market, WorldFixture.Rules.Population, WorldFixture.Rules.Construction }
                .Select(rule => new MissingCoreReference(new CoreDefinitionReference.Rule(rule), "fixture")).ToImmutableArray()
        };
        using var candidate = WorldFixture.Success(CoreFactory.Create().PrepareRestore(PopulationFixture.Capture(session), missing, WorldFixture.Options));
        using var restored = candidate.Activate();
        var unavailable = restored.Read().Economy;
        foreach (var status in new[] { unavailable.MarketRule, unavailable.PopulationRule, unavailable.ConstructionRule })
        {
            Assert.IsNull(status.PublishedRevision);
            Assert.IsTrue(status.Problems.Any(p => p.Code == "content_missing"));
        }
        Assert.AreEqual(WorldFixture.Revision, retained.Economy.PopulationRule.PublishedRevision);
    }

    [TestMethod]
    public void Queries_between_identical_commands_and_steps_do_not_change_events_revision_or_checkpoint()
    {
        var content = ProductionFixture.Content([new(ProductionFixture.Input, 20), new(ProductionFixture.Fuel, 10)],
            work: 1, powerPerWork: 1, laborPerWork: 1);
        using var leftCandidate = ProductionFixture.Prepare(content);
        using var rightCandidate = ProductionFixture.Prepare(content);
        using var queried = leftCandidate.Activate();
        using var control = rightCandidate.Activate();
        var changed = ProductionFixture.Changed(content);
        var completed = false;
        foreach (var step in Enumerable.Range(0, 16))
        {
            _ = Find(queried.Read(), new(1));
            _ = queried.Read().Economy;
            if (step == 2)
            {
                var left = ProductionFixture.Publish(queried, changed);
                var right = ProductionFixture.Publish(control, changed);
                CollectionAssert.AreEqual(left.Events.Select(Event).ToArray(), right.Events.Select(Event).ToArray());
            }
            var actual = ProductionFixture.Step(queried);
            var expected = ProductionFixture.Step(control);
            CollectionAssert.AreEqual(expected.Events.Select(Event).ToArray(), actual.Events.Select(Event).ToArray());
            completed |= actual.Events.Any(e => e.Event is CoreEvent.ProductionCompleted);
            Assert.AreEqual(control.Read().Revision, queried.Read().Revision);
            Assert.AreEqual(PopulationFixture.Capture(control).Checkpoint, PopulationFixture.Capture(queried).Checkpoint);
        }
        Assert.IsTrue(completed);
    }

    private static string Event(EventEnvelope envelope) => JsonSerializer.Serialize(new
    { envelope.Position, envelope.Ordinal, Kind = envelope.Event.GetType().Name, Value = JsonSerializer.Serialize(envelope.Event, envelope.Event.GetType()) });

    internal static SettlementView Find(ICoreReadView view, SettlementId id)
        => ((Lookup<SettlementView>.Found)view.FindSettlement(id)).Value;
}
