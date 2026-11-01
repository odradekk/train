using Godot;
using Train.Content.Contracts;
using Train.Scenes.Contracts;

namespace Train.Rendering;

internal sealed class EditorOverlay : IDisposable
{
    private readonly ImmediateMesh _gridMesh = new();
    private readonly StandardMaterial3D _gridMaterial = new()
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        AlbedoColor = new Color(0.35f, 0.45f, 0.5f)
    };
    private readonly MeshInstance3D _grid;
    private readonly Node3D _highlights = new() { Name = "EditPreviewHighlights" };
    private readonly BoxMesh _highlightMesh = new() { Size = Vector3.One };
    private readonly StandardMaterial3D _highlightMaterial = new()
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        AlbedoColor = new Color(0.2f, 0.8f, 0.3f, 0.25f)
    };
    private readonly MultiMesh _highlightInstances;
    private readonly MultiMeshInstance3D _highlightDisplay;
    private (EditWorkPlane Plane, ScenePoint Origin, Transform3D Transform)? _drawn;

    internal EditorOverlay()
    {
        _highlightInstances = new() { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = _highlightMesh };
        _highlightDisplay = new() { Multimesh = _highlightInstances, MaterialOverride = _highlightMaterial,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        _highlights.AddChild(_highlightDisplay);
        _grid = new()
        {
            Name = "WorkPlaneGrid", Mesh = _gridMesh, MaterialOverride = _gridMaterial,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
        };
    }
    internal bool Visible
    {
        set { _grid.Visible = value; _highlights.Visible = value; }
    }
    internal void Attach(Node parent)
    {
        parent.AddChild(_grid);
        parent.AddChild(_highlights);
    }
    internal void DrawPlane(EditWorkPlane plane, ScenePoint origin, Transform3D transform)
    {
        Visible = true;
        if (_drawn == (plane, origin, transform)) return;
        _drawn = (plane, origin, transform);
        _gridMesh.ClearSurfaces();
        _gridMesh.SurfaceBegin(Mesh.PrimitiveType.Lines, _gridMaterial);
        Vector3 Point(int a, int b)
        {
            var point = plane.Axis switch
            {
                GridAxis.X => new Vector3(plane.Coordinate, a, b),
                GridAxis.Y => new Vector3(a, plane.Coordinate, b),
                _ => new Vector3(a, b, plane.Coordinate)
            };
            return transform * point - ToGodot(origin);
        }
        for (var i = -16; i <= 16; i++)
        {
            _gridMesh.SurfaceAddVertex(Point(i, -16));
            _gridMesh.SurfaceAddVertex(Point(i, 16));
            _gridMesh.SurfaceAddVertex(Point(-16, i));
            _gridMesh.SurfaceAddVertex(Point(16, i));
        }
        _gridMesh.SurfaceEnd();
    }
    internal void ShowPreview(IEditPreview? preview, ScenePoint? origin, Transform3D transform,
        EditFootprint? rejectedFootprint = null)
    {
        _highlightInstances.InstanceCount = 0;
        if (origin is null) return;
        _highlightDisplay.Transform = transform * new Transform3D(Basis.Identity, Vector3.One * 0.5f);
        _highlightDisplay.Position -= ToGodot(origin.Value);
        _highlightMaterial.AlbedoColor = preview is { CanApply: true, AffectedCells: > 0 }
            ? new Color(0.2f, 0.8f, 0.3f, 0.25f) : new Color(0.9f, 0.2f, 0.2f, 0.25f);
        if (preview is not null)
        {
            _highlightInstances.InstanceCount = preview.HighlightCells.Length;
            for (var index = 0; index < preview.HighlightCells.Length; index++)
            {
                var cell = preview.HighlightCells[index];
                Place(index, new(cell.X + 0.5f, cell.Y + 0.5f, cell.Z + 0.5f), Vector3.One);
            }
        }
        else if (Bounds(rejectedFootprint) is { } bounds)
        {
            _highlightInstances.InstanceCount = 1;
            var size = new Vector3((float)((long)bounds.Maximum.X - bounds.Minimum.X + 1),
                (float)((long)bounds.Maximum.Y - bounds.Minimum.Y + 1),
                (float)((long)bounds.Maximum.Z - bounds.Minimum.Z + 1));
            Place(0, new Vector3(bounds.Minimum.X, bounds.Minimum.Y, bounds.Minimum.Z) + size / 2, size);
        }
        void Place(int index, Vector3 center, Vector3 size)
        {
            _highlightInstances.SetInstanceTransform(index,
                new Transform3D(Basis.Identity.Scaled(size + Vector3.One * 0.01f), center - Vector3.One * 0.5f));
        }
    }
    private static GridBounds? Bounds(EditFootprint? footprint) => footprint switch
    {
        EditFootprint.Cell cell => new(cell.At, cell.At),
        EditFootprint.Box box => box.Bounds,
        EditFootprint.Stroke { Samples.IsEmpty: false } stroke => new(
            new(stroke.Samples.Min(cell => cell.X), stroke.Samples.Min(cell => cell.Y), stroke.Samples.Min(cell => cell.Z)),
            new((int)Math.Min(int.MaxValue, (long)stroke.Samples.Max(cell => cell.X) + stroke.BrushWidth - 1),
                (int)Math.Min(int.MaxValue, (long)stroke.Samples.Max(cell => cell.Y) + stroke.BrushWidth - 1),
                (int)Math.Min(int.MaxValue, (long)stroke.Samples.Max(cell => cell.Z) + stroke.BrushWidth - 1))),
        _ => null
    };
    private static Vector3 ToGodot(ScenePoint point) => new((float)point.X, (float)point.Y, (float)point.Z);
    public void Dispose()
    {
        _highlightInstances.Dispose();
        _highlightMesh.Dispose();
        _highlightMaterial.Dispose();
        _gridMesh.Dispose();
        _gridMaterial.Dispose();
    }
}
