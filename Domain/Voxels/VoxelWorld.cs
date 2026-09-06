using System.Collections.ObjectModel;
using Train.Content;

namespace Train.Domain.Voxels;

public readonly record struct WorldVoxelCoord(int X, int Y, int Z);

public readonly record struct VoxelWorldBounds
{
    public int SizeX { get; }
    public int SizeY { get; }
    public int SizeZ { get; }

    public VoxelWorldBounds(int sizeX, int sizeY, int sizeZ)
    {
        if (sizeX <= 0 || sizeY <= 0 || sizeZ <= 0)
            throw new ArgumentOutOfRangeException(nameof(sizeX), "World dimensions must be positive.");
        SizeX = sizeX;
        SizeY = sizeY;
        SizeZ = sizeZ;
    }

    public bool Contains(WorldVoxelCoord position) =>
        position.X >= 0 && position.X < SizeX &&
        position.Y >= 0 && position.Y < SizeY &&
        position.Z >= 0 && position.Z < SizeZ;
}

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

    public bool IsAllowedBy(VoxelOrientationConstraint constraint) => constraint switch
    {
        VoxelOrientationConstraint.None => this == Identity,
        VoxelOrientationConstraint.FourWay => FourWay.Contains(this),
        VoxelOrientationConstraint.All24 => All.Contains(this),
        _ => false
    };

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

public sealed record WorldVoxel(VoxelDefinitionId DefinitionId, int CurrentDurability, VoxelOrientation Orientation);

public sealed record WorldVoxelQuery(WorldVoxelCoord Position, WorldVoxel? Voxel)
{
    public bool IsOccupied => Voxel is not null;
}

public enum WorldCommandStatus
{
    Added,
    Removed,
    OutOfBounds,
    Occupied,
    Empty,
    UnknownDefinition,
    DisallowedOrientation
}

public sealed record WorldCommandResult(WorldCommandStatus Status, WorldVoxelCoord Position)
{
    public bool Succeeded => Status is WorldCommandStatus.Added or WorldCommandStatus.Removed;
}

public sealed class VoxelWorld
{
    private readonly Dictionary<WorldVoxelCoord, WorldVoxel> _voxels = new();

    public VoxelWorldBounds Bounds { get; }
    public int VoxelCount => _voxels.Count;

    public VoxelWorld(VoxelWorldBounds bounds)
    {
        Bounds = bounds;
    }

    public WorldVoxelQuery Query(WorldVoxelCoord position) =>
        new(position, _voxels.GetValueOrDefault(position));

    public IEnumerable<KeyValuePair<WorldVoxelCoord, WorldVoxel>> OccupiedVoxels()
    {
        foreach (var voxel in _voxels)
            yield return voxel;
    }

    internal bool TryAdd(WorldVoxelCoord position, WorldVoxel voxel)
    {
        if (_voxels.ContainsKey(position))
            return false;
        _voxels.Add(position, voxel);
        return true;
    }

    internal bool TryRemove(WorldVoxelCoord position) => _voxels.Remove(position);
}

public sealed class VoxelWorldCommands
{
    private readonly VoxelWorld _world;
    private readonly VoxelCatalog _catalog;

    public VoxelWorldCommands(VoxelWorld world, VoxelCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(catalog);
        _world = world;
        _catalog = catalog;
    }

    public WorldVoxelQuery Query(WorldVoxelCoord position) => _world.Query(position);

    public WorldCommandResult Add(WorldVoxelCoord position, VoxelDefinitionId definitionId, VoxelOrientation orientation)
    {
        if (!_world.Bounds.Contains(position))
            return new(WorldCommandStatus.OutOfBounds, position);
        if (_world.Query(position).IsOccupied)
            return new(WorldCommandStatus.Occupied, position);
        if (_catalog.Current?.TryGet(definitionId, out var entry) != true)
            return new(WorldCommandStatus.UnknownDefinition, position);
        if (!orientation.IsOrthogonal || !orientation.IsAllowedBy(entry.Definition.AllowedOrientations))
            return new(WorldCommandStatus.DisallowedOrientation, position);

        return _world.TryAdd(position, new WorldVoxel(definitionId, entry.Definition.MaxDurability, orientation))
            ? new(WorldCommandStatus.Added, position)
            : new(WorldCommandStatus.Occupied, position);
    }

    public WorldCommandResult Remove(WorldVoxelCoord position)
    {
        if (!_world.Bounds.Contains(position))
            return new(WorldCommandStatus.OutOfBounds, position);
        return _world.TryRemove(position)
            ? new(WorldCommandStatus.Removed, position)
            : new(WorldCommandStatus.Empty, position);
    }
}

public static class VoxelWorldFactory
{
    public static readonly VoxelWorldBounds DevelopmentBounds = new(16, 16, 16);
    public static readonly VoxelDefinitionId DevelopmentDefinitionId = new("core", "iron-ore");

    public static VoxelWorld CreateDevelopmentWorld(VoxelCatalogSnapshot catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (!catalog.TryGet(DevelopmentDefinitionId, out var definition))
            throw new InvalidOperationException("The development world requires core:iron-ore.");

        var world = new VoxelWorld(DevelopmentBounds);
        for (var x = 4; x <= 11; x++)
        for (var y = 4; y <= 11; y++)
        for (var z = 4; z <= 11; z++)
            world.TryAdd(new WorldVoxelCoord(x, y, z), new WorldVoxel(DevelopmentDefinitionId, definition.Definition.MaxDurability, VoxelOrientation.Identity));
        return world;
    }
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

public readonly record struct WorldVoxelFace(WorldVoxelCoord Position, VoxelFaceNormal Normal, WorldVoxel Voxel);

public sealed class VoxelSurfaceMesh
{
    public IReadOnlyList<WorldVoxelFace> Faces { get; }
    public int FaceCount => Faces.Count;

    public VoxelSurfaceMesh(IReadOnlyList<WorldVoxelFace> faces)
    {
        Faces = faces;
    }
}

public static class VoxelSurfaceMesher
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

    public static VoxelSurfaceMesh Build(VoxelWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);
        var faces = new List<WorldVoxelFace>();
        foreach (var (position, voxel) in world.OccupiedVoxels())
        {
            foreach (var (normal, x, y, z) in Neighbors)
            {
                if (!world.Query(new WorldVoxelCoord(position.X + x, position.Y + y, position.Z + z)).IsOccupied)
                    faces.Add(new WorldVoxelFace(position, normal, voxel));
            }
        }

        return new VoxelSurfaceMesh(new ReadOnlyCollection<WorldVoxelFace>(faces));
    }
}
