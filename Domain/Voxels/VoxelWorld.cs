using System.Collections.ObjectModel;
using Train.Content;
using Train.Domain.Composites;
using Train.Domain.Rails;

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

public sealed record WorldVoxel(
    VoxelDefinitionId DefinitionId,
    int CurrentDurability,
    VoxelOrientation Orientation,
    EntityId? RuntimeId = null);

public sealed record WorldVoxelQuery(
    WorldVoxelCoord Position,
    WorldVoxel? Voxel,
    long? Value = null,
    double? Mass = null,
    int? MaxDurability = null)
{
    public bool IsOccupied => Voxel is not null;
}

public enum WorldCommandStatus
{
    Added,
    Removed,
    Damaged,
    OutOfBounds,
    Occupied,
    Empty,
    UnknownDefinition,
    DisallowedOrientation,
    InvalidDamage,
    InputExhausted,
    SimulationFaulted,
    WorldChanging,
    HistoryConfirmationRequired
}

public sealed record WorldCommandResult(WorldCommandStatus Status, WorldVoxelCoord Position)
{
    public bool Succeeded => Status is WorldCommandStatus.Added or WorldCommandStatus.Removed or WorldCommandStatus.Damaged;
}

public enum VoxelCellSetStatus
{
    Valid,
    Applied,
    NoTargets,
    DuplicateTarget,
    InvalidTarget,
    OutOfBounds,
    Occupied,
    Empty,
    UnknownDefinition,
    DisallowedOrientation,
    RailOccupied,
    VehicleOccupied,
    NoChange,
    ExpectedContentMismatch,
    InvalidDurability,
    RangeTooLarge,
    InvalidRange
}

public enum VoxelCellOwnerKind
{
    None,
    RailSegment,
    VehiclePart
}

public sealed record VoxelCellSetResult(
    VoxelCellSetStatus Status,
    WorldVoxelCoord Position = default,
    VoxelCellOwnerKind OwnerKind = VoxelCellOwnerKind.None,
    EntityId OwnerId = default,
    EntityId PartId = default)
{
    public bool Succeeded => Status is VoxelCellSetStatus.Valid or VoxelCellSetStatus.Applied;
    public IReadOnlyList<VoxelCellConflict> Conflicts { get; init; } = Array.Empty<VoxelCellConflict>();
}

public sealed record VoxelCellConflict(VoxelCellSetStatus Status, WorldVoxelCoord Position,
    VoxelCellOwnerKind OwnerKind = VoxelCellOwnerKind.None, EntityId OwnerId = default, EntityId PartId = default);

public readonly record struct VoxelDamageTarget(WorldVoxelCoord Position, int Amount);

public sealed record WorldDamageTargetResult(
    WorldCommandStatus Status,
    WorldVoxelCoord Position,
    int Amount,
    int RemainingDurability);

public sealed record WorldDamageResult(
    WorldCommandStatus Status,
    WorldVoxelCoord Position,
    IReadOnlyList<WorldDamageTargetResult> Targets)
{
    public bool Succeeded => Status is WorldCommandStatus.Damaged or WorldCommandStatus.Removed;
}

public sealed class VoxelWorld
{
    private readonly Dictionary<WorldVoxelCoord, WorldVoxel> _voxels = new();

    public VoxelWorldIdentity Identity { get; }
    public VoxelWorldBounds Bounds => Identity.Bounds;
    public int ChunkSize => Identity.ChunkSize;
    public int VoxelCount => _voxels.Count;

    public VoxelWorld(VoxelWorldBounds bounds)
        : this(VoxelWorldIdentity.FromBounds(bounds))
    {
    }

    public VoxelWorld(VoxelWorldIdentity identity)
    {
        Identity = identity;
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

    internal bool TryReplace(WorldVoxelCoord position, WorldVoxel voxel)
    {
        if (!_voxels.ContainsKey(position))
            return false;
        _voxels[position] = voxel;
        return true;
    }

    internal void ReplaceOccupied(IReadOnlyList<(WorldVoxelCoord Position, WorldVoxel Voxel)> replacements)
    {
        ArgumentNullException.ThrowIfNull(replacements);
        foreach (var (position, voxel) in replacements)
            _voxels[position] = voxel;
    }

    internal KeyValuePair<WorldVoxelCoord, WorldVoxel>[] CaptureVoxels() => _voxels.ToArray();

    internal void RestoreVoxels(IReadOnlyList<KeyValuePair<WorldVoxelCoord, WorldVoxel>> voxels)
    {
        ArgumentNullException.ThrowIfNull(voxels);
        _voxels.Clear();
        foreach (var (position, voxel) in voxels)
            _voxels.Add(position, voxel);
    }
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

    public WorldVoxelQuery Query(WorldVoxelCoord position)
    {
        var query = _world.Query(position);
        if (query.Voxel is null || _catalog.Current?.TryGet(query.Voxel.DefinitionId, out var entry) != true)
            return query;
        return query with
        {
            Value = entry.Definition.Value,
            Mass = entry.Definition.Mass,
            MaxDurability = entry.Definition.MaxDurability
        };
    }

    public VoxelCollisionQueryResult QueryCollision(WorldVoxelCoord cell)
    {
        var probe = VoxelCollisionBox.ForCell(cell);
        if (!_world.Bounds.Contains(cell))
            return VoxelCollisionQueryResult.OutOfBounds(probe);
        return QueryCollision(probe);
    }

    public VoxelCollisionQueryResult QueryCollision(
        double minX,
        double minY,
        double minZ,
        double maxX,
        double maxY,
        double maxZ)
    {
        if (!VoxelCollisionBox.TryCreate(minX, minY, minZ, maxX, maxY, maxZ, out var probe))
            return VoxelCollisionQueryResult.Invalid();
        return QueryCollision(probe);
    }

    public VoxelCollisionQueryResult QueryCollision(VoxelCollisionBox probe)
    {
        if (!probe.IsValid)
            return VoxelCollisionQueryResult.Invalid();
        if (!probe.Overlaps(VoxelCollisionBox.ForWorld(_world.Bounds)))
            return VoxelCollisionQueryResult.OutOfBounds(probe);

        var hits = new List<VoxelCollisionHit>();
        foreach (var (position, voxel) in _world.OccupiedVoxels())
        {
            if (!VoxelCollisionBox.ForCell(position).Overlaps(probe))
                continue;
            if (_catalog.Current?.TryGet(voxel.DefinitionId, out var entry) != true)
                continue;
            if (!entry.Definition.Collision.OverlapsProbe(position, voxel.Orientation, probe))
                continue;
            hits.Add(new VoxelCollisionHit(position, voxel.DefinitionId, entry.Definition.Collision.Kind));
        }

        hits.Sort(CompareHits);
        return hits.Count == 0
            ? VoxelCollisionQueryResult.Miss(probe)
            : VoxelCollisionQueryResult.Hit(probe, hits);
    }

    private static int CompareHits(VoxelCollisionHit left, VoxelCollisionHit right)
    {
        var x = left.Cell.X.CompareTo(right.Cell.X);
        if (x != 0)
            return x;
        var y = left.Cell.Y.CompareTo(right.Cell.Y);
        return y != 0 ? y : left.Cell.Z.CompareTo(right.Cell.Z);
    }

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

    public WorldDamageResult Damage(WorldVoxelCoord position, int amount) =>
        Damage(new[] { new VoxelDamageTarget(position, amount) });

    public WorldDamageResult Damage(IReadOnlyList<VoxelDamageTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        if (targets.Count == 0)
            return new(WorldCommandStatus.InvalidDamage, default, Array.Empty<WorldDamageTargetResult>());

        foreach (var target in targets)
        {
            if (target.Amount <= 0)
                return new(WorldCommandStatus.InvalidDamage, target.Position, Array.Empty<WorldDamageTargetResult>());
            if (!_world.Bounds.Contains(target.Position))
                return new(WorldCommandStatus.OutOfBounds, target.Position, Array.Empty<WorldDamageTargetResult>());
            if (!_world.Query(target.Position).IsOccupied)
                return new(WorldCommandStatus.Empty, target.Position, Array.Empty<WorldDamageTargetResult>());
        }

        var totals = new Dictionary<WorldVoxelCoord, long>();
        foreach (var target in targets)
            totals[target.Position] = totals.GetValueOrDefault(target.Position) + target.Amount;

        foreach (var (position, amount) in totals
            .OrderBy(pair => pair.Key.X)
            .ThenBy(pair => pair.Key.Y)
            .ThenBy(pair => pair.Key.Z))
        {
            var voxel = _world.Query(position).Voxel!;
            var remaining = voxel.CurrentDurability - amount;
            if (remaining > 0)
                _world.TryReplace(position, voxel with { CurrentDurability = (int)remaining });
            else
                _world.TryRemove(position);
        }

        var outcomes = new WorldDamageTargetResult[targets.Count];
        var overall = WorldCommandStatus.Removed;
        for (var index = 0; index < targets.Count; index++)
        {
            var target = targets[index];
            var query = _world.Query(target.Position);
            var status = query.IsOccupied ? WorldCommandStatus.Damaged : WorldCommandStatus.Removed;
            if (status == WorldCommandStatus.Damaged)
                overall = WorldCommandStatus.Damaged;
            outcomes[index] = new(status, target.Position, target.Amount, query.Voxel?.CurrentDurability ?? 0);
        }

        return new(overall, targets[0].Position, outcomes);
    }
}

public static class VoxelWorldFactory
{
    public static readonly VoxelWorldBounds DevelopmentBounds = VoxelWorldIdentity.Slice1.Bounds;
    public static readonly VoxelDefinitionId DevelopmentDefinitionId = new("core", "ferrous-ore");

    public static VoxelWorld CreateDevelopmentWorld(VoxelCatalogSnapshot catalog) =>
        Create(catalog, VoxelWorldIdentity.Slice1);

    public static VoxelWorld Create(VoxelCatalogSnapshot catalog, VoxelWorldIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (identity.GeneratorVersion == BlankWorldSource.GeneratorVersion)
        {
            if (!BlankWorldSource.TryIdentity(identity.Bounds.SizeX, identity.Bounds.SizeY, identity.Bounds.SizeZ, identity.Seed, out var supported) ||
                identity != supported)
                throw new InvalidOperationException("Unsupported blank world identity.");
            return new VoxelWorld(identity);
        }
        if (!catalog.TryGet(DevelopmentDefinitionId, out var definition))
            throw new InvalidOperationException("The development world requires core:ferrous-ore.");

        var world = new VoxelWorld(identity);
        var voxel = new WorldVoxel(DevelopmentDefinitionId, definition.Definition.MaxDurability, VoxelOrientation.Identity);
        if (identity.GeneratorVersion == VoxelWorldIdentity.Slice3GeneratorVersion ||
            identity.GeneratorVersion == VoxelWorldIdentity.Slice4GeneratorVersion)
            return world;
        if (identity.GeneratorVersion == VoxelWorldIdentity.Slice2GeneratorVersion)
            FillSlice2(world, voxel);
        else if (identity.GeneratorVersion == VoxelWorldIdentity.Slice1GeneratorVersion ||
                 identity.GeneratorVersion == "unspecified")
            FillSlice1(world, voxel);
        else
            throw new InvalidOperationException($"Unknown generator version '{identity.GeneratorVersion}'.");

        return world;
    }

    private static void FillSlice1(VoxelWorld world, WorldVoxel voxel)
    {
        for (var x = 4; x <= 11; x++)
        for (var y = 4; y <= 11; y++)
        for (var z = 4; z <= 11; z++)
        {
            var position = new WorldVoxelCoord(x, y, z);
            if (world.Bounds.Contains(position))
                world.TryAdd(position, voxel);
        }
    }

    private static void FillSlice2(VoxelWorld world, WorldVoxel voxel)
    {
        for (var x = 0; x < world.Bounds.SizeX; x++)
        for (var z = 0; z < world.Bounds.SizeZ; z++)
        {
            var ground = new WorldVoxelCoord(x, 0, z);
            if (world.Bounds.Contains(ground))
                world.TryAdd(ground, voxel);
        }

        for (var x = 14; x <= 17; x++)
        for (var y = 14; y <= 17; y++)
        for (var z = 14; z <= 17; z++)
        {
            var position = new WorldVoxelCoord(x, y, z);
            if (world.Bounds.Contains(position))
                world.TryAdd(position, voxel);
        }
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

    public static VoxelSurfaceMesh Build(VoxelWorld world, ChunkCoord? chunk = null)
    {
        ArgumentNullException.ThrowIfNull(world);
        var faces = new List<WorldVoxelFace>();
        foreach (var (position, voxel) in world.OccupiedVoxels())
        {
            if (chunk is { } selected && VoxelChunks.Of(position, world.ChunkSize) != selected)
                continue;
            if (voxel.DefinitionId == StraightRailFixture.RailDefinitionId)
                continue;
            foreach (var (normal, x, y, z) in Neighbors)
            {
                if (!world.Query(new WorldVoxelCoord(position.X + x, position.Y + y, position.Z + z)).IsOccupied)
                    faces.Add(new WorldVoxelFace(position, normal, voxel));
            }
        }

        return new VoxelSurfaceMesh(new ReadOnlyCollection<WorldVoxelFace>(faces));
    }
}
