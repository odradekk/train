using Train.Domain.Voxels;

namespace Train.Domain.Composites;

public readonly record struct CompositeTransform
{
    private const double RigidRotationTolerance = 1e-12;

    public double OriginX { get; }
    public double OriginY { get; }
    public double OriginZ { get; }
    public double RightX { get; }
    public double RightY { get; }
    public double RightZ { get; }
    public double UpX { get; }
    public double UpY { get; }
    public double UpZ { get; }

    public (double X, double Y, double Z) Right => (RightX, RightY, RightZ);
    public (double X, double Y, double Z) Up => (UpX, UpY, UpZ);
    public (double X, double Y, double Z) Forward
    {
        get
        {
            var forward = Normalize(Cross(Right, Up));
            return forward;
        }
    }

    public double ForwardX => Forward.X;
    public double ForwardY => Forward.Y;
    public double ForwardZ => Forward.Z;

    public VoxelOrientation Orientation => FragmentGridSnap.SnapRotation(Right, Up);

    public (double X, double Y, double Z) TransformPoint(double x, double y, double z) =>
        (
            OriginX + x * RightX + y * UpX + z * ForwardX,
            OriginY + x * RightY + y * UpY + z * ForwardY,
            OriginZ + x * RightZ + y * UpZ + z * ForwardZ);

    public bool IsFinite =>
        double.IsFinite(OriginX) && double.IsFinite(OriginY) && double.IsFinite(OriginZ) &&
        double.IsFinite(RightX) && double.IsFinite(RightY) && double.IsFinite(RightZ) &&
        double.IsFinite(UpX) && double.IsFinite(UpY) && double.IsFinite(UpZ);

    public bool IsValid =>
        IsFinite && IsRigidRotation(Right, Up);

    private CompositeTransform(
        double originX,
        double originY,
        double originZ,
        double rightX,
        double rightY,
        double rightZ,
        double upX,
        double upY,
        double upZ)
    {
        OriginX = originX;
        OriginY = originY;
        OriginZ = originZ;
        RightX = rightX;
        RightY = rightY;
        RightZ = rightZ;
        UpX = upX;
        UpY = upY;
        UpZ = upZ;
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
        var right = FragmentGridSnap.AxisVector(orientation.Right);
        var up = FragmentGridSnap.AxisVector(orientation.Up);
        return TryCreate(originX, originY, originZ, right.X, right.Y, right.Z, up.X, up.Y, up.Z, out transform);
    }

    public static bool TryCreate(
        double originX,
        double originY,
        double originZ,
        double rightX,
        double rightY,
        double rightZ,
        double upX,
        double upY,
        double upZ,
        out CompositeTransform transform)
    {
        transform = default;
        if (!double.IsFinite(originX) || !double.IsFinite(originY) || !double.IsFinite(originZ) ||
            !double.IsFinite(rightX) || !double.IsFinite(rightY) || !double.IsFinite(rightZ) ||
            !double.IsFinite(upX) || !double.IsFinite(upY) || !double.IsFinite(upZ))
            return false;
        if (!TryOrthonormalize((rightX, rightY, rightZ), (upX, upY, upZ), out var right, out var up))
            return false;
        transform = new CompositeTransform(originX, originY, originZ, right.X, right.Y, right.Z, up.X, up.Y, up.Z);
        return true;
    }

    public static bool TryRestore(
        double originX,
        double originY,
        double originZ,
        double rightX,
        double rightY,
        double rightZ,
        double upX,
        double upY,
        double upZ,
        out CompositeTransform transform)
    {
        transform = default;
        if (!double.IsFinite(originX) || !double.IsFinite(originY) || !double.IsFinite(originZ) ||
            !double.IsFinite(rightX) || !double.IsFinite(rightY) || !double.IsFinite(rightZ) ||
            !double.IsFinite(upX) || !double.IsFinite(upY) || !double.IsFinite(upZ))
            return false;
        if (!IsRigidRotation((rightX, rightY, rightZ), (upX, upY, upZ)))
            return false;
        transform = new CompositeTransform(originX, originY, originZ, rightX, rightY, rightZ, upX, upY, upZ);
        return true;
    }

    private static bool IsRigidRotation(
        (double X, double Y, double Z) right,
        (double X, double Y, double Z) up) =>
        IsNearlyUnit(right) &&
        IsNearlyUnit(up) &&
        Math.Abs(Dot(right, up)) <= RigidRotationTolerance &&
        IsNearlyUnit(Cross(right, up));

    private static bool IsNearlyUnit((double X, double Y, double Z) value)
    {
        var lengthSquared = value.X * value.X + value.Y * value.Y + value.Z * value.Z;
        return double.IsFinite(lengthSquared) && Math.Abs(lengthSquared - 1) <= RigidRotationTolerance;
    }

    private static double Dot(
        (double X, double Y, double Z) left,
        (double X, double Y, double Z) right) =>
        left.X * right.X + left.Y * right.Y + left.Z * right.Z;

    private static bool TryOrthonormalize(
        (double X, double Y, double Z) right,
        (double X, double Y, double Z) up,
        out (double X, double Y, double Z) orthonormalRight,
        out (double X, double Y, double Z) orthonormalUp)
    {
        orthonormalRight = default;
        orthonormalUp = default;
        if (!TryNormalize(right, out var unitRight) || !TryNormalize(up, out var unitUp))
            return false;
        var forward = Cross(unitRight, unitUp);
        if (!TryNormalize(forward, out var unitForward))
            return false;
        orthonormalRight = unitRight;
        orthonormalUp = Cross(unitForward, unitRight);
        return TryNormalize(orthonormalUp, out orthonormalUp);
    }

    private static bool TryNormalize((double X, double Y, double Z) value, out (double X, double Y, double Z) normalized)
    {
        var length = Math.Sqrt(value.X * value.X + value.Y * value.Y + value.Z * value.Z);
        if (length == 0 || !double.IsFinite(length))
        {
            normalized = default;
            return false;
        }

        normalized = (value.X / length, value.Y / length, value.Z / length);
        return true;
    }

    private static (double X, double Y, double Z) Normalize((double X, double Y, double Z) value) =>
        TryNormalize(value, out var normalized)
            ? normalized
            : throw new ArgumentOutOfRangeException(nameof(value), "Rotation axes must be finite and non-zero.");

    private static (double X, double Y, double Z) Cross(
        (double X, double Y, double Z) left,
        (double X, double Y, double Z) right) =>
        (
            left.Y * right.Z - left.Z * right.Y,
            left.Z * right.X - left.X * right.Z,
            left.X * right.Y - left.Y * right.X);
}
