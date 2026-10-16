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
        ImmutableArray<InstanceView> instances = default, int? trackLimit = null,
        IEnumerable<TrainId>? missingTrains = null)
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
            Position = new(steps, sequence),
            InputIdentity = new("scenes-test"),
            World = new(3, Rule, directMeters, directMeters, [first, last], [edge],
                trains.Select(train => new TrainMapItem(train.Id, train.Position, train.Activity)).ToImmutableArray(), [], []),
            Nodes = new Dictionary<NodeId, Lookup<NodeView>>
            {
                [first.Id] = new Lookup<NodeView>.Found(new(first, [Edge], 80, 0, 80)),
                [last.Id] = new Lookup<NodeView>.Found(new(last, [Edge], 80, 0, 80))
            },
            Edges = new Dictionary<EdgeId, Lookup<EdgeView>> { [Edge] = new Lookup<EdgeView>.Found(new(edge, trackViews, null)) },
            Trains = trains.ToDictionary(train => train.Id, train => (Lookup<TrainView>)new Lookup<TrainView>.Found(train))
                .Concat((missingTrains ?? []).Select(id => KeyValuePair.Create(id, (Lookup<TrainView>)new Lookup<TrainView>.Missing())))
                .ToDictionary(),
            Instances = instances.ToDictionary(instance => instance.Id, instance => (Lookup<InstanceView>)new Lookup<InstanceView>.Found(instance))
        }, content ?? Content());
    }

    internal static FixedContentView Content(TerrainAppearanceProjection? terrain = null,
        RailwayAppearanceProjection? rules = null, DesignEvaluation? design = null,
        bool wheelsetMarkers = false,
        IReadOnlyDictionary<TerrainTypeId, ContentLookup<TerrainAppearanceProjection>>? terrains = null,
        IReadOnlyDictionary<DesignHash, ContentLookup<DesignEvaluation>>? designs = null,
        IReadOnlyDictionary<VoxelDefinitionId, ContentLookup<VoxelAppearanceProjection>>? voxels = null,
        ulong generation = 1)
    {
        var terrainViews = new Dictionary<TerrainTypeId, ContentLookup<TerrainAppearanceProjection>>
        { [Terrain] = new ContentLookup<TerrainAppearanceProjection>.Found(terrain ?? TerrainAppearance) };
        var voxelViews = new Dictionary<VoxelDefinitionId, ContentLookup<VoxelAppearanceProjection>>
        { [SceneFixtures.Voxel] = new ContentLookup<VoxelAppearanceProjection>.Found(SceneFixtures.Cube with { Behaviors = new(wheelsetMarkers, null) }) };
        var designViews = new Dictionary<DesignHash, ContentLookup<DesignEvaluation>>();
        if (design is not null)
        {
            var hash = design is DesignEvaluation.Complete complete ? complete.Design :
                design is DesignEvaluation.Partial partial ? partial.Scene.Design ?? SceneFixtures.Design : SceneFixtures.Design;
            designViews[hash] = new ContentLookup<DesignEvaluation>.Found(design);
        }
        foreach (var item in terrains ?? ImmutableDictionary<TerrainTypeId, ContentLookup<TerrainAppearanceProjection>>.Empty) terrainViews[item.Key] = item.Value;
        foreach (var item in designs ?? ImmutableDictionary<DesignHash, ContentLookup<DesignEvaluation>>.Empty) designViews[item.Key] = item.Value;
        foreach (var item in voxels ?? ImmutableDictionary<VoxelDefinitionId, ContentLookup<VoxelAppearanceProjection>>.Empty) voxelViews[item.Key] = item.Value;
        return new()
        {
            Generation = new(generation),
            Core = SceneFixtures.Content().Core,
            Terrains = terrainViews,
            RailwayAppearances = new Dictionary<RuleDefinitionId, ContentLookup<RailwayAppearanceProjection>>
            { [Rule] = new ContentLookup<RailwayAppearanceProjection>.Found(rules ?? Rules) },
            VoxelAppearances = voxelViews,
            Designs = designViews
        };
    }

    internal static SceneWorldSnapshot ReplaceCore(SceneWorldSnapshot snapshot, NodeMapItem? node = null, EdgeMapItem? edge = null,
        ulong? worldSeed = null)
    {
        var world = snapshot.Core.World;
        var nodes = world.Nodes.Select(item => node is not null && item.Id == node.Id ? node : item).ToImmutableArray();
        var edges = world.Edges.Select(item => edge is not null && item.Id == edge.Id ? edge : item).ToImmutableArray();
        return snapshot with
        {
            Core = new FixedCoreReadView
            {
                Position = snapshot.Core.Position,
                InputIdentity = snapshot.Core.InputIdentity,
                World = world with { Nodes = nodes, Edges = edges, WorldSeed = worldSeed ?? world.WorldSeed },
                Nodes = nodes.ToDictionary(item => item!.Id, item => item!.Id == node?.Id
                    ? new Lookup<NodeView>.Found(((Lookup<NodeView>.Found)snapshot.Core.FindNode(item.Id)).Value with { Map = item })
                    : snapshot.Core.FindNode(item.Id)),
                Edges = edges.ToDictionary(item => item!.Id, item => item!.Id == edge?.Id
                    ? new Lookup<EdgeView>.Found(((Lookup<EdgeView>.Found)snapshot.Core.FindEdge(item.Id)).Value with { Map = item })
                    : snapshot.Core.FindEdge(item.Id)),
                Trains = world.Trains.ToDictionary(item => item.Id, item => snapshot.Core.FindTrain(item.Id)),
                Settlements = world.Nodes.Select(item => item.Occupant).OfType<NodeOccupant.Settlement>()
                    .ToDictionary(item => item.Id, item => snapshot.Core.FindSettlement(item.Id)),
                Instances = world.Trains.SelectMany(item => ((Lookup<TrainView>.Found)snapshot.Core.FindTrain(item.Id)).Value.Vehicles)
                .Select(id => (InstanceId)new InstanceId.Vehicle(id))
                .Concat(world.Nodes.Select(item => item.Occupant).OfType<NodeOccupant.Settlement>()
                    .Select(item => snapshot.Core.FindSettlement(item.Id)).OfType<Lookup<SettlementView>.Found>()
                    .SelectMany(item => item.Value.Plots.Select(plot => plot.Occupant).OfType<PlotOccupant.Building>()
                        .Select(building => (InstanceId)new InstanceId.Building(building.Id))
                        .Concat(item.Value.ParkedVehicles.Select(vehicle => (InstanceId)new InstanceId.Vehicle(vehicle)))))
                .Distinct().ToDictionary(id => id, id => snapshot.Core.FindInstance(id))
            }
        };
    }

    internal static RailwaySegment EmptySegment(SceneWorldSnapshot snapshot, RailwayPath path, int index, CancellationToken token) =>
        new(index, path.Sample(index * RailwayCoordinates.SegmentLengthCells).Position, new([], [], []),
            ImmutableDictionary<GridColumn, double>.Empty,
            RailwayDependencies.Capture(snapshot, path, index, [], [], [], [], false, false), []);

    internal static NodeScene.Built EmptyNode(SceneWorldSnapshot snapshot, NodeId node,
        NodeScene.Cached? previous, CancellationToken token) =>
        new(NodeLayout.Build(snapshot, node, token), ImmutableDictionary<string, NodeScene.Part>.Empty, []);

    internal static RailwayScene Controlled(SceneWorldSnapshot snapshot, SceneVehicles vehicles,
        Func<SceneWorldSnapshot, RailwayPath, int, CancellationToken, RailwaySegment>? build = null)
    {
        var ids = new Dictionary<string, SceneObjectId>();
        ulong next = 0;
        return new(snapshot, Edge, new(), key =>
        {
            if (!ids.TryGetValue(key, out var id)) ids.Add(key, id = new(++next));
            return id;
        }, vehicles, build ?? EmptySegment);
    }

    internal static async Task<SceneFrame> Settled(RailwayScene scene, SceneWorldSnapshot snapshot,
        WorldSceneTarget target, SceneFrameRequest request, SceneFrameStamp stamp,
        SceneWorldSnapshot? previous = null, SceneFrame? displayed = null)
    {
        var frame = scene.Present(snapshot, previous, target, request, stamp, displayed);
        while (scene.PendingWork.Length > 0)
        {
            await Task.WhenAll(scene.PendingWork);
            stamp = stamp with { Revision = stamp.Revision + 1 };
            frame = scene.Present(snapshot, previous, target, request, stamp, displayed);
        }
        return frame;
    }

    internal static async Task<SceneFrame> Settled(WorldSceneSession session, SceneFrameRequest request, ISceneFrame? displayed = null)
    {
        SceneFrame Present() => (SceneFrame)((SceneResult<ISceneFrame>.Success)session.Present(request, displayed)).Value;
        var frame = Present();
        while (Work().Length > 0)
        {
            await Task.WhenAll(Work());
            frame = Present();
        }
        return frame;

        Task[] Work() => session.PendingNodeBuilds.Concat(session.PendingRailwayBuilds)
            .Concat(session.PendingObjectBuilds).ToArray();
    }

    internal static double TrackGradeErrorBound(double length, double relief, double offset, RailwayAppearanceProjection rules)
    {
        var slope = relief == 0 ? 0 : Math.Atan(rules.MaximumGrade);
        var innerScale = 1 - Math.Abs(offset) / (rules.MinimumCurveRadiusMeters * RailwayCoordinates.CellsPerMeter);
        return length * (Math.Sqrt(Math.Pow(Math.Cos(slope), 2) + Math.Pow(Math.Sin(slope) / innerScale, 2)) - 1);
    }

    internal static double MeasuredTrackLength(RailwayPath path, double begin, double end, double offset, int samples = 16384)
    {
        var previous = path.Sample(begin, offset).Position;
        var measured = 0.0;
        for (var step = 1; step <= samples; step++)
        {
            var current = path.Sample(begin + (end - begin) * step / samples, offset).Position;
            measured += Math.Sqrt(Math.Pow(current.X - previous.X, 2) + Math.Pow(current.Y - previous.Y, 2) + Math.Pow(current.Z - previous.Z, 2));
            previous = current;
        }
        return measured;
    }

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
        new(TargetPriority.ArmedFirst, TargetScope.All, EncounterAction.Engage, 0), [], null, [], facing, null);

}
