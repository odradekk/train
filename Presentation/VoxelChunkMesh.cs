using Godot;
using Train.Domain.Voxels;

namespace Train.Presentation;

public partial class VoxelChunkMesh : MeshInstance3D
{
    public int FaceCount { get; private set; }
    public int LastWorldVoxelCount { get; private set; }
    public long Revision { get; private set; }

    public void Refresh(VoxelWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);
        var surface = VoxelSurfaceMesher.Build(world);
        var tool = new SurfaceTool();
        tool.Begin(Mesh.PrimitiveType.Triangles);
        foreach (var face in surface.Faces)
            AddFace(tool, face);

        tool.SetMaterial(new StandardMaterial3D
        {
            AlbedoColor = Colors.White,
            Roughness = 0.88f
            ,VertexColorUseAsAlbedo = true
        });
        Mesh = tool.Commit();
        FaceCount = surface.FaceCount;
        LastWorldVoxelCount = world.VoxelCount;
        Revision++;
    }

    private static void AddFace(SurfaceTool tool, WorldVoxelFace face)
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
        tool.SetColor(FaceColor(face));
        tool.AddVertex(ccw0);
        tool.AddVertex(ccw2);
        tool.AddVertex(ccw1);
        tool.AddVertex(ccw0);
        tool.AddVertex(ccw3);
        tool.AddVertex(ccw2);
    }

    private static Color FaceColor(WorldVoxelFace face)
    {
        var worldAxis = face.Normal switch
        {
            VoxelFaceNormal.PositiveX => VoxelAxis.PositiveX,
            VoxelFaceNormal.NegativeX => VoxelAxis.NegativeX,
            VoxelFaceNormal.PositiveY => VoxelAxis.PositiveY,
            VoxelFaceNormal.NegativeY => VoxelAxis.NegativeY,
            VoxelFaceNormal.PositiveZ => VoxelAxis.PositiveZ,
            VoxelFaceNormal.NegativeZ => VoxelAxis.NegativeZ,
            _ => throw new ArgumentOutOfRangeException(nameof(face), face.Normal, "Unknown face normal.")
        };
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
}
