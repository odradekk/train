using Godot;
using Train.Domain.Rails;
using Train.Domain.Voxels;

namespace Train.Presentation;

public sealed class TrackRendererPresenter
{
    private readonly Dictionary<ChunkCoord, MeshInstance3D> _sections = new();
    private TrackRenderModel _model = new(Array.Empty<TrackRenderSection>());

    public int SolidCount => _model.SolidCount;
    public int RailSolidCount => _model.RailSolidCount;
    public int SleeperSolidCount => _model.SleeperSolidCount;
    public TrackRenderModel Model => _model;

    public void Clear()
    {
        foreach (var mesh in _sections.Values)
            mesh.QueueFree();
        _sections.Clear();
        _model = new TrackRenderModel(Array.Empty<TrackRenderSection>());
    }

    public void Refresh(Node parent, VoxelDevelopmentSession session)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(session);
        session.TakePendingTrackChunks();
        if (!session.HasRailway)
        {
            Clear();
            return;
        }

        _model = TrackRenderModel.From(session.Rails, session.World, session.World.ChunkSize);
        var keep = _model.Sections.Select(section => section.Chunk).ToHashSet();
        foreach (var chunk in _sections.Keys.ToArray())
        {
            if (keep.Contains(chunk))
                continue;
            _sections[chunk].QueueFree();
            _sections.Remove(chunk);
        }

        foreach (var section in _model.Sections)
        {
            if (!_sections.TryGetValue(section.Chunk, out var mesh))
            {
                mesh = new MeshInstance3D();
                parent.AddChild(mesh);
                _sections[section.Chunk] = mesh;
            }

            mesh.Mesh = BuildMesh(section);
        }
    }

    public bool HasGapNear(WorldVoxelCoord cell) => _model.HasGapNear(cell);

    private static ArrayMesh BuildMesh(TrackRenderSection section)
    {
        var mesh = new ArrayMesh();
        AddKind(mesh, section, TrackSolidKind.Rail, new Color(0.62f, 0.64f, 0.68f));
        AddKind(mesh, section, TrackSolidKind.Sleeper, new Color(0.42f, 0.28f, 0.16f));
        return mesh;
    }

    private static void AddKind(ArrayMesh mesh, TrackRenderSection section, TrackSolidKind kind, Color color)
    {
        var solids = section.Solids.Where(solid => solid.Kind == kind).ToArray();
        if (solids.Length == 0)
            return;

        var tool = new SurfaceTool();
        tool.Begin(Mesh.PrimitiveType.Triangles);
        foreach (var solid in solids)
            AddBox(tool, solid.Volume, color);
        tool.GenerateNormals();
        tool.SetMaterial(new StandardMaterial3D
        {
            Roughness = 0.72f,
            VertexColorUseAsAlbedo = true
        });
        tool.Commit(mesh);
    }

    private static void AddBox(SurfaceTool tool, RailVolumeBox volume, Color color)
    {
        var min = new Vector3((float)volume.MinX, (float)volume.MinY, (float)volume.MinZ);
        var max = new Vector3((float)volume.MaxX, (float)volume.MaxY, (float)volume.MaxZ);
        var corners = new[]
        {
            new Vector3(min.X, min.Y, min.Z),
            new Vector3(max.X, min.Y, min.Z),
            new Vector3(max.X, max.Y, min.Z),
            new Vector3(min.X, max.Y, min.Z),
            new Vector3(min.X, min.Y, max.Z),
            new Vector3(max.X, min.Y, max.Z),
            new Vector3(max.X, max.Y, max.Z),
            new Vector3(min.X, max.Y, max.Z)
        };
        AddQuad(tool, corners[0], corners[1], corners[2], corners[3], color);
        AddQuad(tool, corners[5], corners[4], corners[7], corners[6], color);
        AddQuad(tool, corners[4], corners[0], corners[3], corners[7], color);
        AddQuad(tool, corners[1], corners[5], corners[6], corners[2], color);
        AddQuad(tool, corners[3], corners[2], corners[6], corners[7], color);
        AddQuad(tool, corners[4], corners[5], corners[1], corners[0], color);
    }

    private static void AddQuad(SurfaceTool tool, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color color)
    {
        tool.SetColor(color);
        tool.AddVertex(a);
        tool.SetColor(color);
        tool.AddVertex(b);
        tool.SetColor(color);
        tool.AddVertex(c);
        tool.SetColor(color);
        tool.AddVertex(a);
        tool.SetColor(color);
        tool.AddVertex(c);
        tool.SetColor(color);
        tool.AddVertex(d);
    }
}
