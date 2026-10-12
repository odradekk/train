
using Train.Content.Voxels;

namespace Train.Content;

// 定义使用 double，表现使用 float；正边长转换后可能归零。
internal static class VoxelCollisionValidation
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
