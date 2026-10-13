using System.Collections.Immutable;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using Train.Scenes.Contracts;

namespace Train.Scenes.Tests;

internal static class RailwayFixtures
{
    internal static readonly EdgeId Edge = new(1);
    internal static readonly TerrainTypeId Terrain = new("test:terrain");
    internal static readonly RuleDefinitionId Rule = new("test:railway");
    internal static readonly WorldLocation Location = new WorldLocation.Edge(Edge, 0);
    internal static TerrainAppearanceProjection TerrainAppearance => new(Terrain,
        SceneFixtures.Voxel, SceneFixtures.Voxel, SceneFixtures.Voxel, [], [], 0, [], new("Terrain", ""));
    internal static RailwayAppearanceProjection Rules => new(Rule, 40, 0.12);

    internal static SceneWorldSnapshot Snapshot(double lengthMeters = 800, double directMeters = 600,
        double relief = 1, int tracks = 1, ulong steps = 0, ulong sequence = 0,
        IContentView? content = null, ImmutableArray<TrainView> trains = default,
        ImmutableArray<InstanceView> instances = default, int? trackLimit = null)
    {
        if (trains.IsDefault) trains = [];
        if (instances.IsDefault) instances = [];
        var terrain = new TerrainView(Terrain, relief, []);
        var first = new NodeMapItem(new(1), new(0, 0), terrain, new NodeOccupant.Empty(), [], 0, 0, null);
        var last = first with { Id = new(2), Position = new(0, directMeters) };
        var edge = new EdgeMapItem(Edge, first.Id, last.Id, lengthMeters, terrain, tracks,
            trackLimit ?? Math.Max(1, tracks), [], []);
        var trackViews = Enumerable.Range(1, tracks).Select(number => new TrackView(number, null,
            trains.Where(train => train.Position is TrainPosition.Edge position && position.Track == number)
                .Select(train => train.Id).ToImmutableArray())).ToImmutableArray();
        return new(new FixedCoreReadView
        {
            Position = new(steps, sequence), InputIdentity = new("scenes-test"),
            World = new(3, Rule, directMeters, directMeters, [first, last], [edge],
                trains.Select(train => new TrainMapItem(train.Id, train.Position, train.Activity)).ToImmutableArray(), [], []),
            Nodes = new Dictionary<NodeId, Lookup<NodeView>>
            {
                [first.Id] = new Lookup<NodeView>.Found(new(first, [Edge])),
                [last.Id] = new Lookup<NodeView>.Found(new(last, [Edge]))
            },
            Edges = new Dictionary<EdgeId, Lookup<EdgeView>> { [Edge] = new Lookup<EdgeView>.Found(new(edge, trackViews, null)) },
            Locations = new Dictionary<WorldLocation, Lookup<LocationView>>
            {
                [Location] = new Lookup<LocationView>.Found(new(Location,
                    trains.Select(train => train.Id).ToImmutableArray(), instances.Select(instance => instance.Id).ToImmutableArray(), [], [], null))
            },
            Trains = trains.ToDictionary(train => train.Id, train => (Lookup<TrainView>)new Lookup<TrainView>.Found(train)),
            Instances = instances.ToDictionary(instance => instance.Id, instance => (Lookup<InstanceView>)new Lookup<InstanceView>.Found(instance))
        }, content ?? Content());
    }

    internal static FixedContentView Content(TerrainAppearanceProjection? terrain = null,
        RailwayAppearanceProjection? rules = null, DesignEvaluation.Complete? vehicle = null,
        bool wheelsetMarkers = false) => new()
    {
        Generation = new(1), Core = SceneFixtures.Content().Core,
        Terrains = new Dictionary<TerrainTypeId, ContentLookup<TerrainAppearanceProjection>>
        { [Terrain] = new ContentLookup<TerrainAppearanceProjection>.Found(terrain ?? TerrainAppearance) },
        RailwayAppearances = new Dictionary<RuleDefinitionId, ContentLookup<RailwayAppearanceProjection>>
        { [Rule] = new ContentLookup<RailwayAppearanceProjection>.Found(rules ?? Rules) },
        VoxelAppearances = new Dictionary<VoxelDefinitionId, ContentLookup<VoxelAppearanceProjection>>
        { [SceneFixtures.Voxel] = new ContentLookup<VoxelAppearanceProjection>.Found(SceneFixtures.Cube with { Behaviors = new(wheelsetMarkers, null) }) },
        Designs = vehicle is null ? new Dictionary<DesignHash, ContentLookup<DesignEvaluation>>() :
            new Dictionary<DesignHash, ContentLookup<DesignEvaluation>> { [vehicle.Design] = new ContentLookup<DesignEvaluation>.Found(vehicle) }
    };

    internal static DesignEvaluation.Complete Vehicle(SizeClass size = SizeClass.Small, int lengthCells = 8,
        ImmutableArray<GridPosition> cells = default, DesignHash? design = null)
    {
        if (cells.IsDefault) cells = [new(0, 0, 0), new(0, 0, lengthCells - 1)];
        var hash = design ?? SceneFixtures.Design;
        var projection = SceneFixtures.Projection(cells.Select((at, index) => SceneFixtures.Cell(at, index + 1)).ToImmutableArray()) with { Design = hash };
        var core = new DesignProjection(hash, new("composition"), new DesignKind.Vehicle(size, lengthCells), [], 100, 100, 1,
            new(1), [], [], 0, [], true, []);
        return new(hash, core.Composition, SceneFixtures.Draft with { Specification = new DesignSpecification.Vehicle(size) },
            new(new("scenes-test"), core, []), projection, true, []);
    }

    internal static InstanceView Instance(ulong vehicleId = 1, SizeClass size = SizeClass.Small,
        DesignHash? design = null, TrainId? train = null) => new(new InstanceId.Vehicle(new(vehicleId)),
        design ?? SceneFixtures.Design, new("composition"), Location, SettlementRelation.PlayerOwned,
        new InstanceDetails.Vehicle(size, train), InstanceLock.None, new(100, 100), [], []);

    internal static TrainView Train(double distanceMeters = 100, ImmutableArray<VehicleId> vehicles = default,
        ulong trainId = 1, int track = 1, TravelDirection direction = TravelDirection.Forward,
        TrainOrderFacing facing = TrainOrderFacing.FirstEndLeading, double speed = 0) => new(new(trainId),
        new TrainPosition.Edge(Edge, track, direction, distanceMeters), TrainActivity.Running,
        vehicles.IsDefault ? [new(1)] : vehicles, new(trainId), speed, 4, 1, 1, 1, 10, null, null, 0,
        new(TargetPriority.ArmedFirst, TargetScope.All, EncounterAction.Engage, 0), [], null, [], null, facing);
}
