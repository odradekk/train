using Train.Core.Content.Composites;
using Train.Core.Content.Voxels;
using System.Collections.ObjectModel;
using Train.Domain.Voxels;

namespace Train.Domain.Composites;

public readonly record struct CompositeVoxelFace(LocalVoxelCoord Cell, VoxelFaceNormal Normal, CompositePart Part);

public sealed class CompositeSurfaceMesh
{
    public IReadOnlyList<CompositeVoxelFace> Faces { get; }
    public int FaceCount => Faces.Count;
    public int BatchCount => FaceCount == 0 ? 0 : 1;

    public CompositeSurfaceMesh(IReadOnlyList<CompositeVoxelFace> faces)
    {
        Faces = faces;
    }
}

public static class CompositeSurfaceMesher
{
    private static readonly (VoxelFaceNormal Normal, int X, int Y, int Z)[] Neighbors =
    {
        (VoxelFaceNormal.PositiveX, 1, 0, 0),
        (VoxelFaceNormal.NegativeX, -1, 0, 0),
        (VoxelFaceNormal.PositiveY, 0, 1, 0),
        (VoxelFaceNormal.NegativeY, 0, -1, 0),
        (VoxelFaceNormal.PositiveZ, 0, 0, 1),
        (VoxelFaceNormal.NegativeZ, 0, 0, -1)
    };

    public static CompositeSurfaceMesh Build(CompositeInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        var occupied = instance.Parts.ToDictionary(part => part.Cell);
        var faces = new List<CompositeVoxelFace>();
        foreach (var part in instance.Parts)
        {
            foreach (var (normal, x, y, z) in Neighbors)
            {
                var neighbor = new LocalVoxelCoord(part.Cell.X + x, part.Cell.Y + y, part.Cell.Z + z);
                if (!occupied.ContainsKey(neighbor))
                    faces.Add(new CompositeVoxelFace(part.Cell, normal, part));
            }
        }

        return new CompositeSurfaceMesh(new ReadOnlyCollection<CompositeVoxelFace>(faces));
    }
}
