using Train.Domain.Voxels;

namespace Train.Domain.Composites;

public readonly record struct CompositeTransform
{
    public double OriginX { get; }
    public double OriginY { get; }
    public double OriginZ { get; }
    public VoxelOrientation Orientation { get; }

    private CompositeTransform(double originX, double originY, double originZ, VoxelOrientation orientation)
    {
        OriginX = originX;
        OriginY = originY;
        OriginZ = originZ;
        Orientation = orientation;
    }

    public static CompositeTransform Identity { get; } = Translation(0, 0, 0);

    public static CompositeTransform Translation(double x, double y, double z) =>
        TryCreate(x, y, z, VoxelOrientation.Identity, out var transform)
            ? transform
            : throw new ArgumentOutOfRangeException(nameof(x), "Transform components must be finite.");

    public static bool TryCreate(
        double originX,
        double originY,
        double originZ,
        VoxelOrientation orientation,
        out CompositeTransform transform)
    {
        transform = default;
        if (!double.IsFinite(originX) || !double.IsFinite(originY) || !double.IsFinite(originZ))
            return false;
        if (!orientation.IsOrthogonal)
            return false;
        transform = new CompositeTransform(originX, originY, originZ, orientation);
        return true;
    }
}
