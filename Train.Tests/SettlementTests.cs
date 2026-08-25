using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Domain.Settlements;

namespace Train.Tests;

[TestClass]
public sealed class SettlementTests
{
    [TestMethod]
    public void Construction_accepts_complete_valid_state_including_empty_collections()
    {
        var mill = new BuildingTypeId("mill");
        var iron = new NaturalResourceTypeId("iron");
        var station = new Station(new StationId("oak-station"));
        var settlement = Create(
            name: "  Oak Town  ",
            x: -3,
            y: 8,
            population: 12,
            buildings: new Dictionary<BuildingTypeId, int> { [mill] = 2 },
            resources: new HashSet<NaturalResourceTypeId> { iron },
            station: station);

        Assert.AreEqual("oak", settlement.Id.Value);
        Assert.AreEqual("Oak Town", settlement.Name);
        Assert.AreEqual(new SettlementTypeId("city"), settlement.Type);
        Assert.AreEqual(new MapPosition(-3, 8), settlement.Position);
        Assert.AreEqual(12, settlement.Population);
        Assert.AreEqual(2, settlement.Buildings[mill]);
        Assert.IsTrue(settlement.NaturalResourceTypes.Contains(iron));
        Assert.AreEqual(station, settlement.Station);

        var empty = Create();
        Assert.AreEqual(0, empty.Buildings.Count);
        Assert.AreEqual(0, empty.NaturalResourceTypes.Count);
        Assert.IsNull(empty.Station);
    }

    [TestMethod]
    public void Construction_rejects_invalid_arguments()
    {
        Assert.ThrowsException<ArgumentNullException>(() => Create(name: null!));
        Assert.ThrowsException<ArgumentException>(() => Create(name: "  "));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => Create(population: 0));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => Create(population: -1));
        Assert.ThrowsException<ArgumentNullException>(() => new Settlement(
            new SettlementId("oak"),
            "Oak Town",
            new SettlementTypeId("city"),
            new MapPosition(0, 0),
            1,
            null!,
            new HashSet<NaturalResourceTypeId>()));
        Assert.ThrowsException<ArgumentNullException>(() => new Settlement(
            new SettlementId("oak"),
            "Oak Town",
            new SettlementTypeId("city"),
            new MapPosition(0, 0),
            1,
            new Dictionary<BuildingTypeId, int>(),
            null!));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => Create(
            buildings: new Dictionary<BuildingTypeId, int> { [new BuildingTypeId("mill")] = -1 }));
        Assert.ThrowsException<ArgumentException>(() => new Settlement(
            default,
            "Oak Town",
            new SettlementTypeId("city"),
            new MapPosition(0, 0),
            1,
            new Dictionary<BuildingTypeId, int>(),
            new HashSet<NaturalResourceTypeId>()));
    }

    [TestMethod]
    public void Rename_type_and_population_mutate_without_changing_identity()
    {
        var settlement = Create(name: "Oak Town", type: "city", population: 10);
        var id = settlement.Id;

        settlement.Rename("Pine Town");
        settlement.ChangeType(new SettlementTypeId("industrial"));
        settlement.SetPopulation(25);

        Assert.AreEqual(id, settlement.Id);
        Assert.AreEqual("Pine Town", settlement.Name);
        Assert.AreEqual(new SettlementTypeId("industrial"), settlement.Type);
        Assert.AreEqual(25, settlement.Population);

        settlement.Rename("  Pine Town  ");
        settlement.ChangeType(new SettlementTypeId("industrial"));
        settlement.SetPopulation(25);
        Assert.AreEqual("Pine Town", settlement.Name);

        Assert.ThrowsException<ArgumentException>(() => settlement.Rename(" "));
        Assert.ThrowsException<ArgumentException>(() => settlement.ChangeType(default));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => settlement.SetPopulation(0));
    }

    [TestMethod]
    public void Position_and_natural_resource_types_remain_fixed()
    {
        var iron = new NaturalResourceTypeId("iron");
        var input = new HashSet<NaturalResourceTypeId> { iron };
        var settlement = Create(x: 4, y: -9, resources: input);

        input.Add(new NaturalResourceTypeId("coal"));
        settlement.Rename("Renamed");
        settlement.SetPopulation(11);

        Assert.AreEqual(new MapPosition(4, -9), settlement.Position);
        Assert.AreEqual(1, settlement.NaturalResourceTypes.Count);
        Assert.IsTrue(settlement.NaturalResourceTypes.Contains(iron));
        Assert.IsFalse(settlement.NaturalResourceTypes.Contains(new NaturalResourceTypeId("coal")));
    }

    [TestMethod]
    public void Building_counts_are_controlled_and_omit_zero_entries()
    {
        var mill = new BuildingTypeId("mill");
        var settlement = Create();

        settlement.SetBuildingCount(mill, 3);
        Assert.AreEqual(3, settlement.Buildings[mill]);

        settlement.SetBuildingCount(mill, 3);
        settlement.SetBuildingCount(mill, 1);
        Assert.AreEqual(1, settlement.Buildings[mill]);

        settlement.SetBuildingCount(mill, 0);
        Assert.IsFalse(settlement.Buildings.ContainsKey(mill));
        settlement.SetBuildingCount(mill, 0);

        Assert.ThrowsException<ArgumentOutOfRangeException>(() => settlement.SetBuildingCount(mill, -2));

        settlement.AttachStation(new Station(new StationId("oak-station")));
        Assert.IsNotNull(settlement.Station);
        Assert.AreEqual(0, settlement.Buildings.Count);
    }

    [TestMethod]
    public void Station_lifecycle_enforces_zero_or_one()
    {
        var settlement = Create();
        var first = new Station(new StationId("first"));
        var second = new Station(new StationId("second"));

        settlement.AttachStation(first);
        Assert.AreEqual(first, settlement.Station);
        Assert.ThrowsException<InvalidOperationException>(() => settlement.AttachStation(second));
        Assert.AreEqual(first.Id, settlement.Station?.Id);

        settlement.DetachStation();
        Assert.IsNull(settlement.Station);
        Assert.ThrowsException<InvalidOperationException>(() => settlement.DetachStation());

        settlement.AttachStation(second);
        Assert.AreEqual(second, settlement.Station);
        Assert.ThrowsException<ArgumentNullException>(() => settlement.AttachStation(null!));
    }

    [TestMethod]
    public void Collection_inputs_are_copied_and_exposed_state_is_read_only()
    {
        var mill = new BuildingTypeId("mill");
        var iron = new NaturalResourceTypeId("iron");
        var buildings = new Dictionary<BuildingTypeId, int> { [mill] = 2 };
        var resources = new HashSet<NaturalResourceTypeId> { iron };
        var settlement = Create(buildings: buildings, resources: resources);

        buildings[mill] = 99;
        buildings[new BuildingTypeId("warehouse")] = 1;
        resources.Add(new NaturalResourceTypeId("coal"));

        Assert.AreEqual(2, settlement.Buildings[mill]);
        Assert.AreEqual(1, settlement.Buildings.Count);
        Assert.AreEqual(1, settlement.NaturalResourceTypes.Count);

        var buildingView = (ICollection<KeyValuePair<BuildingTypeId, int>>)settlement.Buildings;
        Assert.IsTrue(buildingView.IsReadOnly);
        Assert.ThrowsException<NotSupportedException>(() => buildingView.Add(
            new KeyValuePair<BuildingTypeId, int>(new BuildingTypeId("shop"), 1)));

        var resourceView = (ICollection<NaturalResourceTypeId>)settlement.NaturalResourceTypes;
        Assert.IsTrue(resourceView.IsReadOnly);
        Assert.ThrowsException<NotSupportedException>(() => resourceView.Add(new NaturalResourceTypeId("coal")));
    }

    [TestMethod]
    public void Entity_equality_uses_stable_identity()
    {
        var left = Create(id: "oak", name: "Oak Town", population: 4);
        var right = Create(id: "oak", name: "Renamed", type: "industrial", population: 40);
        var other = Create(id: "pine", name: "Oak Town");
        var firstStation = new Station(new StationId("dock"));
        var secondStation = new Station(new StationId("dock"));

        left.Rename("Changed");
        Assert.AreEqual(left, right);
        Assert.AreEqual(left.GetHashCode(), right.GetHashCode());
        Assert.AreNotEqual(left, other);
        Assert.AreEqual(firstStation, secondStation);
        Assert.AreNotEqual(firstStation, new Station(new StationId("yard")));
    }

    private static Settlement Create(
        string id = "oak",
        string name = "Oak Town",
        string type = "city",
        int x = 0,
        int y = 0,
        int population = 10,
        IReadOnlyDictionary<BuildingTypeId, int>? buildings = null,
        IReadOnlySet<NaturalResourceTypeId>? resources = null,
        Station? station = null) =>
        new(
            new SettlementId(id),
            name,
            new SettlementTypeId(type),
            new MapPosition(x, y),
            population,
            buildings ?? new Dictionary<BuildingTypeId, int>(),
            resources ?? new HashSet<NaturalResourceTypeId>(),
            station);
}
