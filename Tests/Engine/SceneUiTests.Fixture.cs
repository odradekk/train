using System.Collections.Immutable;
using Chickensoft.GoDotTest;
using Chickensoft.GodotTestDriver;
using Godot;
using Train.App;
using Train.App.Logging;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using Train.Scenes.Contracts;
using Train.Scenes.ContractSupport;
using Train.Ui;
using static Train.Engine.Tests.Assertions;
using NVector3 = System.Numerics.Vector3;

namespace Train.Engine.Tests;

public partial class SceneUiTests
{
    private async Task<Train.App.App> Ready(Fixture fixture, Setup setup)
    {
        _previousScale = TestScene.GetTree().Root.ContentScaleFactor;
        UiCapture.ConfigureWindowIfNeeded(TestScene.GetTree().Root);
        TestScene.GetTree().Root.ContentScaleFactor = 1;
        var app = fixture.LoadScene<Train.App.App>("res://App/App.tscn");
        app.CreateHost = setup.Create;
        app.CreateScenes = () => setup.Factory;
        await fixture.AddToRoot(app);
        await _input.WaitUntil(() => app.Host!.GetStatus() is not null && app.Saves!.Status is not null);
        await _input.Frames(2);
        return app;
    }

    private async Task Open(Train.App.App app, Setup setup, Vector2 point, WorldSceneTarget target)
    {
        await _input.Click(point);
        Require(!app.WorldScenes!.View.Visible, "单击地图不能进入场景。");
        await _input.DoubleClick(point);
        await _input.WaitUntil(() => app.WorldScenes!.View.Visible && app.WorldScenes.View.Interactive);
        Require(setup.Scenes[^1].RequestCalls.SequenceEqual(new[] { target }), "地图点击未请求对应世界场景目标。");
    }

    private async Task Cleanup(Fixture fixture, Train.App.App? app)
    {
        var identity = app?.Saves?.Status?.Identity.Id;
        if (app?.Saves is { } saves) { saves.Cancel(); await _input.WaitUntil(() => !saves.HasPendingWork); }
        await fixture.Cleanup();
        TestScene.GetTree().Root.ContentScaleFactor = _previousScale;
        if (identity is { } id)
        {
            UiTestInput.DeleteSaves([id]);
        }
    }

    private sealed class Setup
    {
        internal ScriptedCoreSession Session = null!;
        internal readonly List<FixedDisplaySession> Scenes = [];
        internal readonly List<FixedSceneFrame> Frames = [];
        internal bool RejectNext;
        internal FixedDisplayFactory Factory { get; }
        private readonly float _buildingTop;
        private readonly bool _inCombat, _emptyTrain;
        private readonly double _nodeX;

        internal Setup(bool minimap = false, float buildingTop = 0, bool inCombat = false, bool emptyTrain = false, double nodeX = 250)
        {
            _buildingTop = buildingTop;
            _inCombat = inCombat; _emptyTrain = emptyTrain;
            _nodeX = nodeX;
            Factory = new() { OpenWorldCall = (binding, _) =>
            {
                if (RejectNext) return new SceneResult<IWorldSceneSession>.Failure([new("fixture_node_failed", SceneStage.Build,
                    new WorldObjectId.Node(new(1)), null, null, null, "固定节点构建失败。")]);
                var stamp = new SceneFrameStamp(SceneId, new(1), new(1), 1);
                var subject = new PickSubject.WorldObject(new WorldObjectId.Instance(Vehicle), null, null);
                var hit = new SceneHit(stamp, new(1), subject, new(0, 0, 0), GridFace.PositiveY,
                    NVector3.Zero, NVector3.UnitY, 1, []);
                var frame = new FixedSceneFrame { Update = new(null, stamp, NVector3.Zero, [Geometry()], [], new([], []), []),
                    NodePanBounds = minimap ? new(new(-120, -60), new(240, 120)) : null,
                    NodeOverview = minimap ? new([], [[new(-120, 0), new(240, 0)]], []) : null,
                    Origin = minimap ? new(1000, 0, -2000) : default,
                    PickResult = new ScenePick.Hit(hit), TerrainHeightResult = new Lookup<double>.Found(0),
                    Poses = ImmutableDictionary<WorldObjectId, Lookup<ScenePose>>.Empty.Add(new WorldObjectId.Instance(Vehicle),
                        new Lookup<ScenePose>.Found(new(NVector3.Zero, System.Numerics.Quaternion.Identity))) };
                Frames.Add(frame);
                var session = new FixedDisplaySession { Id = SceneId, Binding = binding,
                    RequestResults = new[] { NodeTarget, EdgeTarget, TrainTarget }.ToImmutableDictionary(target => target,
                        _ => (SceneResult<SceneRequestId>)new SceneResult<SceneRequestId>.Success(new(1))),
                    PresentResults = Enumerable.Repeat<SceneResult<ISceneFrame>>(new SceneResult<ISceneFrame>.Success(frame), 180).ToImmutableArray(),
                    ResolveResults = ImmutableDictionary<SceneHit, SceneResult<PickSubject>>.Empty.Add(hit, new SceneResult<PickSubject>.Success(subject)) };
                Scenes.Add(session);
                return new SceneResult<IWorldSceneSession>.Success(session);
            } };
        }

        internal HostCoordinator Create(TrainLogger logger)
        {
            var capture = new FixedCoreCapture(new(new(0, 0), "scene ui", "scene ui"), HostCoreScenario.References,
                "scene-ui", HostCoreScenario.Input, new(0));
            ScriptedCoreCandidate Candidate()
            {
                Session = UiWorldFixture.Session(UiWorldFixture.Scene(_inCombat, _emptyTrain, _nodeX));
                Session.SetCapture(CapturePurpose.Save, new CoreResult<ICoreCapture>.Success(capture));
                return new(Session, HostCoreScenario.References);
            }
            var core = new ScriptedCoreFactory { DecodeStateCall = _ => new CoreResult<ICoreCapture>.Success(capture),
                PrepareRestoreCall = (_, _, _) => new CoreResult<ICoreSessionCandidate>.Success(Candidate()),
                PrepareNewCall = (_, _, _) => new CoreResult<ICoreSessionCandidate>.Success(Candidate()) };
            var contentView = new FixedContentView(HostContentScenario.CreateProjection(HostCoreScenario.Input), new(1))
            {
                GenerationProfiles = [new(new("fixture:world"), new("固定世界", ""))],
                DefinitionDisplays = new Dictionary<CoreDefinitionReference, ContentLookup<ContentDisplay>>
                { [new CoreDefinitionReference.Terrain(new("fixture:plain"))] = new ContentLookup<ContentDisplay>.NotFound() },
                // 点击列车时按车长找参考中心车辆；单车列车查不到设计时仍跟随这辆车。
                Designs = new Dictionary<DesignHash, ContentLookup<DesignEvaluation>> { [new("fixture-design")] = new ContentLookup<DesignEvaluation>.NotFound() }
            };
            var content = new ScriptedContentFactory { CaptureCall = (view, _) => new ContentResult<IContentCapture>.Success(new FixedContentCapture("scene-ui", view.PersistentRevision)),
                CaptureReplayCall = (history, _) => new ContentResult<IContentReplayCapture>.Success(new FixedContentReplayCapture(history, "ui recording content")),
                DecodeCaptureCall = _ => new ContentResult<IContentCapture>.Success(new FixedContentCapture("scene-ui")),
                PrepareLoadCall = (_, _, _) => new ContentResult<IContentCandidate>.Success(new ScriptedContentCandidate(contentView)),
                CreateWorldInputCall = (_, _, _, _) => new ContentResult<WorldGenerationInput>.Success(HostCoreScenario.World) };
            var options = new CoreSessionOptions(new("fixture", "fixture", "fixture", "fixture", "fixture"), true, 1);
            var host = new HostCoordinator(core, content, new ScriptedContentFiles(), new UnconfiguredAssetInspector(),
                new(new("builtin"), new("mods"), null, "scene-ui", []), logger, sessionOptions: options);
            host.BeginRestore("scene-ui", "scene-ui", options);
            return host;
        }

        private SceneObject Geometry()
        {
            var mesh = new SceneMesh(new SceneMaterial.BuiltIn(BuiltInMaterial.Sleeper),
                [new(new(-20, _buildingTop, -20), NVector3.UnitY, new(0, 0), new(0, 0), ExposedFaceEdges.None, 0, 0),
                 new(new(-20, _buildingTop, 20), NVector3.UnitY, new(0, 1), new(0, 1), ExposedFaceEdges.None, 0, 0),
                 new(new(20, _buildingTop, 20), NVector3.UnitY, new(1, 1), new(1, 1), ExposedFaceEdges.None, 0, 0),
                 new(new(20, _buildingTop, -20), NVector3.UnitY, new(1, 0), new(1, 0), ExposedFaceEdges.None, 0, 0)], [0, 1, 2, 0, 2, 3]);
            return new(new(1), null, new(NVector3.Zero, System.Numerics.Quaternion.Identity),
                new WorldObjectId.Instance(Vehicle), [new SceneVisual.Geometry(mesh, new(false, 0, 0, false))], [], []);
        }

    }
}
