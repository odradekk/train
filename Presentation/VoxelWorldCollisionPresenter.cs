using Godot;
using Train.Content;
using Train.Domain.Voxels;

namespace Train.Presentation;

public sealed class VoxelWorldCollisionPresenter
{
    public const uint VoxelCollisionLayer = 1;

    private readonly Dictionary<ChunkCoord, VoxelChunkCollider> _colliders = new();
    private Node? _parent;
    private VoxelWorldIdentity _identity;

    public IReadOnlyList<ChunkCoord> LastRebuiltChunks { get; private set; } = Array.Empty<ChunkCoord>();
    public int ShapeCount => _colliders.Values.Sum(collider => collider.ShapeCount);
    public int LastWorldVoxelCount { get; private set; }
    public VoxelChunkCollider PrimaryCollider => ColliderFor(new ChunkCoord(0, 0, 0));
    public IEnumerable<VoxelChunkCollider> Colliders => _colliders.Values;

    public VoxelChunkCollider ColliderFor(ChunkCoord chunk) =>
        _colliders.TryGetValue(chunk, out var collider)
            ? collider
            : throw new InvalidOperationException($"No collider for chunk {chunk}.");

    public void Rebuild(Node parent, VoxelWorldIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(parent);
        _parent = parent;
        _identity = identity;
        foreach (var collider in _colliders.Values)
            collider.QueueFree();
        _colliders.Clear();
        foreach (var chunk in VoxelChunks.All(identity))
        {
            var collider = new VoxelChunkCollider();
            parent.AddChild(collider);
            _colliders[chunk] = collider;
        }
    }

    public void Refresh(VoxelDevelopmentSession session, VoxelCatalogSnapshot? catalog)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (_identity != session.Identity)
            Rebuild(_parent ?? throw new InvalidOperationException("The presenter must be attached before refreshing."), session.Identity);
        var dirty = session.TakePendingCollisionDirtyChunks();
        foreach (var chunk in dirty)
            ColliderFor(chunk).Refresh(session.World, chunk, catalog);
        LastRebuiltChunks = dirty;
        LastWorldVoxelCount = session.World.VoxelCount;
    }

    public IReadOnlyList<WorldVoxelCoord> QueryPublishedHits(World3D world, VoxelCollisionBox probe)
    {
        ArgumentNullException.ThrowIfNull(world);
        var space = world.DirectSpaceState;
        var size = new Vector3(
            (float)(probe.MaxX - probe.MinX),
            (float)(probe.MaxY - probe.MinY),
            (float)(probe.MaxZ - probe.MinZ));
        using var shape = new BoxShape3D
        {
            Size = size,
            Margin = 0
        };
        var query = new PhysicsShapeQueryParameters3D
        {
            Shape = shape,
            Transform = new Transform3D(
                Basis.Identity,
                new Vector3(
                    (float)((probe.MinX + probe.MaxX) * 0.5),
                    (float)((probe.MinY + probe.MaxY) * 0.5),
                    (float)((probe.MinZ + probe.MaxZ) * 0.5))),
            CollisionMask = VoxelCollisionLayer,
            CollideWithBodies = true,
            CollideWithAreas = false
        };

        var hits = new HashSet<WorldVoxelCoord>();
        foreach (var result in space.IntersectShape(query, maxResults: 256))
        {
            if (TryReadCell(result, out var cell))
                hits.Add(cell);
        }

        return hits.OrderBy(cell => cell.X).ThenBy(cell => cell.Y).ThenBy(cell => cell.Z).ToArray();
    }

    /// <summary>The first published world-voxel collider a segment hits, with the cell that shape was built for.</summary>
    public (WorldVoxelCoord Cell, Vector3 Position)? QueryPublishedRay(World3D world, Vector3 from, Vector3 to)
    {
        ArgumentNullException.ThrowIfNull(world);
        var query = PhysicsRayQueryParameters3D.Create(from, to, VoxelCollisionLayer);
        query.CollideWithBodies = true;
        query.CollideWithAreas = false;
        var result = world.DirectSpaceState.IntersectRay(query);
        if (result.Count == 0 || !TryReadCell(result, out var cell))
            return null;
        return (cell, result["position"].AsVector3());
    }

    public bool QueryPublishedCellInterior(World3D world, WorldVoxelCoord cell) =>
        QueryPublishedHits(world, CellInterior(cell)).Contains(cell);

    public static VoxelCollisionBox CellInterior(WorldVoxelCoord cell) =>
        new(cell.X + 0.25, cell.Y + 0.25, cell.Z + 0.25, cell.X + 0.75, cell.Y + 0.75, cell.Z + 0.75);

    private static bool TryReadCell(Godot.Collections.Dictionary result, out WorldVoxelCoord cell)
    {
        cell = default;
        if (result["collider"].AsGodotObject() is not VoxelChunkCollider collider)
            return false;
        var index = result["shape"].AsInt32();
        var children = collider.GetChildren();
        if (index < 0 || index >= children.Count || children[index] is not CollisionShape3D shape)
            return false;
        if (!shape.HasMeta("cell_x") || !shape.HasMeta("cell_y") || !shape.HasMeta("cell_z"))
            return false;
        cell = new WorldVoxelCoord(
            shape.GetMeta("cell_x").AsInt32(),
            shape.GetMeta("cell_y").AsInt32(),
            shape.GetMeta("cell_z").AsInt32());
        return true;
    }
}
