using System.Collections.Immutable;
using Godot;
using Train.App.Assets;
using Train.App.Logging;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Rendering;
using Train.Scenes.Contracts;
using Train.Ui;

namespace Train.App;

internal sealed class WorldSceneController : IDisposable
{
    private readonly HostCoordinator _host;
    private readonly IScenesFactory _factory;
    private readonly TrainLogger _logger;
    private readonly GodotMainThreadDispatcher _dispatcher = new();
    private readonly AssetResourceCache _cache;
    private readonly List<Task> _closing = [];
    private readonly HashSet<SceneProblem> _reported = [];
    private IWorldSceneSession? _session;
    private SceneDisplay? _display;
    private HostBinding _hostBinding;
    private ContentGeneration _generation;
    private SceneRequestId _request;
    private WorldSceneTarget? _target;
    private bool _fullReplacement;
    private bool _disposed;

    internal WorldSceneView View { get; } = new();
    internal SceneDisplay? Display => _display;
    internal event Action<SceneProblem?>? ReturnedToMap;
    internal event Action<PickSubject>? Selected;

    internal WorldSceneController(HostCoordinator host, IScenesFactory factory, TrainLogger logger)
    {
        _host = host; _factory = factory; _logger = logger;
        _cache = new(_dispatcher);
        host.BoundaryCompleted += Observe;
        View.PickRequested += Pick;
        View.ReturnRequested += ReturnToMap;
        View.Visible = false;
        View.SetLoading(false);
    }

    internal void Open(WorldSceneTarget target)
    {
        _dispatcher.RequireMainThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_host.GetStatus() is not { } status || _host.Content is not { } content)
            throw new InvalidOperationException("打开世界场景需要活动会话。");
        CloseScene();
        _target = target;
        Start(status.Binding, new(status.View, content));
    }

    private void Start(HostBinding binding, SceneWorldSnapshot snapshot)
    {
        View.Visible = true;
        View.Interactive = false;
        View.SetLoading(true);
        _hostBinding = binding;
        _generation = snapshot.Content.Generation;
        try
        {
            var opened = _factory.OpenWorld(new SceneBinding(Guid.NewGuid()), snapshot);
            if (opened is not SceneResult<IWorldSceneSession>.Success success) { Fail(opened); return; }
            _session = success.Value;
            _display = new(_cache, snapshot.Content);
            View.AddChild(_display.Root);
            var requested = _session.RequestView(_target!);
            if (requested is SceneResult<SceneRequestId>.Success request) _request = request.Value;
            else Fail(requested);
        }
        catch (Exception exception) { AdapterFailed(exception, SceneStage.Input); }
    }

    private void Observe(HostBinding binding, ICoreReadView core, IContentView content,
        ImmutableArray<EventEnvelope> events)
    {
        if (_session is null) return;
        try
        {
            if (binding != _hostBinding || content.Generation != _generation)
            {
                CloseScene();
                Start(binding, new(core, content));
                return;
            }
            var observed = _session.Observe(new(core, content), events);
            if (observed is not SceneResult<SimulationPosition>.Success) Fail(observed);
        }
        catch (Exception exception) { AdapterFailed(exception, SceneStage.Reconcile); }
    }

    internal void ProcessFrame(double delta, bool minimized)
    {
        _dispatcher.RequireMainThread();
        _dispatcher.Pump();
        for (var i = _closing.Count - 1; i >= 0; i--)
            if (_closing[i].IsCompleted)
            {
                try { _closing[i].GetAwaiter().GetResult(); }
                catch (Exception exception) { LogAdapterFailure(exception, SceneStage.Close); }
                _closing.RemoveAt(i);
            }
        if (minimized || _session is null) return;
        try
        {
            var status = _host.GetStatus()!;
            var displayed = _fullReplacement ? null : _display!.DisplayedFrame;
            var camera = displayed is null ? null : new SceneCameraSample(displayed, ToNumerics(View.CameraRig.Focus));
            var interpolation = status.IsPaused ? 1 : status.Remainder / SimulationTime.StepSeconds;
            var result = _session.Present(new(Math.Clamp(interpolation, 0, 1), camera, SceneDebugLayer.None), displayed);
            if (result is SceneResult<ISceneFrame>.Failure { Problems: var problems } &&
                problems.Any(problem => problem.Code == "target_missing") && FollowReplacement()) return;
            if (result is not SceneResult<ISceneFrame>.Success success) { Fail(result); return; }
            var frame = success.Value;
            foreach (var diagnostic in frame.Diagnostics) Report(diagnostic);
            if (frame.Readiness == SceneReadiness.Failed) { Fail(frame.Diagnostics); return; }
            if (frame.Diagnostics.Any(problem => problem.Code == "target_missing"))
            {
                if (!FollowReplacement()) Fail(frame.Diagnostics);
                return;
            }
            if (frame.Update.Frame.Session != _session.Id || frame.Update.Frame.Request != _request)
                throw new InvalidOperationException("显示帧不属于活动场景请求。");
            if (frame.Update.Base is { } baseline && baseline != displayed?.Update.Frame)
            { _fullReplacement = true; return; }
            using var prepared = _display!.Prepare(frame);
            if (prepared is not null)
            {
                prepared.Commit();
                _fullReplacement = false;
                View.ShowFrame(frame, FollowSubject());
            }
            View.CameraRig.Update(delta);
        }
        catch (Exception exception) { AdapterFailed(exception, SceneStage.Present); }
    }

    private WorldObjectId? FollowSubject() => _target is WorldSceneTarget.Follow follow
        ? new WorldObjectId.Instance(new InstanceId.Vehicle(follow.Vehicle)) : null;

    private bool FollowReplacement()
    {
        if (_target is not WorldSceneTarget.Follow follow ||
            _host.GetStatus()!.View.FindTrain(follow.Train) is not Lookup<TrainView>.Found train) return false;
        var core = _host.GetStatus()!.View;
        var replacement = train.Value.Vehicles.OrderBy(vehicle => vehicle.Value).FirstOrDefault(vehicle =>
            core.FindInstance(new InstanceId.Vehicle(vehicle)) is Lookup<InstanceView>.Found instance &&
            instance.Value.Structure.EffectiveCurrent > 0);
        if (replacement == default) return false;
        var target = new WorldSceneTarget.Follow(follow.Train, replacement);
        var result = _session!.RequestView(target);
        if (result is not SceneResult<SceneRequestId>.Success success) { Fail(result); return true; }
        _target = target;
        _request = success.Value;
        _fullReplacement = true;
        View.Interactive = false;
        return true;
    }

    internal void Pick(Vector2 screenPoint)
    {
        _dispatcher.RequireMainThread();
        if (!View.Interactive || _session is null || _display?.DisplayedFrame is not { } frame) return;
        try
        {
            var camera = View.CameraRig.Camera;
            var inverse = _display.Root.GlobalTransform.AffineInverse();
            var origin = inverse * camera.ProjectRayOrigin(screenPoint);
            var direction = (inverse.Basis * camera.ProjectRayNormal(screenPoint)).Normalized();
            if (frame.Pick(new(ToNumerics(origin), ToNumerics(direction), camera.Far)) is not ScenePick.Hit hit) return;
            var result = _session.Resolve(hit.Value);
            if (result is SceneResult<PickSubject>.Success selected) Selected?.Invoke(selected.Value);
            else if (result is SceneResult<PickSubject>.Failure rejected)
                foreach (var problem in rejected.Problems) Report(problem);
            else Fail(result);
        }
        catch (Exception exception) { AdapterFailed(exception, SceneStage.Pick); }
    }

    internal void ReturnToMap()
    {
        _dispatcher.RequireMainThread();
        CloseScene();
        _target = null;
        ReturnedToMap?.Invoke(null);
    }

    private void Fail<T>(SceneResult<T> result) where T : notnull
    {
        if (result is SceneResult<T>.Failure failure) Fail(failure.Problems);
        else if (result is SceneResult<T>.Faulted faulted) Fail([faulted.Problem]);
    }
    private void Fail(ImmutableArray<SceneProblem> problems)
    {
        foreach (var problem in problems) Report(problem);
        var reason = problems.FirstOrDefault() ?? new SceneProblem("scene_failed", SceneStage.Present,
            null, null, null, null, "场景构建失败。");
        CloseScene();
        _target = null;
        ReturnedToMap?.Invoke(reason);
    }
    private void Report(SceneProblem problem)
    {
        if (!_reported.Add(problem)) return;
        _logger.Warning("scenes", "scene_problem", problem.Detail,
            new Dictionary<string, object?> { ["code"] = problem.Code, ["content"] = problem.Content?.ToString(),
                ["package"] = problem.Package?.Value, ["stage"] = problem.Stage.ToString(),
                ["subject"] = problem.Subject?.ToString() });
    }
    private void AdapterFailed(Exception exception, SceneStage stage)
    {
        LogAdapterFailure(exception, stage);
        Fail([new("scene_adapter_failed", stage, null, null, null, null, "场景显示失败。")]);
    }
    private void LogAdapterFailure(Exception exception, SceneStage stage) =>
        _logger.Error("scenes", "scene_adapter_failed", "场景接入或释放失败。",
            new Dictionary<string, object?> { ["stage"] = stage.ToString() }, exception);

    private void CloseScene()
    {
        View.Interactive = false;
        View.Visible = false;
        View.SetLoading(false);
        try { _display?.Dispose(); }
        catch (Exception exception) { LogAdapterFailure(exception, SceneStage.Close); }
        _display = null;
        if (_session is { } session)
        {
            _session = null;
            try { _closing.Add(session.DisposeAsync().AsTask()); }
            catch (Exception exception) { LogAdapterFailure(exception, SceneStage.Close); }
        }
        _reported.Clear();
        _fullReplacement = false;
    }

    public void Dispose()
    {
        _dispatcher.RequireMainThread();
        if (_disposed) return;
        _disposed = true;
        _host.BoundaryCompleted -= Observe;
        CloseScene();
        foreach (var task in _closing)
            _ = task.ContinueWith(completed =>
            {
                if (completed.Exception is { } exception) LogAdapterFailure(exception, SceneStage.Close);
            }, TaskScheduler.Default);
        _closing.Clear();
        View.Release();
        _cache.Dispose();
        _dispatcher.Dispose();
    }

    private static System.Numerics.Vector3 ToNumerics(Vector3 value) => new(value.X, value.Y, value.Z);
}
