using System.Collections.Immutable;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using Train.Scenes.Contracts;

namespace Train.Engine.Tests;

// 有限公开快照输入；不执行 Core，不作为生产时序证据。
internal static class NodeTransitionFixture
{
    internal static readonly WorldObjectId Subject = new WorldObjectId.Instance(new InstanceId.Vehicle(new(1)));

    private static readonly VoxelDefinitionId Cube = new("preview:cube");

    private static readonly DesignHash Vehicle = new("preview:vehicle");

    private static readonly DesignHash Station = new("preview:station");

    private static readonly TerrainTypeId Terrain = new("preview:terrain");

    private static readonly RuleDefinitionId Rule = new("preview:railway");

    internal static FixedContentView Content()
    {
        var bytes = Godot.FileAccess.GetFileAsBytes("res://builtin/core/resources/voxels/shared/organic-face.png");
        using var image = new Godot.Image();
        if (image.LoadPngFromBuffer(bytes) != Godot.Error.Ok) throw new InvalidOperationException("预览 PNG 无法解码。");
        var asset = new ContentAsset(new("preview-face"), ImmutableArray.CreateRange(bytes), new AssetMetadata.Png(image.GetWidth(), image.GetHeight()), []);
        return new()
        {
            Generation = new(1),
            Core = new(new("node-transition-preview"), [], [], [], [], [], [], [], [], [], [], [], [], [], []),
            Designs = new Dictionary<DesignHash, ContentLookup<DesignEvaluation>>
            { [Vehicle] = new ContentLookup<DesignEvaluation>.Found(Design(false)), [Station] = new ContentLookup<DesignEvaluation>.Found(Design(true)) },
            Assets = new Dictionary<AssetRevision, ContentLookup<ContentAsset>> { [asset.Revision] = new ContentLookup<ContentAsset>.Found(asset) },
            VoxelAppearances = new Dictionary<VoxelDefinitionId, ContentLookup<VoxelAppearanceProjection>>
            { [Cube] = new ContentLookup<VoxelAppearanceProjection>.Found(new(Cube, AllowedVoxelOrientations.All24, new VoxelCollision.None(),
                new VoxelPresentation.Cube([], MaterialCategory.PaintedMetal, 1, asset.Revision, asset.Revision, asset.Revision, asset.Revision), new(false, null))) },
            Terrains = new Dictionary<TerrainTypeId, ContentLookup<TerrainAppearanceProjection>>
            { [Terrain] = new ContentLookup<TerrainAppearanceProjection>.Found(new(Terrain, Cube, Cube, Cube, [], [], 0, [], new("Preview", ""))) },
            RailwayAppearances = new Dictionary<RuleDefinitionId, ContentLookup<RailwayAppearanceProjection>>
            { [Rule] = new ContentLookup<RailwayAppearanceProjection>.Found(new(Rule, Cube, 80, .12)) },
            SettlementLayouts = new Dictionary<SettlementId, ContentLookup<SettlementLayoutProjection>>
            { [new(1)] = new ContentLookup<SettlementLayoutProjection>.Found(new(new(1), new("preview:town"),
                new(new("ground"), Projection(new("ground"), []), new("Ground", "")),
                [new(1, PlotSize.SmallStation, new(0, 0, 0), VoxelOrientation.PzPy)], new("Town", ""))) }
        };
    }

    private static DesignSceneProjection Projection(DesignHash hash, ImmutableArray<SceneVoxelProjection> cells) => new(hash, null, null, true, null, null,
        cells, cells.Select(cell => new DesignElementProjection(cell.Number, cell.Element, new ComputedValue<ImmutableArray<GridPosition>>.Known([cell.At]), false, false)).ToImmutableArray(), [], [], []);

    private static DesignEvaluation Design(bool station)
    {
        var hash = station ? Station : Vehicle;
        var positions = station ? new GridPosition[] { new(0, 0, -22), new(0, 0, 22) }
            : Enumerable.Range(0, 8).SelectMany(z => Enumerable.Range(0, 3).Select(x => new GridPosition(x, 0, z))).ToArray();
        var cells = positions.Select((at, i) => new SceneVoxelProjection(at, Cube, VoxelOrientation.PxPy, null,
            new(station ? "#b0b0b0" : "#dd5533"), new(i + 1), new DesignElementPath.Cell([], i), [], at, VoxelOrientation.PxPy, false)).ToImmutableArray();
        var core = new DesignProjection(hash, new("preview"), station ? new DesignKind.Building(PlotSize.SmallStation, BuildingKind.Station, []) : new DesignKind.Vehicle(SizeClass.Small, 8),
            cells.Select(cell => new PartProjection(cell.Number, new CoreDefinitionReference.Voxel(Cube), new GoodId.Voxel(Cube), 100, 1, new(1), station ? [new PartCapability.Platform()] : [], false)).ToImmutableArray(),
            100, 100, 1, new(1), [], [], 0, [], true, []);
        return new DesignEvaluation.Complete(hash, core.Composition, new(new("Preview", ""), station ? new DesignSpecification.Building(PlotSize.SmallStation, BuildingKind.Station, []) : new DesignSpecification.Vehicle(SizeClass.Small), new([], [], [])),
            new(new("node-transition-preview"), core, []), Projection(hash, cells), true, []);
    }

    internal static SceneWorldSnapshot Snapshot(FixedContentView content, TrainPosition position, ulong step, TrainOrderFacing facing = TrainOrderFacing.FirstEndLeading)
    {
        var terrain = new TerrainView(Terrain, 0, []);
        var center = new NodeMapItem(new(1), new(0, 0), terrain, new NodeOccupant.Settlement(new(1), SettlementRelation.NonHostile), [], 0, 2, null);
        ImmutableArray<NodeMapItem> nodes = [center, center with { Id = new(2), Position = new(600, 0), Occupant = new NodeOccupant.Empty(), Platforms = 0 },
            center with { Id = new(3), Position = new(0, -600), Occupant = new NodeOccupant.Empty(), Platforms = 0 },
            center with { Id = new(4), Position = new(20, -600), Occupant = new NodeOccupant.Empty(), Platforms = 0 }];
        ImmutableArray<EdgeMapItem> edges =
        [
            new(new(1), new(1), new(2), 800, terrain, 2, 2, [], []),
            new(new(2), new(3), new(1), 800, terrain, 2, 2, [], []),
            new(new(3), new(4), new(1), 800, terrain, 2, 2, [], [])
        ];
        var activity = position switch
        {
            TrainPosition.Node { Phase: NodeTravelPhase.Docked } => TrainActivity.Servicing,
            TrainPosition.Node { Phase: NodeTravelPhase.Waiting } => TrainActivity.Stopped,
            _ => TrainActivity.Running
        };
        ulong? queueEnteredStep = position is TrainPosition.Node { Phase: NodeTravelPhase.Waiting, PlatformNumber: null }
            ? 1UL : null;
        var train = new TrainView(new(1), position, activity, [new(1)], new(1), activity == TrainActivity.Running ? 10 : 0, 4, 1, 1, 1, 10, null, null, 0,
            new(TargetPriority.ArmedFirst, TargetScope.All, EncounterAction.Engage, 0), [], null, [], facing, queueEnteredStep);
        var location = position is TrainPosition.Edge edge
            ? (WorldLocation)new WorldLocation.Edge(edge.Id, edge.DistanceMeters)
            : new WorldLocation.Node(new(1));
        var vehicle = new InstanceView(new InstanceId.Vehicle(new(1)), Vehicle, new("preview"), location, SettlementRelation.PlayerOwned,
            new InstanceDetails.Vehicle(SizeClass.Small, new(1)), InstanceLock.None, new(100, 100), [], []);
        var station = new InstanceView(new InstanceId.Building(new(1)), Station, new("preview"), new WorldLocation.Node(new(1)), SettlementRelation.NonHostile,
            new InstanceDetails.Building(new(1), PlotSize.SmallStation), InstanceLock.None, new(100, 100), [new(new(1), new(100, 100), false, false), new(new(2), new(100, 100), false, false)], []);
        var settlement = new SettlementView(new(1), new(1), new("preview:town"), new("preview:town"), SettlementRelation.NonHostile, new(1),
            [new(new(1), 1, PlotSize.SmallStation, [BuildingKind.Station], new PlotOccupant.Building(new(1)))], [], 0, 2, 0, [], [], [], new(0, 0, 0, 0),
            new(new(new("preview:population"), null, []), 0, 0, 0, 0, new PopulationExecution.NotStarted([]), null, []),
            new(TargetPriority.ArmedFirst, TargetScope.All), 1, false, [], [], [],
            new(new(new("preview:market"), null, []), new(0, 0, 0, 0, 0, 0, 0), []));
        return new(new FixedCoreReadView
        {
            Position = new(step, 0),
            InputIdentity = content.Core.Identity,
            Economy = UiWorldFixture.Economy,
            World = new(3, Rule, 1200, 1200, nodes, edges, [new(train.Id, position, activity)], [], []),
            Nodes = nodes.ToDictionary(n => n.Id, n => (Lookup<NodeView>)new Lookup<NodeView>.Found(new(n, edges.Where(e => e.Start == n.Id || e.End == n.Id).Select(e => e.Id).ToImmutableArray(), 80, n.Id.Value == 1 ? 96 : 0, 80))),
            Edges = edges.ToDictionary(e => e.Id, e => (Lookup<EdgeView>)new Lookup<EdgeView>.Found(new(e, [new(1, null, []), new(2, null, [])], null))),
            Trains = new Dictionary<TrainId, Lookup<TrainView>> { [train.Id] = new Lookup<TrainView>.Found(train) },
            Instances = new Dictionary<InstanceId, Lookup<InstanceView>> { [vehicle.Id] = new Lookup<InstanceView>.Found(vehicle), [station.Id] = new Lookup<InstanceView>.Found(station) },
            Settlements = new Dictionary<SettlementId, Lookup<SettlementView>> { [settlement.Id] = new Lookup<SettlementView>.Found(settlement) }
        }, content);
    }
}
