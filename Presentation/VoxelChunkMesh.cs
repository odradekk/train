using Godot;
using Train.Content;
using Train.Domain.Voxels;

namespace Train.Presentation;

public partial class VoxelChunkMesh : MeshInstance3D
{
    public int FaceCount { get; private set; }
    public int LastWorldVoxelCount { get; private set; }
    public long Revision { get; private set; }
    public string DisplayFingerprint { get; private set; } = "";

    public void Refresh(VoxelWorld world, VoxelCatalogSnapshot? catalog = null) =>
        Refresh(world, chunk: null, catalog);

    public void Refresh(VoxelWorld world, ChunkCoord? chunk, VoxelCatalogSnapshot? catalog = null)
    {
        ArgumentNullException.ThrowIfNull(world);
        var surface = VoxelSurfaceMesher.Build(world, chunk);
        var groups = new Dictionary<string, (DecodedImage? Image, List<WorldVoxelFace> Faces)>(StringComparer.Ordinal);
        foreach (var face in surface.Faces)
        {
            var key = TextureKey(face, catalog, out var image);
            if (!groups.TryGetValue(key, out var group))
            {
                group = (image, new List<WorldVoxelFace>());
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

        Mesh = mesh ?? new ArrayMesh();
        FaceCount = surface.FaceCount;
        LastWorldVoxelCount = world.VoxelCount;
        DisplayFingerprint = string.Join("|", groups
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => pair.Key));
        Revision++;
    }

    private static string TextureKey(WorldVoxelFace face, VoxelCatalogSnapshot? catalog, out DecodedImage? image)
    {
        image = null;
        if (catalog?.TryGet(face.Voxel.DefinitionId, out var entry) != true)
            return "color:" + face.Voxel.DefinitionId.Value;
        var path = FaceImagePath(face, entry.Definition.Presentation);
        if (path is not null && entry.PresentationImages.TryGetValue(path, out image))
            return entry.AssetRevisions.TryGetValue(path, out var revision)
                ? "asset:" + revision
                : "image:" + path;
        return "color:" + face.Voxel.DefinitionId.Value;
    }

    private static string? FaceImagePath(WorldVoxelFace face, VoxelPresentation presentation)
    {
        var worldAxis = FaceWorldAxis(face.Normal);
        var localAxis = Enum.GetValues<VoxelAxis>().Single(axis => face.Voxel.Orientation.Transform(axis) == worldAxis);
        return localAxis switch
        {
            VoxelAxis.PositiveY => presentation.TopPath,
            VoxelAxis.NegativeY => presentation.BottomPath,
            _ => presentation.SidePath
        };
    }

    private static void AddFace(SurfaceTool tool, WorldVoxelFace face, bool textured)
    {
        var p = new Vector3(face.Position.X, face.Position.Y, face.Position.Z);
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

    private static void AddOrientedVertex(SurfaceTool tool, WorldVoxelFace face, Vector3 vertex)
    {
        var dx = (int)MathF.Round(vertex.X - face.Position.X);
        var dy = (int)MathF.Round(vertex.Y - face.Position.Y);
        var dz = (int)MathF.Round(vertex.Z - face.Position.Z);
        var (u, v) = VoxelCubeUv.ForWorldFace(face.Voxel.Orientation, face.Normal, dx, dy, dz);
        tool.SetUV(new Vector2(u, v));
        tool.AddVertex(vertex);
    }

    private static Image ToGodotImage(DecodedImage image)
    {
        var pixels = image.Pixels as byte[] ?? image.Pixels.ToArray();
        return Image.CreateFromData(image.Width, image.Height, false, Image.Format.Rgba8, pixels);
    }

    private static Color FaceColor(WorldVoxelFace face)
    {
        var worldAxis = FaceWorldAxis(face.Normal);
        var localAxis = Enum.GetValues<VoxelAxis>().Single(axis => face.Voxel.Orientation.Transform(axis) == worldAxis);
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
