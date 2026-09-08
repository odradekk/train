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
                entry.Definition.Collision.Kind != VoxelCollisionKind.FullBox)
                continue;

            var shape = new CollisionShape3D
            {
                Shape = new BoxShape3D
                {
                    Size = Vector3.One,
                    Margin = 0
                },
                Position = new Vector3(position.X + 0.5f, position.Y + 0.5f, position.Z + 0.5f)
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
}
