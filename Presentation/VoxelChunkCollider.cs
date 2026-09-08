using Godot;
using Train.Content;
using Train.Domain.Voxels;

namespace Train.Presentation;

public partial class VoxelChunkCollider : StaticBody3D
{
    public int ShapeCount { get; private set; }
    public int LastWorldVoxelCount { get; private set; }
    public long Revision { get; private set; }

    public VoxelChunkCollider()
    {
        CollisionLayer = 1;
        CollisionMask = 0;
    }

    public void Refresh(VoxelWorld world, ChunkCoord chunk, VoxelCatalogSnapshot? catalog)
    {
        ArgumentNullException.ThrowIfNull(world);
        foreach (var (position, voxel) in world.OccupiedVoxels())
        {
            if (VoxelChunks.Of(position, world.ChunkSize) != chunk)
                continue;
            if (catalog?.TryGet(voxel.DefinitionId, out var entry) != true ||
                !entry.Definition.Collision.TryDerivedExtents(
                    position,
                    voxel.Orientation,
                    out _,
                    out _,
                    out _,
                    out var pendingSizeX,
                    out var pendingSizeY,
                    out var pendingSizeZ))
                continue;
            if (!VoxelCollisionAdapter.TryDeriveSize(pendingSizeX, out _) ||
                !VoxelCollisionAdapter.TryDeriveSize(pendingSizeY, out _) ||
                !VoxelCollisionAdapter.TryDeriveSize(pendingSizeZ, out _))
                throw new InvalidOperationException(
                    $"Published collision at ({position.X},{position.Y},{position.Z}) lost a positive adapter volume.");
        }

        foreach (var child in GetChildren().ToArray())
        {
            RemoveChild(child);
            child.Free();
        }

        var shapes = 0;
        foreach (var (position, voxel) in world.OccupiedVoxels())
        {
            if (VoxelChunks.Of(position, world.ChunkSize) != chunk)
                continue;
            if (catalog?.TryGet(voxel.DefinitionId, out var entry) != true ||
                !TryPublishedAdapterBox(
                    entry.Definition.Collision,
                    position,
                    voxel.Orientation,
                    out var centerX,
                    out var centerY,
                    out var centerZ,
                    out var derivedX,
                    out var derivedY,
                    out var derivedZ))
                continue;

            var shape = new CollisionShape3D
            {
                Shape = new BoxShape3D
                {
                    Size = new Vector3(derivedX, derivedY, derivedZ),
                    Margin = 0
                },
                Position = new Vector3((float)centerX, (float)centerY, (float)centerZ)
            };
            shape.SetMeta("cell_x", position.X);
            shape.SetMeta("cell_y", position.Y);
            shape.SetMeta("cell_z", position.Z);
            AddChild(shape);
            shapes++;
        }

        ShapeCount = shapes;
        LastWorldVoxelCount = world.VoxelCount;
        Revision++;
    }

    private static bool TryPublishedAdapterBox(
        VoxelCollision collision,
        WorldVoxelCoord position,
        VoxelOrientation orientation,
        out float centerX,
        out float centerY,
        out float centerZ,
        out float sizeX,
        out float sizeY,
        out float sizeZ)
    {
        centerX = centerY = centerZ = sizeX = sizeY = sizeZ = 0;
        if (!collision.TryDerivedExtents(
                position,
                orientation,
                out var derivedCenterX,
                out var derivedCenterY,
                out var derivedCenterZ,
                out var derivedSizeX,
                out var derivedSizeY,
                out var derivedSizeZ) ||
            !VoxelCollisionAdapter.TryDeriveSize(derivedSizeX, out sizeX) ||
            !VoxelCollisionAdapter.TryDeriveSize(derivedSizeY, out sizeY) ||
            !VoxelCollisionAdapter.TryDeriveSize(derivedSizeZ, out sizeZ))
            return false;
        centerX = (float)derivedCenterX;
        centerY = (float)derivedCenterY;
        centerZ = (float)derivedCenterZ;
        return true;
    }

    public bool TryGetBoxSize(WorldVoxelCoord cell, out Vector3 size)
    {
        size = default;
        foreach (var child in GetChildren())
        {
            if (child is not CollisionShape3D shape ||
                shape.Shape is not BoxShape3D box ||
                !shape.HasMeta("cell_x") ||
                !shape.HasMeta("cell_y") ||
                !shape.HasMeta("cell_z"))
                continue;
            if (shape.GetMeta("cell_x").AsInt32() != cell.X ||
                shape.GetMeta("cell_y").AsInt32() != cell.Y ||
                shape.GetMeta("cell_z").AsInt32() != cell.Z)
                continue;
            size = box.Size;
            return true;
        }

        return false;
    }
}
