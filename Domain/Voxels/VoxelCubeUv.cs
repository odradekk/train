namespace Train.Domain.Voxels;

public static class VoxelCubeUv
{
    public static (int X, int Y, int Z) LocalCorner(VoxelOrientation orientation, int dx, int dy, int dz)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(dx, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(dx, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(dy, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(dy, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(dz, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(dz, 1);
        return (
            AxisComponent(orientation.Right, dx, dy, dz),
            AxisComponent(orientation.Up, dx, dy, dz),
            AxisComponent(orientation.Forward, dx, dy, dz));
    }

    public static (float U, float V) ForWorldFace(
        VoxelOrientation orientation,
        VoxelFaceNormal worldNormal,
        int dx,
        int dy,
        int dz)
    {
        var local = LocalCorner(orientation, dx, dy, dz);
        var localFace = orientation.InverseTransform(ToAxis(worldNormal));
        return ForLocalFace(localFace, local.X, local.Y, local.Z);
    }

    public static IReadOnlyDictionary<(int X, int Y, int Z), (float U, float V)> TopCorners(VoxelOrientation orientation)
    {
        var corners = new Dictionary<(int X, int Y, int Z), (float U, float V)>();
        foreach (var dx in new[] { 0, 1 })
        foreach (var dz in new[] { 0, 1 })
            corners[(dx, 1, dz)] = ForWorldFace(orientation, VoxelFaceNormal.PositiveY, dx, 1, dz);
        return corners;
    }

    // Identity keeps the original CCW world-face UVs: ccw0=(0,1), ccw1=(0,0), ccw2=(1,0), ccw3=(1,1).
    private static (float U, float V) ForLocalFace(VoxelAxis localFace, int localX, int localY, int localZ) =>
        localFace switch
        {
            VoxelAxis.PositiveY => (localX, 1 - localZ),
            VoxelAxis.NegativeY => (localZ, 1 - localX),
            VoxelAxis.PositiveX => (localZ, 1 - localY),
            VoxelAxis.NegativeX => (localY, 1 - localZ),
            VoxelAxis.PositiveZ => (localY, 1 - localX),
            VoxelAxis.NegativeZ => (localX, 1 - localY),
            _ => throw new ArgumentOutOfRangeException(nameof(localFace), localFace, "Unknown voxel axis.")
        };

    private static int AxisComponent(VoxelAxis axis, int dx, int dy, int dz) => axis switch
    {
        VoxelAxis.PositiveX => dx,
        VoxelAxis.NegativeX => 1 - dx,
        VoxelAxis.PositiveY => dy,
        VoxelAxis.NegativeY => 1 - dy,
        VoxelAxis.PositiveZ => dz,
        VoxelAxis.NegativeZ => 1 - dz,
        _ => throw new ArgumentOutOfRangeException(nameof(axis), axis, "Unknown voxel axis.")
    };

    private static VoxelAxis ToAxis(VoxelFaceNormal normal) => normal switch
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
