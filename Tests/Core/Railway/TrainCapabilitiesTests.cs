using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.Railway;
using Train.Core.Tests.World;
using Train.Core.World;

namespace Train.Core.Tests.Railway;

[TestClass]
public sealed class TrainCapabilitiesTests
{
    [TestMethod]
    public void Mixed_vehicles_use_design_length_and_mass_with_cargo_and_live_propulsion()
    {
        var first = Design(SizeClass.Small, 4, 10, [Part(1, new PartCapability.Propulsion(30, 12)), Part(2, new PartCapability.Control())]);
        var second = Design(SizeClass.Large, 10, 20, [Part(3, new PartCapability.Propulsion(20, 8))])
            with { Design = new(new string('D', 64)) };
        var ore = new GoodId.Voxel(new("test:ore"));
        var fuel = new GoodId.Composite(new("test:fuel"));

        var result = Calculate([(first, [(new(1), 1), (new(2), 50)]), (second, [(new(3), 100)])],
            [new(ore, 5), new(fuel, 4)], [Good(ore, 2), Good(fuel, 0.5)]);

        Assert.AreEqual(7.0, result.LengthMeters);
        Assert.AreEqual(42.0, result.MassTons);
        Assert.AreEqual(50.0, result.TractionKilonewtons);
        Assert.AreEqual(8.0, result.MaximumSpeedMetersPerSecond);
        Assert.AreEqual(25.0 / 21, result.Acceleration, 1e-14);
        Assert.IsTrue(result.HasControl);
    }

    [TestMethod]
    public void Damaged_live_parts_keep_full_capability_but_destroyed_and_missing_parts_do_not()
    {
        var design = Design(SizeClass.Small, 6, 12,
            [Part(1, new PartCapability.Propulsion(8, 6)), Part(2, new PartCapability.Propulsion(100, 1)),
                Part(3, new PartCapability.Propulsion(200, 0.5)) with { Missing = true },
                Part(4, new PartCapability.Control()), Part(5, new PartCapability.Control()) with { Missing = true }]);
        var result = Calculate([(design, [(new(1), 1), (new(2), 0), (new(3), 100), (new(4), 0), (new(5), 100)])], [], []);

        Assert.AreEqual(8.0, result.TractionKilonewtons);
        Assert.AreEqual(6.0, result.MaximumSpeedMetersPerSecond);
        Assert.AreEqual(2.0 / 3, result.Acceleration, 1e-14);
        Assert.IsFalse(result.HasControl);
    }

    [TestMethod]
    public void Departure_problems_prioritize_control_then_traction_then_overload()
    {
        var missing = new TrainCapabilities(2, 10, 0, 0, 0, false, []);
        var control = missing.DepartureProblem(new(7), 0.5)!;
        Assert.AreEqual("control_missing", control.Code);
        Assert.AreEqual("Train.Vehicles", control.Field);
        Assert.AreEqual(new WorldObjectId.Train(new(7)), control.Subject);
        var traction = (missing with { HasControl = true }).DepartureProblem(new(7), 0.5)!;
        Assert.AreEqual("traction_missing", traction.Code);
        Assert.AreEqual("Train.TractionKilonewtons", traction.Field);
    }

    [TestMethod]
    public void Cargo_can_prevent_departure_but_acceleration_at_the_minimum_is_allowed()
    {
        var design = Design(SizeClass.Small, 4, 20,
            [Part(1, new PartCapability.Propulsion(10, 5)), Part(2, new PartCapability.Control())]);
        var cargo = new GoodId.Voxel(new("test:cargo"));
        var empty = Calculate([(design, [(new(1), 100), (new(2), 100)])], [], []);
        Assert.IsNull(empty.DepartureProblem(new(1), 0.5));
        var loaded = Calculate([(design, [(new(1), 100), (new(2), 100)])], [new(cargo, 1)], [Good(cargo, 1)]);
        var problem = loaded.DepartureProblem(new(1), 0.5)!;
        Assert.AreEqual("overloaded", problem.Code);
        Assert.AreEqual("Train.Acceleration", problem.Field);
    }

    [TestMethod]
    public void No_live_propulsion_has_zero_speed_and_traction_and_zero_effective_control_is_unusable()
    {
        var design = Design(SizeClass.Small, 4, 10,
            [Part(1, new PartCapability.Propulsion(10, 5)), Part(2, new PartCapability.Control()) with { MaxDurability = 0 }]);
        var result = Calculate([(design, [(new(1), 0), (new(2), 100)])], [], []);
        Assert.AreEqual(0.0, result.TractionKilonewtons);
        Assert.AreEqual(0.0, result.MaximumSpeedMetersPerSecond);
        Assert.AreEqual(0.0, result.Acceleration);
        Assert.IsFalse(result.HasControl);
    }

    [TestMethod]
    public void Cargo_enumeration_order_does_not_change_floating_point_mass()
    {
        var design = Design(SizeClass.Small, 2, 1, []);
        var heavy = new GoodId.Voxel(new("test:a"));
        var small = new GoodId.Voxel(new("test:b"));
        var other = new GoodId.Voxel(new("test:c"));
        ImmutableArray<GoodsQuantity> cargo = [new(heavy, 1_000_000_000), new(small, 1), new(other, 1)];
        ImmutableArray<GoodDefinitionProjection> goods = [Good(heavy, 1_000_000_000), Good(small, 64), Good(other, 64)];
        var first = Calculate([(design, [])], cargo, goods);
        var reversed = Calculate([(design, [])], cargo.Reverse().ToImmutableArray(), goods.Reverse().ToImmutableArray());
        Assert.AreEqual(1e18, first.MassTons);
        Assert.AreEqual(1e18, reversed.MassTons);
    }

    [TestMethod]
    public void Missing_cargo_is_reported_in_canonical_order_and_only_known_mass_is_used()
    {
        var design = Design(SizeClass.Small, 2, 10,
            [Part(1, new PartCapability.Control()), Part(2, new PartCapability.Propulsion(20, 5))]);
        var a = new GoodId.Voxel(new("test:a"));
        var z = new GoodId.Voxel(new("test:z"));
        var c = new GoodId.Composite(new("test:a"));
        var known = new GoodId.Voxel(new("test:known"));
        var result = Calculate([(design, [(new(1), 100), (new(2), 100)])],
            [new(c, 1), new(z, 2), new(known, 3), new(a, 4)], [Good(known, 2)]);
        Assert.AreEqual(16.0, result.MassTons);
        Assert.AreEqual(1.25, result.Acceleration);
        CollectionAssert.AreEqual(new GoodId[] { c, a, z }, result.MissingGoods.ToArray());
        Assert.AreEqual("content_missing", result.DepartureProblem(new(1), 2)!.Code);
        Assert.AreEqual("traction_missing", (result with { TractionKilonewtons = 0 }).DepartureProblem(new(1), 2)!.Code);
        Assert.AreEqual("control_missing", (result with { HasControl = false }).DepartureProblem(new(1), 2)!.Code);
    }

    private static TrainCapabilities Calculate(
        ImmutableArray<(DesignProjection Design, ImmutableArray<(PartId Part, long Current)> Durability)> vehicles,
        ImmutableArray<GoodsQuantity> inventory, ImmutableArray<GoodDefinitionProjection> goods)
    {
        var state = WorldFixture.State();
        var member = state.World.Instances.First(i => i.Id is InstanceId.Vehicle);
        var instances = vehicles.Select((v, index) => member with
        {
            Id = new InstanceId.Vehicle(new((ulong)index + 1)), Design = v.Design.Design,
            Parts = v.Durability.Select(p => new PartDurability(p.Part, p.Current)).ToImmutableArray()
        }).ToImmutableArray();
        var train = state.World.Trains[0] with
        { Vehicles = instances.Select(i => ((InstanceId.Vehicle)i.Id).Id).ToImmutableArray() };
        var world = state.World with
        {
            Instances = instances, Designs = vehicles.Select(v => v.Design).ToImmutableArray(),
            Inventories = [new(train.Inventory, new InventoryOwner.Train(train.Id), inventory, [])],
            DesignGoods = goods.Where(g => g.Good is GoodId.Assembly).ToImmutableArray()
        };
        var content = state.Content with
        {
            Designs = world.Designs,
            Goods = goods.Where(g => g.Good is not GoodId.Assembly)
                .Append(Good(new GoodId.Voxel(new("test:part")), 1)).ToImmutableArray()
        };
        return TrainCapabilities.Calculate(world, content, train);
    }

    private static DesignProjection Design(SizeClass size, int length, double mass, ImmutableArray<PartProjection> parts)
        => new(new(new string('A', 64)), new(new string('B', 64)), new DesignKind.Vehicle(size, length),
            parts, 100, 1, mass, new(1), [], [], 1, [], true, []);

    private static PartProjection Part(int id, PartCapability capability)
        => new(new(id), new CoreDefinitionReference.Voxel(new("test:part")), new GoodId.Voxel(new("test:part")),
            100, 1, new(1), [capability], false);

    private static GoodDefinitionProjection Good(GoodId id, double mass)
        => new(id, new(new string('C', 64)), new(1), mass, true, null);
}
