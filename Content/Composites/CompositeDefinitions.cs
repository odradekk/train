using System.Collections.ObjectModel;
using Train.Content.Voxels;

namespace Train.Content.Composites;

public sealed class CompositeVoxelSpec
{
    public LocalVoxelCoord Cell { get; }
    public VoxelDefinitionId DefinitionId { get; }
    public VoxelOrientation Orientation { get; }

    public CompositeVoxelSpec(LocalVoxelCoord cell, VoxelDefinitionId definitionId, VoxelOrientation orientation)
    {
        Cell = cell;
        DefinitionId = definitionId;
        Orientation = orientation;
    }
}

public sealed class CompositeDefinition
{
    public CompositeDefinitionId Id { get; }
    public string Name { get; }
    public string Description { get; }
    public VehicleSizeClass SizeClass { get; }
    public IReadOnlyList<CompositeVoxelSpec> Voxels { get; }

    public CompositeDefinition(
        CompositeDefinitionId id,
        string name,
        string description,
        VehicleSizeClass sizeClass,
        IReadOnlyList<CompositeVoxelSpec> voxels)
    {
        ArgumentNullException.ThrowIfNull(voxels);
        if (string.IsNullOrEmpty(id.Namespace) || string.IsNullOrEmpty(id.LocalId))
            throw new ArgumentException("CompositeDefinitionId cannot be blank.", nameof(id));
        if (!Enum.IsDefined(sizeClass))
            throw new ArgumentOutOfRangeException(nameof(sizeClass), sizeClass, "Unknown vehicle size class.");
        if (voxels.Count == 0)
            throw new ArgumentException("A composite layout must contain at least one voxel.", nameof(voxels));
        if (voxels.Any(item => item is null))
            throw new ArgumentNullException(nameof(voxels), "A composite layout cannot contain null voxels.");
        if (voxels.GroupBy(item => item.Cell).Any(group => group.Skip(1).Any()))
            throw new ArgumentException("A composite layout cannot contain duplicate coordinates.", nameof(voxels));

        Id = id;
        Name = VoxelDefinition.NormalizeDisplayText(name, VoxelDefinition.NameMinLength, VoxelDefinition.NameMaxLength, nameof(name));
        Description = VoxelDefinition.NormalizeDisplayText(
            description,
            VoxelDefinition.DescriptionMinLength,
            VoxelDefinition.DescriptionMaxLength,
            nameof(description));
        SizeClass = sizeClass;
        Voxels = new ReadOnlyCollection<CompositeVoxelSpec>(voxels.OrderBy(item => item.Cell).ToArray());
    }
}
