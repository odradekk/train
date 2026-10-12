using System.Collections.ObjectModel;

namespace Train.Content.Voxels;

public enum VoxelAxis
{
    PositiveX,
    NegativeX,
    PositiveY,
    NegativeY,
    PositiveZ,
    NegativeZ
}

public readonly record struct VoxelOrientation
{
    private static readonly VoxelAxis[] Axes = Enum.GetValues<VoxelAxis>();
    private static readonly IReadOnlyList<VoxelOrientation> AllOrientations = BuildAll();
    private static readonly IReadOnlyList<VoxelOrientation> FourWayOrientations = Array.AsReadOnly(new[]
    {
        new VoxelOrientation(VoxelAxis.PositiveX, VoxelAxis.PositiveY),
        new VoxelOrientation(VoxelAxis.PositiveZ, VoxelAxis.PositiveY),
        new VoxelOrientation(VoxelAxis.NegativeX, VoxelAxis.PositiveY),
        new VoxelOrientation(VoxelAxis.NegativeZ, VoxelAxis.PositiveY)
    });

    public VoxelAxis Right { get; }
    public VoxelAxis Up { get; }
    public VoxelAxis Forward { get; }
    public bool IsOrthogonal => ArePerpendicular(Right, Up) && Forward == Cross(Right, Up);

    public static VoxelOrientation Identity { get; } = new(VoxelAxis.PositiveX, VoxelAxis.PositiveY);
    public static VoxelOrientation Yaw90 { get; } = new(VoxelAxis.PositiveZ, VoxelAxis.PositiveY);
    public static IReadOnlyList<VoxelOrientation> All => AllOrientations;
    public static IReadOnlyList<VoxelOrientation> FourWay => FourWayOrientations;

    public VoxelOrientation(VoxelAxis right, VoxelAxis up)
    {
        if (!ArePerpendicular(right, up))
            throw new ArgumentException("Right and up axes must be perpendicular.");
        Right = right;
        Up = up;
        Forward = Cross(right, up);
    }

    public VoxelAxis Transform(VoxelAxis localAxis) => localAxis switch
    {
        VoxelAxis.PositiveX => Right,
        VoxelAxis.NegativeX => Opposite(Right),
        VoxelAxis.PositiveY => Up,
        VoxelAxis.NegativeY => Opposite(Up),
        VoxelAxis.PositiveZ => Forward,
        VoxelAxis.NegativeZ => Opposite(Forward),
        _ => throw new ArgumentOutOfRangeException(nameof(localAxis), localAxis, "Unknown voxel axis.")
    };

    public VoxelAxis InverseTransform(VoxelAxis worldAxis)
    {
        foreach (var local in Axes)
        {
            if (Transform(local) == worldAxis)
                return local;
        }

        throw new ArgumentOutOfRangeException(nameof(worldAxis), worldAxis, "World axis is not produced by this orientation.");
    }

    public (double X, double Y, double Z) ToWorldCell(double localX, double localY, double localZ)
    {
        if (!IsFinite(localX) || !IsFinite(localY) || !IsFinite(localZ))
            throw new ArgumentOutOfRangeException(nameof(localX), "Local coordinates must be finite.");
        return (
            AlongWorld(VoxelAxis.PositiveX, localX, localY, localZ),
            AlongWorld(VoxelAxis.PositiveY, localX, localY, localZ),
            AlongWorld(VoxelAxis.PositiveZ, localX, localY, localZ));
    }

    public (double X, double Y, double Z) ToLocalCell(double worldX, double worldY, double worldZ)
    {
        if (!IsFinite(worldX) || !IsFinite(worldY) || !IsFinite(worldZ))
            throw new ArgumentOutOfRangeException(nameof(worldX), "World-relative coordinates must be finite.");
        return (
            AlongLocal(VoxelAxis.PositiveX, worldX, worldY, worldZ),
            AlongLocal(VoxelAxis.PositiveY, worldX, worldY, worldZ),
            AlongLocal(VoxelAxis.PositiveZ, worldX, worldY, worldZ));
    }

    public override string ToString() => $"{ToWire(Right)}_{ToWire(Up)}";

    public static bool TryParse(string? value, out VoxelOrientation orientation)
    {
        foreach (var candidate in All)
        {
            if (string.Equals(candidate.ToString(), value, StringComparison.OrdinalIgnoreCase))
            {
                orientation = candidate;
                return true;
            }
        }

        orientation = default;
        return false;
    }

    private static IReadOnlyList<VoxelOrientation> BuildAll()
    {
        var orientations = new List<VoxelOrientation>(24);
        foreach (var right in Axes)
        {
            foreach (var up in Axes)
            {
                if (ArePerpendicular(right, up))
                    orientations.Add(new VoxelOrientation(right, up));
            }
        }

        return new ReadOnlyCollection<VoxelOrientation>(orientations);
    }

    private static bool ArePerpendicular(VoxelAxis first, VoxelAxis second) =>
        AxisDimension(first) != AxisDimension(second);

    private static int AxisDimension(VoxelAxis axis) => axis switch
    {
        VoxelAxis.PositiveX or VoxelAxis.NegativeX => 0,
        VoxelAxis.PositiveY or VoxelAxis.NegativeY => 1,
        VoxelAxis.PositiveZ or VoxelAxis.NegativeZ => 2,
        _ => throw new ArgumentOutOfRangeException(nameof(axis), axis, "Unknown voxel axis.")
    };

    private static VoxelAxis Cross(VoxelAxis first, VoxelAxis second)
    {
        var (x1, y1, z1) = Components(first);
        var (x2, y2, z2) = Components(second);
        return FromComponents(y1 * z2 - z1 * y2, z1 * x2 - x1 * z2, x1 * y2 - y1 * x2);
    }

    private static (int X, int Y, int Z) Components(VoxelAxis axis) => axis switch
    {
        VoxelAxis.PositiveX => (1, 0, 0),
        VoxelAxis.NegativeX => (-1, 0, 0),
        VoxelAxis.PositiveY => (0, 1, 0),
        VoxelAxis.NegativeY => (0, -1, 0),
        VoxelAxis.PositiveZ => (0, 0, 1),
        VoxelAxis.NegativeZ => (0, 0, -1),
        _ => throw new ArgumentOutOfRangeException(nameof(axis), axis, "Unknown voxel axis.")
    };

    private double AlongWorld(VoxelAxis world, double localX, double localY, double localZ)
    {
        if (Right == world)
            return localX;
        if (Opposite(Right) == world)
            return 1 - localX;
        if (Up == world)
            return localY;
        if (Opposite(Up) == world)
            return 1 - localY;
        if (Forward == world)
            return localZ;
        if (Opposite(Forward) == world)
            return 1 - localZ;
        throw new ArgumentOutOfRangeException(nameof(world), world, "World axis is not produced by this orientation.");
    }

    private double AlongLocal(VoxelAxis local, double worldX, double worldY, double worldZ)
    {
        var world = Transform(local);
        var value = world switch
        {
            VoxelAxis.PositiveX or VoxelAxis.NegativeX => worldX,
            VoxelAxis.PositiveY or VoxelAxis.NegativeY => worldY,
            VoxelAxis.PositiveZ or VoxelAxis.NegativeZ => worldZ,
            _ => throw new ArgumentOutOfRangeException(nameof(local), local, "Unknown voxel axis.")
        };
        return world is VoxelAxis.NegativeX or VoxelAxis.NegativeY or VoxelAxis.NegativeZ
            ? 1 - value
            : value;
    }

    private static bool IsFinite(double value) =>
        !double.IsNaN(value) && !double.IsInfinity(value);

    private static VoxelAxis Opposite(VoxelAxis axis) => axis switch
    {
        VoxelAxis.PositiveX => VoxelAxis.NegativeX,
        VoxelAxis.NegativeX => VoxelAxis.PositiveX,
        VoxelAxis.PositiveY => VoxelAxis.NegativeY,
        VoxelAxis.NegativeY => VoxelAxis.PositiveY,
        VoxelAxis.PositiveZ => VoxelAxis.NegativeZ,
        VoxelAxis.NegativeZ => VoxelAxis.PositiveZ,
        _ => throw new ArgumentOutOfRangeException(nameof(axis), axis, "Unknown voxel axis.")
    };

    private static VoxelAxis FromComponents(int x, int y, int z) => (x, y, z) switch
    {
        (1, 0, 0) => VoxelAxis.PositiveX,
        (-1, 0, 0) => VoxelAxis.NegativeX,
        (0, 1, 0) => VoxelAxis.PositiveY,
        (0, -1, 0) => VoxelAxis.NegativeY,
        (0, 0, 1) => VoxelAxis.PositiveZ,
        (0, 0, -1) => VoxelAxis.NegativeZ,
        _ => throw new ArgumentException("The axes are not orthogonal.")
    };

    private static string ToWire(VoxelAxis axis) => axis switch
    {
        VoxelAxis.PositiveX => "px",
        VoxelAxis.NegativeX => "nx",
        VoxelAxis.PositiveY => "py",
        VoxelAxis.NegativeY => "ny",
        VoxelAxis.PositiveZ => "pz",
        VoxelAxis.NegativeZ => "nz",
        _ => throw new ArgumentOutOfRangeException(nameof(axis), axis, "Unknown voxel axis.")
    };
}

public enum VoxelFaceNormal
{
    PositiveX,
    NegativeX,
    PositiveY,
    NegativeY,
    PositiveZ,
    NegativeZ
}
