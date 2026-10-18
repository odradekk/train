using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Combat;
using Train.Core.Contracts;
using Train.Core.Sessions;
using Train.Core.Tests.World;
using Train.Core.World;

namespace Train.Core.Tests.Combat;

[TestClass]
public sealed class CombatDamageTests
{
    [TestMethod]
    public void Weapons_fire_on_the_join_step_and_then_on_their_interval()
    {
        using var session = CombatFixture.Session(damage: 1);
        var hostile = CombatFixture.HostileNode(session);
        var train = new TrainId(1);
        CombatFixture.Send(session, hostile, train);
        var events = CombatFixture.AdvanceUntil(session, view => view.World.Combats.Any());
        Assert.IsTrue(events.OfType<CoreEvent.CombatHits>().Any());
        var quiet = session.Advance(59);
        Assert.IsNull(quiet.Fault);
        Assert.IsFalse(quiet.Events.Select(envelope => envelope.Event).OfType<CoreEvent.CombatHits>().Any());
        var next = session.Advance(1);
        Assert.IsNull(next.Fault);
        Assert.IsTrue(next.Events.Select(envelope => envelope.Event).OfType<CoreEvent.CombatHits>().Any());
    }

    [TestMethod]
    public void Part_damage_overflows_exactly_and_destroyed_parts_route_all_damage_to_structure()
    {
        var content = CombatFixture.Content(damage: 10) with
        {
            Designs = CombatFixture.Content(damage: 10).Designs.Select(design => design.Design != CombatFixture.ArmedBuilding
                ? design : design with
                {
                    StructureHitWeight = 0,
                    Parts = design.Parts.Select(part => part with
                    {
                        MaxDurability = 5,
                        Capabilities = [new PartCapability.Weapon(1, 1)]
                    }).ToImmutableArray()
                }).ToImmutableArray()
        };
        using var session = CombatFixture.SessionWithContent(content);
        var hostile = CombatFixture.HostileNode(session);
        var building = HostileBuilding(session, hostile);
        var train = new TrainId(1);
        CombatFixture.Accepted(session.Submit(new CoreCommand.SetTrainEngagement(train,
            new(TargetPriority.ArmedFirst, TargetScope.All, EncounterAction.Engage, 0)), CommandSource.AutomationTest));
        CombatFixture.Send(session, hostile, train);
        CombatFixture.AdvanceUntilEvent(session, value => value is CoreEvent.CombatDestruction destruction
            && destruction.Parts.Any(part => part.Instance.Instance == building));
        var first = Instance(session, building);
        Assert.AreEqual(95, first.Structure.EffectiveCurrent);
        Assert.IsTrue(first.Parts.Single().Destroyed);
        var combat = session.Read().World.Combats.Single();
        CombatFixture.Accepted(session.Submit(new CoreCommand.FocusFire(combat.Id, new CombatController.Train(train),
            new WorldObjectId.Instance(building)), CommandSource.AutomationTest));
        session.Advance(60);
        var second = Instance(session, building);
        Assert.AreEqual(85, second.Structure.EffectiveCurrent);
        Assert.IsTrue(second.Parts.Single().Destroyed);
    }

    [TestMethod]
    public void Same_step_hits_share_one_event_and_sum_against_structure()
    {
        var content = StructureTarget(100);
        using var session = CombatFixture.SessionWithContent(content, vehicles: 2);
        var hostile = CombatFixture.HostileNode(session);
        var building = HostileBuilding(session, hostile);
        CombatFixture.Send(session, hostile, new TrainId(1));
        var events = CombatFixture.AdvanceUntil(session, view => view.World.Combats.Any());
        var hits = events.OfType<CoreEvent.CombatHits>().ToArray();
        Assert.AreEqual(1, hits.Length);
        var trainHits = hits[0].Hits.Where(hit => hit.Source is AttackSource.Weapon weapon
            && weapon.Instance.Instance is InstanceId.Vehicle).ToArray();
        Assert.AreEqual(2, trainHits.Length);
        Assert.AreEqual(20, trainHits.Sum(hit => hit.Damage));
        Assert.AreEqual(80, Instance(session, building).Structure.EffectiveCurrent);
    }

    [TestMethod]
    public void Same_step_hits_report_a_destroyed_unit_only_once()
    {
        var content = StructureTarget(15);
        using var session = CombatFixture.SessionWithContent(content, vehicles: 2);
        var hostile = CombatFixture.HostileNode(session);
        var building = HostileBuilding(session, hostile);
        CombatFixture.Send(session, hostile, new TrainId(1));
        var events = CombatFixture.AdvanceUntil(session,
            view => view.FindInstance(building) is Lookup<InstanceView>.Missing);
        var destruction = events.OfType<CoreEvent.CombatDestruction>().ToArray();
        Assert.AreEqual(1, destruction.Length);
        Assert.AreEqual(1, destruction[0].Units.Count(unit => unit is DestroyedUnit.Instance instance
            && instance.Value.Instance == building));
        Assert.IsTrue(destruction[0].Parts.IsEmpty);
    }

    [TestMethod]
    public void Destroying_all_vehicles_deletes_the_train_loses_cargo_and_ends_the_run()
    {
        var content = CombatFixture.Content(damage: 1) with
        {
            Designs = CombatFixture.Content(damage: 1).Designs.Select(design => design.Design != CombatFixture.ArmedBuilding
                ? design : design with
                {
                    Parts = design.Parts.Select(part => part with
                    {
                        Capabilities = [new PartCapability.Weapon(100, 1)]
                    }).ToImmutableArray()
                }).ToImmutableArray()
        };
        var cargo = ImmutableArray.Create(new GoodsQuantity(new GoodId.Voxel(WorldFixture.Material), 3));
        using var session = CombatFixture.SessionWithContent(content, cargo: cargo);
        var hostile = CombatFixture.HostileNode(session);
        var train = new TrainId(1);
        var inventory = ((Lookup<TrainView>.Found)session.Read().FindTrain(train)).Value.Inventory;
        CombatFixture.Send(session, hostile, train);
        var batch = DeletionBatch(session, train, hostile);
        var names = batch.Select(value => value.GetType().Name).ToArray();
        var destructionIndex = Array.IndexOf(names, nameof(CoreEvent.CombatDestruction));
        var compositionIndex = Array.IndexOf(names, nameof(CoreEvent.TrainCompositionChanged));
        var reportIndex = Array.IndexOf(names, nameof(CoreEvent.RunReported));
        var endedIndex = Array.IndexOf(names, nameof(CoreEvent.CombatEnded));
        Assert.IsTrue(destructionIndex >= 0 && destructionIndex < compositionIndex);
        Assert.IsTrue(compositionIndex < reportIndex && reportIndex < endedIndex);

        var destroyed = batch.OfType<CoreEvent.CombatDestruction>().Single().Units
            .OfType<DestroyedUnit.Instance>().Single(unit => unit.Value.Instance is InstanceId.Vehicle).Value;
        AssertPresentation(destroyed, CombatFixture.ArmedVehicle, hostile);
        var lastHit = batch.OfType<CoreEvent.CombatHits>().SelectMany(value => value.Hits)
            .Last(hit => Presentation(hit.Target).Instance == destroyed.Instance);
        AssertPresentation(Presentation(lastHit.Target), CombatFixture.ArmedVehicle, hostile);

        var composition = batch.OfType<CoreEvent.TrainCompositionChanged>().Single(value => value.Train == train);
        Assert.IsEmpty(composition.Vehicles);
        var report = batch.OfType<CoreEvent.RunReported>().Single(value => value.Report.Train == train);
        Assert.AreEqual(RunEndReason.AllVehiclesDestroyed, report.Report.Reason);
        var ended = batch.OfType<CoreEvent.CombatEnded>().Single();
        var player = ended.Summary.Sides.Single(side => side.Owner is CombatOwner.Player);
        var lost = player.UnitsLost.OfType<DestroyedUnit.Instance>()
            .Single(unit => unit.Value.Instance == destroyed.Instance).Value;
        AssertPresentation(lost, CombatFixture.ArmedVehicle, hostile);
        Assert.AreEqual(3, player.CargoLost.Single().Quantity);
        Assert.IsInstanceOfType<Lookup<InventoryView>.Missing>(session.Read().FindInventory(inventory));
        Assert.IsInstanceOfType<Lookup<TrainView>.Missing>(session.Read().FindTrain(train));
    }

    [TestMethod]
    public void Destroying_a_building_frees_its_plot_and_reports_it()
    {
        var content = CombatFixture.Content(damage: 1) with
        {
            Designs = CombatFixture.Content(damage: 1).Designs.Select(design => design.Design != CombatFixture.ArmedBuilding
                ? design : design with
                {
                    Parts = design.Parts.Select(part => part with
                    {
                        MaxDurability = 1000,
                        Capabilities = [new PartCapability.Weapon(1, 1)]
                    }).ToImmutableArray()
                }).ToImmutableArray()
        };
        content = content with
        {
            Designs = content.Designs.Select(design => design.Design != CombatFixture.ArmedVehicle ? design : design with
            {
                Parts = design.Parts.Select(part => part with
                {
                    Capabilities = part.Capabilities.Select(capability => capability is PartCapability.Weapon
                        ? (PartCapability)new PartCapability.Weapon(100, 1) : capability).ToImmutableArray()
                }).ToImmutableArray()
            }).ToImmutableArray()
        };
        using var session = CombatFixture.SessionWithContent(content);
        var hostile = CombatFixture.HostileNode(session);
        var building = HostileBuilding(session, hostile);
        var train = new TrainId(1);
        CombatFixture.Send(session, hostile, train);
        var events = CombatFixture.AdvanceUntil(session,
            view => view.FindInstance(building) is Lookup<InstanceView>.Missing);
        var destroyed = events.OfType<CoreEvent.CombatDestruction>().Single().Units
            .OfType<DestroyedUnit.Instance>().Single(unit => unit.Value.Instance == building).Value;
        AssertPresentation(destroyed, CombatFixture.ArmedBuilding, hostile);
        var lastHit = events.OfType<CoreEvent.CombatHits>().SelectMany(value => value.Hits)
            .Last(hit => Presentation(hit.Target).Instance == building);
        AssertPresentation(Presentation(lastHit.Target), CombatFixture.ArmedBuilding, hostile);

        var settlementId = ((Lookup<LocationView>.Found)session.Read().FindLocation(
            new WorldLocation.Node(hostile))).Value.Occupant is NodeOccupant.Settlement occupant
            ? occupant.Id : throw new AssertFailedException("敌对节点没有聚居地。");
        var settlement = ((Lookup<SettlementView>.Found)session.Read().FindSettlement(settlementId)).Value;
        Assert.IsTrue(settlement.Plots.Any(plot => plot.Occupant is PlotOccupant.Empty));
        var ended = events.OfType<CoreEvent.CombatEnded>().Single();
        var side = ended.Summary.Sides.Single(value => value.Owner is CombatOwner.Settlement);
        var lost = side.UnitsLost.OfType<DestroyedUnit.Instance>().Single(unit => unit.Value.Instance == building).Value;
        AssertPresentation(lost, CombatFixture.ArmedBuilding, hostile);
    }

    [TestMethod]
    public void Losing_the_middle_vehicle_keeps_the_train_inventory_and_survivor_order()
    {
        var content = CombatFixture.Content(damage: 1) with
        {
            Designs = CombatFixture.Content(damage: 1).Designs.Select(design =>
                design.Design == CombatFixture.ArmedBuilding ? design with
                {
                    Parts = design.Parts.Select(part => part with
                    {
                        Capabilities = [new PartCapability.Weapon(100, 1)]
                    }).ToImmutableArray()
                } : design.Design == CombatFixture.ArmedVehicle || design.Design == CombatFixture.UnarmedVehicle
                    ? design with
                    {
                        StructureHitWeight = 1,
                        Parts = design.Parts.Select(part => part with { HitWeight = 0 }).ToImmutableArray()
                    } : design).ToImmutableArray()
        };
        var cargo = ImmutableArray.Create(new GoodsQuantity(new GoodId.Voxel(WorldFixture.Material), 3));
        using var session = CombatFixture.SessionWithContent(content, cargo: cargo,
            designs: [CombatFixture.UnarmedVehicle, CombatFixture.ArmedVehicle, CombatFixture.UnarmedVehicle]);
        var hostile = CombatFixture.HostileNode(session);
        var train = new TrainId(1);
        var initial = ((Lookup<TrainView>.Found)session.Read().FindTrain(train)).Value;
        Assert.AreEqual(3, initial.Vehicles.Length);
        var middle = initial.Vehicles[1];
        CombatFixture.Send(session, hostile, train);
        var events = CombatFixture.AdvanceUntil(session, view => view.FindTrain(train) is
            Lookup<TrainView>.Found { Value.Vehicles.Length: 2 }, 2000);
        var atNode = ((Lookup<TrainView>.Found)session.Read().FindTrain(train)).Value;
        var destroyed = events.OfType<CoreEvent.CombatDestruction>().SelectMany(value => value.Units)
            .OfType<DestroyedUnit.Instance>().Select(unit => unit.Value.Instance).OfType<InstanceId.Vehicle>()
            .Select(vehicle => vehicle.Id).ToHashSet();
        CollectionAssert.AreEqual(new[] { middle }, destroyed.ToArray());
        var expected = initial.Vehicles.Where(vehicle => !destroyed.Contains(vehicle)).ToImmutableArray();
        Assert.AreEqual(2, expected.Length);
        Assert.IsFalse(expected.Contains(middle));
        var composition = events.OfType<CoreEvent.TrainCompositionChanged>()
            .Last(value => value.Train == train && value.Vehicles.Length == 2);
        CollectionAssert.AreEqual(expected.ToArray(), composition.Vehicles.ToArray());
        var view = ((Lookup<TrainView>.Found)session.Read().FindTrain(train)).Value;
        CollectionAssert.AreEqual(expected.ToArray(), view.Vehicles.ToArray());
        Assert.AreEqual(atNode.Position, view.Position);
        Assert.AreEqual(atNode.OrderFacing, view.OrderFacing);
        var length = ((DesignKind.Vehicle)content.Designs.Single(design =>
            design.Design == CombatFixture.ArmedVehicle).Kind).LengthCells * 0.5;
        Assert.AreEqual(expected.Length * length, view.LengthMeters, 1e-9);
        var inventory = ((Lookup<InventoryView>.Found)session.Read().FindInventory(view.Inventory)).Value;
        Assert.AreEqual(3, inventory.Stacks.Single().Quantity);
        Assert.IsFalse(events.OfType<CoreEvent.CombatEnded>().Any());
    }

    [TestMethod]
    public void Parked_npc_vehicle_participates_and_is_removed_when_destroyed()
    {
        var content = CombatFixture.Content(damage: 1, builder: true, parked: true) with
        {
            Designs = CombatFixture.Content(damage: 1, builder: true, parked: true).Designs.Select(design =>
                design.Design == CombatFixture.ArmedVehicle ? design with
                {
                    Parts = design.Parts.Select(part => part with
                    {
                        Capabilities = part.Capabilities.Select(capability => capability is PartCapability.Weapon
                            ? (PartCapability)new PartCapability.Weapon(100, 1) : capability).ToImmutableArray()
                    }).ToImmutableArray()
                } : design.Design == CombatFixture.ParkedVehicle ? design with
                {
                    StructureHitWeight = 1,
                    Parts = design.Parts.Select(part => part with { HitWeight = 0 }).ToImmutableArray()
                } : design).ToImmutableArray()
        };
        using var session = CombatFixture.SessionWithContent(content, parked: true);
        var hostile = CombatFixture.HostileNode(session);
        var train = new TrainId(1);
        var settlementId = ((Lookup<LocationView>.Found)session.Read().FindLocation(
            new WorldLocation.Node(hostile))).Value.Occupant is NodeOccupant.Settlement occupant
            ? occupant.Id : throw new AssertFailedException("敌对节点没有聚居地。");
        var parked = ((Lookup<SettlementView>.Found)session.Read().FindSettlement(settlementId)).Value
            .ParkedVehicles.Single();
        var vehicle = new InstanceId.Vehicle(parked);
        var before = ((Lookup<SettlementView>.Found)session.Read().FindSettlement(settlementId)).Value;
        CollectionAssert.Contains(before.VehiclesForSale.ToArray(), parked);

        CombatFixture.Send(session, hostile, train);
        var events = CombatFixture.AdvanceUntil(session, view => view.World.Combats.Any());
        Assert.IsTrue(CombatFixture.Participant(session, new CombatUnitId.Instance(vehicle)).Retreating == false);
        Assert.IsTrue(CombatRules.Armed(session.State.World, session.State.Content,
            session.State.World.Instances.Single(instance => instance.Id == vehicle)));
        var firstTrainHit = events.OfType<CoreEvent.CombatHits>().SelectMany(value => value.Hits)
            .First(hit => hit.Source is AttackSource.Weapon weapon && weapon.Instance.Instance is InstanceId.Vehicle);
        Assert.AreEqual(HostileBuilding(session, hostile), Presentation(firstTrainHit.Target).Instance);

        var destroyedEvents = CombatFixture.AdvanceUntil(session,
            view => view.FindInstance(vehicle) is Lookup<InstanceView>.Missing, 1000);
        var destroyed = destroyedEvents.OfType<CoreEvent.CombatDestruction>().Single().Units
            .OfType<DestroyedUnit.Instance>().Single(unit => unit.Value.Instance == vehicle).Value;
        AssertPresentation(destroyed, CombatFixture.ParkedVehicle, hostile);
        var after = ((Lookup<SettlementView>.Found)session.Read().FindSettlement(settlementId)).Value;
        Assert.IsFalse(after.ParkedVehicles.Contains(parked));
        Assert.IsFalse(after.VehiclesForSale.Contains(parked));
    }

    [TestMethod]
    public void Destroying_a_production_building_merges_its_output_buffer_into_storage()
    {
        var content = CombatFixture.Content(damage: 1, builder: true) with
        {
            Designs = CombatFixture.Content(damage: 1, builder: true).Designs.Select(design =>
                design.Design == CombatFixture.ArmedVehicle ? design with
                {
                    Parts = design.Parts.Select(part => part with
                    {
                        Capabilities = part.Capabilities.Select(capability => capability is PartCapability.Weapon
                            ? (PartCapability)new PartCapability.Weapon(100, 1) : capability).ToImmutableArray()
                    }).ToImmutableArray()
                } : design.Design == CombatFixture.ArmedBuilding ? design with
                {
                    MaxStructureDurability = 1000
                } : design).ToImmutableArray()
        };
        using var original = CombatFixture.SessionWithContent(content);
        var hostile = CombatFixture.HostileNode(original);
        var train = new TrainId(1);
        var settlementId = ((Lookup<LocationView>.Found)original.Read().FindLocation(
            new WorldLocation.Node(hostile))).Value.Occupant is NodeOccupant.Settlement occupant
            ? occupant.Id : throw new AssertFailedException("敌对节点没有聚居地。");
        var building = (InstanceId.Building)Building(original, hostile, CombatFixture.BuilderBuilding);
        var inventory = original.State.World.Settlements.Single(item => item.Id == settlementId).Inventory;
        var capacity = ((Lookup<InventoryView>.Found)original.Read().FindInventory(inventory)).Value.Capacity;
        Assert.IsTrue(capacity > 0);
        var state = original.State;
        original.Dispose();
        var good = new GoodId.Voxel(WorldFixture.Material);
        var world = state.World with
        {
            Settlements = state.World.Settlements.Select(settlement => settlement.Id != settlementId
                ? settlement : settlement with
                {
                    ProductionFacilities =
                    [
                        new ProductionFacilityState(new FacilityKey(building.Id, CapabilityKind.Production),
                            null, [new(good, 7)], 0, [], 0, 0, 0)
                    ]
                }).ToImmutableArray(),
            Inventories = state.World.Inventories.Select(item => item.Id != inventory
                ? item : item with { Goods = [new(good, capacity)] }).ToImmutableArray()
        };
        using var session = new CoreSession(state with { World = world });
        CombatFixture.Accepted(session.Submit(new CoreCommand.SetTrainEngagement(train,
            new(TargetPriority.ArmedFirst, TargetScope.All, EncounterAction.Engage, 0)), CommandSource.AutomationTest));
        CombatFixture.Send(session, hostile, train);
        CombatFixture.AdvanceUntil(session, view => view.World.Combats.Any());
        var combat = session.Read().World.Combats.Single();
        CombatFixture.Accepted(session.Submit(new CoreCommand.FocusFire(combat.Id, new CombatController.Train(train),
            new WorldObjectId.Instance(building)), CommandSource.AutomationTest));
        CombatFixture.AdvanceUntil(session, view => view.FindInstance(building) is Lookup<InstanceView>.Missing, 1000);
        var after = ((Lookup<InventoryView>.Found)session.Read().FindInventory(inventory)).Value;
        Assert.AreEqual(capacity + 7, after.Used);
        Assert.AreEqual(capacity + 7, after.Stacks.Single(stack => stack.Good == good).Quantity);
        Assert.IsTrue(after.Used > after.Capacity);
    }

    [TestMethod]
    public void Destroying_a_station_reduces_the_node_platform_count()
    {
        var content = CombatFixture.Content(damage: 1) with
        {
            Designs = CombatFixture.Content(damage: 1).Designs.Select(design =>
                design.Design == CombatFixture.ArmedVehicle ? design with
                {
                    Parts = design.Parts.Select(part => part with
                    {
                        Capabilities = part.Capabilities.Select(capability => capability is PartCapability.Weapon
                            ? (PartCapability)new PartCapability.Weapon(100, 1) : capability).ToImmutableArray()
                    }).ToImmutableArray()
                } : design.Design == WorldFixture.Station ? design with { MaxStructureDurability = 1 }
                : design).ToImmutableArray()
        };
        using var session = CombatFixture.SessionWithContent(content);
        var hostile = CombatFixture.HostileNode(session);
        var train = new TrainId(1);
        var settlementId = ((Lookup<LocationView>.Found)session.Read().FindLocation(
            new WorldLocation.Node(hostile))).Value.Occupant is NodeOccupant.Settlement occupant
            ? occupant.Id : throw new AssertFailedException("敌对节点没有聚居地。");
        var station = Station(session, hostile);
        var platforms = content.Designs.Single(design => design.Design == WorldFixture.Station).Parts
            .Count(part => part.Capabilities.Any(capability => capability is PartCapability.Platform));
        Assert.IsTrue(platforms > 0);
        Assert.AreEqual(platforms, ((Lookup<SettlementView>.Found)session.Read().FindSettlement(settlementId)).Value.Platforms);
        CombatFixture.Accepted(session.Submit(new CoreCommand.SetTrainEngagement(train,
            new(TargetPriority.ArmedFirst, TargetScope.All, EncounterAction.Engage, 0)), CommandSource.AutomationTest));
        CombatFixture.Send(session, hostile, train);
        CombatFixture.AdvanceUntil(session, view => view.World.Combats.Any());
        var combat = session.Read().World.Combats.Single();
        CombatFixture.Accepted(session.Submit(new CoreCommand.FocusFire(combat.Id, new CombatController.Train(train),
            new WorldObjectId.Instance(station)), CommandSource.AutomationTest));
        CombatFixture.AdvanceUntil(session, view => view.FindInstance(station) is Lookup<InstanceView>.Missing, 1000);
        Assert.AreEqual(0, ((Lookup<SettlementView>.Found)session.Read().FindSettlement(settlementId)).Value.Platforms);
        Assert.IsTrue(session.Read().World.Combats.Any());
    }

    [TestMethod]
    public void Hit_weights_follow_the_documented_key_layout()
    {
        var part = CombatFixture.Content(damage: 1).Designs
            .Single(design => design.Design == CombatFixture.ArmedBuilding).Parts.Single();
        var content = CombatFixture.Content(damage: 1) with
        {
            Designs = CombatFixture.Content(damage: 1).Designs.Select(design =>
                design.Design != CombatFixture.ArmedBuilding ? design : design with
                {
                    StructureHitWeight = 1,
                    Parts =
                    [
                        part with { HitWeight = 1 },
                        part with { Id = new PartId(1), HitWeight = 2, Capabilities = [] },
                        part with { Id = new PartId(2), HitWeight = 3, Capabilities = [] }
                    ]
                }).ToImmutableArray()
        };
        using var session = CombatFixture.SessionWithContent(content);
        var hostile = CombatFixture.HostileNode(session);
        var train = new TrainId(1);
        CombatFixture.Send(session, hostile, train);
        EventEnvelope? envelope = null;
        CombatHit? shot = null;
        for (var step = 0; step < 1000 && shot is null; step++)
        {
            var result = session.Advance(1);
            Assert.IsNull(result.Fault);
            foreach (var item in result.Events)
            {
                if (item.Event is not CoreEvent.CombatHits hits) continue;
                var match = hits.Hits.FirstOrDefault(hit => hit.Source is AttackSource.Weapon weapon
                    && weapon.Instance.Instance is InstanceId.Vehicle);
                if (match is null) continue;
                envelope = item;
                shot = match;
            }
        }
        Assert.IsNotNull(envelope);
        Assert.IsNotNull(shot);
        var weapon = (AttackSource.Weapon)shot.Source;
        var design = content.Designs.Single(item => item.Design == CombatFixture.ArmedBuilding);
        var buckets = new List<(long Weight, PartId? Part)> { (design.StructureHitWeight, null) };
        buckets.AddRange(design.Parts.OrderBy(item => item.Id.Value).Where(item => item.HitWeight > 0)
            .Select(item => (item.HitWeight, (PartId?)item.Id)));
        var total = buckets.Sum(bucket => bucket.Weight);
        Assert.AreEqual(7, total);
        var vehicle = (InstanceId.Vehicle)weapon.Instance.Instance;
        var key = new RandomKey(session.State.World.Source.WorldSeed, "combat.hit.v1",
            [session.Read().World.Combats.Single().Id.Value, (ulong)envelope.Position.CompletedSteps, 0,
                vehicle.Id.Value, (ulong)weapon.WeaponPart.Value]);
        var choice = WorldRandom.Integer(KeyedRandom.Sample(key), 0, total - 1);
        PartId? expected = null;
        foreach (var bucket in buckets)
        {
            if (choice < bucket.Weight)
            {
                expected = bucket.Part;
                break;
            }
            choice -= bucket.Weight;
        }
        if (expected is { } hitPart)
            Assert.AreEqual(hitPart, ((HitTarget.Part)shot.Target).HitPart);
        else
            Assert.IsInstanceOfType<HitTarget.Structure>(shot.Target);
    }

    private static List<CoreEvent> DeletionBatch(ICoreSession session, TrainId train, NodeId hostile)
    {
        for (var step = 0; step < 1000; step++)
        {
            var result = session.Advance(1);
            Assert.IsNull(result.Fault);
            var events = result.Events.Select(envelope => envelope.Event).ToList();
            if (events.OfType<CoreEvent.TrainCompositionChanged>()
                .Any(value => value.Train == train && value.Vehicles.IsEmpty)) return events;
            Assert.IsFalse(events.OfType<CoreEvent.CombatEnded>().Any(value => value.Location is WorldLocation.Node node
                && node.Id == hostile));
        }
        Assert.Fail("未在限定步数内删除列车。");
        return [];
    }

    private static InstanceId HostileBuilding(ICoreSession session, NodeId node)
        => Building(session, node, CombatFixture.ArmedBuilding);

    private static InstanceId Building(ICoreSession session, NodeId node, DesignHash design)
        => ((Lookup<LocationView>.Found)session.Read().FindLocation(new WorldLocation.Node(node))).Value.Instances
            .Single(instance => ((Lookup<InstanceView>.Found)session.Read().FindInstance(instance)).Value.Design
                == design);

    private static InstanceId Station(ICoreSession session, NodeId node) => Building(session, node, WorldFixture.Station);

    private static CoreContentProjection StructureTarget(long structure)
        => CombatFixture.Content(damage: 10) with
        {
            Designs = CombatFixture.Content(damage: 10).Designs.Select(design => design.Design != CombatFixture.ArmedBuilding
                ? design : design with
                {
                    StructureHitWeight = 1, MaxStructureDurability = structure,
                    Parts = design.Parts.Select(part => part with
                    {
                        HitWeight = 0, Capabilities = [new PartCapability.Weapon(1, 1)]
                    }).ToImmutableArray()
                }).ToImmutableArray()
        };

    private static InstanceView Instance(ICoreSession session, InstanceId id)
        => ((Lookup<InstanceView>.Found)session.Read().FindInstance(id)).Value;

    private static InstancePresentationRef Presentation(HitTarget target) => target switch
    {
        HitTarget.Structure structure => structure.Instance,
        HitTarget.Part part => part.Instance,
        _ => throw new AssertFailedException("不支持的目标。")
    };

    private static void AssertPresentation(InstancePresentationRef reference, DesignHash design, NodeId node)
    {
        Assert.AreEqual(design, reference.Design);
        Assert.AreEqual(new WorldLocation.Node(node), reference.Location);
    }
}
