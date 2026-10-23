using Godot;
using Train.App.Assets;
using Train.Content.Contracts;
using Train.Rendering;
using Train.Scenes.Contracts;
using Train.Ui;

namespace Train.App.Editing;

internal sealed class EditorSceneController : IDisposable
{
    private readonly EditorService _service;
    private readonly GodotMainThreadDispatcher _dispatcher = new();
    private readonly AssetResourceCache _cache;
    private SceneDisplay? _display;
    private IDesignEditSession? _session;
    private EditStamp? _stamp;
    private EditStamp? _displayStamp;
    private EditElementId? _entered;
    private System.Collections.Immutable.ImmutableArray<EditElementId> _selection = [];
    private DesignEvaluation? _evaluation;
    private readonly EditorOverlay _overlay = new();
    private bool _retrying;
    private bool _openingFramed;
    private const float OpeningYaw = 0.6f;
    private const float OpeningPitch = 0.55f;
    internal WorldSceneView View { get; } = new();
    internal Rect2 VisibleArea { get; set; }
    internal SceneDisplay? Display => _display;
    internal EditStamp? DisplayedStamp => _displayStamp;
    internal EditViewOptions Options { get; set; } = new(new(GridAxis.Y, 0), null, SceneDebugLayer.None);
    internal event Action<ScenePick>? Picked;
    internal EditorSceneController(EditorService service)
    {
        _service = service;
        _cache = new(_dispatcher);
        service.Changed += OnChanged;
        View.SetActive(false);
        View.SetLoading(false);
        _overlay.Attach(View);
        View.PickRequested += position =>
        {
            if (Pick(position) is { } result) Picked?.Invoke(result);
        };
    }
    private void OnChanged()
    {
        var state = _service.State;
        if (state?.Snapshot.Stamp == _stamp && state?.EnteredObject == _entered &&
            (state?.Selection ?? []).SequenceEqual(_selection) && ReferenceEquals(state?.CurrentEvaluation, _evaluation)) return;
        _display?.CancelPreparation();
        _display?.ClearDebugLines();
        View.Interactive = false;
        ShowPreview(null);
    }
    internal void Process(double delta, bool retry = false)
    {
        if (_service.Session is not { } session || _service.FixedContent is not { } content)
        {
            Clear();
            return;
        }
        if (_session != session)
        {
            Clear();
            _session = session;
            _display = new(_cache, content);
            View.AddChild(_display.Root);
        }
        View.SetActive(true);
        if (retry)
        {
            _retrying = true;
            _display!.CancelPreparation();
        }
        if (_service.SceneUnavailable && !_retrying)
        {
            HideScene();
            return;
        }
        try
        {
            var state = _service.State;
            var stamp = state?.Snapshot.Stamp;
            _entered = state?.EnteredObject;
            _selection = state?.Selection ?? [];
            _evaluation = state?.CurrentEvaluation;
            if (_stamp != stamp)
            {
                _display!.CancelPreparation();
                _display.ClearDebugLines();
                ShowPreview(null);
                _stamp = stamp;
            }
            var result = session.Present(Options, _retrying ? null : _display!.DisplayedFrame);
            if (result is SceneResult<ISceneFrame>.Success success)
            {
                if (success.Value.Readiness == SceneReadiness.Failed)
                {
                    Fail(success.Value.Diagnostics);
                    return;
                }
                using var prepared = _display!.Prepare(success.Value);
                if (prepared is not null)
                {
                    prepared.Commit();
                    _displayStamp = _stamp;
                    View.ShowFrame(prepared.Frame, null);
                    FrameOpening(prepared.Frame);
                    _overlay.DrawPlane(Options.WorkPlane, prepared.Frame.Origin, EnteredTransform());
                    if (_retrying && prepared.Frame.Update.Base is null)
                    {
                        _retrying = false;
                        _service.SceneRecovered();
                    }
                }
                _service.SetFrameDiagnostics(success.Value.Diagnostics);
                if (!_retrying) View.CameraRig.Update(delta);
            }
            else if (result is SceneResult<ISceneFrame>.Failure failure) Fail(failure.Problems);
            else if (result is SceneResult<ISceneFrame>.Faulted fault) Fail([fault.Problem]);
        }
        catch (Exception exception)
        {
            Fail([new("editor_display_failed", SceneStage.Present, null, null, null, null, exception.Message)]);
        }
    }
    private void HideScene()
    {
        if (_display is not null) _display.Root.Visible = false;
        _overlay.Visible = false;
        View.Interactive = false;
    }
    private void Fail(IEnumerable<SceneProblem> problems)
    {
        _retrying = false;
        _service.SetFrameDiagnostics([]);
        _display?.CancelPreparation();
        HideScene();
        _service.SceneFailures(problems);
    }
    internal ScenePick? Pick(Vector2 position)
    {
        try { return PickCore(position); }
        catch (Exception error)
        {
            Fail([new("editor_pick_failed", SceneStage.Pick, null, null, null, null, error.Message)]);
            return null;
        }
    }
    private ScenePick? PickCore(Vector2 position)
    {
        if (_retrying || _display?.IsPreparing == true) return new ScenePick.NotReady();
        if (_service.SceneUnavailable || _service.Session != _session || _service.State?.Snapshot.Stamp != _displayStamp ||
            _display?.DisplayedFrame is not { } frame || !_display.Root.Visible) return null;
        var camera = View.CameraRig.Camera;
        var origin = camera.ProjectRayOrigin(position);
        var direction = camera.ProjectRayNormal(position);
        var result = frame.Pick(new(new(origin.X, origin.Y, origin.Z), new(direction.X, direction.Y, direction.Z), 4000));
        if (result is not ScenePick.Hit hit) return result;
        if (hit.Value.Frame != frame.Update.Frame || hit.Value.Subject is not PickSubject.Editing subject ||
            subject.Session != _session!.Id || subject.Revision != _stamp!.Revision)
        {
            _service.Report("拾取结果已过期，请重新选择。");
            return null;
        }
        return result;
    }
    internal void ShowPreview(IEditPreview? preview)
    {
        _overlay.ShowPreview(preview, _display?.DisplayedFrame?.Origin, EnteredTransform());
    }
    internal GridPosition? PickPlane(Vector2 position)
    {
        if (_retrying || _display?.IsPreparing == true) return null;
        if (_service.SceneUnavailable || _service.Session != _session ||
            _service.State?.Snapshot.Stamp != _displayStamp ||
            _display?.DisplayedFrame is not { } frame || !_display.Root.Visible) return null;
        var camera = View.CameraRig.Camera;
        var inverse = EnteredTransform().AffineInverse();
        var origin = inverse * (camera.ProjectRayOrigin(position) + new Vector3((float)frame.Origin.X, (float)frame.Origin.Y, (float)frame.Origin.Z));
        var direction = inverse.Basis * camera.ProjectRayNormal(position);
        var plane = Options.WorkPlane;
        var start = plane.Axis == GridAxis.X ? origin.X : plane.Axis == GridAxis.Y ? origin.Y : origin.Z;
        var component = plane.Axis == GridAxis.X ? direction.X : plane.Axis == GridAxis.Y ? direction.Y : direction.Z;
        if (Math.Abs(component) < 0.00001) return null;
        var distance = (plane.Coordinate - start) / component;
        if (distance < 0 || distance > 4000) return null;
        var point = origin + direction * (float)distance;
        return new(plane.Axis == GridAxis.X ? plane.Coordinate : (int)Math.Floor(point.X),
            plane.Axis == GridAxis.Y ? plane.Coordinate : (int)Math.Floor(point.Y),
            plane.Axis == GridAxis.Z ? plane.Coordinate : (int)Math.Floor(point.Z));
    }
    internal GridPosition CellFor(SceneHit hit, bool adding)
    {
        var cell = new Vector3(hit.Cell.X + 0.5f, hit.Cell.Y + 0.5f, hit.Cell.Z + 0.5f);
        if (adding) cell += FaceNormal(hit.Face);
        var local = EnteredTransform().AffineInverse() * (TransformFor(hit.NestedObjectPath) * cell);
        return new((int)MathF.Floor(local.X), (int)MathF.Floor(local.Y), (int)MathF.Floor(local.Z));
    }
    internal GridAxis FaceAxis(SceneHit hit)
    {
        var normal = EnteredTransform().Basis.Inverse() * (TransformFor(hit.NestedObjectPath).Basis * FaceNormal(hit.Face));
        return Math.Abs(normal.X) > 0.5 ? GridAxis.X : Math.Abs(normal.Y) > 0.5 ? GridAxis.Y : GridAxis.Z;
    }
    private static Vector3 FaceNormal(GridFace face) => face switch
    {
        GridFace.NegativeX => Vector3.Left, GridFace.PositiveX => Vector3.Right,
        GridFace.NegativeY => Vector3.Down, GridFace.PositiveY => Vector3.Up,
        GridFace.NegativeZ => new(0, 0, -1), _ => new(0, 0, 1)
    };
    internal DesignDraft? EnteredDraft => DraftAt(EnteredPath());
    private System.Collections.Immutable.ImmutableArray<int> EnteredPath() => _service.State is { EnteredObject: { } entered } state
        ? state.Elements.First(item => item.Id == entered).ObjectPath : [];
    private Transform3D EnteredTransform() => TransformFor(EnteredPath());
    private DesignDraft? DraftAt(System.Collections.Immutable.ImmutableArray<int> path)
    {
        var draft = _service.State?.Snapshot.Draft;
        foreach (var index in path)
            draft = draft?.Layout.NestedItems[index] switch { NestedDesignItem.AssemblyCopy copy => copy.Design, NestedDesignItem.SceneModelCopy copy => copy.Design, _ => null };
        return draft;
    }
    private Transform3D TransformFor(System.Collections.Immutable.ImmutableArray<int> path)
    {
        var draft = _service.State?.Snapshot.Draft;
        var transform = Transform3D.Identity;
        foreach (var index in path)
        {
            if (draft is null) break;
            var item = draft.Layout.NestedItems[index];
            var (at, orientation) = item switch
            {
                NestedDesignItem.AssemblyCopy copy => (copy.At, copy.Orientation),
                NestedDesignItem.SceneModelCopy copy => (copy.At, copy.Orientation),
                NestedDesignItem.FunctionalCompositeReference reference => (reference.At, reference.Orientation),
                _ => throw new InvalidOperationException("未知嵌套对象。")
            };
            var name = orientation.ToString();
            var right = Axis(name[..2]); var up = Axis(name[2..]);
            var basis = new Basis(right, up, right.Cross(up));
            var center = Vector3.One * 0.5f;
            transform *= new Transform3D(basis, new Vector3(at.X, at.Y, at.Z) + center - basis * center);
            draft = item switch { NestedDesignItem.AssemblyCopy copy => copy.Design, NestedDesignItem.SceneModelCopy copy => copy.Design, _ => null };
        }
        return transform;
    }
    private static Vector3 Axis(string axis) => axis switch
    {
        "Px" => Vector3.Right, "Nx" => Vector3.Left, "Py" => Vector3.Up, "Ny" => Vector3.Down,
        "Pz" => new(0, 0, 1), "Nz" => new(0, 0, -1), _ => throw new InvalidOperationException("无效正交朝向。")
    };
    private void Clear()
    {
        _display?.Dispose();
        _display = null;
        _session = null;
        _stamp = null;
        _displayStamp = null;
        _evaluation = null;
        _retrying = false;
        _overlay.Visible = false;
        _openingFramed = false;
        View.CameraRig.ZoomCeiling = WorldCamera.MaximumDistance;
        View.SetActive(false);
        View.SetLoading(false);
    }
    private void FrameOpening(ISceneFrame frame)
    {
        if (_openingFramed) return;
        if (OccupiedBounds() is not { } bounds)
        {
            _openingFramed = true;
            return;
        }
        var area = VisibleArea;
        if (area.Size.X < 1f || area.Size.Y < 1f) return;
        _openingFramed = true;
        var minimum = DisplayMinimum(bounds, frame);
        var maximum = minimum + new Vector3(bounds.Maximum.X - bounds.Minimum.X + 1,
            bounds.Maximum.Y - bounds.Minimum.Y + 1, bounds.Maximum.Z - bounds.Minimum.Z + 1);
        var focus = (minimum + maximum) * 0.5f;
        var fitted = FitDistance(focus, minimum, maximum, area);
        var camera = View.CameraRig;
        // 比取景距离多一个滚轮步长，第一下向外才是 exp(0.15)，而不是停在取景距离上。
        var ceiling = Mathf.Min(camera.Camera.Far - 1f,
            Mathf.Max(WorldCamera.MaximumDistance, fitted * Mathf.Exp(WorldCamera.ZoomStep)));
        camera.ZoomCeiling = ceiling;
        camera.PlaceAbsolute(focus, fitted, OpeningYaw, OpeningPitch);
    }
    private float FitDistance(Vector3 focus, Vector3 minimum, Vector3 maximum, Rect2 area)
    {
        var low = 8f;
        var high = View.CameraRig.Camera.Far - 1f;
        if (!ProjectsInside(focus, minimum, maximum, high, area)) return high;
        for (var step = 0; step < 24; step++)
        {
            var mid = (low + high) * 0.5f;
            if (ProjectsInside(focus, minimum, maximum, mid, area)) high = mid;
            else low = mid;
        }
        return high;
    }
    private bool ProjectsInside(Vector3 focus, Vector3 minimum, Vector3 maximum, float distance, Rect2 area)
    {
        var camera = View.CameraRig;
        camera.PlaceAbsolute(focus, distance, OpeningYaw, OpeningPitch);
        var view = camera.Camera;
        for (var corner = 0; corner < 8; corner++)
        {
            var point = new Vector3((corner & 1) == 0 ? minimum.X : maximum.X,
                (corner & 2) == 0 ? minimum.Y : maximum.Y, (corner & 4) == 0 ? minimum.Z : maximum.Z);
            if (view.IsPositionBehind(point)) return false;
            if (!area.HasPoint(view.UnprojectPosition(point))) return false;
        }
        return true;
    }
    private GridBounds? OccupiedBounds() => _evaluation switch
    {
        DesignEvaluation.Complete complete => complete.Scene.OccupiedBounds,
        DesignEvaluation.Partial partial => partial.Scene.OccupiedBounds,
        _ => null
    };
    private static Vector3 DisplayMinimum(GridBounds bounds, ISceneFrame frame)
    {
        var origin = frame.Origin;
        return new(bounds.Minimum.X - (float)origin.X, bounds.Minimum.Y - (float)origin.Y, bounds.Minimum.Z - (float)origin.Z);
    }
    public void Dispose()
    {
        _service.Changed -= OnChanged;
        Clear();
        _overlay.Dispose();
        _cache.Dispose();
        _dispatcher.Dispose();
    }
}
