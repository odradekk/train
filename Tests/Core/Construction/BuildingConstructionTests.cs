using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.Sessions;
using Train.Core.Tests.World;
using Train.Core.World;
using static Train.Core.Tests.Construction.ConstructionFixture;

namespace Train.Core.Tests.Construction;

[TestClass]
public sealed class BuildingConstructionTests
{
    [TestMethod]
    public void Build_consumes_the_bill_once_and_marks_the_plot_as_a_site()
    {
        var content = WithPlot(Content(), PlotSize.Small);
        using var session = Session(content);
        var plot = Empty(session);
        var design = Building(content, PlotSize.Small, BuildingKind.Production, '9');
        var job = Start(session, BuildOn(plot, design));
        Assert.AreEqual(2L, job.FrozenTotalWork);
        CollectionAssert.AreEqual(new[] { new GoodsQuantity(Material, 2) }, job.InvestedGoods.ToArray());
        Assert.AreEqual(new ConstructionTarget.NewBuilding(plot, design.Design.Design), job.Target);
        Assert.AreEqual(8L, Stock(session, Local));
        Assert.IsInstanceOfType<PlotOccupant.Construction>(Plot(session, plot).Occupant);
        Assert.AreEqual(job.Id, ((PlotOccupant.Construction)Plot(session, plot).Occupant).Id);
        var settlement = ((Lookup<SettlementView>.Found)session.Read().FindSettlement(session.State.World.Settlements.Single().Id)).Value;
        CollectionAssert.Contains(settlement.OwnedJobs.ToArray(), job.Id);

        Reject(session, BuildOn(plot, design), "Plot.ConstructionJob", "busy");
        Assert.AreEqual(8L, Stock(session, Local));
        var stored = session.State.World.Designs.Single(d => d.Design == design.Design.Design);
        var updated = Changed(content) with
        {
            ConstructionRules = [content.ConstructionRules[0] with { ConstructionWorkPerItem = 100 }],
            Designs = content.Designs.Add(stored)
        };
        Accept(session, new CoreCommand.PublishContent(updated));
        Assert.AreEqual(2L, Job(session.Read(), job.Id).FrozenTotalWork);
    }

    [TestMethod]
    public void Rejected_plot_requests_do_not_consume_or_occupy()
    {
        var content = BuildingLimit(WithPlot(Content(), PlotSize.Small), PlotSize.Giant);
        using var session = Session(content);
        var plot = Empty(session);
        Reject(session, BuildOn(new(999), Building(content, PlotSize.Small, BuildingKind.Production, '1')), "Plot", "object_missing");
        Reject(session, BuildOn(Occupied(session), Building(content, PlotSize.Small, BuildingKind.Production, '2')), "Plot", "plot_occupied");
        Reject(session, BuildOn(plot, Design(content)), "Design", "invalid_design");
        Reject(session, BuildOn(plot, Building(content, PlotSize.Medium, BuildingKind.Production, '3')), "Plot", "conditions_not_met");
        Assert.AreEqual(10L, Stock(session, Local));
        Assert.IsTrue(session.State.World.ConstructionJobs.IsEmpty);
        Assert.IsInstanceOfType<PlotOccupant.Empty>(Plot(session, plot).Occupant);

        var stationContent = WithPlot(Content(), PlotSize.SmallStation);
        using var station = Session(stationContent);
        var stationPlot = Empty(station);
        Reject(station, BuildOn(stationPlot, Building(stationContent, PlotSize.SmallStation, BuildingKind.Station, '4', [new PartCapability.Platform()])),
            "Facility", "conditions_not_met");
        Assert.AreEqual(10L, Stock(station, Local));
        Assert.IsInstanceOfType<PlotOccupant.Empty>(Plot(station, stationPlot).Occupant);
    }

    [TestMethod]
    public void Completed_building_is_ordered_before_vehicles_and_opens_a_planned_facility()
    {
        var content = WithPlot(Content(), PlotSize.Small) with
        {
            Processes = [new(new ProcessDetails.Production(new("fixture:build"), [new(Material, 1)], [new(Material, 1)]),
                WorldFixture.Revision, 1, 0, 0, FacilityTag.Agriculture, [WorldFixture.Plain])]
        };
        using var unplanned = Session(content);
        var settlement = unplanned.State.World.Settlements.Single();
        var plot = settlement.Plots.Single(p => p.Building is null).Id;
        var planned = settlement with
        {
            Template = settlement.Template with
            {
                ProductionPlans = settlement.Template.ProductionPlans.Add(
                    new(settlement.Plots.Single(p => p.Id == plot).TemplateNumber, CapabilityKind.Production,
                        new ProcessRef.Production(new("fixture:build")), false, 0))
            }
        };
        using var session = new CoreSession(unplanned.State with
            { World = unplanned.State.World with { Settlements = [planned] } });
        var design = Building(content, PlotSize.Small, BuildingKind.Production, '9');
        var started = Start(session, BuildOn(plot, design));
        var completed = Step(session, 2).Events.Select(e => e.Event).OfType<CoreEvent.ConstructionCompleted>().Single();
        Assert.AreEqual(started.Id, completed.Job);
        Assert.IsInstanceOfType<Lookup<JobView>.Missing>(session.Read().FindJob(started.Id));
        var created = (InstanceId.Building)completed.CreatedInstance!;
        var world = session.State.World;
        var instance = world.Instances.Single(i => i.Id == created);
        var site = world.Settlements.Single().Plots.Single(p => p.Id == plot);
        Assert.AreEqual(created.Id, site.Building);
        Assert.AreEqual(new InstanceDetails.Building(plot, site.Size), instance.Details);
        Assert.AreEqual(design.Design.MaxStructureDurability, instance.StructureDurability);
        Assert.IsTrue(instance.Parts.All(p => p.Current == design.Design.Parts.Single(part => part.Id == p.Id).MaxDurability));
        Assert.IsInstanceOfType<PlotOccupant.Building>(Plot(session, plot).Occupant);
        var ids = world.Instances.Select(i => i.Id).ToArray();
        var buildings = ids.TakeWhile(i => i is InstanceId.Building).OfType<InstanceId.Building>().Select(i => i.Id.Value).ToArray();
        Assert.AreEqual(ids.OfType<InstanceId.Building>().Count(), buildings.Length);
        CollectionAssert.AreEqual(buildings.OrderBy(id => id).ToArray(), buildings);
        var facility = world.Settlements.Single().ProductionFacilities.Single();
        Assert.AreEqual(new FacilityKey(created.Id, CapabilityKind.Production), facility.Facility);
        Assert.IsNull(facility.Batch);
        Assert.IsTrue(facility.OutputBuffer.IsEmpty);
        RoundTrip(session, content);
    }

    [TestMethod]
    public void Destroyed_facility_fails_the_site_without_refund()
    {
        var content = WithPlot(Content(), PlotSize.Small);
        using var initial = Session(content);
        var plot = Empty(initial);
        Start(initial, BuildOn(plot, Building(content, PlotSize.Small, BuildingKind.Production, '9')));
        var count = initial.State.World.Instances.Length;
        using var destroyed = new CoreSession(initial.State with { World = initial.State.World with
        {
            Instances = initial.State.World.Instances.Select(i => i.Id != new InstanceId.Building(Facility.Building) ? i : i with
                { StructureDurability = 0 }).ToImmutableArray()
        } });
        var changed = Step(destroyed).Events.Select(e => e.Event).OfType<CoreEvent.ConstructionStateChanged>().Single();
        Assert.AreEqual(ConstructionState.Failed, changed.After);
        Assert.AreEqual("instance_destroyed", changed.Reasons.Single().Code);
        Assert.AreEqual("Facility", changed.Reasons.Single().Field);
        Assert.IsFalse(destroyed.State.World.ConstructionJobs.Any());
        Assert.AreEqual(count, destroyed.State.World.Instances.Length);
        Assert.AreEqual(8L, Stock(destroyed, Local));
        Assert.IsInstanceOfType<PlotOccupant.Empty>(Plot(destroyed, plot).Occupant);
    }

    [TestMethod]
    public void Station_completion_recounts_platforms_and_restores()
    {
        var content = BuildingLimit(WithPlot(Content(), PlotSize.SmallStation), PlotSize.Giant);
        using var session = Session(content);
        var before = session.State.World.Nodes.Single().Platforms;
        var plot = Empty(session);
        Start(session, BuildOn(plot, Building(content, PlotSize.SmallStation, BuildingKind.Station, '8', [new PartCapability.Platform()])));
        Step(session, 2);
        var world = session.State.World;
        var settlement = world.Settlements.Single();
        var platforms = WorldState.CountPlatforms(settlement, world.Instances.ToDictionary(i => i.Id), world.Designs.ToDictionary(d => d.Design));
        Assert.AreEqual(before + 1, platforms);
        Assert.AreEqual(platforms, world.Nodes.Single(n => n.Id == settlement.Node).Platforms);
        RoundTrip(session, content);
    }

    [TestMethod]
    public void Restored_building_job_finishes_like_an_uninterrupted_run()
    {
        var content = WithPlot(Content(), PlotSize.Small);
        using var continuous = Session(content);
        var plot = Empty(continuous);
        var design = Building(content, PlotSize.Small, BuildingKind.Production, '9');
        var job = Start(continuous, BuildOn(plot, design));
        Step(continuous);
        var saved = WorldFixture.Success(continuous.Capture(CapturePurpose.Save));
        Step(continuous);
        using var candidate = WorldFixture.Success(CoreFactory.Create().PrepareRestore(
            WorldFixture.Success(CoreFactory.Create().DecodeState(saved.Encode())), content, WorldFixture.Options));
        using var restored = (CoreSession)candidate.Activate();
        Assert.AreEqual(1d, Job(restored.Read(), job.Id).ProgressWork, 1e-12);
        Step(restored);
        Assert.IsInstanceOfType<Lookup<JobView>.Missing>(restored.Read().FindJob(job.Id));
        Assert.IsTrue(SameInstances(continuous.State.World.Instances, restored.State.World.Instances));
        Assert.IsTrue(continuous.State.World.Settlements.Single().Plots.SequenceEqual(restored.State.World.Settlements.Single().Plots));
        Assert.IsTrue(restored.State.World.Instances.Any(i => i.Design == design.Design.Design));
        RoundTrip(restored, content);
    }

    [TestMethod]
    public void Repair_locks_production_and_restores_only_defined_parts()
    {
        var (session, content, building, design) = BuildProductionBuilding();
        using (session)
        {
            Assert.AreEqual(ProductionState.Disabled, FacilityState(session, building));
            using var missing = MarkPartMissing(session, design.Design.Design, 2);
            var settlement = Settlement(missing);
            var power = settlement.Power.GenerationCapacity;
            var storage = settlement.Market.Storage.PhysicalCapacity;
            var platforms = settlement.Platforms;
            Assert.AreEqual(10d, power, 1e-12);
            using var damaged = DamageParts(missing, new InstanceId.Building(building));
            var repair = Accept(damaged, RepairBuilding(building));
            var job = repair.Events.Select(e => e.Event).OfType<CoreEvent.ConstructionStarted>().Single().Job;
            var locked = repair.Events.Select(e => e.Event).OfType<CoreEvent.ProductionStateChanged>().Single();
            Assert.AreEqual(ProductionState.Disabled, locked.Before);
            Assert.AreEqual(ProductionState.Locked, locked.After);
            Assert.AreEqual(ProductionState.Locked, FacilityState(damaged, building));
            var during = Settlement(damaged);
            Assert.AreEqual(0d, during.Power.GenerationCapacity, 1e-12);
            Assert.AreEqual(storage, during.Market.Storage.PhysicalCapacity);
            Assert.AreEqual(platforms, during.Platforms);
            Assert.AreEqual(1L, job.FrozenTotalWork);
            CollectionAssert.AreEqual(new[] { new GoodsQuantity(Material, 1) }, job.InvestedGoods.ToArray());
            Reject(damaged, new CoreCommand.StartOwnedJob(new JobRequest.BuildVehicle(
                new(building, CapabilityKind.Construction), Design(content), [Local], 0)), "Facility", "conditions_not_met");
            var finished = Step(damaged).Events.Select(e => e.Event).OfType<CoreEvent.ProductionStateChanged>().Single();
            Assert.AreEqual(ProductionState.Locked, finished.Before);
            Assert.AreEqual(ProductionState.Disabled, finished.After);
            Assert.AreEqual(power, Settlement(damaged).Power.GenerationCapacity, 1e-12);
            Assert.AreEqual(storage, Settlement(damaged).Market.Storage.PhysicalCapacity);
            var restored = damaged.State.World.Instances.Single(i => i.Id == new InstanceId.Building(building));
            Assert.AreEqual(design.Design.MaxStructureDurability, restored.StructureDurability);
            Assert.AreEqual(design.Design.Parts[0].MaxDurability, restored.Parts.Single(p => p.Id.Value == 0).Current);
            Assert.AreEqual(design.Design.Parts[0].MaxDurability, restored.Parts.Single(p => p.Id.Value == 1).Current);
            Assert.AreEqual(0L, restored.Parts.Single(p => p.Id.Value == 2).Current);
            RoundTrip(damaged, content);
        }
    }

    [TestMethod]
    public void Repair_rejects_self_executor_batch_buffer_and_a_building_outside_the_settlement()
    {
        var content = Content();
        using (var session = Session(content))
            Reject(session, RepairBuilding(Facility.Building), "Instance", "invalid_state");

        var (session2, _, building, _) = BuildProductionBuilding();
        using var host = session2;
        using var busy = new CoreSession(host.State);
        Start(busy, new CoreCommand.StartOwnedJob(new JobRequest.BuildVehicle(
            new(building, CapabilityKind.Construction), Design(host.State.Content), [Local], 0)));
        Reject(busy, RepairBuilding(building), "Instance.ConstructionJob", "invalid_state");

        using var batch = WithFacility(host, building, batch: true, buffer: false);
        Reject(batch, RepairBuilding(building), "Instance.Batch", "invalid_state");
        using var buffer = WithFacility(host, building, batch: false, buffer: true);
        Reject(buffer, RepairBuilding(building), "Instance.OutputBuffer", "invalid_state");
        using var unowned = new CoreSession(host.State with { World = host.State.World with
        {
            Instances = host.State.World.Instances.Select(i => i.Id != new InstanceId.Building(building) ? i : i with
                { Ownership = SettlementRelation.NonHostile }).ToImmutableArray()
        } });
        Reject(unowned, RepairBuilding(building), "Instance", "not_owned");
        using var destroyed = new CoreSession(host.State with { World = host.State.World with
        {
            Instances = host.State.World.Instances.Select(i => i.Id != new InstanceId.Building(building) ? i : i with
                { StructureDurability = 0 }).ToImmutableArray()
        } });
        Reject(destroyed, RepairBuilding(building), "Instance", "instance_destroyed");
        Reject(host, RepairBuilding(new(999)), "Instance", "object_missing");

        var orphanId = new InstanceId.Building(new(50));
        var station = host.State.World.Designs.Single(d => d.Design == WorldFixture.Station);
        using var orphan = new CoreSession(host.State with { World = host.State.World with
        {
            Instances = host.State.World.Instances.Add(WorldState.CreateInstance(orphanId, station,
                SettlementRelation.PlayerOwned, new InstanceDetails.Building(new(50), station.Kind is DesignKind.Building kind ? kind.PlotSize : PlotSize.Small)))
        } });
        Reject(orphan, RepairBuilding(orphanId.Id), "Instance", "conditions_not_met");
    }

    [TestMethod]
    public void Station_repair_recounts_platforms()
    {
        var (session, content, building, _) = BuildProductionBuilding();
        using (session)
        {
            using var damaged = new CoreSession(session.State with { World = session.State.World with
            {
                Instances = session.State.World.Instances.Select(i => i.Id != new InstanceId.Building(Facility.Building) ? i : i with
                    { Parts = i.Parts.Select(p => p with { Current = 0 }).ToImmutableArray() }).ToImmutableArray()
            } });
            Start(damaged, RepairBuilding(Facility.Building, new(building, CapabilityKind.Construction)));
            Step(damaged);
            var world = damaged.State.World;
            var settlement = world.Settlements.Single();
            Assert.AreEqual(WorldState.CountPlatforms(settlement, world.Instances.ToDictionary(i => i.Id), world.Designs.ToDictionary(d => d.Design)),
                world.Nodes.Single(n => n.Id == settlement.Node).Platforms);
            RoundTrip(damaged, content);
        }
    }

    [TestMethod]
    public void Refit_consumes_the_difference_and_scales_structure_from_retained_parts()
    {
        var (session, _, building, design) = BuildPlain();
        using (session)
        {
            var id = new InstanceId.Building(building);
            using var damaged = new CoreSession(session.State with { World = session.State.World with
            {
                Instances = session.State.World.Instances.Select(i => i.Id != id ? i : i with
                    { StructureDurability = 50, Parts = i.Parts.Select(p => p with { Current = 40 }).ToImmutableArray() }).ToImmutableArray()
            } });
            var before = Instance(damaged, id);
            var part = design.Design.Parts[0];
            var target = design with { Design = design.Design with
            {
                Design = new(new string('8', 64)), Composition = new(new string('7', 64)), MaxStructureDurability = 201,
                Parts = [part with { Id = new(1) }, part with { Id = new(0), Capabilities = [] }]
            } };
            var change = new DesignChangeProjection(before.Design, before.Composition, target, [new(new(0), new(1))], [new(Material, 1)], [], 1);
            Assert.AreEqual(8L, Stock(damaged, Local));
            var job = Start(damaged, RefitBuilding(building, change));
            Assert.AreEqual(7L, Stock(damaged, Local));
            Assert.AreEqual(1L, job.FrozenTotalWork);
            Assert.AreEqual(InstanceLock.UnderRefit, Instance(damaged, id).Lock);
            Step(damaged);
            var after = damaged.State.World.Instances.Single(i => i.Id == id);
            Assert.AreEqual(target.Design.Design, after.Design);
            Assert.AreEqual(101L, after.StructureDurability);
            Assert.AreEqual(40L, after.Parts.Single(p => p.Id.Value == 1).Current);
            Assert.AreEqual(part.MaxDurability, after.Parts.Single(p => p.Id.Value == 0).Current);
            Assert.AreEqual(before.Details, Instance(damaged, id).Details);
            Assert.AreEqual(InstanceLock.None, Instance(damaged, id).Lock);
            Assert.AreEqual(7L, Stock(damaged, Local));
        }
    }

    [TestMethod]
    public void Refit_rounds_returns_down_and_caps_them_at_the_opening_reservation()
    {
        var (session, content, building, design) = BuildPlain();
        using (session)
        {
            var id = new InstanceId.Building(building);
            using var damaged = new CoreSession(session.State with { World = session.State.World with
            {
                Instances = session.State.World.Instances.Select(i => i.Id != id ? i : i with
                    { StructureDurability = 50, Parts = i.Parts.Select(p => p with { Current = 40 }).ToImmutableArray() }).ToImmutableArray()
            } });
            var before = Instance(damaged, id);
            var target = design with { Design = design.Design with
            {
                Design = new(new string('8', 64)), Composition = new(new string('7', 64)), Parts = []
            } };
            var change = new DesignChangeProjection(before.Design, before.Composition, target, [], [new(Material, 1)],
                [new(Material, 1, [new(0)], [], []), new(Material, 1, [], [new(Material, 1)], []), new(Material, 2, [], [new(Material, 2)], [])], 5);
            Start(damaged, RefitBuilding(building, change));
            var reservation = LocalInventory(damaged).Reservations.Single();
            Assert.AreEqual(2L, reservation.Capacity);
            CollectionAssert.AreEqual(new[] { new GoodsQuantity(Material, 2) }, reservation.ExpectedReturns.ToArray());
            using var later = new CoreSession(damaged.State with { World = damaged.State.World with
            {
                Instances = damaged.State.World.Instances.Select(i => i.Id != id ? i : i with
                    { Parts = i.Parts.Select(p => p with { Current = 0 }).ToImmutableArray() }).ToImmutableArray()
            } });
            var published = Changed(content) with { Designs = later.State.World.Designs.Select(d => d.Design != design.Design.Design
                ? d : d with { MaxStructureDurability = 50 }).ToImmutableArray() };
            Accept(later, new CoreCommand.PublishContent(published));
            var completed = Step(later, 6).Events.Select(e => e.Event).OfType<CoreEvent.ConstructionCompleted>().Single();
            CollectionAssert.AreEqual(new[] { new GoodsQuantity(Material, 2) }, completed.ReturnedGoods.ToArray());
            Assert.AreEqual(9L, Stock(later, Local));
            Assert.IsTrue(LocalInventory(later).Reservations.IsEmpty);
        }
    }

    [TestMethod]
    public void Zero_work_building_refit_is_a_job_and_immediate_application_keeps_composition()
    {
        var (session, content, building, design) = BuildPlain();
        using (session)
        {
            var id = new InstanceId.Building(building);
            var before = Instance(session, id);
            var formal = design with { Design = design.Design with
                { Design = new(new string('8', 64)), Composition = new(new string('7', 64)) } };
            var change = new DesignChangeProjection(before.Design, before.Composition, formal, [new(new(0), new(0))], [], [], 0);
            var job = Start(session, RefitBuilding(building, change));
            Assert.AreEqual(0L, job.FrozenTotalWork);
            Assert.AreEqual(0d, job.Progress);
            Assert.AreEqual(8L, Stock(session, Local));
            var completed = Step(session).Events.Select(e => e.Event).OfType<CoreEvent.ConstructionCompleted>().Single();
            Assert.AreEqual(job.Id, completed.Job);
            Assert.AreEqual(formal.Design.Design, Instance(session, id).Design);

            var immediate = formal with { Design = formal.Design with { Design = new(new string('6', 64)) } };
            var same = new DesignChangeProjection(formal.Design.Design, formal.Design.Composition, immediate, [new(new(0), new(0))], [], [], 0);
            var applied = Accept(session, new CoreCommand.ApplyInstanceDesign(id, same));
            Assert.IsFalse(applied.Events.Any(e => e.Event is CoreEvent.ConstructionStarted));
            Assert.AreEqual(immediate.Design.Design, Instance(session, id).Design);
            Assert.AreEqual(formal.Design.Composition, Instance(session, id).Composition);
            var current = Instance(session, id);
            var changed = immediate with { Design = immediate.Design with
                { Design = new(new string('2', 64)), Composition = new(new string('5', 64)) } };
            Reject(session, new CoreCommand.ApplyInstanceDesign(id, new DesignChangeProjection(current.Design, current.Composition, changed,
                [new(new(0), new(0))], [], [], 0)), "Change.Target", "invalid_design");
            Reject(session, RefitBuilding(building, new DesignChangeProjection(current.Design, current.Composition,
                immediate with { Design = immediate.Design with
                {
                    Design = new(new string('1', 64)),
                    Kind = new DesignKind.Building(PlotSize.Medium, BuildingKind.Production, [])
                } }, [new(new(0), new(0))], [], [], 0)), "Instance.Details.Plot", "conditions_not_met");
            Assert.AreEqual(8L, Stock(session, Local));
        }
        using var station = Session(content);
        var stationDesign = station.State.World.Designs.Single(d => d.Design == WorldFixture.Station);
        var self = new DesignChangeProjection(stationDesign.Design, stationDesign.Composition,
            new(content.Identity, stationDesign with { Design = new(new string('4', 64)), Composition = new(new string('3', 64)) }, []),
            [new(stationDesign.Parts[0].Id, stationDesign.Parts[0].Id)], [], [], 0);
        Reject(station, new CoreCommand.StartOwnedJob(new JobRequest.Refit(Facility, new InstanceId.Building(Facility.Building), self, [Local], 0)),
            "Instance", "invalid_state");
    }

    [TestMethod]
    public void Station_refit_recounts_platforms()
    {
        var (session, content, building, _) = BuildProductionBuilding();
        using (session)
        {
            var stationId = new InstanceId.Building(Facility.Building);
            var stationDesign = session.State.World.Designs.Single(d => d.Design == WorldFixture.Station);
            var part = stationDesign.Parts[0];
            var target = stationDesign with
            {
                Design = new(new string('8', 64)), Composition = new(new string('7', 64)),
                Parts = [part, part with { Id = new(1), Capabilities = [new PartCapability.Platform()] }]
            };
            var before = Instance(session, stationId);
            var change = new DesignChangeProjection(before.Design, before.Composition,
                new(content.Identity, target, []), [new(part.Id, part.Id)], [new(Material, 1)], [], 1);
            Start(session, new CoreCommand.StartOwnedJob(new JobRequest.Refit(
                new(building, CapabilityKind.Construction), stationId, change, [Local], 0)));
            Step(session);
            var world = session.State.World;
            var settlement = world.Settlements.Single();
            Assert.AreEqual(2, world.Nodes.Single(n => n.Id == settlement.Node).Platforms);
            Assert.AreEqual(WorldState.CountPlatforms(settlement, world.Instances.ToDictionary(i => i.Id), world.Designs.ToDictionary(d => d.Design)),
                world.Nodes.Single(n => n.Id == settlement.Node).Platforms);
            Assert.AreEqual(new InstanceDetails.Building(settlement.Plots.Single(p => p.Building == Facility.Building).Id,
                settlement.Plots.Single(p => p.Building == Facility.Building).Size), world.Instances.Single(i => i.Id == stationId).Details);
            RoundTrip(session, content);
        }
    }

    private static (CoreSession Session, CoreContentProjection Content, BuildingId Building, DesignInput Design) BuildPlain()
    {
        var content = WithPlot(Content(), PlotSize.Small);
        var session = Session(content);
        var plot = Empty(session);
        var design = Building(content, PlotSize.Small, BuildingKind.Production, '9');
        Start(session, BuildOn(plot, design));
        var created = ((InstanceId.Building)Step(session, 2).Events.Select(e => e.Event).OfType<CoreEvent.ConstructionCompleted>().Single().CreatedInstance!).Id;
        return (session, content, created, design);
    }

    private static CoreCommand RefitBuilding(BuildingId building, DesignChangeProjection change)
        => new CoreCommand.StartOwnedJob(new JobRequest.Refit(Facility, new InstanceId.Building(building), change, [Local], 0));

    private static InstanceView Instance(ICoreSession session, InstanceId id)
        => ((Lookup<InstanceView>.Found)session.Read().FindInstance(id)).Value;

    private static InventoryView LocalInventory(ICoreSession session)
        => ((Lookup<InventoryView>.Found)session.Read().FindInventory(Local)).Value;

    private static (CoreSession Session, CoreContentProjection Content, BuildingId Building, DesignInput Design) BuildProductionBuilding()
    {
        var (session, content, plot) = Planned(PlotSize.Small);
        var design = Building(content, PlotSize.Small, BuildingKind.Production, '9',
            [new PartCapability.Production(1), new PartCapability.Power(10), new PartCapability.Storage(25),
                new PartCapability.Construction(60, PlotSize.Large, SizeClass.Small)]);
        var part = design.Design.Parts[0];
        design = design with { Design = design.Design with { Parts = [part, part with { Id = new(1), Capabilities = [] },
            part with { Id = new(2), Capabilities = [] }] } };
        Start(session, BuildOn(plot, design));
        var created = ((InstanceId.Building)Step(session, 2).Events.Select(e => e.Event).OfType<CoreEvent.ConstructionCompleted>().Single().CreatedInstance!).Id;
        return (session, content, created, design);
    }

    private static (CoreSession Session, CoreContentProjection Content, PlotId Plot) Planned(PlotSize size)
    {
        var content = WithPlot(Content(), size) with
        {
            Processes = [new(new ProcessDetails.Production(new("fixture:build"), [new(Material, 1)], [new(Material, 1)]),
                WorldFixture.Revision, 1, 0, 0, FacilityTag.Agriculture, [WorldFixture.Plain])]
        };
        using var unplanned = Session(content);
        var settlement = unplanned.State.World.Settlements.Single();
        var plot = settlement.Plots.Single(p => p.Building is null);
        var planned = settlement with
        {
            Template = settlement.Template with
            {
                ProductionPlans = settlement.Template.ProductionPlans.Add(new(plot.TemplateNumber, CapabilityKind.Production,
                    new ProcessRef.Production(new("fixture:build")), false, 0))
            }
        };
        return (new(unplanned.State with { World = unplanned.State.World with { Settlements = [planned] } }), content, plot.Id);
    }

    private static CoreSession MarkPartMissing(CoreSession session, DesignHash design, int part) => new(session.State with
    {
        World = session.State.World with
        {
            Designs = session.State.World.Designs.Select(d => d.Design != design ? d : d with
            {
                Parts = d.Parts.Select(p => p.Id.Value != part ? p : p with { Missing = true }).ToImmutableArray()
            }).ToImmutableArray()
        }
    });

    private static CoreSession DamageParts(CoreSession session, InstanceId id) => new(session.State with { World = session.State.World with
    {
        Instances = session.State.World.Instances.Select(i => i.Id != id ? i : i with
            { Parts = i.Parts.Select(p => p.Id.Value == 0 ? p : p with { Current = 0 }).ToImmutableArray() }).ToImmutableArray()
    } });

    private static CoreSession WithFacility(CoreSession session, BuildingId building, bool batch, bool buffer)
    {
        var row = session.State.World.Settlements.Single().ProductionFacilities.Single(f => f.Facility.Building == building);
        var updated = row with
        {
            Batch = batch ? new ProductionBatchState(new ProcessRef.Production(new("fixture:build")), WorldFixture.Revision, [new(Material, 1)], 1, 0, 0, 0) : null,
            OutputBuffer = buffer ? [new(Material, 1)] : []
        };
        var settlement = session.State.World.Settlements.Single();
        return new(session.State with { World = session.State.World with
        {
            Settlements = [settlement with
            {
                ProductionFacilities = settlement.ProductionFacilities.Select(f => f.Facility == row.Facility ? updated : f).ToImmutableArray()
            }]
        } });
    }

    private static CoreCommand RepairBuilding(BuildingId building, FacilityKey? facility = null)
        => new CoreCommand.StartOwnedJob(new JobRequest.Repair(facility ?? Facility, new InstanceId.Building(building), [Local], 0));

    private static SettlementView Settlement(ICoreSession session)
    {
        var id = ((CoreSession)session).State.World.Settlements.Single().Id;
        return ((Lookup<SettlementView>.Found)session.Read().FindSettlement(id)).Value;
    }

    private static ProductionState FacilityState(ICoreSession session, BuildingId building)
        => ((Lookup<FacilityView>.Found)session.Read().FindFacility(new(building, CapabilityKind.Production))).Value.State;

    private static CoreContentProjection WithPlot(CoreContentProjection content, PlotSize size)
    {
        var template = content.SettlementTemplates[0];
        var number = template.Plots.Max(p => p.Number) + 1;
        return content with
        {
            SettlementTemplates = [template with { Plots = template.Plots.Add(new(number, size, null)) }]
        };
    }

    private static bool SameInstances(ImmutableArray<InstanceState> left, ImmutableArray<InstanceState> right)
        => left.Length == right.Length && left.Zip(right).All(pair =>
            pair.First.Id.Equals(pair.Second.Id) && pair.First.Design.Equals(pair.Second.Design)
            && pair.First.Ownership == pair.Second.Ownership && pair.First.Details.Equals(pair.Second.Details)
            && pair.First.StructureDurability == pair.Second.StructureDurability
            && pair.First.Parts.SequenceEqual(pair.Second.Parts));

    private static CoreContentProjection BuildingLimit(CoreContentProjection content, PlotSize limit) => content with
    {
        Designs = content.Designs.Select(d => d.Design != WorldFixture.Station ? d : d with
        {
            Parts = d.Parts.Select(p => p with
            {
                Capabilities = p.Capabilities.Select(c => c is PartCapability.Construction construction
                    ? construction with { MaximumBuildingPlotSize = limit } : c).ToImmutableArray()
            }).ToImmutableArray()
        }).ToImmutableArray()
    };

    private static DesignInput Building(CoreContentProjection content, PlotSize size, BuildingKind kind, char hash,
        ImmutableArray<PartCapability>? capabilities = null)
    {
        var basis = content.Designs.First(d => d.Kind is DesignKind.Vehicle);
        return new(content.Identity, basis with
        {
            Design = new(new string(hash, 64)),
            Kind = new DesignKind.Building(size, kind, []),
            Parts = [basis.Parts[0] with { Capabilities = capabilities ?? [] }],
            BillOfMaterials = [new(Material, 2)],
            BillOfMaterialsItemCount = 2,
            CanConstruct = true
        }, []);
    }

    private static CoreCommand BuildOn(PlotId plot, DesignInput design)
        => new CoreCommand.StartOwnedJob(new JobRequest.BuildBuilding(Facility, plot, design, [Local], 0));

    private static PlotId Empty(ICoreSession session)
        => ((CoreSession)session).State.World.Settlements.Single().Plots.Single(p => p.Building is null).Id;

    private static PlotId Occupied(ICoreSession session)
        => ((CoreSession)session).State.World.Settlements.Single().Plots.Single(p => p.Building is not null).Id;

    private static PlotView Plot(ICoreSession session, PlotId id)
    {
        var settlement = ((CoreSession)session).State.World.Settlements.Single().Id;
        return ((Lookup<SettlementView>.Found)session.Read().FindSettlement(settlement)).Value.Plots.Single(p => p.Id == id);
    }

    private static void RoundTrip(CoreSession session, CoreContentProjection content)
    {
        var saved = WorldFixture.Success(session.Capture(CapturePurpose.Save));
        using var candidate = WorldFixture.Success(CoreFactory.Create().PrepareRestore(
            WorldFixture.Success(CoreFactory.Create().DecodeState(saved.Encode())), content, WorldFixture.Options));
        using var restored = (CoreSession)candidate.Activate();
        Assert.AreEqual(saved.Encode(), WorldFixture.Success(restored.Capture(CapturePurpose.Save)).Encode());
    }
}
