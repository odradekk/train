using System.Collections.Immutable;
using Godot;
using Train.App;
using Train.App.Assets;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using Train.Engine.Tests.Assets;
using Train.Scenes.Contracts;
using Train.Scenes.ContractSupport;
using NVector2 = System.Numerics.Vector2;
using NVector3 = System.Numerics.Vector3;
using NQuaternion = System.Numerics.Quaternion;

namespace Train.Engine.Tests;

public partial class WorldScenePreview : Node
{
    private static readonly WorldSceneTarget Target = new WorldSceneTarget.Follow(new(1), new(1));
    private static readonly WorldObjectId Subject = new WorldObjectId.Instance(new InstanceId.Vehicle(new(1)));
    private static readonly WorldObjectId SecondSubject = new WorldObjectId.Instance(new InstanceId.Vehicle(new(2)));
    private readonly Label _status = new() { Position = new(12, 56), MouseFilter = Control.MouseFilterEnum.Ignore };
    private readonly ColorRect _map = new() { Color = new Color("17202c"), MouseFilter = Control.MouseFilterEnum.Stop };
    private readonly Button _enter = new() { Text = "进入", Name = "Enter", Disabled = true };
    private readonly Button _next = new() { Text = "下一情景", Name = "NextFrame" };
    private Train.App.App? _app;
    private ScriptedCoreSession? _core;
    private PreviewSession? _session;
    private int _queuedSteps;
    private bool _running;
    private int _minimizedSteps;
    private int _minimizedPresents;
    private HostCoordinator? _host;
    private WorldSceneController? _world;
    private FixedContentView? _content;
    private ImmutableArray<SceneObject> _gallery;
    private readonly Dictionary<SceneObjectId, string> _names = [];
    private int _stage;
    private string _selected = "未选择";
    private static readonly string[] Stages = ["初始就绪", "原点 +100 / 已有根 -100 / 新对象", "部分就绪 / 远处雾", "另一空间 / 跟随连续"];

    public static void Start(Node root) => root.AddChild(new WorldScenePreview { Name = "WorldScenePreview" });

    public override void _Ready()
    {
        try
        {
            _content = CreateContent();
            _gallery = Gallery();
            _core = new(new FixedCoreReadView(new(0, 0), HostCoreScenario.Input));
            _app = GD.Load<PackedScene>("res://App/App.tscn").Instantiate<Train.App.App>();
            _app.GetNode<Control>("MainMenu").Visible = false;
            _app.CreateHost = logger => WorldSceneFixture.CreateHost(_core, _content, logger);
            _app.CreateScenes = () => new FixedDisplayFactory
            {
                OpenWorldCall = (binding, _) => new SceneResult<IWorldSceneSession>.Success(
                    _session = new PreviewSession(this, binding))
            };
            AddChild(_app);
            _host = _app.Host!;
            _host.AutoPauseOnFocusLoss = false;
            _world = _app.WorldScenes!;
            _world.View.CameraRig.Zoom(1);
            _world.Selected += _ => { _selected = "命中已核对"; UpdateStatus(); };
            _world.ReturnedToMap += reason =>
            {
                _map.Visible = true; _stage = 0;
                _status.Text = reason is null ? "验证地图：已退出，可重新进入。" : $"验证地图：{reason.Code}";
            };
            var mapLayer = new CanvasLayer { Name = "ValidationMap", Layer = 9 };
            AddChild(mapLayer);
            mapLayer.AddChild(_map);
            _map.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            var title = new Label { Text = "显示帧验证地图\n点击“进入”显示固定场景。\n这不是生产地图或真实体素工厂。",
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MouseFilter = Control.MouseFilterEnum.Ignore };
            _map.AddChild(title);
            title.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            var hud = new CanvasLayer { Name = "ValidationHud", Layer = 10 };
            AddChild(hud);
            var toolbar = new HBoxContainer { Name = "Toolbar", Position = new(12, 12), MouseFilter = Control.MouseFilterEnum.Ignore };
            hud.AddChild(toolbar);
            var exit = new Button { Text = "退出", Name = "Exit" };
            var reopen = new Button { Text = "切换车辆", Name = "SwitchVehicle" };
            var node = new Button { Text = "节点", Name = "NodeLocation" };
            var railway = new Button { Text = "线路", Name = "RailwayLocation" };
            var minimize = new Button { Text = "最小化", Name = "Minimize" };
            foreach (var button in new[] { _enter, _next, exit, reopen, node, railway, minimize })
            {
                button.CustomMinimumSize = new(110, 36);
                toolbar.AddChild(button);
            }
            hud.AddChild(_status);
            _enter.Pressed += Enter;
            reopen.Pressed += () => Open(new WorldSceneTarget.Follow(new(1), new(_session?.Target is WorldSceneTarget.Follow { Vehicle.Value: 1 } ? 2UL : 1UL)));
            node.Pressed += () => Open(new WorldSceneTarget.Location(new WorldLocation.Node(new(1))));
            railway.Pressed += () => Open(new WorldSceneTarget.Location(new WorldLocation.Edge(new(1), 0)));
            minimize.Pressed += () => GetWindow().Mode = GetWindow().Mode == Window.ModeEnum.Minimized
                ? Window.ModeEnum.Windowed : Window.ModeEnum.Minimized;
            _next.Pressed += () => { if (_world.Display is not null && _stage < Stages.Length - 1) _stage++; };
            exit.Pressed += _world.ReturnToMap;
            _status.Text = "准备固定宿主读档…";
        }
        catch (Exception exception) { GD.PrintErr(exception); GetTree().Quit(1); }
    }

    public override void _Process(double delta)
    {
        try
        {
            if (_host is null || _world is null) return;
            // App 在子节点的真实帧回调中消费这些固定单步；预览只补充本帧可能需要的有限输入。
            var needed = checked((int)Math.Ceiling(delta * SimulationTime.StepsPerSecond) + 1);
            while (_queuedSteps - _core!.AdvanceCount < needed)
            {
                var position = new SimulationPosition((ulong)++_queuedSteps, 0);
                _core.QueueAdvance(new(1, position, [], null, null), new FixedCoreReadView(position, HostCoreScenario.Input));
            }
            var ready = _host.GetStatus() is not null;
            if (ready && !_running) { _host.SetPaused(false); _running = true; }
            _enter.Disabled = !ready;
            _next.Disabled = _world.Display is null || _stage >= Stages.Length - 1;
            if (GetWindow().Mode == Window.ModeEnum.Minimized)
            {
                _minimizedSteps = _core!.AdvanceCount;
                _minimizedPresents = _session?.PresentCalls ?? 0;
            }
            if (_world.Display?.DisplayedFrame is not null) AddNames();
            UpdateStatus();
        }
        catch (Exception exception) { GD.PrintErr(exception); GetTree().Quit(1); }
    }

    private void Enter() { _stage = 0; Open(Target); }

    private void Open(WorldSceneTarget target)
    {
        if (_host?.GetStatus() is null) return;
        _map.Visible = false;
        _selected = "未选择";
        _world!.Open(target);
    }

    private void UpdateStatus()
    {
        var display = _world?.Display;
        var root = display?.Objects.GetValueOrDefault(new(1))?.Root;
        var fog = display?.Root.GetNodeOrNull<Node3D>("LoadingFog");
        _status.Text = $"情景 {_stage + 1}/4：{Stages[_stage]} | 选择：{_selected}\n" +
            $"模拟步数={_core?.AdvanceCount} Present={_session?.PresentCalls} Observe={_session?.ObserveCalls} " +
            $"Session={_session?.Id.Value.ToString()[..8]} Request={_session?.Request.Value}\n" +
            $"最小化最后观测：Steps={_minimizedSteps} Present={_minimizedPresents}\n" +
            $"Root={root?.GetInstanceId()} Fog={fog?.GetInstanceId()} TreeExit={_session?.TreeExits} " +
            $"Camera={_world?.View.CameraRig.Camera.Current} Environment={_world?.View.GetChildren().OfType<WorldEnvironment>().Single().Environment is not null}\n" +
            "右拖旋转 / 中拖平移 / 滚轮缩放 / 左键拾取 / Escape 返回；最小化后用任务栏恢复。";
        if (root is not null && _session is { WatchedRoot: null } session)
        {
            session.WatchedRoot = root;
            root.TreeExited += () => session.TreeExits++;
        }
    }

    private void AddNames()
    {
        foreach (var (id, text) in _names)
            if (_world!.Display!.Objects.TryGetValue(id, out var item) && item.Root.GetNodeOrNull<Label3D>("Caption") is null)
                item.Root.AddChild(new Label3D { Name = "Caption", Text = text, Position = new(0, 3.5f, 0), FontSize = 40,
                    PixelSize = 0.01f, Billboard = BaseMaterial3D.BillboardModeEnum.Enabled });
    }

    // 只接受工具栏定义的两辆车、一个节点和一条线路；每帧从当前情景生成不可变帧。
    private sealed class PreviewSession(WorldScenePreview preview, SceneBinding binding) : IWorldSceneSession
    {
        public SceneSessionId Id { get; } = new(Guid.NewGuid());
        public SceneBinding Binding { get; } = binding;
        internal SceneRequestId Request { get; private set; }
        internal WorldSceneTarget? Target { get; private set; }
        internal int PresentCalls { get; private set; }
        internal int ObserveCalls { get; private set; }
        internal int TreeExits { get; set; }
        internal Node3D? WatchedRoot { get; set; }
        private int _presentedStage = -1;
        private SceneHit? _hit;
        private bool _disposed;

        public SceneResult<SceneRequestId> RequestView(WorldSceneTarget target)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (target is not (WorldSceneTarget.Follow { Train.Value: 1, Vehicle.Value: 1 or 2 }
                or WorldSceneTarget.Location { Value: WorldLocation.Node { Id.Value: 1 } }
                or WorldSceneTarget.Location { Value: WorldLocation.Edge { Id.Value: 1, DistanceMeters: 0 } }))
                throw new InvalidOperationException("UNCONFIGURED: 预览目标。");
            Target = target;
            Request = new(Request.Value + 1);
            _presentedStage = -1;
            return new SceneResult<SceneRequestId>.Success(Request);
        }

        public SceneResult<SimulationPosition> Observe(SceneWorldSnapshot snapshot, ImmutableArray<EventEnvelope> events)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (snapshot.Core.InputIdentity != HostCoreScenario.Input || snapshot.Content != preview._content || !events.IsEmpty)
                throw new InvalidOperationException("UNCONFIGURED: 预览观察输入。");
            ObserveCalls++;
            return new SceneResult<SimulationPosition>.Success(snapshot.Core.Position);
        }

        public SceneResult<ISceneFrame> Present(SceneFrameRequest request, ISceneFrame? displayedFrame)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (Target is null || displayedFrame is not null &&
                (displayedFrame.Update.Frame.Session != Id || displayedFrame.Update.Frame.Request != Request))
                throw new InvalidOperationException("UNCONFIGURED: 预览显示基准。");
            var stage = preview._stage;
            var space = Target is WorldSceneTarget.Location { Value: WorldLocation.Node }
                ? 3UL : stage == 3 ? 2UL : 1UL;
            var full = displayedFrame is null || displayedFrame.Update.Frame.Space.Value != space;
            var changed = stage != _presentedStage;
            var stamp = new SceneFrameStamp(Id, Request, new(space), (ulong)++PresentCalls);
            var pose = stage == 3 ? new ScenePose(new(10, 0, -10), NQuaternion.CreateFromAxisAngle(NVector3.UnitY, 0.5f))
                : new ScenePose(new(stage == 0 ? 0 : -100, 0, 0), NQuaternion.Identity);
            var secondPose = pose with { Position = pose.Position + NVector3.Transform(new(8, 0, 0), pose.Rotation) };
            var second = Target is WorldSceneTarget.Follow { Vehicle.Value: 2 };
            var subject = second ? SecondSubject : Subject;
            ImmutableArray<SceneObject> objects = full ? preview._gallery.Select(item => item.Id.Value switch
            {
                1 => item with { Pose = pose },
                91 => item with { Pose = secondPose },
                _ => item
            }).ToImmutableArray() : [];
            if (stage is 1 or 2 && (full || changed && stage == 1))
                objects = objects.Add(new SceneObject(new(90), null, new(new(-81, 0, -5), NQuaternion.Identity), null,
                    [new SceneVisual.Geometry(Cube(new SceneMaterial.BuiltIn(BuiltInMaterial.Construction)), new(false, 0, 0, false))], [], []));
            ImmutableArray<SceneBounds> fog = stage == 2 ? [new(new(-110, -1, -14), new(-90, 5, -12))] : [];
            _hit = new(stamp, new(second ? 91UL : 1UL), new PickSubject.WorldObject(subject, null, null), default,
                GridFace.PositiveY, second ? secondPose.Position : pose.Position, NVector3.UnitY, 1, []);
            var frame = new FixedSceneFrame
            {
                Update = new(full ? null : displayedFrame!.Update.Frame, stamp,
                    !full && changed && stage == 1 ? new(-100, 0, 0) : NVector3.Zero, objects, [], new(fog, []), []),
                Origin = stage is 1 or 2 ? new(100, 0, 0) : default,
                NodePanBounds = Target is WorldSceneTarget.Location { Value: WorldLocation.Node }
                    ? new(new(-20, -20), new(20, 20)) : null,
                Readiness = stage == 2 ? SceneReadiness.Partial : SceneReadiness.Ready,
                PickResult = new ScenePick.Hit(_hit),
                Poses = ImmutableDictionary<WorldObjectId, Lookup<ScenePose>>.Empty
                    .Add(Subject, new Lookup<ScenePose>.Found(pose))
                    .Add(SecondSubject, new Lookup<ScenePose>.Found(secondPose)),
                TerrainHeightResult = new Lookup<double>.Found(-2)
            };
            _presentedStage = stage;
            return new SceneResult<ISceneFrame>.Success(frame);
        }

        public SceneResult<PickSubject> Resolve(SceneHit hit) => hit == _hit
            ? new SceneResult<PickSubject>.Success(hit.Subject)
            : throw new InvalidOperationException("UNCONFIGURED: 预览命中核对。");
        public ValueTask DisposeAsync() { _disposed = true; return ValueTask.CompletedTask; }
    }

    private FixedContentView CreateContent()
    {
        using var image = Image.CreateEmpty(32, 32, false, Image.Format.Rgba8);
        for (var y = 0; y < 32; y++) for (var x = 0; x < 32; x++)
            image.SetPixel(x, y, (x / 4 + y / 4) % 2 == 0 ? new Color("c5ccd0") : new Color("6f7a83"));
        var png = new ContentAsset(new("preview-checker"), ImmutableArray.CreateRange(image.SavePngToBuffer()), new AssetMetadata.Png(32, 32), []);
        var bytes = GlbFixture.Create(feature: "tangent", png: image.SavePngToBuffer(), baseColorFactor: [0.5f, 0.8f, 1, 1]);
        using var dispatcher = new GodotMainThreadDispatcher();
        var inspected = (AssetInspection.Success)new GodotAssetInspector(dispatcher).Inspect(AssetKind.Glb, new("preview-model"), bytes);
        var model = new ContentAsset(new("preview-model"), bytes, inspected.Metadata, []);
        return new(HostContentScenario.CreateProjection(HostCoreScenario.Input), new(1))
        {
            Assets = new Dictionary<AssetRevision, ContentLookup<ContentAsset>>
                { [png.Revision] = new ContentLookup<ContentAsset>.Found(png), [model.Revision] = new ContentLookup<ContentAsset>.Found(model) }
        };
    }

    private ImmutableArray<SceneObject> Gallery()
    {
        var objects = ImmutableArray.CreateBuilder<SceneObject>();
        objects.Add(new(new(1), null, new(NVector3.Zero, NQuaternion.Identity), Subject, [], [], []));
        ulong id = 2;
        void Add(string name, NVector3 position, SceneVisual visual)
        {
            _names.Add(new(id), name);
            objects.Add(new(new(id++), new(1), new(position, NQuaternion.Identity), null, [visual], [], []));
        }
        var categories = Enum.GetValues<MaterialCategory>();
        var categoryNames = new[] { "涂装金属", "裸金属", "混凝土", "岩土", "玻璃", "橡胶", "有机物" };
        for (var index = 0; index < categories.Length; index++)
            Add(categoryNames[index], new((index - 3) * 5, 0, -4),
                new SceneVisual.Geometry(Cube(new SceneMaterial.Voxel(new("preview-checker"), categories[index], categories[index] == MaterialCategory.PaintedMetal ? new("#5080b0") : null)), new(true, ulong.MaxValue, 0.2f, false)));
        var builtins = Enum.GetValues<BuiltInMaterial>();
        var builtinNames = new[] { "钢轨", "轨枕", "水面", "施工" };
        for (var index = 0; index < builtins.Length; index++)
        {
            var material = new SceneMaterial.BuiltIn(builtins[index]);
            var position = new NVector3((index - 1.5f) * 5, 0, 4);
            Add(builtinNames[index], position, new SceneVisual.Geometry(builtins[index] == BuiltInMaterial.Water ? Plane(material) : Cube(material), new(false, 0, 0, false)));
            if (builtins[index] == BuiltInMaterial.Water)
                Add("河床", position + new NVector3(0, -1, 0), new SceneVisual.Geometry(
                    Plane(new SceneMaterial.Voxel(new("preview-checker"), MaterialCategory.StoneSoil, new("#a08560"))), new(false, 0, 0, false)));
        }
        Add("轮对", new(-15, 0, 7), new SceneVisual.Wheelset(7, 1.25, 0.375, 0.5));
        Add("模型 / 三表面", new(15, -1, 3), new SceneVisual.Model(new("preview-model"),
            [MaterialCategory.BareMetal, MaterialCategory.PaintedMetal, MaterialCategory.Glass], new("#90b0c0"), new(true, 77, 0.3f, false), new(0, 0.1f, 0, 0)));
        Add("验证地面", new(0, -2, -4), new SceneVisual.Geometry(
            Plane(new SceneMaterial.Voxel(new("preview-checker"), MaterialCategory.StoneSoil, new("#807860")), 50, 44),
            new(false, 0, 0, false)));
        _names.Add(new(90), "新加入对象");
        _names.Add(new(91), "车辆 2");
        objects.Add(new(new(91), null, new(new(8, 0, 0), NQuaternion.Identity), SecondSubject,
            [new SceneVisual.Geometry(Cube(new SceneMaterial.BuiltIn(BuiltInMaterial.Construction)), new(false, 0, 0, false))], [], []));
        return objects.ToImmutable();
    }

    private static SceneMesh Cube(SceneMaterial material)
    {
        var vertices = ImmutableArray.CreateBuilder<SceneVertex>();
        var indices = ImmutableArray.CreateBuilder<int>();
        foreach (var normal in new[] { NVector3.UnitX, -NVector3.UnitX, NVector3.UnitY, -NVector3.UnitY, NVector3.UnitZ, -NVector3.UnitZ })
        {
            var up = Math.Abs(normal.Y) > 0.5f ? NVector3.UnitZ : NVector3.UnitY;
            var right = NVector3.Cross(up, normal);
            var at = vertices.Count;
            foreach (var uv in new[] { new NVector2(0, 1), new NVector2(1, 1), new NVector2(1, 0), new NVector2(0, 0) })
            {
                var position = normal * 1.5f + right * ((uv.X - 0.5f) * 3) + up * ((0.5f - uv.Y) * 3);
                vertices.Add(new(position, normal, uv, uv, ExposedFaceEdges.MinimumU | ExposedFaceEdges.MaximumU | ExposedFaceEdges.MinimumV | ExposedFaceEdges.MaximumV, (position.Y + 1.5f) / 3, 0.1f));
            }
            indices.AddRange(new[] { at, at + 1, at + 2, at, at + 2, at + 3 });
        }
        return new(material, vertices.ToImmutable(), indices.ToImmutable());
    }

    private static SceneMesh Plane(SceneMaterial material, float width = 4, float depth = 4) => new(material,
        [new(new(-width/2, 0, -depth/2), NVector3.UnitY, new(0, 0), new(0, 0), ExposedFaceEdges.MinimumU | ExposedFaceEdges.MinimumV, 0, 0),
         new(new(-width/2, 0, depth/2), NVector3.UnitY, new(0, 1), new(0, 1), ExposedFaceEdges.MinimumU | ExposedFaceEdges.MaximumV, 0, 0),
         new(new(width/2, 0, depth/2), NVector3.UnitY, new(1, 1), new(1, 1), ExposedFaceEdges.MaximumU | ExposedFaceEdges.MaximumV, 0, 0),
         new(new(width/2, 0, -depth/2), NVector3.UnitY, new(1, 0), new(1, 0), ExposedFaceEdges.MaximumU | ExposedFaceEdges.MinimumV, 0, 0)],
        [0, 1, 2, 0, 2, 3]);

}
