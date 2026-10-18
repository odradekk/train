using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
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
    public void Part_damage_overflows_to_structure_and_reports_the_destroyed_part()
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
        CombatFixture.Send(session, hostile, train);
        var events = CombatFixture.AdvanceUntilEvent(session, value => value is CoreEvent.CombatDestruction destruction
            && destruction.Parts.Any(part => part.Instance.Instance == building));
        var destruction = events.OfType<CoreEvent.CombatDestruction>()
            .First(value => value.Parts.Any(part => part.Instance.Instance == building));
        Assert.IsTrue(destruction.Parts.Any(part => part.Instance.Instance == building));
        var view = ((Lookup<InstanceView>.Found)session.Read().FindInstance(building)).Value;
        Assert.IsTrue(view.Structure.EffectiveCurrent < view.Structure.Maximum);
        Assert.IsTrue(view.Parts.Single().Destroyed);
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
        var events = CombatFixture.AdvanceUntil(session, view => view.FindTrain(train) is Lookup<TrainView>.Missing);
        Assert.IsTrue(events.OfType<CoreEvent.CombatDestruction>().Any(value =>
            value.Units.Any(unit => unit is DestroyedUnit.Instance instance
                && instance.Value.Instance is InstanceId.Vehicle)));
        var composition = events.OfType<CoreEvent.TrainCompositionChanged>().Single(value => value.Train == train);
        Assert.IsEmpty(composition.Vehicles);
        var report = events.OfType<CoreEvent.RunReported>().Single(value => value.Report.Train == train);
        Assert.AreEqual(RunEndReason.AllVehiclesDestroyed, report.Report.Reason);
        var names = events.Select(value => value.GetType().Name).ToArray();
        Assert.IsTrue(Array.IndexOf(names, nameof(CoreEvent.CombatDestruction))
            < Array.IndexOf(names, nameof(CoreEvent.TrainCompositionChanged)));
        Assert.IsTrue(Array.IndexOf(names, nameof(CoreEvent.TrainCompositionChanged))
            < Array.IndexOf(names, nameof(CoreEvent.RunReported)));
        Assert.IsTrue(Array.IndexOf(names, nameof(CoreEvent.RunReported))
            < Array.IndexOf(names, nameof(CoreEvent.CombatEnded)));
        var ended = events.OfType<CoreEvent.CombatEnded>().Single();
        var player = ended.Summary.Sides.Single(side => side.Owner is CombatOwner.Player);
        Assert.IsTrue(player.UnitsLost.Any(unit => unit is DestroyedUnit.Instance instance
            && instance.Value.Instance is InstanceId.Vehicle));
        Assert.AreEqual(3, player.CargoLost.Single().Quantity);
        Assert.IsInstanceOfType<Lookup<InventoryView>.Missing>(session.Read().FindInventory(inventory));
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
        Assert.IsTrue(events.OfType<CoreEvent.CombatDestruction>().Any(value =>
            value.Units.Any(unit => unit is DestroyedUnit.Instance instance
                && instance.Value.Instance == building)));
        var settlementId = ((Lookup<LocationView>.Found)session.Read().FindLocation(
            new WorldLocation.Node(hostile))).Value.Occupant is NodeOccupant.Settlement occupant
            ? occupant.Id : throw new AssertFailedException("敌对节点没有聚居地。");
        var settlement = ((Lookup<SettlementView>.Found)session.Read().FindSettlement(settlementId)).Value;
        Assert.IsTrue(settlement.Plots.Any(plot => plot.Occupant is PlotOccupant.Empty));
        var ended = events.OfType<CoreEvent.CombatEnded>().Single();
        Assert.IsTrue(ended.Summary.Sides.Any(side => side.Owner is CombatOwner.Settlement));
    }

    [TestMethod]
    public void Losing_one_vehicle_keeps_the_train_inventory_and_survivor_order()
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
        using var session = CombatFixture.SessionWithContent(content, vehicles: 2, cargo: cargo);
        var hostile = CombatFixture.HostileNode(session);
        var train = new TrainId(1);
        CombatFixture.Send(session, hostile, train);
        var events = CombatFixture.AdvanceUntilEvent(session, value => value is CoreEvent.TrainCompositionChanged changed
            && changed.Train == train && changed.Vehicles.Length == 1);
        var composition = events.OfType<CoreEvent.TrainCompositionChanged>()
            .First(value => value.Train == train && value.Vehicles.Length == 1);
        var view = ((Lookup<TrainView>.Found)session.Read().FindTrain(train)).Value;
        CollectionAssert.AreEqual(composition.Vehicles.ToArray(), view.Vehicles.ToArray());
        var inventory = ((Lookup<InventoryView>.Found)session.Read().FindInventory(view.Inventory)).Value;
        Assert.AreEqual(3, inventory.Stacks.Single().Quantity);
        Assert.IsFalse(events.OfType<CoreEvent.CombatEnded>().Any());
    }

    [TestMethod]
    public void Combat_state_captures_and_decodes()
    {
        using var session = CombatFixture.Session(damage: 1);
        var hostile = CombatFixture.HostileNode(session);
        CombatFixture.Send(session, hostile, new TrainId(1));
        CombatFixture.AdvanceUntil(session, view => view.World.Combats.Any());
        var capture = ((CoreResult<ICoreCapture>.Success)session.Capture(CapturePurpose.Save)).Value;
        var payload = capture.Encode();
        var decoded = Train.Core.CoreFactory.Create().DecodeState(payload);
        Assert.IsInstanceOfType<CoreResult<ICoreCapture>.Success>(decoded);
    }

    private static InstanceId HostileBuilding(ICoreSession session, NodeId node)
        => ((Lookup<LocationView>.Found)session.Read().FindLocation(new WorldLocation.Node(node))).Value.Instances
            .Single(instance => ((Lookup<InstanceView>.Found)session.Read().FindInstance(instance)).Value.Design
                == CombatFixture.ArmedBuilding);
}
