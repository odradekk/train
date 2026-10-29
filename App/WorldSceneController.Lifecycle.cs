using Train.App.Logging;
using Train.Content.Contracts;
using Train.Scenes.Contracts;

namespace Train.App;

internal sealed partial class WorldSceneController
{
    private readonly Func<IScenesFactory> _createFactory;
    private IScenesFactory? _factory;
    private HostBinding _hostBinding;
    private ContentGeneration _generation;
    private SceneBinding _sceneBinding;

    internal WorldSceneController(HostCoordinator host, Func<IScenesFactory> createFactory, TrainLogger logger)
    {
        _host = host; _createFactory = createFactory; _logger = logger;
        _cache = new(_dispatcher);
        host.BoundaryCompleted += Observe;
        View.CameraRig.ObjectTop = id => _display?.ObjectTopY(id);
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
        if (binding != _hostBinding) _factory = null;
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
}
