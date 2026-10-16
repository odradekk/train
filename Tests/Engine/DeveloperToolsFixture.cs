using System.Collections.Immutable;
using Train.App;
using Train.App.Logging;
using Train.App.Recording;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using Train.Scenes.Contracts;
using Train.Scenes.ContractSupport;

namespace Train.Engine.Tests;

// 有限契约情景：普通会话运行至步2；重放先单步，再运行至2:0，最后导出。
internal sealed class DeveloperToolsFixture
{
    internal static readonly CoreSessionOptions Options = new(new("engine-preview", "fixture", "fixture", "fixture", "fixture"), true, 1);
    internal static readonly WorldSceneTarget Target = new WorldSceneTarget.Location(new WorldLocation.Node(new(1)));
    internal string ReplayPath { get; }
    internal string ExportPath { get; }
    internal ScriptedCoreSession InitialSession { get; }
    internal List<ScriptedCoreSession> RestoredSessions { get; } = [];
    internal ScriptedCoreReplay? Replay { get; private set; }
    internal List<ScriptedCoreCandidate> Candidates { get; } = [];
    internal FixedContentView Content { get; } = new(HostContentScenario.CreateProjection(HostCoreScenario.Input), new(1));
    internal FixedDisplayFactory Scenes { get; } = new()
    {
        OpenWorldCall = (binding, _) => new SceneResult<IWorldSceneSession>.Success(new PreviewScene(binding))
    };

    internal DeveloperToolsFixture(string directory)
    {
        Directory.CreateDirectory(directory);
        ReplayPath = Path.Combine(directory, "developer-fixture.zip");
        ExportPath = Path.Combine(directory, "replay-boundary.train");
        InitialSession = CreateSession(0);
        var history = new FixedContentReplayCapture([Content], "engine-fixture-content");
        var start = new RecordingStart(new(1), Capture(0));
        var window = new CoreRecordingWindow(new(2, 0), [start], [], [Capture(0).Checkpoint, Capture(2).Checkpoint], [], [], HostCoreScenario.References);
        var package = RecordingPackage.Capture(window, history, Options.Runtime);
        if (!File.Exists(ReplayPath)) package.Write(ReplayPath);
        package.Validate(ReplayPath);
    }

    internal HostCoordinator CreateHost(TrainLogger logger)
    {
        var core = new ScriptedCoreFactory
        {
            DecodeStateCall = payload => payload switch
            {
                "engine-state-0" => new CoreResult<ICoreCapture>.Success(Capture(0)),
                "engine-state-2" => new CoreResult<ICoreCapture>.Success(Capture(2)),
                _ => throw new InvalidOperationException("UNCONFIGURED: fixture state payload")
            },
            PrepareRestoreCall = (capture, _, _) =>
            {
                var session = Candidates.Count == 0 ? InitialSession : CreateSession(capture.Position.CompletedSteps);
                if (Candidates.Count > 0) RestoredSessions.Add(session);
                var candidate = new ScriptedCoreCandidate(session, HostCoreScenario.References);
                Candidates.Add(candidate);
                return new CoreResult<ICoreSessionCandidate>.Success(candidate);
            },
            PrepareReplayCall = (input, runtime, history) =>
            {
                if (input.InitialStatePayload != "engine-state-0" || input.EndPosition != new SimulationPosition(2, 0) ||
                    runtime != Options.Runtime || history.Length != 1 || history[0].Identity != HostCoreScenario.Input)
                    throw new InvalidOperationException("UNCONFIGURED: fixture replay input");
                var replay = new ScriptedCoreReplay(View(0));
                replay.QueueAdvance(1, null, new(ReplayStop.BudgetReached, new(1, 0), [], null, null), View(1));
                replay.QueueAdvance(1, new(2, 0), new(ReplayStop.RequestedPosition, new(2, 0), [], null, null), View(2));
                replay.SetCapture(Capture(2));
                Replay = replay;
                return new CoreResult<ICoreReplay>.Success(replay);
            },
            CompareCheckpointsCall = (left, right) => new CoreResult<CheckpointComparison>.Success(
                left == right ? new CheckpointComparison.Equal() :
                    new CheckpointComparison.Different(new(right.Position, "fixture-step", left.Position.ToString(), right.Position.ToString())))
        };
        var content = new ScriptedContentFactory
        {
            DecodeCaptureCall = payload => payload == "engine-fixture-save-content"
                ? new ContentResult<IContentCapture>.Success(new FixedContentCapture(payload))
                : throw new InvalidOperationException("UNCONFIGURED: fixture content payload"),
            PrepareLoadCall = (request, _, _) => request.Purpose is ContentLoadPurpose.Restore
                ? new ContentResult<IContentCandidate>.Success(new ScriptedContentCandidate(Content))
                : throw new InvalidOperationException("UNCONFIGURED: fixture load purpose"),
            CaptureCall = (_, _) => new ContentResult<IContentCapture>.Success(new FixedContentCapture("engine-fixture-save-content")),
            CaptureReplayCall = (history, _) => new ContentResult<IContentReplayCapture>.Success(new FixedContentReplayCapture(history, "engine-fixture-content")),
            DecodeReplayCall = payload => payload == "engine-fixture-content"
                ? new ContentResult<IContentReplayCapture>.Success(new FixedContentReplayCapture([Content], payload))
                : throw new InvalidOperationException("UNCONFIGURED: fixture replay content")
        };
        var host = new HostCoordinator(core, content, new ScriptedContentFiles(), new UnconfiguredAssetInspector(),
            new(new("builtin"), new("mods"), null, "engine-fixture", []), logger, sessionOptions: Options);
        // 只配置替身对捕获请求的固定答复；位置仍只由契约Advance推进。
        host.BoundaryCompleted += (_, view, _, _) =>
        {
            if (host.GetStatus() is { IsReplay: true }) Replay!.SetCapture(Capture(view.Position.CompletedSteps));
            else
            {
                var session = RestoredSessions.LastOrDefault() ?? InitialSession;
                session.SetCapture(CapturePurpose.Save, new CoreResult<ICoreCapture>.Success(Capture(view.Position.CompletedSteps)));
            }
        };
        host.BeginRestore("engine-state-0", "engine-fixture-save-content", Options);
        return host;
    }

    private static ScriptedCoreSession CreateSession(ulong from)
    {
        var session = new ScriptedCoreSession(View(from));
        for (var step = from + 1; step <= 2; step++)
            session.QueueAdvance(new(1, new(step, 0), [], null, null), View(step));
        session.SetCapture(CapturePurpose.Save, new CoreResult<ICoreCapture>.Success(Capture(from)));
        var start = new RecordingStart(new(1), Capture(from));
        session.ReadRecordingCall = () => new(session.CurrentView.Position, [start], [],
            session.CurrentView.Position == start.State.Position ? [start.State.Checkpoint] :
                [start.State.Checkpoint, Capture(session.CurrentView.Position.CompletedSteps).Checkpoint], [], [], HostCoreScenario.References);
        session.ConfirmRecordingStartCall = (_, _) => new([], []);
        return session;
    }

    private static FixedCoreCapture Capture(ulong step) => new(new(new(step, 0), "fixture-step-" + step, "fixture-hash-" + step),
        HostCoreScenario.References, "engine-state-" + step, HostCoreScenario.Input, new(step));

    private static FixedCoreReadView View(ulong step)
    {
        var node = new NodeView(new(new(1), new(0, 0), new(new("fixture:terrain"), 0, []), new NodeOccupant.Empty(), [], 0, 1, null), [], 80, 0, 80);
        var train = new TrainView(new(1), new TrainPosition.Node(new(1), null, null, NodeTravelPhase.Waiting, 80, null), TrainActivity.Stopped, [], new(1),
            0, 0, 0, 0, 0, 0, null, null, 0, new(TargetPriority.ArmedFirst, TargetScope.All, EncounterAction.Engage, 0),
            [], null, [], TrainOrderFacing.FirstEndLeading, null);
        return new(new(step, 0), HostCoreScenario.Input, revision: new(step))
        {
            World = new(1, new("fixture:railway"), 100, 100, [node.Map], [], [new(new(1), train.Position, train.Activity)], [], []),
            Nodes = new Dictionary<NodeId, Lookup<NodeView>> { [new(1)] = new Lookup<NodeView>.Found(node) },
            Trains = new Dictionary<TrainId, Lookup<TrainView>> { [new(1)] = new Lookup<TrainView>.Found(train) }
        };
    }

    private sealed class PreviewScene(SceneBinding binding) : IWorldSceneSession
    {
        public SceneSessionId Id { get; } = new(Guid.NewGuid());
        public SceneBinding Binding => binding;
        private ulong revision;
        private SceneRequestId request;
        public SceneResult<SceneRequestId> RequestView(WorldSceneTarget target) => target == Target
            ? new SceneResult<SceneRequestId>.Success(request = new(request.Value + 1))
            : throw new InvalidOperationException("UNCONFIGURED: fixture scene target");
        public SceneResult<SimulationPosition> Observe(SceneWorldSnapshot snapshot, ImmutableArray<EventEnvelope> events)
            => new SceneResult<SimulationPosition>.Success(snapshot.Core.Position);
        public SceneResult<ISceneFrame> Present(SceneFrameRequest options, ISceneFrame? displayedFrame)
        {
            var frame = new FixedSceneFrame
            {
                Update = new(null, new(Id, request, new(1), ++revision), default, [], [], new([], []), []),
                NodePanBounds = new(new(-10, -10), new(10, 10)),
                TerrainHeightResult = new Lookup<double>.Found(0),
                DebugLines = options.DebugLayers == SceneDebugLayer.None ? [] :
                    [new(new(-5, 1, 0), new(5, 1, 0), new(1, 0, 0, 1)), new(new(0, 1, -5), new(0, 1, 5), new(0, 1, 0, 1))]
            };
            return new SceneResult<ISceneFrame>.Success(frame);
        }
        public SceneResult<PickSubject> Resolve(SceneHit hit) => throw new InvalidOperationException("UNCONFIGURED: fixture picking");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
