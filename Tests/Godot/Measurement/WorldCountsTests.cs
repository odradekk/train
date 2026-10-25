using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.App.Measurement;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Core.ContractSupport;

namespace Train.Godot.Tests.Measurement;

[TestClass]
public sealed class WorldCountsTests
{
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public void Counts_match_fixed_world_globally_and_at_each_selected_node(int scope)
    {
        var (view, content) = World();
        var actual = WorldCounts.Read(view, content, scope == 0 ? null : new NodeId((ulong)scope));
        var expected = new (string Key, double Global, double Node1, double Node2)[]
        {
            ("armed_buildings", 2, 1, 0),
            ("armed_combats", 2, 1, 0),
            ("armed_line_combats", 1, 0, 0),
            ("armed_node_combats", 1, 1, 0),
            ("armed_trains_in_combat", 1, 1, 0),
            ("buildings", 4, 2, 1),
            ("creatures", 15, 5, 4),
            ("creatures_guarding", 2, 2, 0),
            ("creatures_raiding", 8, 3, 0),
            ("creatures_wandering", 4, 0, 4),
            ("dismantles_working", 1, 1, 0),
            ("facilities_running", 2, 1, 1),
            ("jobs_working", 3, 2, 0),
            ("large_creatures", 3, 1, 0),
            ("largest_train_meters", 50, 20, 50),
            ("largest_train_vehicles", 2, 2, 1),
            ("nodes", 3, 1, 1),
            ("occupied_platforms", 3, 2, 1),
            ("platforms", 6, 3, 2),
            ("player_settlements", 1, 1, 0),
            ("settlements", 3, 1, 1),
            ("titan_builds_working", 1, 1, 0),
            ("titan_trains_in_plan", 2, 1, 0),
            ("trains_in_plan", 4, 3, 0),
            ("trains_queued", 1, 1, 0),
            ("trains_running", 1, 0, 0),
            ("trains_servicing", 1, 1, 0),
            ("vehicles_in_plan", 6, 4, 0)
        };
        CollectionAssert.AreEquivalent(expected.Select(row => row.Key).ToArray(), actual.Keys.ToArray());
        foreach (var row in expected)
            Assert.AreEqual(scope switch { 0 => row.Global, 1 => row.Node1, _ => row.Node2 },
                actual[row.Key], $"scope={scope}, key={row.Key}");
    }

    private static (FixedCoreReadView, FixedContentView) World()
    {
        WorldLocation node1 = new WorldLocation.Node(new(1));
        WorldLocation node2 = new WorldLocation.Node(new(2));
        WorldLocation node3 = new WorldLocation.Node(new(3));
        WorldLocation edge = new WorldLocation.Edge(new(1), 10);
        var terrain = new TerrainView(new("plain"), 0, []);
        var trains = new[]
        {
            Train(1, 1, TrainActivity.Combat, [new(1), new(2)], 20),
            Train(2, 1, TrainActivity.Servicing, [new(3)], 10),
            Train(3, 1, TrainActivity.Waiting, [new(4)], 8) with
            {
                Blockers = [new("platform_wait", "position", new WorldObjectId.Train(new(3)), [])],
                PlatformQueueEnteredStep = 0
            },
            Train(4, 2, TrainActivity.Stopped, [new(5)], 50),
            Train(5, 1, TrainActivity.Running, [new(6), new(7)], 40) with
            {
                Position = new TrainPosition.Edge(new(1), 1, TravelDirection.Forward, 10)
            }
        };
        var settlements = new[]
        {
            Settlement(1, SettlementRelation.PlayerOwned, [new(1), new(2)], [new(1), new(2)], 3, 2, ProductionState.Running),
            Settlement(2, SettlementRelation.NonHostile, [new(3)], [new(4)], 2, 1, ProductionState.Running),
            Settlement(3, SettlementRelation.Hostile, [new(4)], [new(3)], 1, 0, ProductionState.Disabled)
        };
        var instances = new[]
        {
            Instance(new InstanceId.Building(new(1)), "building", node1, SettlementRelation.PlayerOwned,
                new InstanceDetails.Building(new(1), PlotSize.Small)),
            Instance(new InstanceId.Building(new(2)), "building", node1, SettlementRelation.PlayerOwned,
                new InstanceDetails.Building(new(2), PlotSize.Small)) with
                { Parts = [new(new(1), new(0, 10), false, true)] },
            Instance(new InstanceId.Building(new(3)), "building", node2, SettlementRelation.NonHostile,
                new InstanceDetails.Building(new(3), PlotSize.Small)) with
                { Parts = [new(new(1), new(10, 10), true, false)] },
            Instance(new InstanceId.Building(new(4)), "building", node3, SettlementRelation.Hostile,
                new InstanceDetails.Building(new(4), PlotSize.Small)),
            Instance(new InstanceId.Vehicle(new(1)), "titan", node1, SettlementRelation.PlayerOwned,
                new InstanceDetails.Vehicle(SizeClass.Titan, new(1))),
            Instance(new InstanceId.Vehicle(new(2)), "small", node1, SettlementRelation.PlayerOwned,
                new InstanceDetails.Vehicle(SizeClass.Small, new(1))),
            Instance(new InstanceId.Vehicle(new(3)), "small", node1, SettlementRelation.PlayerOwned,
                new InstanceDetails.Vehicle(SizeClass.Small, new(2))),
            Instance(new InstanceId.Vehicle(new(4)), "small", node1, SettlementRelation.PlayerOwned,
                new InstanceDetails.Vehicle(SizeClass.Small, new(3))),
            Instance(new InstanceId.Vehicle(new(5)), "small", node2, SettlementRelation.PlayerOwned,
                new InstanceDetails.Vehicle(SizeClass.Small, new(4))),
            Instance(new InstanceId.Vehicle(new(6)), "titan", edge, SettlementRelation.PlayerOwned,
                new InstanceDetails.Vehicle(SizeClass.Titan, new(5))),
            Instance(new InstanceId.Vehicle(new(7)), "small", edge, SettlementRelation.PlayerOwned,
                new InstanceDetails.Vehicle(SizeClass.Small, new(5)))
        };
        var jobs = new[]
        {
            Job(1, 1, 1, ConstructionKind.Construction, new ConstructionTarget.NewVehicle(new("titan"))),
            Job(2, 1, 1, ConstructionKind.Deconstruction,
                new ConstructionTarget.Existing(new InstanceId.Building(new(2)), new("building"))),
            Job(3, 3, 4, ConstructionKind.Construction, new ConstructionTarget.NewVehicle(new("small"))),
            Job(4, 2, 3, ConstructionKind.Construction, new ConstructionTarget.NewVehicle(new("small"))) with
                { State = ConstructionState.Queued, ActualWorkPerSecond = 0 }
        };
        var groups = new[]
        {
            Group(1, node1, CreatureActivity.Guarding, 2, 1),
            Group(2, node1, CreatureActivity.RaidingSettlement, 3, 0),
            Group(3, node2, CreatureActivity.Wandering, 4, 0),
            Group(4, edge, CreatureActivity.RaidingRailway, 5, 2),
            Group(5, node3, CreatureActivity.Combat, 1, 0)
        };
        var combats = new[]
        {
            new CombatView(new(1), node1, 0, [new(new CombatUnitId.Instance(new InstanceId.Vehicle(new(1))), false, 0, null)]),
            new CombatView(new(2), edge, 0, [new(new CombatUnitId.Instance(new InstanceId.Vehicle(new(6))), false, 0, null)]),
            new CombatView(new(3), node3, 0, [new(new CombatUnitId.Instance(new InstanceId.Building(new(4))), false, 0, null)]),
            new CombatView(new(4), node1, 0, [new(new CombatUnitId.Instance(new InstanceId.Building(new(2))), false, 0, null)])
        };
        var view = new FixedCoreReadView
        {
            World = new(1, new("rail"), 100, 100,
                settlements.Select(s => new NodeMapItem(s.Node, new((double)s.Node.Value, 0), terrain,
                    new NodeOccupant.Settlement(s.Id, s.Relation), [], s.OccupiedPlatforms, s.Platforms, null)).ToImmutableArray(),
                [new(new(1), new(1), new(2), 100, terrain, 1, 1, [], [])],
                trains.Select(t => new TrainMapItem(t.Id, t.Position, t.Activity)).ToImmutableArray(),
                groups.Select(g => new CreatureGroupMapItem(g.Id, g.Location, g.Activity)).ToImmutableArray(),
                combats.Select(c => new CombatMapItem(c.Id, c.Location)).ToImmutableArray()),
            Trains = trains.ToDictionary(t => t.Id, t => (Lookup<TrainView>)new Lookup<TrainView>.Found(t)),
            Settlements = settlements.ToDictionary(s => s.Id, s => (Lookup<SettlementView>)new Lookup<SettlementView>.Found(s)),
            Instances = instances.ToDictionary(i => i.Id, i => (Lookup<InstanceView>)new Lookup<InstanceView>.Found(i)),
            Jobs = jobs.ToDictionary(j => j.Id, j => (Lookup<JobView>)new Lookup<JobView>.Found(new JobView.Construction(j))),
            CreatureGroups = groups.ToDictionary(g => g.Id, g => (Lookup<CreatureGroupView>)new Lookup<CreatureGroupView>.Found(g)),
            Combats = combats.ToDictionary(c => c.Id, c => (Lookup<CombatView>)new Lookup<CombatView>.Found(c))
        };
        var designs = new[]
        {
            Design("building", new DesignKind.Building(PlotSize.Small, BuildingKind.Construction, [])),
            Design("small", new DesignKind.Vehicle(SizeClass.Small, 8)),
            Design("titan", new DesignKind.Vehicle(SizeClass.Titan, 16))
        };
        return (view, new FixedContentView
        {
            Designs = designs.ToDictionary(d => d.Design,
                d => (ContentLookup<DesignEvaluation>)new ContentLookup<DesignEvaluation>.Found(d))
        });
    }

    private static TrainView Train(ulong id, ulong node, TrainActivity activity, ImmutableArray<VehicleId> vehicles, double length)
        => new(new(id), new TrainPosition.Node(new(node), null, null, null, null, NodeTravelPhase.Waiting, 0, null),
            activity, vehicles, new(id + 10), 0, length, 1, 1, 1, 10,
            activity == TrainActivity.Stopped ? null : new(false, [new PlanStop.Waypoint(new(2))]),
            activity == TrainActivity.Stopped ? null : 0, 0, new(TargetPriority.ArmedFirst, TargetScope.All, EncounterAction.Engage, 0),
            [], null, [], TrainOrderFacing.FirstEndLeading, null);

    private static SettlementView Settlement(ulong id, SettlementRelation relation, ImmutableArray<BuildingId> buildings,
        ImmutableArray<JobId> jobs, int platforms, int occupied, ProductionState production)
    {
        var rule = new RuleStatusView(new("rule"), null, []);
        return new(new(id), new(id), new("town"), null, relation, new(id),
            buildings.Select(b => new PlotView(new(b.Value), (int)b.Value, PlotSize.Small,
                [BuildingKind.Construction], new PlotOccupant.Building(b))).ToImmutableArray(),
            [], 10, platforms, occupied, [], [],
            [new(new(buildings[0], CapabilityKind.Production), new ProcessRef.Production(new("process")),
                production != ProductionState.Disabled, 0, production, 1, production == ProductionState.Running ? 1 : 0, [], null, [], [], [])],
            new(0, 0, 0, 0), new(rule, 10, 10, 0, 10, new PopulationExecution.NotStarted([]), null, []),
            new(TargetPriority.ArmedFirst, TargetScope.All), 1, false,
            relation == SettlementRelation.PlayerOwned ? [] : jobs,
            relation == SettlementRelation.PlayerOwned ? jobs : [], [], new(rule, new(100, 0, 0, 0, 0, 100, 0), []));
    }

    private static InstanceView Instance(InstanceId id, string design, WorldLocation location,
        SettlementRelation ownership, InstanceDetails details)
        => new(id, new(design), new(design), location, ownership, details, InstanceLock.None,
            new(100, 100), [new(new(1), new(10, 10), false, false)], []);

    private static ConstructionJobView Job(ulong id, ulong settlement, ulong building, ConstructionKind kind, ConstructionTarget target)
        => new(new(id), kind, new(settlement), new(new(building), CapabilityKind.Construction), target,
            ConstructionState.Working, 0, 100, 10, 0.1, 1, [], [], []);

    private static CreatureGroupView Group(ulong id, WorldLocation location, CreatureActivity activity, int count, int large)
        => new(new(id), null, location, null, activity, 0,
            Enumerable.Range(0, count).Select(i => new CreatureView(new(id * 10 + (ulong)i),
                new(i < large ? "core:large-creature" : "core:small-creature"), 10, 10)).ToImmutableArray());

    private static DesignEvaluation.Complete Design(string name, DesignKind kind)
    {
        var projection = new DesignProjection(new(name), new(name), kind,
            [new(new(1), new CoreDefinitionReference.Voxel(new("weapon")), new GoodId.Voxel(new("weapon")),
                10, 1, new(1), [new PartCapability.Weapon(1, 1)], false)],
            100, 1, 1, new(1), [], [], 0, [], true, []);
        var specification = kind is DesignKind.Vehicle vehicle
            ? (DesignSpecification)new DesignSpecification.Vehicle(vehicle.Size)
            : new DesignSpecification.Building(PlotSize.Small, BuildingKind.Construction, []);
        return new(new(name), new(name), new(new(name, ""), specification, new([], [], [])),
            new(new("input"), projection, []), new(null, null, null, true, null, null, [], [], [], [], []), true, []);
    }
}
