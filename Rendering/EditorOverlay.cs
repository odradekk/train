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
    private readonly BoxMesh _highlightMesh = new() { Size = Vector3.One * 1.01f };
    private readonly StandardMaterial3D _highlightMaterial = new()
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        AlbedoColor = new Color(0.2f, 0.8f, 1, 0.25f)
    };
    private (EditWorkPlane Plane, ScenePoint Origin, Transform3D Transform)? _drawn;

    internal EditorOverlay()
    {
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
    internal void ShowPreview(IEditPreview? preview, ScenePoint? origin, Transform3D transform)
    {
        foreach (var child in _highlights.GetChildren())
        {
            _highlights.RemoveChild(child);
            child.QueueFree();
        }
        if (preview is null || origin is null) return;
        foreach (var cell in preview.HighlightCells)
            _highlights.AddChild(new MeshInstance3D
            {
                Mesh = _highlightMesh, MaterialOverride = _highlightMaterial,
                Position = transform * new Vector3(cell.X + 0.5f, cell.Y + 0.5f, cell.Z + 0.5f) - ToGodot(origin.Value),
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
            });
    }
    private static Vector3 ToGodot(ScenePoint point) => new((float)point.X, (float)point.Y, (float)point.Z);
    public void Dispose()
    {
        _highlightMesh.Dispose();
        _highlightMaterial.Dispose();
        _gridMesh.Dispose();
        _gridMaterial.Dispose();
    }
}
