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
    private SceneBinding _sceneBinding;
    private SceneRequestId _request;
    private WorldSceneTarget? _target;
    private ScenePickRaySample? _pickRay;
    private bool _fullReplacement;
    private bool _awaitingRequest;
    private bool _disposed;

    internal WorldSceneView View { get; } = new();
    internal SceneDisplay? Display => _display;
    internal SceneDebugLayer DebugLayers { get; set; }
    internal event Action<SceneProblem?>? ReturnedToMap;
    internal event Action<PickSubject>? Selected;

    internal WorldSceneController(HostCoordinator host, IScenesFactory factory, TrainLogger logger)
    {
        _host = host; _factory = factory; _logger = logger;
        _cache = new(_dispatcher);
        host.BoundaryCompleted += Observe;
        View.PickRequested += Pick;
        View.ReturnRequested += ReturnToMap;
        View.SetActive(false);
        View.SetLoading(false);
    }

    internal void Open(WorldSceneTarget target)
    {
        _dispatcher.RequireMainThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_host.GetStatus() is not { } status || _host.Content is not { } content)
            throw new InvalidOperationException("打开世界场景需要活动会话。");
        if (_session is not null && status.Binding == _hostBinding && content.Generation == _generation)
        {
            try { RequestTarget(target); }
            catch (Exception exception) { AdapterFailed(exception, SceneStage.Input); }
            return;
        }
        CloseScene();
        UpdateBinding(status.Binding, content.Generation);
        Start(new(status.View, content), target);
    }

    private void UpdateBinding(HostBinding binding, ContentGeneration generation)
    {
        if (binding == _hostBinding && generation == _generation) return;
        _hostBinding = binding;
        _generation = generation;
        _sceneBinding = new(Guid.NewGuid());
    }

    internal SceneProblem? Regenerate()
    {
        _dispatcher.RequireMainThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_host.IsBusy)
            return new("busy", SceneStage.Input, null, null, null, null, "宿主正在处理操作，请稍后重试。");
        if (_session is null || _target is not { } target ||
            _host.GetStatus() is not { } status || _host.Content is not { } content)
            return new("no_scene", SceneStage.Input, null, null, null, null, "没有可重新生成的世界场景。");
        CloseScene();
        UpdateBinding(status.Binding, content.Generation);
        Start(new(status.View, content), target);
        return _session is not null ? null :
            new("scene_failed", SceneStage.Input, null, null, null, null, "重新生成场景失败。");
    }

    private void Start(SceneWorldSnapshot snapshot, WorldSceneTarget target)
    {
        View.SetActive(true);
        View.Interactive = false;
        View.SetLoading(true);
        try
        {
            var opened = _factory.OpenWorld(_sceneBinding, snapshot);
            if (opened is not SceneResult<IWorldSceneSession>.Success success) { Fail(opened); return; }
            _session = success.Value;
            _display = new(_cache, snapshot.Content);
            View.AddChild(_display.Root);
            var requested = RequestTarget(target);
            if (requested is SceneResult<SceneRequestId>.Failure) Fail(requested);
        }
        catch (Exception exception) { AdapterFailed(exception, SceneStage.Input); }
    }

    private void Observe(HostBinding binding, ICoreReadView core, IContentView content,
        ImmutableArray<EventEnvelope> events)
    {
        try
        {
            if (binding != _hostBinding)
            {
                UpdateBinding(binding, content.Generation);
                ReturnToMap();
                return;
            }
            if (content.Generation != _generation)
            {
                UpdateBinding(binding, content.Generation);
                if (_session is not null)
                {
                    var target = _target!;
                    CloseScene();
                    Start(new(core, content), target);
                }
                return;
            }
            if (_session is null) return;
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
        if (_session is null) return;
        try
        {
            var status = _host.GetStatus();
            if (status is null)
            {
                CloseScene();
                _target = null;
                return;
            }
            if (minimized) return;
            var actual = _display!.DisplayedFrame;
            var displayed = _fullReplacement ? null : actual;
            var poseFrame = _fullReplacement ? actual : _display.PoseFrame;
            var camera = poseFrame is null ? null : new SceneCameraSample(poseFrame,
                ToNumerics(View.CameraRig.Focus - (_fullReplacement ? _display.Root.Position : Vector3.Zero)));
            var interpolation = status.Remainder / SimulationTime.StepSeconds;
            var result = _session.Present(new(Math.Clamp(interpolation, 0, 1), camera, DebugLayers, _pickRay), displayed);
            if (result is SceneResult<ISceneFrame>.Failure { Problems: var problems } &&
                problems.Length > 0 && problems.All(problem => problem.Code == "target_missing"))
            {
                foreach (var problem in problems) Report(problem);
                if (FollowReplacement()) return;
            }
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
            if (_awaitingRequest)
            {
                _awaitingRequest = false;
                if (frame.Readiness == SceneReadiness.Loading) View.SetLoading(_display!.DisplayedFrame is null);
            }
            if (frame.Update.Base is { } baseline && baseline != displayed?.Update.Frame)
            { _fullReplacement = true; return; }
            var shift = _fullReplacement ? (Vector3?)null : _display.UpdatePoses(frame);
            if (_pickRay is { } sample && sample.Frame.Update.Frame.Space != frame.Update.Frame.Space) _pickRay = null;
            using var prepared = _display!.Prepare(frame);
            if (prepared is not null)
            {
                prepared.Commit();
                // 仅较早候选需要恢复P姿态；相机仍使用提交前算出的整帧平移。
                if (!ReferenceEquals(prepared.Frame, frame)) _display.UpdatePoses(frame);
                _fullReplacement = false;
                View.SetLoading(false);
                View.Interactive = true;
            }
            if (!_fullReplacement && _display.DisplayedFrame is not null)
            {
                View.CameraRig.DisplayedFrame(frame, FollowSubject(),
                    _target is WorldSceneTarget.Location { Value: WorldLocation.Edge }, shift);
                View.CameraRig.Update(delta);
            }
        }
        catch (Exception exception) { AdapterFailed(exception, SceneStage.Present); }
    }

    private WorldObjectId? FollowSubject() => _target is WorldSceneTarget.Follow follow
        ? new WorldObjectId.Instance(new InstanceId.Vehicle(follow.Vehicle)) : null;

    private SceneResult<SceneRequestId> RequestTarget(WorldSceneTarget target)
    {
        var result = _session!.RequestView(target);
        if (result is SceneResult<SceneRequestId>.Success success)
        {
            _display!.CancelPreparation();
            _target = target;
            _request = success.Value;
            _pickRay = null;
            _fullReplacement = true;
            _awaitingRequest = true;
            View.Interactive = false;
            View.SetLoading(_display!.DisplayedFrame is null);
        }
        else if (result is SceneResult<SceneRequestId>.Failure failure)
            foreach (var problem in failure.Problems) Report(problem);
        else Fail(result);
        return result;
    }

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
        var result = RequestTarget(target);
        return result is SceneResult<SceneRequestId>.Success || _session is null;
    }

    internal void Pick(Vector2 screenPoint)
    {
        _dispatcher.RequireMainThread();
        var frame = _fullReplacement ? _display?.DisplayedFrame : _display?.PoseFrame;
        if (!View.Interactive || _session is null || frame is null) return;
        try
        {
            var camera = View.CameraRig.Camera;
            var inverse = (_fullReplacement ? _display!.Root.GlobalTransform : View.GlobalTransform).AffineInverse();
            var origin = inverse * camera.ProjectRayOrigin(screenPoint);
            var direction = (inverse.Basis * camera.ProjectRayNormal(screenPoint)).Normalized();
            var ray = new SceneRay(ToNumerics(origin), ToNumerics(direction), camera.Far);
            var picked = frame.Pick(ray);
            _pickRay = new(frame, ray);
            if (picked is not ScenePick.Hit hit || _fullReplacement || !_display!.CanSelect(hit.Value)) return;
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
        _pickRay = null;
        View.Interactive = false;
        View.SetActive(false);
        View.CameraRig.ReleaseFrame();
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
        _awaitingRequest = false;
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
