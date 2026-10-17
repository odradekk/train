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
    private EditElementId? _entered;
    private System.Collections.Immutable.ImmutableArray<EditElementId> _selection = [];
    internal bool LastPickWasMiss { get; private set; }
    private DesignEvaluation? _evaluation;
    private EditWorkPlane? _drawnPlane;
    private ScenePoint? _gridOrigin;
    private readonly ImmediateMesh _gridMesh = new();
    private readonly StandardMaterial3D _gridMaterial = new() { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = new Color(0.35f, 0.45f, 0.5f) };
    private MeshInstance3D _grid = null!;
    private readonly Node3D _highlights = new() { Name = "EditPreviewHighlights" };
    private readonly BoxMesh _highlightMesh = new() { Size = Vector3.One * 1.01f };
    private readonly StandardMaterial3D _highlightMaterial = new()
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha, AlbedoColor = new Color(0.2f, 0.8f, 1, 0.25f)
    };
    internal WorldSceneView View { get; } = new();
    internal SceneDisplay? Display => _display;
    internal EditViewOptions Options { get; set; } = new(new(GridAxis.Y, 0), null, SceneDebugLayer.None);
    internal event Action<SceneHit?>? Picked;
    internal EditorSceneController(EditorService service)
    {
        _service = service; _cache = new(_dispatcher); service.Changed += OnChanged;
        View.SetActive(false); View.AddChild(_highlights);
        _grid = new() { Name = "WorkPlaneGrid", Mesh = _gridMesh, MaterialOverride = _gridMaterial, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off }; View.AddChild(_grid);
        View.PickRequested += position => { var hit = Pick(position); if (hit is not null || LastPickWasMiss) Picked?.Invoke(hit); };
        View.ReturnRequested += () => service.RequestClose(() => { });
    }
    private void OnChanged()
    {
        if (_service.State?.Snapshot.Stamp == _stamp && _service.State?.EnteredObject == _entered && (_service.State?.Selection ?? []).SequenceEqual(_selection) && ReferenceEquals(_service.State?.CurrentEvaluation, _evaluation)) return;
        if (_display is not null) { _display.Root.Visible = false; _display.CancelPreparation(); }
        View.Interactive = false; ShowPreview(null);
    }
    internal void Process(double delta, bool retry = false)
    {
        if (_service.Session is not { } session || _service.FixedContent is not { } content)
        { Clear(); return; }
        if (_session != session)
        {
            Clear(); _session = session; _display = new(_cache, content); View.AddChild(_display.Root);
        }
        View.SetActive(true);
        if (_service.SceneUnavailable && !retry) { View.Interactive = false; return; }
        try
        {
            var stamp = _service.State?.Snapshot.Stamp;
            if (_entered != _service.State?.EnteredObject) { _entered = _service.State?.EnteredObject; _drawnPlane = null; }
            _selection = _service.State?.Selection ?? [];
            _evaluation = _service.State?.CurrentEvaluation;
            if (_stamp != stamp) { _display!.Root.Visible = false; _display.CancelPreparation(); ShowPreview(null); _stamp = stamp; }
            var result = session.Present(Options, retry ? null : _display!.DisplayedFrame);
            if (result is SceneResult<ISceneFrame>.Success success)
            {
                if (success.Value.Readiness == SceneReadiness.Failed)
                { foreach (var problem in success.Value.Diagnostics) _service.SceneFailed(problem); return; }
                using var prepared = _display!.Prepare(success.Value);
                if (prepared is not null) { prepared.Commit(); View.ShowFrame(success.Value, null); }
                _display.UpdateDebugLines(success.Value.DebugLines);
                DrawPlane(success.Value);
                View.CameraRig.Update(delta);
                if (retry) _service.SceneRecovered();
            }
            else if (result is SceneResult<ISceneFrame>.Failure failure)
                foreach (var problem in failure.Problems) _service.SceneFailed(problem);
            else if (result is SceneResult<ISceneFrame>.Faulted fault) _service.SceneFailed(fault.Problem);
        }
        catch (Exception exception) { _service.SceneFailed(new("editor_display_failed", SceneStage.Present, null, null, null, null, exception.Message)); }
    }
    internal SceneHit? Pick(Vector2 position)
    {
        try { return PickCore(position); }
        catch (Exception error)
        {
            LastPickWasMiss = false;
            _service.SceneFailed(new("editor_pick_failed", SceneStage.Pick, null, null, null, null, error.Message));
            return null;
        }
    }
    private SceneHit? PickCore(Vector2 position)
    {
        LastPickWasMiss = false;
        if (_service.SceneUnavailable || _service.Session != _session || _service.State?.Snapshot.Stamp != _stamp || _display?.DisplayedFrame is not { } frame || !_display.Root.Visible) return null;
        var camera = View.CameraRig.Camera;
        var origin = camera.ProjectRayOrigin(position); var direction = camera.ProjectRayNormal(position);
        var result = frame.Pick(new(new(origin.X, origin.Y, origin.Z), new(direction.X, direction.Y, direction.Z), 4000));
        LastPickWasMiss = result is ScenePick.Miss;
        if (result is not ScenePick.Hit hit) return null;
        if (hit.Value.Frame != frame.Update.Frame || hit.Value.Subject is not PickSubject.Editing subject ||
            subject.Session != _session!.Id || subject.Revision != _stamp!.Revision)
        { _service.Report("拾取结果已过期，请重新选择。"); return null; }
        return hit.Value;
    }
    private void DrawPlane(ISceneFrame frame)
    {
        if (_drawnPlane == Options.WorkPlane && _gridOrigin == frame.Origin) return;
        _drawnPlane = Options.WorkPlane; _gridOrigin = frame.Origin;
        _gridMesh.ClearSurfaces(); _gridMesh.SurfaceBegin(Mesh.PrimitiveType.Lines, _gridMaterial);
        var plane = Options.WorkPlane;
        Vector3 Point(int a, int b)
        {
            var p = plane.Axis switch { GridAxis.X => new Vector3(plane.Coordinate, a, b), GridAxis.Y => new Vector3(a, plane.Coordinate, b), _ => new Vector3(a, b, plane.Coordinate) };
            return EnteredTransform() * p - new Vector3((float)frame.Origin.X, (float)frame.Origin.Y, (float)frame.Origin.Z);
        }
        for (var i = -16; i <= 16; i++)
        {
            _gridMesh.SurfaceAddVertex(Point(i, -16)); _gridMesh.SurfaceAddVertex(Point(i, 16));
            _gridMesh.SurfaceAddVertex(Point(-16, i)); _gridMesh.SurfaceAddVertex(Point(16, i));
        }
        _gridMesh.SurfaceEnd();
    }
    internal void ShowPreview(IEditPreview? preview)
    {
        foreach (var child in _highlights.GetChildren()) { _highlights.RemoveChild(child); child.QueueFree(); }
        if (preview is null || _display?.DisplayedFrame is not { } frame) return;
        foreach (var cell in preview.HighlightCells)
            _highlights.AddChild(new MeshInstance3D
            {
                Mesh = _highlightMesh, MaterialOverride = _highlightMaterial,
                Position = EnteredTransform() * new Vector3(cell.X + 0.5f, cell.Y + 0.5f, cell.Z + 0.5f) - new Vector3((float)frame.Origin.X, (float)frame.Origin.Y, (float)frame.Origin.Z),
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
            });
    }
    internal GridPosition? PickPlane(Vector2 position)
    {
        if (_service.SceneUnavailable || _service.Session != _session || _service.State?.Snapshot.Stamp != _stamp || _display?.DisplayedFrame is not { } frame || !_display.Root.Visible) return null;
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
            var item = draft!.Layout.NestedItems[index];
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
    { _display?.Dispose(); _display = null; _session = null; _stamp = null; _evaluation = null; View.SetActive(false); }
    public void Dispose() { _service.Changed -= OnChanged; Clear(); ShowPreview(null); _highlightMesh.Dispose(); _highlightMaterial.Dispose(); _gridMesh.Dispose(); _gridMaterial.Dispose(); _cache.Dispose(); _dispatcher.Dispose(); }
}
