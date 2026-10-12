
using Train.Content.Behaviors;

namespace Train.Content.Voxels;

public interface ICoreVoxelDefinition
{
    VoxelDefinitionId Id { get; }

    long Value { get; }

    double Mass { get; }

    int MaxDurability { get; }

    IReadOnlySet<string> Tags { get; }

    IReadOnlyList<VoxelBehavior> Behaviors { get; }
}
