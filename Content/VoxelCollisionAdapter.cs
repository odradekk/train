using Train.Domain.Voxels;

namespace Train.Content;

internal static class VoxelCollisionAdapter
{
    public static bool CanDerivePositiveVolume(VoxelCollision collision)
    {
        ArgumentNullException.ThrowIfNull(collision);
        if (collision.Kind is VoxelCollisionKind.None or VoxelCollisionKind.FullBox)
            return true;
        return TryDeriveSize(collision.SizeX, out _) &&
               TryDeriveSize(collision.SizeY, out _) &&
               TryDeriveSize(collision.SizeZ, out _);
    }

    public static bool TryDeriveSize(double size, out float derived)
    {
        derived = (float)size;
        return derived > 0f;
    }
}
