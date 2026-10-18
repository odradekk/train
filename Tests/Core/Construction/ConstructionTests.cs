using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.Sessions;
using Train.Core.Tests.World;
using static Train.Core.Tests.Construction.ConstructionFixture;

namespace Train.Core.Tests.Construction;

[TestClass]
public sealed class ConstructionTests
{
    [TestMethod]
    public void New_design_input_and_rejected_busy_command_survive_capture_and_strict_replay()
    {
        var content = Content();
        using var session = Session(content);
        var input = Design(content) with { Design = Design(content).Design with { Design = new(new string('9', 64)) } };
        var command = new CoreCommand.StartOwnedJob(new JobRequest.BuildVehicle(Facility, input, [Local], 0));
        var job = Start(session, command);
        Reject(session, command, "Facility", "busy");
        Step(session, 2);
        var saved = WorldFixture.Success(session.Capture(CapturePurpose.Save));
        Assert.IsTrue(saved.References.Designs.Contains(input.Design.Design));
        Assert.AreEqual(content.Identity, saved.InputIdentity);
        Assert.AreEqual(saved.Encode(), WorldFixture.Success(CoreFactory.Create().DecodeState(saved.Encode())).Encode());
        var recording = session.ReadRecording();
        using var replay = WorldFixture.Success(CoreFactory.Create().PrepareReplay(new(2, WorldFixture.Options.Runtime,
            recording.Starts[0].State.Encode(), recording.Commands, recording.Checkpoints, recording.EndPosition, null),
            WorldFixture.Options.Runtime, [content]));
        var result = replay.Advance(2);
        Assert.AreEqual(ReplayStop.EndOfRecording, result.Stop);
        Assert.IsNull(result.Difference);
        Assert.AreEqual(saved.Checkpoint, replay.CaptureBoundary().Checkpoint);
        Assert.IsInstanceOfType<Lookup<JobView>.Missing>(session.Read().FindJob(job.Id));
    }

    [TestMethod]
    public void Deferred_construction_commands_do_not_take_a_command_number()
    {
        var content = Content();
        using var session = Session(content);
        var input = Design(content);
        CoreCommand[] commands =
        [
            new CoreCommand.StartOwnedJob(new JobRequest.BuildBuilding(Facility, new(1), input, [Local], 0)),
            new CoreCommand.StartOwnedJob(new JobRequest.BuildEquipment(Facility, input, [Local], 0)),
            new CoreCommand.StartOwnedJob(new JobRequest.Repair(Facility, new InstanceId.Building(new(1)), [Local], 0)),
            new CoreCommand.StartOwnedJob(new JobRequest.Dismantle(Facility, new InstanceId.Building(new(1)), 0)),
            new CoreCommand.StartOwnedJob(new JobRequest.Refit(Facility, new InstanceId.Building(new(1)),
                new(input.Design.Design, input.Design.Composition, input, [], [], [], 0), [], 0)),
            new CoreCommand.SetJobPriority(new(1), 0)
        ];
        var before = session.State;
        foreach (var command in commands)
            Assert.ThrowsExactly<NotImplementedException>(() => session.Submit(command, CommandSource.AutomationTest));
        Assert.AreEqual(before, session.State);
        Assert.IsTrue(session.ReadRecording().Commands.IsEmpty);
    }

    [TestMethod]
    public void Invalid_sources_are_atomic_and_do_not_reserve_the_facility()
    {
        var content = Content();
        using var session = Session(content);
        Reject(session, Build(content), "Sources", "insufficient_goods");
        Reject(session, Build(content, Local, Local), "Sources[1]", "duplicate_source");
        Reject(session, Build(content, Local, new(999)), "Sources[1]", "object_missing");
        Reject(session, Build(content, Cargo), "Sources[0]");
        Assert.AreEqual(10L, Stock(session, Local));
        Start(session, Build(content, Local));
        Reject(session, Build(content, Local), "Facility", "busy");
        Assert.AreEqual(8L, Stock(session, Local));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Explicit_source_order_consumes_the_first_accessible_inventory(bool cargoFirst)
    {
        var content = Content(stock: 1);
        using var initial = Session(content);
        using var session = new CoreSession(initial.State with { World = initial.State.World with
        { Trains = initial.State.World.Trains.Select(t => t with { Activity = TrainActivity.Servicing,
            Plan = new(false, [new PlanStop.Station(((TrainPosition.Node)t.Position).Id, 100, [])]),
            NextStopIndex = 0, RemainingDwellSteps = 100 }).ToImmutableArray() } });
        var job = Start(session, cargoFirst ? Build(content, Cargo, Local) : Build(content, Local, Cargo));
        Assert.AreEqual(cargoFirst ? 1L : 0L, Stock(session, Local));
        Assert.AreEqual(cargoFirst ? 8L : 9L, Stock(session, Cargo));
        CollectionAssert.AreEqual(new[] { new GoodsQuantity(Material, 2) }, job.InvestedGoods.ToArray());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Missing_or_insufficient_vehicle_rank_rejects_without_consuming(bool missing)
    {
        var content = Content(maximum: missing ? null : SizeClass.Small);
        using var session = Session(content);
        var design = Design(content);
        if (!missing) design = design with { Design = design.Design with
            { Design = new(new string('7', 64)), Kind = new DesignKind.Vehicle(SizeClass.Medium, 1) } };
        Reject(session, new CoreCommand.StartOwnedJob(new JobRequest.BuildVehicle(Facility, design, [Local], 0)), "Facility");
        Assert.AreEqual(10L, Stock(session, Local));
    }

    [TestMethod]
    public void Completed_vehicle_is_parked_and_can_be_formed_and_started()
    {
        var content = Content();
        using var candidate = WorldFixture.Prepare(WorldFixture.Input with
            { Configuration = WorldFixture.Input.Configuration with { WidthMeters = 20 } }, content);
        using var generated = (CoreSession)candidate.Activate();
        var state = generated.State;
        var node = ((TrainPosition.Node)state.World.Trains.Single().Position).Id;
        var origin = state.World.Settlements.Single(s => s.Node == node);
        using var session = new CoreSession(state with { World = state.World with
        {
            Settlements = state.World.Settlements.Select(s => s.Id != origin.Id ? s : s with
                { Relation = SettlementRelation.PlayerOwned }).ToImmutableArray(),
            Nodes = state.World.Nodes.Select(n => n.Id != node ? n : n with
                { Occupant = new NodeOccupant.Settlement(origin.Id, SettlementRelation.PlayerOwned) }).ToImmutableArray(),
            Instances = state.World.Instances.Select(i => !origin.Plots.Any(p => i.Id == new InstanceId.Building(p.Building!.Value))
                ? i : i with { Ownership = SettlementRelation.PlayerOwned }).ToImmutableArray()
        } });
        var facility = new FacilityKey(origin.Plots.Single().Building!.Value, CapabilityKind.Construction);
        var started = Start(session, new CoreCommand.StartOwnedJob(new JobRequest.BuildVehicle(facility, Design(content), [origin.Inventory], 0)));
        var old = session.Read();
        var events = Step(session, 2).Events.Select(e => e.Event).ToArray();
        var completed = events.OfType<CoreEvent.ConstructionCompleted>().Single();
        Assert.AreEqual(started.Id, completed.Job);
        var vehicle = ((InstanceId.Vehicle)completed.CreatedInstance!).Id;
        Assert.IsInstanceOfType<Lookup<JobView>.Missing>(session.Read().FindJob(started.Id));
        Assert.AreEqual(0d, Job(old, started.Id).ProgressWork);
        var settlement = ((Lookup<SettlementView>.Found)session.Read().FindSettlement(origin.Id)).Value;
        CollectionAssert.Contains(settlement.ParkedVehicles.ToArray(), vehicle);
        Accept(session, new CoreCommand.DisbandTrain(new(1)));
        var formed = Accept(session, new CoreCommand.FormTrain([vehicle]));
        var train = ((CommandDecision.Accepted)formed.Receipt.Decision).Created.OfType<WorldObjectId.Train>().Single().Id;
        var destination = session.Read().World.Nodes.Single(n => n.Id != node).Id;
        Accept(session, new CoreCommand.ReplaceRunPlan(train, new(false, [new PlanStop.Station(destination, 1, [])])));
        Accept(session, new CoreCommand.StartTrain(train));
        Step(session, 10);
        var position = ((Lookup<TrainView>.Found)session.Read().FindTrain(train)).Value.Position;
        Assert.IsTrue(position is TrainPosition.Edge || position is TrainPosition.Node arrival && arrival.Id == destination);
    }

    [TestMethod]
    public void Power_shortage_keeps_progress_and_frozen_rule_survives_publication()
    {
        var content = Content(power: 1);
        using var session = Session(content);
        var job = Start(session, Build(content, Local));
        Step(session, 2);
        var blocked = Job(session.Read(), job.Id);
        Assert.AreEqual(ConstructionState.ResourceBlocked, blocked.State);
        Assert.AreEqual(0d, blocked.ProgressWork);
        CollectionAssert.Contains(blocked.Limits.ToArray(), WorkLimit.Power);
        var updated = Changed(content) with
        {
            ConstructionRules = [content.ConstructionRules[0] with { ConstructionWorkPerItem = 100, PowerPerWork = 0 }],
            Designs = content.Designs.Select(d => d.Design != WorldFixture.Station ? d : d with
            { Parts = d.Parts.Select(p => p with { Capabilities = p.Capabilities.Add(new PartCapability.Power(60)) }).ToImmutableArray() }).ToImmutableArray(),
            Goods = content.Goods.Select(g => g.Good == Material ? g with { FuelEnergyPerUnit = 1 } : g).ToImmutableArray()
        };
        Accept(session, new CoreCommand.PublishContent(updated));
        Assert.AreEqual(2L, Job(session.Read(), job.Id).FrozenTotalWork);
        Step(session);
        Assert.AreEqual(1d, Job(session.Read(), job.Id).ProgressWork, 1e-12);
        Step(session);
        Assert.IsInstanceOfType<Lookup<JobView>.Missing>(session.Read().FindJob(job.Id));
    }

    [TestMethod]
    public void Damaged_capability_blocks_but_loss_of_qualification_fails_and_releases()
    {
        var content = Content();
        using var initial = Session(content);
        var job = Start(initial, Build(content, Local));
        using var damaged = new CoreSession(initial.State with { World = initial.State.World with
        { Instances = initial.State.World.Instances.Select(i => i.Id != new InstanceId.Building(Facility.Building) ? i : i with
            { Parts = i.Parts.Select(p => p with { Current = 0 }).ToImmutableArray() }).ToImmutableArray() } });
        Step(damaged);
        Assert.AreEqual(ConstructionState.ResourceBlocked, Job(damaged.Read(), job.Id).State);
        CollectionAssert.Contains(Job(damaged.Read(), job.Id).Limits.ToArray(), WorkLimit.Capability);
        var disqualified = Changed(content) with { Designs = content.Designs.Select(d => d.Design != WorldFixture.Station ? d : d with
        { Parts = d.Parts.Select(p => p with { Capabilities = p.Capabilities.Where(c => c is not PartCapability.Construction).ToImmutableArray() }).ToImmutableArray() }).ToImmutableArray() };
        var publication = Accept(damaged, new CoreCommand.PublishContent(disqualified));
        var advance = Step(damaged);
        Assert.IsTrue(publication.Events.Concat(advance.Events).Any(e => e.Event is CoreEvent.ConstructionStateChanged { After: ConstructionState.Failed }));
        Assert.IsInstanceOfType<Lookup<JobView>.Missing>(damaged.Read().FindJob(job.Id));
    }

    [TestMethod]
    public void Active_job_prevents_required_definition_removal()
    {
        var content = Content();
        using var session = Session(content);
        var job = Start(session, Build(content, Local));
        var removed = Changed(content) with { Goods = content.Goods.Where(g => g.Good != Material).ToImmutableArray() };
        Reject(session, new CoreCommand.PublishContent(removed));
        Assert.AreEqual(content.Identity, session.Read().InputIdentity);
        Assert.AreEqual(0d, Job(session.Read(), job.Id).ProgressWork);
    }

    [TestMethod]
    public void Invested_material_remains_a_required_reference_after_the_design_bom_changes()
    {
        var invested = new GoodId.Voxel(new("fixture:invested-material"));
        var content = Content();
        content = content with
        {
            Goods = content.Goods.Add(content.Goods[0] with { Good = invested }),
            Designs = content.Designs.Select(d => d.Design != WorldFixture.Vehicle ? d : d with
                { BillOfMaterials = [new(invested, 1)], BillOfMaterialsItemCount = 1 }).ToImmutableArray()
        };
        using var initial = Session(content);
        using var session = new CoreSession(initial.State with { World = initial.State.World with
        {
            Inventories = initial.State.World.Inventories.Select(i => i.Id != Local ? i : i with
                { Goods = i.Goods.Add(new(invested, 1)).OrderBy(g => Train.Core.World.WorldReferences.GoodKey(g.Good), StringComparer.Ordinal).ToImmutableArray() }).ToImmutableArray()
        } });
        var job = Start(session, Build(content, Local));
        var updated = Changed(content) with
        {
            Goods = content.Goods.Where(g => g.Good != invested).ToImmutableArray(),
            Designs = content.Designs.Select(d => d.Design != WorldFixture.Vehicle ? d : d with
                { BillOfMaterials = [new(Material, 1)] }).ToImmutableArray()
        };
        var rejected = (CommandDecision.Rejected)Submit(session, new CoreCommand.PublishContent(updated)).Receipt.Decision;
        Assert.IsTrue(rejected.Problems.Any(p => p.Field == $"World.ConstructionJobs[{job.Id.Value}].InvestedGoods"));
        Assert.AreEqual(content.Identity, session.Read().InputIdentity);
    }

    [TestMethod]
    public void Loss_of_labor_preserves_existing_progress_until_supply_returns()
    {
        var content = Content(labor: 1);
        using var session = Session(content);
        var job = Start(session, Build(content, Local));
        Step(session);
        var progress = Job(session.Read(), job.Id).ProgressWork;
        Assert.IsTrue(progress > 0);
        var unavailable = Changed(content) with { PopulationRules = [content.PopulationRules[0] with { LaborRatio = 0 }] };
        Accept(session, new CoreCommand.PublishContent(unavailable));
        Step(session, 2);
        Assert.AreEqual(progress, Job(session.Read(), job.Id).ProgressWork);
        Assert.AreEqual(ConstructionState.ResourceBlocked, Job(session.Read(), job.Id).State);
        CollectionAssert.Contains(Job(session.Read(), job.Id).Limits.ToArray(), WorkLimit.Labor);
        Accept(session, new CoreCommand.PublishContent(Changed(content, '8')));
        Step(session);
        Assert.IsTrue(Job(session.Read(), job.Id).ProgressWork > progress);
    }

    [TestMethod]
    public void Destroyed_repair_target_fails_and_releases_the_facility()
    {
        var content = Content();
        using var original = Session(content, damaged: true);
        var job = Start(original, Repair(Local));
        using var session = new CoreSession(original.State with { World = original.State.World with
        { Instances = original.State.World.Instances.Select(i => i.Id != Vehicle ? i : i with { StructureDurability = 0 }).ToImmutableArray() } });
        var advanced = Step(session);
        Assert.IsTrue(advanced.Events.Any(e => e.Event is CoreEvent.ConstructionStateChanged { After: ConstructionState.Failed }));
        Assert.IsInstanceOfType<Lookup<JobView>.Missing>(session.Read().FindJob(job.Id));
        Assert.AreEqual(8L, Stock(session, Local));
        Start(session, Build(content, Local));
    }

    [TestMethod]
    public void Repair_locks_train_operations_but_allows_disbanding_and_forming()
    {
        using var initial = Session(damaged: true);
        using var session = new CoreSession(initial.State with { World = initial.State.World with
        { Inventories = initial.State.World.Inventories.Select(i => i.Id != Cargo ? i : i with { Goods = [] }).ToImmutableArray() } });
        var job = Start(session, Repair(Local));
        Assert.AreEqual(InstanceLock.UnderRepair, ((Lookup<InstanceView>.Found)session.Read().FindInstance(Vehicle)).Value.Lock);
        Reject(session, Repair(Local), "Instance", "instance_locked");
        Reject(session, new CoreCommand.StartTrain(new(1)), "Train", "instance_locked");
        Reject(session, new CoreCommand.LoadGoods(new(1), new(1), Local, Material, 1), "Train");
        Reject(session, new CoreCommand.UnloadGoods(new(1), new(1), Local, Material, 1), "Train");
        Reject(session, new CoreCommand.BuyGoods(new(1), new(1), Local, Material, 1, new(100)), "Train");
        Reject(session, new CoreCommand.SellGoods(new(1), new(1), Local, Material, 1, new(0)), "Train");
        Reject(session, new CoreCommand.SellVehicle(new(1), new(1), new(0)), "Vehicle", "instance_locked");
        Accept(session, new CoreCommand.DisbandTrain(new(1)));
        Accept(session, new CoreCommand.FormTrain([new(1)]));
        var completed = Step(session, checked((uint)job.FrozenTotalWork)).Events.Select(e => e.Event).OfType<CoreEvent.ConstructionCompleted>().Single();
        Assert.AreEqual(ConstructionKind.Repair, completed.Kind);
        var repaired = ((Lookup<InstanceView>.Found)session.Read().FindInstance(Vehicle)).Value;
        Assert.AreEqual(InstanceLock.None, repaired.Lock);
        Assert.AreEqual(repaired.Structure.Maximum, repaired.Structure.EffectiveCurrent);
        Assert.IsTrue(repaired.Parts.All(p => p.Durability.EffectiveCurrent == p.Durability.Maximum));
    }

    [TestMethod]
    [DataRow(50L, 2L, 3L)]
    [DataRow(150L, 1L, 2L)]
    public void Repair_skips_missing_parts_and_structural_goods_and_clamps_saved_structure(long structure, long consumed, long work)
    {
        var absent = new GoodId.Voxel(WorldFixture.Ore);
        var content = Content();
        content = content with
        {
            Goods = content.Goods.Add(content.Goods[0] with { Good = absent }),
            Designs = content.Designs.Select(d => d.Design != WorldFixture.Vehicle ? d : d with
            {
                Parts = [d.Parts[0], d.Parts[0] with { Id = new(1), Definition = new CoreDefinitionReference.Voxel(WorldFixture.Ore), RepairGood = absent },
                    d.Parts[0] with { Id = new(2), Capabilities = [] }],
                StructuralGoods = [new(Material, 2), new(absent, 4)],
                BillOfMaterials = [new(Material, 2), new(absent, 5)], BillOfMaterialsItemCount = 7
            }).ToImmutableArray()
        };
        using var generated = Session(content);
        using var original = new CoreSession(generated.State with { World = generated.State.World with
        { Instances = generated.State.World.Instances.Select(i => i.Id != Vehicle ? i : i with
            { StructureDurability = structure, Parts = [new(new(0), 0), new(new(1), 0), new(new(2), 50)] }).ToImmutableArray() } });
        var saved = WorldFixture.Success(original.Capture(CapturePurpose.Save));
        var missing = Changed(content) with { Goods = content.Goods.Where(g => g.Good != absent).ToImmutableArray() };
        using var candidate = WorldFixture.Success(CoreFactory.Create().PrepareRestore(
            WorldFixture.Success(CoreFactory.Create().DecodeState(saved.Encode())), missing, WorldFixture.Options));
        using var session = candidate.Activate();
        var job = Start(session, Repair(Local));
        Assert.AreEqual(work, job.FrozenTotalWork);
        CollectionAssert.AreEqual(new[] { new GoodsQuantity(Material, consumed) }, job.InvestedGoods.ToArray());
        Assert.AreEqual(10 - consumed, Stock(session, Local));
        Step(session, checked((uint)work));
        var repaired = ((Lookup<InstanceView>.Found)session.Read().FindInstance(Vehicle)).Value;
        Assert.AreEqual(100L, repaired.Structure.EffectiveCurrent);
        Assert.AreEqual(100L, repaired.Parts.Single(p => p.Part == new PartId(0)).Durability.EffectiveCurrent);
        Assert.AreEqual(100L, repaired.Parts.Single(p => p.Part == new PartId(2)).Durability.EffectiveCurrent);
        Assert.AreEqual(0L, repaired.Parts.Single(p => p.Part == new PartId(1)).Durability.EffectiveCurrent);
        Assert.IsTrue(repaired.Parts.Single(p => p.Part == new PartId(1)).DefinitionMissing);
    }

    [TestMethod]
    public void Undamaged_repair_is_rejected_but_damage_without_material_cost_accepts_empty_sources()
    {
        using var intact = Session();
        Reject(intact, Repair(), "Instance.Durability", "invalid_state");
        using var damaged = new CoreSession(intact.State with { World = intact.State.World with
        { Instances = intact.State.World.Instances.Select(i => i.Id != Vehicle ? i : i with
            { Parts = i.Parts.Select(p => p with { Current = 50 }).ToImmutableArray() }).ToImmutableArray() } });
        var job = Start(damaged, Repair());
        Assert.IsTrue(job.InvestedGoods.IsEmpty);
        Assert.AreEqual(1L, job.FrozenTotalWork);
        Assert.AreEqual(10L, Stock(damaged, Local));
        Step(damaged);
        Assert.IsInstanceOfType<Lookup<JobView>.Missing>(damaged.Read().FindJob(job.Id));
    }

    [TestMethod]
    public void Missing_repair_target_fails_after_its_train_membership_is_removed()
    {
        using var original = Session(damaged: true);
        var job = Start(original, Repair(Local));
        using var session = new CoreSession(original.State with { World = original.State.World with
        {
            Instances = original.State.World.Instances.Where(i => i.Id != Vehicle).ToImmutableArray(),
            Trains = [],
            Inventories = original.State.World.Inventories.Where(i => i.Id != Cargo).ToImmutableArray()
        } });
        var result = Step(session);
        var failed = result.Events.Select(e => e.Event).OfType<CoreEvent.ConstructionStateChanged>().Single();
        Assert.AreEqual(ConstructionState.Failed, failed.After);
        Assert.AreEqual("object_missing", failed.Reasons.Single().Code);
        Assert.IsInstanceOfType<Lookup<JobView>.Missing>(session.Read().FindJob(job.Id));
        Start(session, Build(session.State.Content, Local));
    }

    [TestMethod]
    public void Surviving_small_capability_cannot_execute_a_medium_job_qualified_by_a_destroyed_part()
    {
        var content = Content(maximum: SizeClass.Medium);
        content = content with { Designs = content.Designs.Select(d => d.Design != WorldFixture.Station ? d : d with
        { Parts = d.Parts.Add(d.Parts[0] with { Id = new(1), Capabilities = [new PartCapability.Construction(60, PlotSize.Small, SizeClass.Small)] }) }).ToImmutableArray() };
        using var original = Session(content);
        var design = Design(content) with { Design = Design(content).Design with
            { Design = new(new string('7', 64)), Kind = new DesignKind.Vehicle(SizeClass.Medium, 1) } };
        var job = Start(original, new CoreCommand.StartOwnedJob(new JobRequest.BuildVehicle(Facility, design, [Local], 0)));
        using var session = new CoreSession(original.State with { World = original.State.World with
        { Instances = original.State.World.Instances.Select(i => i.Id != new InstanceId.Building(Facility.Building) ? i : i with
            { Parts = i.Parts.Select(p => p.Id == new PartId(0) ? p with { Current = 0 } : p).ToImmutableArray() }).ToImmutableArray() } });
        Step(session);
        Assert.AreEqual(ConstructionState.ResourceBlocked, Job(session.Read(), job.Id).State);
        CollectionAssert.Contains(Job(session.Read(), job.Id).Limits.ToArray(), WorkLimit.Capability);
        Assert.AreEqual(0d, Job(session.Read(), job.Id).ProgressWork);
    }

    [TestMethod]
    public void Construction_and_production_on_the_same_building_share_one_labor_budget()
    {
        var content = Production.ProductionFixture.Content(rate: 60, work: 10, laborPerWork: 1);
        content = content with
        {
            Designs = content.Designs.Select(d => d.Design == WorldFixture.Vehicle ? d with
                { BillOfMaterials = [new(Material, 2)], BillOfMaterialsItemCount = 2 }
                : d.Design != WorldFixture.FactoryBuilding ? d : d with
            { Parts = d.Parts.Select(p => p with { Capabilities = p.Capabilities.Add(new PartCapability.Construction(60, PlotSize.Small, SizeClass.Small)) }).ToImmutableArray() }).ToImmutableArray(),
            ConstructionRules = [content.ConstructionRules[0] with { LaborPerWork = 1 }]
        };
        using var session = Session(content);
        Step(session);
        var job = Start(session, new CoreCommand.StartOwnedJob(new JobRequest.BuildVehicle(new(new(2), CapabilityKind.Construction), Design(content), [Local], 0)));
        Step(session);
        var construction = Job(session.Read(), job.Id);
        var production = Production.ProductionFixture.Read(session);
        var population = ((Lookup<SettlementView>.Found)session.Read().FindSettlement(new(1))).Value.Population;
        Assert.IsTrue(production.ActualWorkPerSecond > 0 || construction.ActualWorkPerSecond > 0);
        Assert.IsTrue(production.ActualWorkPerSecond + construction.ActualWorkPerSecond <= population.AvailableLabor + 1e-12);
        Assert.AreEqual(production.ActualWorkPerSecond + construction.ActualWorkPerSecond, population.AllocatedLabor, 1e-12);
    }

    [TestMethod]
    public void Settlement_retains_the_completing_steps_resource_usage_until_the_next_step()
    {
        var content = Content(power: 1, labor: .05);
        content = content with
        {
            Designs = content.Designs.Select(d => d.Design != WorldFixture.Station ? d : d with
            { Parts = d.Parts.Select(p => p with { Capabilities = p.Capabilities.Add(new PartCapability.Power(60)) }).ToImmutableArray() }).ToImmutableArray(),
            Goods = content.Goods.Select(g => g.Good == Material ? g with { FuelEnergyPerUnit = 1 } : g).ToImmutableArray()
        };
        using var session = Session(content);
        var job = Start(session, Build(content, Local));
        Step(session, 2);
        Assert.IsInstanceOfType<Lookup<JobView>.Missing>(session.Read().FindJob(job.Id));
        var completed = ((Lookup<SettlementView>.Found)session.Read().FindSettlement(new(1))).Value;
        Assert.AreEqual(60d, completed.Power.Demand, 1e-12);
        Assert.AreEqual(60d, completed.Power.ActualSupply, 1e-12);
        Assert.AreEqual(3d, completed.Population.AllocatedLabor, 1e-12);

        var saved = WorldFixture.Success(session.Capture(CapturePurpose.Save));
        using var candidate = WorldFixture.Success(CoreFactory.Create().PrepareRestore(
            WorldFixture.Success(CoreFactory.Create().DecodeState(saved.Encode())), content, WorldFixture.Options));
        using var restored = candidate.Activate();
        var retained = ((Lookup<SettlementView>.Found)restored.Read().FindSettlement(new(1))).Value;
        Assert.AreEqual(completed.Power, retained.Power);
        Assert.AreEqual(completed.Population.AllocatedLabor, retained.Population.AllocatedLabor);
        Step(restored);
        var idle = ((Lookup<SettlementView>.Found)restored.Read().FindSettlement(new(1))).Value;
        Assert.AreEqual(0d, idle.Power.Demand);
        Assert.AreEqual(0d, idle.Power.ActualSupply);
        Assert.AreEqual(0d, idle.Population.AllocatedLabor);
    }

    [TestMethod]
    [DataRow("progress_work", "-1")]
    [DataRow("frozen_total_work", "0")]
    [DataRow("frozen_power_per_work", "-1")]
    [DataRow("last_actual_work_per_second", "-1")]
    [DataRow("source_consumption", "[]")]
    [DataRow("next", "0")]
    public void Invalid_construction_progress_frozen_terms_sources_and_next_identity_are_rejected(string field, string value)
    {
        var content = Content();
        using var session = Session(content);
        Start(session, Build(content, Local));
        Step(session);
        var json = JsonNode.Parse(WorldFixture.Success(session.Capture(CapturePurpose.Save)).Encode())!;
        if (field == "next") json["world"]!["next"]!["job"] = JsonNode.Parse(value);
        else json["world"]!["construction_jobs"]![0]![field] = JsonNode.Parse(value);
        Assert.IsInstanceOfType<CoreResult<ICoreCapture>.Failure>(CoreFactory.Create().DecodeState(json.ToJsonString()));
    }
}
