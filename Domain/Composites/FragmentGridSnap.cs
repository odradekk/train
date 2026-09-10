using Train.Domain.Voxels;

namespace Train.Domain.Composites;

public static class FragmentGridSnap
{
    public const int SearchRadius = 1;

    private static readonly VoxelAxis[] OrderedAxes =
    {
        VoxelAxis.PositiveX,
        VoxelAxis.PositiveY,
        VoxelAxis.PositiveZ,
        VoxelAxis.NegativeX,
        VoxelAxis.NegativeY,
        VoxelAxis.NegativeZ
    };

    public static IReadOnlyList<VoxelOrientation> OrderedOrientations { get; } = BuildOrderedOrientations();

    public static IReadOnlyList<WorldVoxelCoord> SearchOffsets { get; } = BuildSearchOffsets();

    public static int SnapCoordinate(double value)
    {
        if (!double.IsFinite(value))
            throw new ArgumentOutOfRangeException(nameof(value), "Snap coordinates must be finite.");

        var floor = Math.Floor(value);
        var fraction = value - floor;
        if (fraction < 0.5)
            return (int)floor;
        return (int)(floor + 1);
    }

    public static WorldVoxelCoord SnapAnchor(double x, double y, double z) =>
        new(SnapCoordinate(x), SnapCoordinate(y), SnapCoordinate(z));

    public static VoxelOrientation SnapRotation(
        (double X, double Y, double Z) localPlusX,
        (double X, double Y, double Z) localPlusY)
    {
        var right = Normalize(localPlusX);
        var up = Normalize(localPlusY);
        var forward = Normalize(Cross(right, up));
        up = Cross(forward, right);

        var best = OrderedOrientations[0];
        var bestAngle = double.PositiveInfinity;
        foreach (var candidate in OrderedOrientations)
        {
            var angle = RotationAngle(right, up, forward, candidate);
            if (angle + 1e-12 < bestAngle)
            {
                best = candidate;
                bestAngle = angle;
            }
        }

        return best;
    }

    public static VoxelOrientation SnapRotation(VoxelOrientation orientation) =>
        SnapRotation(AxisVector(orientation.Right), AxisVector(orientation.Up));

    public static WorldVoxelCoord TransformCell(
        VoxelOrientation orientation,
        LocalVoxelCoord cell,
        WorldVoxelCoord anchor)
    {
        var minX = int.MaxValue;
        var minY = int.MaxValue;
        var minZ = int.MaxValue;
        for (var dx = 0; dx <= 1; dx++)
        for (var dy = 0; dy <= 1; dy++)
        for (var dz = 0; dz <= 1; dz++)
        {
            var world = RotateOffset(orientation, cell.X + dx, cell.Y + dy, cell.Z + dz);
            minX = Math.Min(minX, world.X);
            minY = Math.Min(minY, world.Y);
            minZ = Math.Min(minZ, world.Z);
        }

        return new WorldVoxelCoord(anchor.X + minX, anchor.Y + minY, anchor.Z + minZ);
    }

    public static VoxelOrientation Compose(VoxelOrientation outer, VoxelOrientation inner) =>
        new(outer.Transform(inner.Right), outer.Transform(inner.Up));

    public static (double X, double Y, double Z) AxisVector(VoxelAxis axis) => axis switch
    {
        VoxelAxis.PositiveX => (1, 0, 0),
        VoxelAxis.NegativeX => (-1, 0, 0),
        VoxelAxis.PositiveY => (0, 1, 0),
        VoxelAxis.NegativeY => (0, -1, 0),
        VoxelAxis.PositiveZ => (0, 0, 1),
        VoxelAxis.NegativeZ => (0, 0, -1),
        _ => throw new ArgumentOutOfRangeException(nameof(axis), axis, "Unknown voxel axis.")
    };

    public static IReadOnlyList<WorldVoxelCoord> TargetCells(
        CompositeTransform transform,
        IReadOnlyList<LocalVoxelCoord> cells,
        WorldVoxelCoord offset)
    {
        ArgumentNullException.ThrowIfNull(cells);
        var orientation = SnapRotation(transform.Orientation);
        var anchor = SnapAnchor(transform.OriginX, transform.OriginY, transform.OriginZ);
        var shifted = new WorldVoxelCoord(anchor.X + offset.X, anchor.Y + offset.Y, anchor.Z + offset.Z);
        return cells.Select(cell => TransformCell(orientation, cell, shifted)).ToArray();
    }

    private static IReadOnlyList<VoxelOrientation> BuildOrderedOrientations()
    {
        var orientations = new List<VoxelOrientation>(24);
        foreach (var right in OrderedAxes)
        {
            foreach (var up in OrderedAxes)
            {
                if (AxisDimension(right) == AxisDimension(up))
                    continue;
                orientations.Add(new VoxelOrientation(right, up));
            }
        }

        return orientations;
    }

    private static IReadOnlyList<WorldVoxelCoord> BuildSearchOffsets()
    {
        var offsets = new List<WorldVoxelCoord>();
        for (var x = -SearchRadius; x <= SearchRadius; x++)
        for (var y = -SearchRadius; y <= SearchRadius; y++)
        for (var z = -SearchRadius; z <= SearchRadius; z++)
        {
            if (Math.Abs(x) + Math.Abs(y) + Math.Abs(z) <= SearchRadius)
                offsets.Add(new WorldVoxelCoord(x, y, z));
        }

        return offsets
            .OrderBy(offset => Math.Abs(offset.X) + Math.Abs(offset.Y) + Math.Abs(offset.Z))
            .ThenBy(offset => offset.X)
            .ThenBy(offset => offset.Y)
            .ThenBy(offset => offset.Z)
            .ToArray();
    }

    private static int AxisDimension(VoxelAxis axis) => axis switch
    {
        VoxelAxis.PositiveX or VoxelAxis.NegativeX => 0,
        VoxelAxis.PositiveY or VoxelAxis.NegativeY => 1,
        VoxelAxis.PositiveZ or VoxelAxis.NegativeZ => 2,
        _ => throw new ArgumentOutOfRangeException(nameof(axis), axis, "Unknown voxel axis.")
    };

    private static double RotationAngle(
        (double X, double Y, double Z) right,
        (double X, double Y, double Z) up,
        (double X, double Y, double Z) forward,
        VoxelOrientation candidate)
    {
        var trace =
            Dot(right, AxisVector(candidate.Right)) +
            Dot(up, AxisVector(candidate.Up)) +
            Dot(forward, AxisVector(candidate.Forward));
        var cosine = Math.Clamp((trace - 1.0) / 2.0, -1.0, 1.0);
        return Math.Acos(cosine);
    }

    private static (double X, double Y, double Z) Normalize((double X, double Y, double Z) value)
    {
        var length = Math.Sqrt(value.X * value.X + value.Y * value.Y + value.Z * value.Z);
        if (length == 0 || !double.IsFinite(length))
            throw new ArgumentOutOfRangeException(nameof(value), "Rotation axes must be finite and non-zero.");
        return (value.X / length, value.Y / length, value.Z / length);
    }

    private static (double X, double Y, double Z) Cross(
        (double X, double Y, double Z) left,
        (double X, double Y, double Z) right) =>
        (
            left.Y * right.Z - left.Z * right.Y,
            left.Z * right.X - left.X * right.Z,
            left.X * right.Y - left.Y * right.X);

    private static double Dot(
        (double X, double Y, double Z) left,
        (double X, double Y, double Z) right) =>
        left.X * right.X + left.Y * right.Y + left.Z * right.Z;

    private static (int X, int Y, int Z) RotateOffset(VoxelOrientation orientation, int x, int y, int z)
    {
        var right = AxisInt(orientation.Right);
        var up = AxisInt(orientation.Up);
        var forward = AxisInt(orientation.Forward);
        return (
            x * right.X + y * up.X + z * forward.X,
            x * right.Y + y * up.Y + z * forward.Y,
            x * right.Z + y * up.Z + z * forward.Z);
    }

    private static (int X, int Y, int Z) AxisInt(VoxelAxis axis)
    {
        var vector = AxisVector(axis);
        return ((int)vector.X, (int)vector.Y, (int)vector.Z);
    }
}
