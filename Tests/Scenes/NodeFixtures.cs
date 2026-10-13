using System.Collections.Immutable;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using Train.Scenes.Contracts;

namespace Train.Scenes.Tests;

internal static class NodeFixtures
{
    internal static readonly NodeId NodeId = new(1);
    internal static readonly VoxelDefinitionId Concrete = new("test:concrete");
    internal static readonly WorldLocation Location = new WorldLocation.Node(NodeId);
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
        TrainOrderFacing facing = TrainOrderFacing.FirstEndLeading, double speed = 0) =>
        RailwayFixtures.Train(vehicles: vehicles.IsDefault ? [new(trainId)] : vehicles,
            trainId: trainId, facing: facing, speed: speed) with
        {
            Position = new TrainPosition.Node(NodeId), Activity = activity,
            NodeTransit = new(enteredFrom, nextEdge)
        };

    internal static SceneWorldSnapshot Snapshot(int platforms = 2, ImmutableArray<TrainView> trains = default,
        ImmutableArray<InstanceView> instances = default, IContentView? content = null,
        ulong steps = 0, ulong sequence = 0, int tracks = 1, int? trackLimit = null)
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
        var neighbors = new[]
        {
            node with { Id = new(2), Position = new(600, 0), Occupant = new NodeOccupant.Empty(), Platforms = 0 },
            node with { Id = new(3), Position = new(0, -600), Occupant = new NodeOccupant.Empty(), Platforms = 0 }
        };
        ImmutableArray<NodeMapItem> nodes = [node, .. neighbors];
        ImmutableArray<EdgeMapItem> edges = [Edge(new(1), NodeId, new(2), tracks, trackLimit), Edge(new(2), new(3), NodeId, tracks, trackLimit)];
        return new(new FixedCoreReadView
        {
            Position = new(steps, sequence), InputIdentity = new("scenes-test"),
            World = new(3, RailwayFixtures.Rule, 1200, 1200, nodes, edges,
                trains.Select(train => new TrainMapItem(train.Id, train.Position, train.Activity)).ToImmutableArray(), [], []),
            Nodes = nodes.ToDictionary(item => item.Id, item => (Lookup<NodeView>)new Lookup<NodeView>.Found(new(item,
                edges.Where(edge => edge.Start == item.Id || edge.End == item.Id).Select(edge => edge.Id).ToImmutableArray()))),
            Edges = edges.ToDictionary(item => item.Id, item => (Lookup<EdgeView>)new Lookup<EdgeView>.Found(new(item,
                Enumerable.Range(1, item.Tracks).Select(number => new TrackView(number, null,
                    trains.Where(train => train.Position is TrainPosition.Edge position && position.Id == item.Id && position.Track == number)
                        .Select(train => train.Id).ToImmutableArray())).ToImmutableArray(), null))),
            Trains = trains.ToDictionary(train => train.Id, train => (Lookup<TrainView>)new Lookup<TrainView>.Found(train)),
            Instances = instances.ToDictionary(instance => instance.Id, instance => (Lookup<InstanceView>)new Lookup<InstanceView>.Found(instance))
        }, content ?? Content());
    }

    internal static FixedContentView Content(ulong generation = 1, VoxelAppearanceProjection? concrete = null,
        bool missing = false, DesignEvaluation? design = null, TerrainAppearanceProjection? terrain = null,
        RailwayAppearanceProjection? rules = null, VoxelAppearanceProjection? voxel = null)
    {
        design ??= RailwayFixtures.Vehicle();
        var hash = design is DesignEvaluation.Complete complete ? complete.Design : SceneFixtures.Design;
        return new()
        {
            Generation = new(generation), Core = SceneFixtures.Content().Core,
            Voxels = [new(Concrete, null), new(SceneFixtures.Voxel, null)],
            Designs = new Dictionary<DesignHash, ContentLookup<DesignEvaluation>> { [hash] = new ContentLookup<DesignEvaluation>.Found(design) },
            VoxelAppearances = new Dictionary<VoxelDefinitionId, ContentLookup<VoxelAppearanceProjection>>
            {
                [SceneFixtures.Voxel] = new ContentLookup<VoxelAppearanceProjection>.Found(voxel ?? SceneFixtures.Cube),
                [Concrete] = missing ? new ContentLookup<VoxelAppearanceProjection>.Missing([]) :
                    new ContentLookup<VoxelAppearanceProjection>.Found(concrete ?? ConcreteCube)
            },
            Terrains = new Dictionary<TerrainTypeId, ContentLookup<TerrainAppearanceProjection>>
            { [RailwayFixtures.Terrain] = new ContentLookup<TerrainAppearanceProjection>.Found(terrain ?? RailwayFixtures.TerrainAppearance) },
            RailwayAppearances = new Dictionary<RuleDefinitionId, ContentLookup<RailwayAppearanceProjection>>
            { [RailwayFixtures.Rule] = new ContentLookup<RailwayAppearanceProjection>.Found(rules ?? RailwayFixtures.Rules) }
        };
    }
}
