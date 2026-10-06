using System.Collections.ObjectModel;
using Train.Core.Content.Voxels;
using Train.Core.Content;
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
    VoxelDraftExists,
    WorldChanging,
    UnsupportedOperation,
    NotWorldCells,
    RegionTooLarge,
    InvalidRegion
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
    VoxelCellOwnerKind OwnerKind = VoxelCellOwnerKind.None, EntityId OwnerId = default, EntityId PartId = default)
{
    public VoxelCellSetResult ToResult() => new(Status, Position, OwnerKind, OwnerId, PartId);
}

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
