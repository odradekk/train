using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using Train.Scenes.Contracts;

namespace Train.Scenes.Tests;

internal static class NodeFixtures
{
    internal sealed class DisplayState
    {
        internal ulong Revision;
        internal SceneFrame? Displayed;
    }
    internal static readonly ConditionalWeakTable<NodeScene, DisplayState> Displays = new();

    internal static SceneFrameStamp NextStamp(NodeScene scene) =>
        new(new(Guid.Empty), new(1), new(1), ++Displays.GetValue(scene, static _ => new()).Revision);

    internal static SceneWorldSnapshot EmptyNodeSnapshot(SettlementLayoutProjection layout,
        ImmutableArray<TrainView> trains, double relief = 0, DesignEvaluation? design = null,
        ulong steps = 0, double travelMeters = 80)
    {
        var original = Snapshot(platforms: 0, trains: trains,
            content: Content(layout: layout, design: design), platformLengthMeters: 0, steps: steps);
        var node = original.Core.World.Nodes.Single(item => item.Id == NodeId);
        node = node with { Terrain = node.Terrain with { Relief = relief } };
        return original with { Core = new FixedCoreReadView
        {
            Position = original.Core.Position, InputIdentity = original.Core.InputIdentity,
            Nodes = new Dictionary<NodeId, Lookup<NodeView>>
            {
                [node.Id] = new Lookup<NodeView>.Found(
                    ((Lookup<NodeView>.Found)original.Core.FindNode(node.Id)).Value with
                    { Map = node, ApproachLengthMeters = travelMeters, DepartureLengthMeters = travelMeters })
            },
            World = original.Core.World with { Nodes = [node], Edges = [] },
            Trains = trains.ToDictionary(train => train.Id, train => original.Core.FindTrain(train.Id)),
            Instances = trains.SelectMany(train => train.Vehicles).ToDictionary(vehicle => (InstanceId)new InstanceId.Vehicle(vehicle),
                vehicle => original.Core.FindInstance(new InstanceId.Vehicle(vehicle))),
            Settlements = new Dictionary<SettlementId, Lookup<SettlementView>>
            { [new(1)] = new Lookup<SettlementView>.Found(Settlement(0) with { Plots = [] }) }
        } };
    }

    internal static NodeScene.Built Build(SceneWorldSnapshot snapshot, NodeLayout layout,
        NodeScene.Built? previous = null, CancellationToken token = default)
    {
        var prepared = NodeScene.Prepare(snapshot, layout, previous is null ? null : NodeScene.Cached.From(previous), token);
        var parts = prepared.Global.Parts.ToBuilder();
        foreach (var (key, segment) in prepared.Rails)
            parts[key] = segment.Build(previous?.Parts.GetValueOrDefault(key), token);
        for (var z = (int)layout.Bounds.Minimum.Y; z < layout.Bounds.Maximum.Y; z += VoxelGrid.ChunkSize)
        for (var x = (int)layout.Bounds.Minimum.X; x < layout.Bounds.Maximum.X; x += VoxelGrid.ChunkSize)
        {
            var from = new GridColumn(x, z);
            var key = NodeScene.ChunkKey(from);
            parts[key] = prepared.BuildChunk(from, previous?.Parts.GetValueOrDefault(key), token);
        }
        return prepared.Global with { Parts = parts.ToImmutable() };
    }

    internal static void AssertPartEqual(NodeScene.Part expected, NodeScene.Part actual)
    {
        Assert.AreEqual(expected.Inputs, actual.Inputs);
        Assert.AreEqual(expected.Origin, actual.Origin);
        CollectionAssert.AreEquivalent(expected.Heights.ToArray(), actual.Heights.ToArray());
        var before = expected.Geometry;
        var after = actual.Geometry;
        CollectionAssert.AreEqual(before.Geometry.Diagnostics.ToArray(), after.Geometry.Diagnostics.ToArray());
        CollectionAssert.AreEqual(before.Geometry.Objects.Select(item => before.Keys[item.Id]).ToArray(),
            after.Geometry.Objects.Select(item => after.Keys[item.Id]).ToArray());
        for (var index = 0; index < before.Geometry.Objects.Length; index++)
        {
            var a = before.Geometry.Objects[index];
            var b = after.Geometry.Objects[index];
            Assert.AreEqual(a.Parent is { } parentA ? before.Keys[parentA] : null, b.Parent is { } parentB ? after.Keys[parentB] : null);
            Assert.AreEqual(a.Pose, b.Pose);
            Assert.AreEqual(a.Subject, b.Subject);
            CollectionAssert.AreEqual(a.Lights.ToArray(), b.Lights.ToArray());
            CollectionAssert.AreEqual(a.Effects.ToArray(), b.Effects.ToArray());
            Assert.AreEqual(a.Visuals.Length, b.Visuals.Length);
            for (var visual = 0; visual < a.Visuals.Length; visual++)
                if (a.Visuals[visual] is SceneVisual.Geometry mesh)
                {
                    var other = (SceneVisual.Geometry)b.Visuals[visual];
                    Assert.AreEqual(mesh.Surface, other.Surface);
                    Assert.AreEqual(mesh.Mesh.Material, other.Mesh.Material);
                    Assert.IsTrue(mesh.Mesh.Vertices.AsSpan().SequenceEqual(other.Mesh.Vertices.AsSpan()), "Mesh vertices differ.");
                    Assert.IsTrue(mesh.Mesh.TriangleIndices.AsSpan().SequenceEqual(other.Mesh.TriangleIndices.AsSpan()), "Mesh indices differ.");
                }
                else Assert.AreEqual(a.Visuals[visual], b.Visuals[visual]);
        }
        CollectionAssert.AreEqual(before.Geometry.Picking.Select(item => before.Keys[item.Object]).ToArray(),
            after.Geometry.Picking.Select(item => after.Keys[item.Object]).ToArray());
        for (var index = 0; index < before.Geometry.Picking.Length; index++)
        {
            var a = before.Geometry.Picking[index];
            var b = after.Geometry.Picking[index];
            Assert.AreEqual(a.Pose, b.Pose);
            Assert.IsTrue(a.Grid.Cells.SequenceEqual(b.Grid.Cells), "Picking cells differ.");
        }
    }

    internal static readonly NodeId NodeId = new(1);
    internal static readonly VoxelDefinitionId Concrete = new("test:concrete");
    internal static readonly WorldLocation Location = new WorldLocation.Node(NodeId);
    internal static readonly DesignHash StationDesign = new("test-station");
    internal static SettlementLayoutProjection Layout => new(new(1), new("test:town"),
        new(new("test-ground"), SceneFixtures.Projection([]), new("Ground", "")),
        [new(1, PlotSize.SmallStation, new(0, 0, 0), VoxelOrientation.PzPy)], new("Town", ""));

    internal static SettlementView Settlement(int platforms = 2) => new(new(1), NodeId, new("test:town"), new("test:town"),
        SettlementRelation.NonHostile, new(1),
        [new(new(1), 1, PlotSize.SmallStation, [BuildingKind.Station], platforms > 0 ? new PlotOccupant.Building(new(1)) : new PlotOccupant.Empty())],
        [], 0, platforms, 0, [], [], [], new(0, 0, 0, 0),
        new(new(new("test:population"), null, []), 0, 0, 0, 0, new PopulationExecution.NotStarted([]), null, []),
        new(TargetPriority.ArmedFirst, TargetScope.All), 1, false, [], [], [],
        new(new(new("test:market"), null, []), new(0, 0, 0, 0, 0, 0, 0), []));

    internal static InstanceView Station(int platforms) => new(new InstanceId.Building(new(1)), StationDesign, new("station"),
        Location, SettlementRelation.NonHostile, new InstanceDetails.Building(new(1), PlotSize.SmallStation), InstanceLock.None,
        new(100, 100), Enumerable.Range(1, 2).Select(number => new PartDamageView(new(number), new(number <= platforms ? 100 : 0, 100), false, number > platforms)).ToImmutableArray(), []);

    internal static DesignEvaluation.Complete StationEvaluation()
    {
        var cells = new[] { -22, 22 }.Select((z, index) => SceneFixtures.Cell(new(0, 0, z), index + 1)).ToImmutableArray();
        var core = new DesignProjection(StationDesign, new("station"), new DesignKind.Building(PlotSize.SmallStation, BuildingKind.Station, []),
            cells.Select(cell => new PartProjection(cell.Number, new CoreDefinitionReference.Voxel(SceneFixtures.Voxel),
                new GoodId.Voxel(SceneFixtures.Voxel), 100, 1, new(1), [new PartCapability.Platform()], false)).ToImmutableArray(),
            100, 100, 1, new(1), [], [], 0, [], true, []);
        return new(StationDesign, core.Composition, SceneFixtures.Draft with { Specification = new DesignSpecification.Building(PlotSize.SmallStation, BuildingKind.Station, []) },
            new(new("scenes-test"), core, []), SceneFixtures.Projection(cells) with { Design = StationDesign }, true, []);
    }
    internal static VoxelAppearanceProjection ConcreteCube => SceneFixtures.Cube with
    {
        Definition = Concrete,
        Presentation = new VoxelPresentation.Cube([], MaterialCategory.Concrete, 1,
            new("concrete-top"), new("concrete-side"), new("concrete-bottom"), new("concrete-side"))
    };

    internal static NodeMapItem Node(int platforms = 2, NodeOccupant? occupant = null) => new(NodeId,
        new(0, 0), new(RailwayFixtures.Terrain, 0, []),
        occupant ?? new NodeOccupant.Settlement(new(1), SettlementRelation.NonHostile), [], 0, platforms, null);

    internal static EdgeMapItem Edge(EdgeId id, NodeId start, NodeId end, int tracks = 1, int? trackLimit = null) =>
        new(id, start, end, 800, new(RailwayFixtures.Terrain, 0, []), tracks, trackLimit ?? Math.Max(1, tracks), [], []);

    internal static TrainView Train(ulong trainId = 1, ImmutableArray<VehicleId> vehicles = default,
        EdgeId? enteredFrom = null, EdgeId? nextEdge = null, TrainActivity activity = TrainActivity.Stopped,
        TrainOrderFacing facing = TrainOrderFacing.FirstEndLeading, double speed = 0, int? platformNumber = null) =>
        RailwayFixtures.Train(vehicles: vehicles.IsDefault ? [new(trainId)] : vehicles,
            trainId: trainId, facing: facing, speed: speed) with
        {
            Position = new TrainPosition.Node(NodeId, enteredFrom, enteredFrom is null ? null : 1, nextEdge, activity == TrainActivity.Running && nextEdge is not null ? 1 : null,
                activity == TrainActivity.Servicing ? NodeTravelPhase.Docked :
                activity == TrainActivity.Running ? NodeTravelPhase.Departing : NodeTravelPhase.Waiting,
                activity is TrainActivity.Servicing or TrainActivity.Running ? 128 : 80, platformNumber), Activity = activity
        };

    internal static SceneWorldSnapshot Snapshot(int platforms = 2, ImmutableArray<TrainView> trains = default,
        ImmutableArray<InstanceView> instances = default, IContentView? content = null,
        ulong steps = 0, ulong sequence = 0, int tracks = 1, int? trackLimit = null, double platformLengthMeters = 96,
        bool sameSideEntry = false, IReadOnlyList<CreatureGroupView>? groups = null, IReadOnlyList<CombatView>? combats = null,
        SettlementView? settlement = null)
    {
        if (trains.IsDefault) trains = [Train()];
        if (instances.IsDefault)
            instances = trains.SelectMany(train => train.Vehicles.Select(vehicle =>
                RailwayFixtures.Instance(vehicle.Value, train: train.Id) with
                {
                    Location = train.Position is TrainPosition.Node position ? new WorldLocation.Node(position.Id) :
                        new WorldLocation.Edge(((TrainPosition.Edge)train.Position).Id, ((TrainPosition.Edge)train.Position).DistanceMeters)
                })).ToImmutableArray();
        var node = Node(platforms);
        if (platforms > 0) instances = instances.Add(Station(platforms));
        var neighbors = new List<NodeMapItem>
        {
            node with { Id = new(2), Position = new(600, 0), Occupant = new NodeOccupant.Empty(), Platforms = 0 },
            node with { Id = new(3), Position = new(0, -600), Occupant = new NodeOccupant.Empty(), Platforms = 0 }
        };
        if (sameSideEntry)
            neighbors.Add(node with { Id = new(4), Position = new(0, 600), Occupant = new NodeOccupant.Empty(), Platforms = 0 });
        ImmutableArray<NodeMapItem> nodes = [node, .. neighbors];
        ImmutableArray<EdgeMapItem> edges = [Edge(new(1), NodeId, new(2), tracks, trackLimit), Edge(new(2), new(3), NodeId, tracks, trackLimit)];
        if (sameSideEntry) edges = edges.Add(Edge(new(3), NodeId, new(4), tracks, trackLimit));
        return new(new FixedCoreReadView
        {
            Position = new(steps, sequence), InputIdentity = new("scenes-test"),
            World = new(3, RailwayFixtures.Rule, 1200, 1200, nodes, edges,
                trains.Select(train => new TrainMapItem(train.Id, train.Position, train.Activity)).ToImmutableArray(),
                (groups ?? []).Select(group => new CreatureGroupMapItem(group.Id, group.Location, group.Activity)).ToImmutableArray(),
                (combats ?? []).Select(combat => new CombatMapItem(combat.Id, combat.Location)).ToImmutableArray()),
            CreatureGroups = (groups ?? []).ToDictionary(group => group.Id, group => (Lookup<CreatureGroupView>)new Lookup<CreatureGroupView>.Found(group)),
            Creatures = (groups ?? []).SelectMany(group => group.Creatures).ToDictionary(creature => creature.Id,
                creature => (Lookup<CreatureView>)new Lookup<CreatureView>.Found(creature)),
            Combats = (combats ?? []).ToDictionary(combat => combat.Id, combat => (Lookup<CombatView>)new Lookup<CombatView>.Found(combat)),
            Nodes = nodes.ToDictionary(item => item.Id, item => (Lookup<NodeView>)new Lookup<NodeView>.Found(new(item,
                edges.Where(edge => edge.Start == item.Id || edge.End == item.Id).Select(edge => edge.Id).ToImmutableArray(),
                80, item.Id == NodeId ? platformLengthMeters : 0, 80))),
            Edges = edges.ToDictionary(item => item.Id, item => (Lookup<EdgeView>)new Lookup<EdgeView>.Found(new(item,
                Enumerable.Range(1, item.Tracks).Select(number => new TrackView(number, null,
                    trains.Where(train => train.Position is TrainPosition.Edge position && position.Id == item.Id && position.Track == number)
                        .Select(train => train.Id).ToImmutableArray())).ToImmutableArray(), null))),
            Trains = trains.ToDictionary(train => train.Id, train => (Lookup<TrainView>)new Lookup<TrainView>.Found(train)),
            Settlements = new Dictionary<SettlementId, Lookup<SettlementView>> { [new(1)] = new Lookup<SettlementView>.Found(settlement ?? Settlement(platforms)) },
            Instances = instances.ToDictionary(instance => instance.Id, instance => (Lookup<InstanceView>)new Lookup<InstanceView>.Found(instance))
        }, content ?? Content());
    }

    internal static FixedContentView Content(ulong generation = 1, VoxelAppearanceProjection? concrete = null,
        bool missing = false, DesignEvaluation? design = null, TerrainAppearanceProjection? terrain = null,
        RailwayAppearanceProjection? rules = null, VoxelAppearanceProjection? voxel = null,
        IReadOnlyDictionary<DesignHash, ContentLookup<DesignEvaluation>>? designs = null,
        SettlementLayoutProjection? layout = null,
        IReadOnlyDictionary<LairTypeId, ContentLookup<LairAppearanceProjection>>? lairs = null,
        IReadOnlyDictionary<VoxelDefinitionId, ContentLookup<VoxelAppearanceProjection>>? voxelAppearances = null,
        IReadOnlyDictionary<CreatureDefinitionId, ContentLookup<CreatureAppearanceProjection>>? creatures = null)
    {
        design ??= RailwayFixtures.Vehicle();
        var hash = design is DesignEvaluation.Complete complete ? complete.Design : SceneFixtures.Design;
        var evaluations = new Dictionary<DesignHash, ContentLookup<DesignEvaluation>>
        {
            [hash] = new ContentLookup<DesignEvaluation>.Found(design),
            [StationDesign] = new ContentLookup<DesignEvaluation>.Found(StationEvaluation())
        };
        foreach (var item in designs ?? ImmutableDictionary<DesignHash, ContentLookup<DesignEvaluation>>.Empty) evaluations[item.Key] = item.Value;
        var appearances = new Dictionary<VoxelDefinitionId, ContentLookup<VoxelAppearanceProjection>>
        {
            [SceneFixtures.Voxel] = new ContentLookup<VoxelAppearanceProjection>.Found(voxel ?? SceneFixtures.Cube),
            [Concrete] = missing ? new ContentLookup<VoxelAppearanceProjection>.Missing([]) :
                new ContentLookup<VoxelAppearanceProjection>.Found(concrete ?? ConcreteCube)
        };
        if (voxelAppearances is not null)
            foreach (var item in voxelAppearances) appearances[item.Key] = item.Value;
        return new()
        {
            Generation = new(generation), Core = SceneFixtures.Content().Core,
            Voxels = [new(Concrete, null), new(SceneFixtures.Voxel, null)],
            Designs = evaluations,
            SettlementLayouts = new Dictionary<SettlementId, ContentLookup<SettlementLayoutProjection>> { [new(1)] = new ContentLookup<SettlementLayoutProjection>.Found(layout ?? Layout) },
            Lairs = lairs ?? new Dictionary<LairTypeId, ContentLookup<LairAppearanceProjection>>(),
            VoxelAppearances = appearances,
            Terrains = new Dictionary<TerrainTypeId, ContentLookup<TerrainAppearanceProjection>>
            { [RailwayFixtures.Terrain] = new ContentLookup<TerrainAppearanceProjection>.Found(terrain ?? RailwayFixtures.TerrainAppearance) },
            RailwayAppearances = new Dictionary<RuleDefinitionId, ContentLookup<RailwayAppearanceProjection>>
            { [RailwayFixtures.Rule] = new ContentLookup<RailwayAppearanceProjection>.Found(rules ?? RailwayFixtures.Rules) },
            Creatures = creatures ?? new Dictionary<CreatureDefinitionId, ContentLookup<CreatureAppearanceProjection>>()
        };
    }
}
