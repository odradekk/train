using Godot;
using Train.App.Assets;
using Train.App.Editing;
using Train.Rendering;
using Train.Scenes.Contracts;
using Train.Core.Contracts;
using Train.Ui;

namespace Train.App.Authoring;

internal sealed class TemplateSceneController : IDisposable
{
    private readonly AuthoringService _service;
    private readonly EditorService _editor;
    private readonly GodotMainThreadDispatcher _dispatcher = new();
    private readonly AssetResourceCache _cache;
    private SceneDisplay? _display;
    private ITemplatePreview? _preview;
    private bool _failed;
    internal WorldSceneView View { get; } = new() { Name = "TemplateSceneView" };
    internal event Action<int>? PlotSelected;
    internal TemplateSceneController(AuthoringService service, EditorService editor)
    {
        _service = service; _editor = editor; _cache = new(_dispatcher);
        View.SetActive(false); View.SetLoading(false); View.PickRequested += Pick;
        service.Changed += Changed;
    }
    private void Changed()
    {
        if (!ReferenceEquals(_preview, _service.TemplatePreview))
        {
            View.Interactive = false;
            _display?.CancelPreparation();
            if (_display is not null) _display.Root.Visible = false;
        }
    }
    internal void Process(double delta)
    {
        if (_service.TemplatePreview is not { } preview || _editor.IsOpen || _editor.Host.Content is not { } content || content.Generation != preview.Content)
        { Clear(); return; }
        if (!ReferenceEquals(_preview, preview))
        {
            Clear(); _preview = preview;
            _display = new(_cache, content); View.AddChild(_display.Root);
        }
        if (_failed) return;
        View.SetActive(true);
        try
        {
            if (preview.Frame.Readiness == SceneReadiness.Failed) throw new InvalidOperationException("模板预览场景准备失败。");
            if (!ReferenceEquals(_display!.DisplayedFrame, preview.Frame))
            {
                using var prepared = _display.Prepare(preview.Frame);
                if (prepared is not null) { prepared.Commit(); View.ShowFrame(prepared.Frame, null); }
            }
            View.Interactive = !_display.IsPreparing && ReferenceEquals(_display.DisplayedFrame, preview.Frame);
            View.CameraRig.Update(delta);
        }
        catch (Exception error)
        {
            _failed = true; View.Interactive = false;
            _display!.Root.Visible = false;
            _service.Report("模板预览显示失败：" + error.Message);
        }
    }
    private void Pick(Vector2 position)
    {
        if (!View.Interactive || _display?.IsPreparing == true || _preview is not { } preview ||
            !ReferenceEquals(preview, _service.TemplatePreview) || !ReferenceEquals(_display?.DisplayedFrame, preview.Frame)) return;
        try
        {
            var camera = View.CameraRig.Camera;
            var origin = camera.ProjectRayOrigin(position); var direction = camera.ProjectRayNormal(position);
            var result = _service.PickPlot(new(new(origin.X, origin.Y, origin.Z), new(direction.X, direction.Y, direction.Z), 4000));
            if (result is Lookup<int>.Found found) PlotSelected?.Invoke(found.Value);
        }
        catch (Exception error) { _service.Report("地块拾取失败：" + error.Message); }
    }
    private void Clear()
    {
        _display?.Dispose(); _display = null; _preview = null; _failed = false;
        View.Interactive = false; View.SetActive(false); View.SetLoading(false);
    }
    public void Dispose()
    {
        _service.Changed -= Changed; View.PickRequested -= Pick;
        Clear(); _cache.Dispose(); _dispatcher.Dispose();
    }
}
