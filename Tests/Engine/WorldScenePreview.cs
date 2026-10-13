using System.Collections.Immutable;
using Godot;
using Train.App;
using Train.App.Assets;
using Train.App.Logging;
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

// 仅供 #319 真实窗口检查的 Debug 固定场景，不接真实工厂。
public partial class WorldScenePreview : Node
{
    private static readonly WorldSceneTarget Target = new WorldSceneTarget.Follow(new(1), new(1));
    private static readonly WorldObjectId Subject = new WorldObjectId.Instance(new InstanceId.Vehicle(new(1)));
    private readonly Label _status = new() { Position = new(12, 56), MouseFilter = Control.MouseFilterEnum.Ignore };
    private readonly ColorRect _map = new() { Color = new Color("17202c"), MouseFilter = Control.MouseFilterEnum.Stop };
    private readonly Button _enter = new() { Text = "进入", Name = "Enter", Disabled = true };
    private readonly Button _next = new() { Text = "下一帧", Name = "NextFrame" };
    private TrainLogger? _logger;
    private HostCoordinator? _host;
    private WorldSceneController? _world;
    private FixedContentView? _content;
    private ImmutableArray<SceneObject> _gallery;
    private readonly Dictionary<SceneObjectId, string> _names = [];
    private bool _advance;
    private int _stage;
    private string _selected = "未选择";
    private static readonly string[] Stages = ["初始就绪", "原点 +100 / 已有根 -100 / 新对象", "部分就绪 / 远处雾", "另一空间 / 跟随连续"];

    public static void Start(Node root) => root.AddChild(new WorldScenePreview { Name = "WorldScenePreview" });

    public override void _Ready()
    {
        try
        {
            _logger = TrainLogger.StartSession(ProjectSettings.GlobalizePath("res://TestResults/issue-319-preview/logs"), LogLevel.Debug, new GodotLogOutput());
            _content = CreateContent();
            _gallery = Gallery();
            _host = CreateHost(_content, _logger);
            var factory = new FixedDisplayFactory { OpenWorldCall = (binding, _) => new SceneResult<IWorldSceneSession>.Success(Session(binding)) };
            _world = new(_host, factory, _logger);
            _world.View.CameraRig.Zoom(1);
            AddChild(_world.View);
            _world.Selected += _ => { _selected = "车辆 1（命中已核对）"; UpdateStatus(); };
            _world.ReturnedToMap += reason =>
            {
                _map.Visible = true; _advance = false; _stage = 0;
                _status.Text = reason is null ? "验证地图：已退出，可重新进入。" : $"验证地图：{reason.Code}";
            };
            var mapLayer = new CanvasLayer { Name = "ValidationMap", Layer = 9 };
            AddChild(mapLayer);
            mapLayer.AddChild(_map);
            _map.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            var title = new Label { Text = "#319 验证地图\n点击“进入”显示固定场景。\n这不是生产地图或真实体素工厂。",
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MouseFilter = Control.MouseFilterEnum.Ignore };
            _map.AddChild(title);
            title.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            var hud = new CanvasLayer { Name = "ValidationHud", Layer = 10 };
            AddChild(hud);
            var toolbar = new HBoxContainer { Name = "Toolbar", Position = new(12, 12), MouseFilter = Control.MouseFilterEnum.Ignore };
            hud.AddChild(toolbar);
            var exit = new Button { Text = "退出", Name = "Exit" };
            var reopen = new Button { Text = "重开", Name = "Reopen" };
            foreach (var button in new[] { _enter, _next, exit, reopen })
            {
                button.CustomMinimumSize = new(110, 36);
                toolbar.AddChild(button);
            }
            hud.AddChild(_status);
            _enter.Pressed += Enter;
            reopen.Pressed += Enter;
            _next.Pressed += () => { if (_world.Display is not null && _stage < Stages.Length) _advance = true; };
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
            _host.ProcessFrame(0);
            var ready = _host.GetStatus() is not null;
            if (_enter.Disabled && ready) _status.Text = "验证地图：固定宿主已准备，点击进入。";
            _enter.Disabled = !ready;
            _next.Disabled = _world.Display is null || _stage >= Stages.Length;
            if (_advance && GetWindow().Mode != Window.ModeEnum.Minimized)
            {
                _advance = false;
                _world.ProcessFrame(delta, false);
                if (_world.Display?.DisplayedFrame is not null)
                {
                    _stage++;
                    AddNames();
                    UpdateStatus();
                }
            }
            else _world.View.CameraRig.Update(delta);
            _logger?.DrainGodotQueue();
        }
        catch (Exception exception) { GD.PrintErr(exception); GetTree().Quit(1); }
    }

    private void Enter()
    {
        if (_host?.GetStatus() is null) return;
        _map.Visible = false; _stage = 0; _selected = "未选择";
        _world!.Open(Target);
        _advance = true;
        _status.Text = "加载固定首帧…";
    }

    private void UpdateStatus() => _status.Text = $"阶段 {_stage}/4：{(_stage > 0 ? Stages[_stage - 1] : "准备")} | 选择：{_selected}\n" +
        "右拖旋转 / 滚轮缩放 / 左键拾取 / Escape 退出；顶部按钮遮挡不拾取。";

    private void AddNames()
    {
        foreach (var (id, text) in _names)
            if (_world!.Display!.Objects.TryGetValue(id, out var item) && item.Root.GetNodeOrNull<Label3D>("Caption") is null)
                item.Root.AddChild(new Label3D { Name = "Caption", Text = text, Position = new(0, 3.5f, 0), FontSize = 40,
                    PixelSize = 0.01f, Billboard = BaseMaterial3D.BillboardModeEnum.Enabled });
    }

    private FixedDisplaySession Session(SceneBinding binding)
    {
        var id = new SceneSessionId(Guid.NewGuid());
        var frames = ImmutableArray.CreateBuilder<SceneResult<ISceneFrame>>();
        var hits = ImmutableDictionary.CreateBuilder<SceneHit, SceneResult<PickSubject>>();
        for (var stage = 0; stage < 4; stage++)
        {
            var stamp = new SceneFrameStamp(id, new(1), new(stage == 3 ? 2UL : 1UL), (ulong)stage + 1);
            var pose = stage == 3 ? new ScenePose(new(10, 0, -10), NQuaternion.CreateFromAxisAngle(NVector3.UnitY, 0.5f))
                : new ScenePose(new(stage == 0 ? 0 : -100, 0, 0), NQuaternion.Identity);
            var hit = new SceneHit(stamp, new(1), new PickSubject.WorldObject(Subject, null, null), default,
                GridFace.PositiveY, pose.Position, NVector3.UnitY, 1, []);
            hits.Add(hit, new SceneResult<PickSubject>.Success(hit.Subject));
            ImmutableArray<SceneObject> objects = stage switch
            {
                0 => _gallery,
                1 => [new(new(90), null, new(new(-81, 0, -5), NQuaternion.Identity), null,
                    [new SceneVisual.Geometry(Cube(new SceneMaterial.BuiltIn(BuiltInMaterial.Construction)), new(false, 0, 0, false))], [], [])],
                2 => [],
                _ => _gallery.Select(item => item.Id == new SceneObjectId(1) ? item with { Pose = pose } : item).ToImmutableArray()
            };
            ImmutableArray<SceneBounds> fog = stage == 2 ? [new(new(-110, -1, -14), new(-90, 5, -12))] : [];
            var baseline = stage is 1 or 2 ? ((SceneResult<ISceneFrame>.Success)frames[stage - 1]).Value.Update.Frame : (SceneFrameStamp?)null;
            frames.Add(new SceneResult<ISceneFrame>.Success(new FixedSceneFrame
            {
                Update = new(baseline, stamp, stage == 1 ? new(-100, 0, 0) : NVector3.Zero, objects, [], new(fog, []), []),
                Origin = stage is 1 or 2 ? new(100, 0, 0) : default,
                Readiness = stage == 2 ? SceneReadiness.Partial : SceneReadiness.Ready,
                PickResult = new ScenePick.Hit(hit),
                Poses = ImmutableDictionary<WorldObjectId, Lookup<ScenePose>>.Empty.Add(Subject, new Lookup<ScenePose>.Found(pose)),
                TerrainHeightResult = new Lookup<double>.Found(-2)
            }));
        }
        return new() { Id = id, Binding = binding, PresentResults = frames.ToImmutable(), ResolveResults = hits.ToImmutable(),
            RequestResults = ImmutableDictionary<WorldSceneTarget, SceneResult<SceneRequestId>>.Empty.Add(Target, new SceneResult<SceneRequestId>.Success(new(1))) };
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

    private static HostCoordinator CreateHost(FixedContentView content, TrainLogger logger)
    {
        var session = new ScriptedCoreSession(new FixedCoreReadView(new(0, 0), HostCoreScenario.Input));
        var capture = new FixedCoreCapture(new(new(0, 0), "preview", "preview"), HostCoreScenario.References, "opaque", HostCoreScenario.Input, new(1));
        var core = new ScriptedCoreFactory { DecodeStateCall = _ => new CoreResult<ICoreCapture>.Success(capture),
            PrepareRestoreCall = (_, _, _) => new CoreResult<ICoreSessionCandidate>.Success(new ScriptedCoreCandidate(session, HostCoreScenario.References)) };
        var factory = new ScriptedContentFactory { DecodeCaptureCall = _ => new ContentResult<IContentCapture>.Success(new FixedContentCapture("opaque")),
            PrepareLoadCall = (_, _, _) => new ContentResult<IContentCandidate>.Success(new ScriptedContentCandidate(content)) };
        var host = new HostCoordinator(core, factory, new ScriptedContentFiles(), new UnconfiguredAssetInspector(),
            new(new("builtin"), new("mods"), null, "preview", []), logger);
        host.BeginRestore("opaque", "opaque", new(new("preview", "preview", "preview", "preview", "preview"), true, 1));
        return host;
    }

    public override void _ExitTree()
    {
        _world?.Dispose();
        _host?.Dispose();
        _logger?.Dispose();
    }
}
