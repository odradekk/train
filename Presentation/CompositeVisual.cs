using Godot;
using Train.Content;
using Train.Domain.Composites;
using Train.Domain.Rails;
using Train.Domain.Voxels;

namespace Train.Presentation;

public partial class CompositeVisual : Node3D
{
    private MeshInstance3D _mesh = null!;

    public int FaceCount { get; private set; }
    public int MeshInstanceCount { get; private set; }
    public string DisplayFingerprint { get; private set; } = "";
    public Mesh? GeneratedMesh => _mesh?.Mesh;

    public override void _Ready()
    {
        if (_mesh is not null)
            return;
        _mesh = new MeshInstance3D();
        AddChild(_mesh);
    }

    public void Rebuild(CompositeInstance instance, VoxelCatalogSnapshot? catalog)
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (_mesh is null)
        {
            _mesh = new MeshInstance3D();
            AddChild(_mesh);
        }

        var surface = CompositeSurfaceMesher.Build(instance);
        var groups = new Dictionary<string, (DecodedImage? Image, List<CompositeVoxelFace> Faces)>(StringComparer.Ordinal);
        foreach (var face in surface.Faces)
        {
            var key = TextureKey(face, catalog, out var image);
            if (!groups.TryGetValue(key, out var group))
            {
                group = (image, new List<CompositeVoxelFace>());
                groups[key] = group;
            }

            group.Faces.Add(face);
        }

        ArrayMesh? mesh = null;
        foreach (var (image, faces) in groups.Values)
        {
            var tool = new SurfaceTool();
            tool.Begin(Mesh.PrimitiveType.Triangles);
            foreach (var face in faces)
                AddFace(tool, face, textured: image is not null);
            var material = new StandardMaterial3D
            {
                Roughness = 0.88f,
                VertexColorUseAsAlbedo = true
            };
            if (image is not null)
                material.AlbedoTexture = ImageTexture.CreateFromImage(ToGodotImage(image));
            else
                material.AlbedoColor = Colors.White;
            tool.SetMaterial(material);
            mesh = tool.Commit(mesh);
        }

        _mesh.Mesh = mesh ?? new ArrayMesh();
        FaceCount = surface.FaceCount;
        MeshInstanceCount = 1;
        DisplayFingerprint = string.Join("|", groups
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => pair.Key));
        ApplyRigidTransform(instance.Transform);
    }

    public ulong MeshInstanceId => _mesh?.GetInstanceId() ?? 0;

    public void ApplyRigidTransform(CompositeTransform transform)
    {
        Position = new Vector3((float)transform.OriginX, (float)transform.OriginY, (float)transform.OriginZ);
        Basis = new Basis(
            new Vector3((float)transform.RightX, (float)transform.RightY, (float)transform.RightZ),
            new Vector3((float)transform.UpX, (float)transform.UpY, (float)transform.UpZ),
            new Vector3((float)transform.ForwardX, (float)transform.ForwardY, (float)transform.ForwardZ));
        Scale = Vector3.One;
    }

    private static string TextureKey(CompositeVoxelFace face, VoxelCatalogSnapshot? catalog, out DecodedImage? image)
    {
        image = null;
        if (catalog?.TryGet(face.Part.DefinitionId, out var entry) != true)
            return "color:" + face.Part.DefinitionId.Value;
        var path = FaceImagePath(face, entry.Definition.Presentation);
        if (path is not null && entry.PresentationImages.TryGetValue(path, out image))
            return entry.AssetRevisions.TryGetValue(path, out var revision)
                ? "asset:" + revision
                : "image:" + path;
        return "color:" + face.Part.DefinitionId.Value;
    }

    private static string? FaceImagePath(CompositeVoxelFace face, VoxelPresentation presentation)
    {
        var worldAxis = FaceWorldAxis(face.Normal);
        var localAxis = Enum.GetValues<VoxelAxis>().Single(axis => face.Part.Orientation.Transform(axis) == worldAxis);
        return localAxis switch
        {
            VoxelAxis.PositiveY => presentation.TopPath,
            VoxelAxis.NegativeY => presentation.BottomPath,
            _ => presentation.SidePath
        };
    }

    private static void AddFace(SurfaceTool tool, CompositeVoxelFace face, bool textured)
    {
        var p = new Vector3(face.Cell.X, face.Cell.Y, face.Cell.Z);
        var (normal, ccw0, ccw1, ccw2, ccw3) = face.Normal switch
        {
            VoxelFaceNormal.PositiveX => (Vector3.Right, p + new Vector3(1, 0, 0), p + new Vector3(1, 1, 0), p + new Vector3(1, 1, 1), p + new Vector3(1, 0, 1)),
            VoxelFaceNormal.NegativeX => (Vector3.Left, p, p + new Vector3(0, 0, 1), p + new Vector3(0, 1, 1), p + new Vector3(0, 1, 0)),
            VoxelFaceNormal.PositiveY => (Vector3.Up, p + new Vector3(0, 1, 0), p + new Vector3(0, 1, 1), p + new Vector3(1, 1, 1), p + new Vector3(1, 1, 0)),
            VoxelFaceNormal.NegativeY => (Vector3.Down, p, p + new Vector3(1, 0, 0), p + new Vector3(1, 0, 1), p + new Vector3(0, 0, 1)),
            VoxelFaceNormal.PositiveZ => (Vector3.Back, p + new Vector3(0, 0, 1), p + new Vector3(1, 0, 1), p + new Vector3(1, 1, 1), p + new Vector3(0, 1, 1)),
            VoxelFaceNormal.NegativeZ => (Vector3.Forward, p, p + new Vector3(0, 1, 0), p + new Vector3(1, 1, 0), p + new Vector3(1, 0, 0)),
            _ => throw new ArgumentOutOfRangeException(nameof(face), face.Normal, "Unknown face normal.")
        };
        tool.SetNormal(normal);
        tool.SetColor(textured ? Colors.White : FaceColor(face));
        AddOrientedVertex(tool, face, ccw0);
        AddOrientedVertex(tool, face, ccw2);
        AddOrientedVertex(tool, face, ccw1);
        AddOrientedVertex(tool, face, ccw0);
        AddOrientedVertex(tool, face, ccw3);
        AddOrientedVertex(tool, face, ccw2);
    }

    private static void AddOrientedVertex(SurfaceTool tool, CompositeVoxelFace face, Vector3 vertex)
    {
        var dx = (int)MathF.Round(vertex.X - face.Cell.X);
        var dy = (int)MathF.Round(vertex.Y - face.Cell.Y);
        var dz = (int)MathF.Round(vertex.Z - face.Cell.Z);
        var (u, v) = VoxelCubeUv.ForWorldFace(face.Part.Orientation, face.Normal, dx, dy, dz);
        tool.SetUV(new Vector2(u, v));
        tool.AddVertex(vertex);
    }

    private static Image ToGodotImage(DecodedImage image)
    {
        var pixels = image.Pixels as byte[] ?? image.Pixels.ToArray();
        return Image.CreateFromData(image.Width, image.Height, false, Image.Format.Rgba8, pixels);
    }

    private static Color FaceColor(CompositeVoxelFace face)
    {
        var worldAxis = FaceWorldAxis(face.Normal);
        var localAxis = Enum.GetValues<VoxelAxis>().Single(axis => face.Part.Orientation.Transform(axis) == worldAxis);
        return localAxis switch
        {
            VoxelAxis.PositiveY => new Color(0.91f, 0.63f, 0.17f),
            VoxelAxis.NegativeY => new Color(0.23f, 0.16f, 0.08f),
            VoxelAxis.PositiveZ => new Color(0.78f, 0.25f, 0.17f),
            VoxelAxis.NegativeZ => new Color(0.39f, 0.10f, 0.08f),
            VoxelAxis.PositiveX => new Color(0.18f, 0.46f, 0.79f),
            VoxelAxis.NegativeX => new Color(0.08f, 0.19f, 0.36f),
            _ => throw new ArgumentOutOfRangeException(nameof(localAxis), localAxis, "Unknown voxel axis.")
        };
    }

    private static VoxelAxis FaceWorldAxis(VoxelFaceNormal normal) => normal switch
    {
        VoxelFaceNormal.PositiveX => VoxelAxis.PositiveX,
        VoxelFaceNormal.NegativeX => VoxelAxis.NegativeX,
        VoxelFaceNormal.PositiveY => VoxelAxis.PositiveY,
        VoxelFaceNormal.NegativeY => VoxelAxis.NegativeY,
        VoxelFaceNormal.PositiveZ => VoxelAxis.PositiveZ,
        VoxelFaceNormal.NegativeZ => VoxelAxis.NegativeZ,
        _ => throw new ArgumentOutOfRangeException(nameof(normal), normal, "Unknown face normal.")
    };
}

public enum CompositeVisualSyncKind
{
    None = 0,
    Rebuilt = 1,
    TransformOnly = 2
}

public sealed class CompositeVisualPresenter
{
    private readonly Dictionary<EntityId, CompositeVisual> _visuals = new();

    public int VehicleCount => _visuals.Count;
    public int FaceCount => _visuals.Values.Sum(visual => visual.FaceCount);
    public int MeshInstanceCount => _visuals.Values.Sum(visual => visual.MeshInstanceCount);
    public CompositeVisualSyncKind LastSync { get; private set; }
    public IEnumerable<CompositeVisual> Visuals => _visuals.Values;
    public CompositeVisual? VisualFor(EntityId vehicleId) => _visuals.GetValueOrDefault(vehicleId);
    public IReadOnlyList<ulong> MeshInstanceIds =>
        _visuals.OrderBy(pair => pair.Key.Value).Select(pair => pair.Value.MeshInstanceId).ToArray();

    public void Clear()
    {
        foreach (var visual in _visuals.Values)
            visual.Free();
        _visuals.Clear();
    }

    public void Refresh(Node parent, VoxelDevelopmentSession session, VoxelCatalogSnapshot? catalog)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(session);
        if (!session.TakePendingCompositeVisualRebuild() && _visuals.Count == session.Vehicles.Count)
        {
            foreach (var vehicle in session.Vehicles)
            {
                if (_visuals.TryGetValue(vehicle.Id, out var visual))
                    visual.ApplyRigidTransform(vehicle.Transform);
            }

            LastSync = CompositeVisualSyncKind.TransformOnly;
            return;
        }

        Clear();
        foreach (var vehicle in session.Vehicles)
        {
            var visual = new CompositeVisual();
            parent.AddChild(visual);
            visual.Rebuild(vehicle, catalog);
            _visuals[vehicle.Id] = visual;
        }

        LastSync = CompositeVisualSyncKind.Rebuilt;
    }

    public void ApplyDisplayPoses(TrainDisplaySample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        foreach (var pose in sample.Vehicles)
        {
            if (_visuals.TryGetValue(pose.VehicleId, out var visual))
                visual.ApplyRigidTransform(pose.Transform);
        }
    }
}
