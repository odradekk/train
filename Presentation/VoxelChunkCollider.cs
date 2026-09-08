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
                !entry.Definition.Collision.TryDerivedExtents(
                    position,
                    voxel.Orientation,
                    out var centerX,
                    out var centerY,
                    out var centerZ,
                    out var sizeX,
                    out var sizeY,
                    out var sizeZ))
                continue;

            var shape = new CollisionShape3D
            {
                Shape = new BoxShape3D
                {
                    Size = new Vector3((float)sizeX, (float)sizeY, (float)sizeZ),
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
