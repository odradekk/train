using System.Collections.Immutable;
using Godot;
using Train.App;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using Train.Scenes.Contracts;

namespace Train.Engine.Tests;

public partial class InspectionLightingPreview : Node
{
    private static readonly EdgeId Edge = new(1);
    private static readonly TerrainTypeId Terrain = new("preview:terrain");
    private static readonly RuleDefinitionId Rule = new("preview:railway");
    private static readonly DesignHash Design = new("preview-locomotive");
    private static readonly WorldSceneTarget Location = new WorldSceneTarget.Location(new WorldLocation.Edge(Edge, 128));
    private readonly Label _status = new() { Position = new(12, 56), MouseFilter = Control.MouseFilterEnum.Ignore };
    private HostCoordinator? _host;
    private WorldSceneController? _world;
    private bool _opened;
    private bool _reportedReady;

    public static void Start(Node root) => root.AddChild(new InspectionLightingPreview { Name = "InspectionLightingPreview" });

    public override void _Ready()
    {
        try
        {
            var content = CreateContent();
            var core = new ScriptedCoreSession(CreateCore());
            var app = GD.Load<PackedScene>("res://App/App.tscn").Instantiate<Train.App.App>();
            app.Name = "App";
            app.GetNode<Control>("MainMenu").Visible = false;
            app.CreateHost = logger => WorldSceneFixture.CreateHost(core, content, logger);
            app.CreateScenes = Train.Scenes.ScenesFactory.Create;
            AddChild(app);
            _host = app.Host!;
            _host.AutoPauseOnFocusLoss = false;
            _world = app.WorldScenes!;
            var hud = new CanvasLayer { Name = "PreviewHud", Layer = 10 };
            AddChild(hud);
            var toolbar = new HBoxContainer { Position = new(12, 12) };
            hud.AddChild(toolbar);
            var location = new Button { Name = "Railway", Text = "线路 / 重新进入" };
            var follow = new Button { Name = "FollowTrain", Text = "跟随列车" };
            toolbar.AddChild(location);
            toolbar.AddChild(follow);
            location.Pressed += () => Open(Location);
            follow.Pressed += () => Open(new WorldSceneTarget.Follow(new(1), new(1)));
            hud.AddChild(_status);
        }
        catch (Exception exception) { GD.PrintErr(exception); GetTree().Quit(1); }
    }

    public override void _Process(double delta)
    {
        try
        {
            if (_host?.GetStatus() is null || _world is null) return;
            if (!_opened) { _host.SetPaused(true); Open(Location); _opened = true; }
            var frame = _world.Display?.DisplayedFrame;
            _status.Text = $"真实 Scenes 工厂 / Godot 渲染；固定 Core / Content 输入（暂停）\n" +
                $"场景：{frame?.Readiness.ToString() ?? "等待"}；光照：{_world.View.Lighting}\n" +
                "反引号 / ~ 控制台：lighting daylight / overcast / night / restore\n" +
                "右拖旋转 / 线路视角中拖平移 / 滚轮缩放 / Escape 返回";
            if (!_reportedReady && frame?.Readiness == SceneReadiness.Ready)
            {
                _reportedReady = true;
                GD.Print("preview_ready: InspectionLightingPreview; real Scenes/Godot; fixed Core/Content");
            }
        }
        catch (Exception exception) { GD.PrintErr(exception); GetTree().Quit(1); }
    }

    private void Open(WorldSceneTarget target)
    {
        if (_host?.GetStatus() is not null) _world!.Open(target);
    }

    private static FixedCoreReadView CreateCore()
    {
        var terrain = new TerrainView(Terrain, 0, []);
        var first = new NodeMapItem(new(1), new(0, 0), terrain, new NodeOccupant.Empty(), [], 0, 0, null);
        var last = first with { Id = new(2), Position = new(0, 256) };
        var edge = new EdgeMapItem(Edge, first.Id, last.Id, 256, terrain, 1, 1, [], []);
        var train = new TrainView(new(1), new TrainPosition.Edge(Edge, 1, TravelDirection.Forward, 128),
            TrainActivity.Running, [new(1)], new(1), 0, 6, 1, 1, 1, 10, null, null, 0,
            new(TargetPriority.ArmedFirst, TargetScope.All, EncounterAction.Engage, 0), [], null, [], null, TrainOrderFacing.FirstEndLeading);
        var instance = new InstanceView(new InstanceId.Vehicle(new(1)), Design, new("preview-composition"),
            new WorldLocation.Edge(Edge, 128), SettlementRelation.PlayerOwned, new InstanceDetails.Vehicle(SizeClass.Small, train.Id),
            InstanceLock.None, new(100, 100), [], []);
        return new(new(0, 0), HostCoreScenario.Input)
        {
            World = new(3, Rule, 256, 256, [first, last], [edge], [new(train.Id, train.Position, train.Activity)], [], []),
            Nodes = new Dictionary<NodeId, Lookup<NodeView>>
            { [first.Id] = new Lookup<NodeView>.Found(new(first, [Edge])), [last.Id] = new Lookup<NodeView>.Found(new(last, [Edge])) },
            Edges = new Dictionary<EdgeId, Lookup<EdgeView>> { [Edge] = new Lookup<EdgeView>.Found(new(edge, [new(1, null, [train.Id])], null)) },
            Trains = new Dictionary<TrainId, Lookup<TrainView>> { [train.Id] = new Lookup<TrainView>.Found(train) },
            Instances = new Dictionary<InstanceId, Lookup<InstanceView>> { [instance.Id] = new Lookup<InstanceView>.Found(instance) }
        };
    }

    private static FixedContentView CreateContent()
    {
        var assets = new Dictionary<AssetRevision, ContentLookup<ContentAsset>>();
        AssetRevision Texture(string name)
        {
            var revision = new AssetRevision("preview-" + name);
            var bytes = Godot.FileAccess.GetFileAsBytes($"res://builtin/core/resources/voxels/shared/{name}-face.png");
            using var image = new Image();
            if (image.LoadPngFromBuffer(bytes) != Error.Ok) throw new InvalidOperationException("预览 PNG 读取失败。");
            var asset = new ContentAsset(revision, ImmutableArray.CreateRange(bytes), new AssetMetadata.Png(image.GetWidth(), image.GetHeight()), []);
            assets.Add(revision, new ContentLookup<ContentAsset>.Found(asset));
            return revision;
        }
        var metal = Texture("metal");
        var soil = Texture("soil");
        var equipment = Texture("equipment");
        var body = new VoxelDefinitionId("preview:body");
        var glass = new VoxelDefinitionId("preview:glass");
        var wheel = new VoxelDefinitionId("preview:wheel");
        var ground = new VoxelDefinitionId("preview:ground");
        VoxelAppearanceProjection Appearance(VoxelDefinitionId id, MaterialCategory category, AssetRevision texture, bool wheelset = false) =>
            new(id, AllowedVoxelOrientations.All24, new VoxelCollision.None(),
                new VoxelPresentation.Cube([], category, 1, texture, texture, texture, texture), new(wheelset, null));
        var cells = ImmutableArray.CreateBuilder<SceneVoxelProjection>();
        for (var z = 0; z < 12; z++) for (var y = 1; y <= (z >= 8 ? 4 : 2); y++) for (var x = -1; x <= 1; x++)
        {
            var at = new GridPosition(x, y, z);
            var window = z >= 8 && y == 3 && (x != 0 || z == 11);
            var id = window ? glass : y == 1 && x == 0 && z is 2 or 9 ? wheel : body;
            var number = cells.Count + 1;
            cells.Add(new(at, id, VoxelOrientation.PxPy, null, window ? null : new PaintColor("#b54832"),
                new(number), new DesignElementPath.Cell([], number - 1), [], at, VoxelOrientation.PxPy, false));
        }
        var scene = new DesignSceneProjection(Design, null, null, true, null, null, cells.ToImmutable(), [], [], [], []);
        var core = new DesignProjection(Design, new("preview-composition"), new DesignKind.Vehicle(SizeClass.Small, 12),
            [], 100, 100, 1, new(1), [], [], 0, [], true, []);
        var evaluation = new DesignEvaluation.Complete(Design, core.Composition,
            new(new("预览小型机车", ""), new DesignSpecification.Vehicle(SizeClass.Small), new([], [], [])),
            new(HostCoreScenario.Input, core, []), scene, true, []);
        return new(HostContentScenario.CreateProjection(HostCoreScenario.Input), new(1))
        {
            Assets = assets,
            Designs = new Dictionary<DesignHash, ContentLookup<DesignEvaluation>> { [Design] = new ContentLookup<DesignEvaluation>.Found(evaluation) },
            Terrains = new Dictionary<TerrainTypeId, ContentLookup<TerrainAppearanceProjection>>
            { [Terrain] = new ContentLookup<TerrainAppearanceProjection>.Found(new(Terrain, ground, ground, ground, [], [], 0, [], new("预览地面", ""))) },
            RailwayAppearances = new Dictionary<RuleDefinitionId, ContentLookup<RailwayAppearanceProjection>>
            { [Rule] = new ContentLookup<RailwayAppearanceProjection>.Found(new(Rule, 40, 0.12)) },
            VoxelAppearances = new Dictionary<VoxelDefinitionId, ContentLookup<VoxelAppearanceProjection>>
            {
                [body] = new ContentLookup<VoxelAppearanceProjection>.Found(Appearance(body, MaterialCategory.PaintedMetal, metal)),
                [glass] = new ContentLookup<VoxelAppearanceProjection>.Found(Appearance(glass, MaterialCategory.Glass, equipment)),
                [wheel] = new ContentLookup<VoxelAppearanceProjection>.Found(Appearance(wheel, MaterialCategory.BareMetal, metal, true)),
                [ground] = new ContentLookup<VoxelAppearanceProjection>.Found(Appearance(ground, MaterialCategory.StoneSoil, soil))
            }
        };
    }
}
