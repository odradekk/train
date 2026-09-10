using System.Collections.ObjectModel;
using Train.Domain.Voxels;

namespace Train.Domain.Composites;

public sealed class ConstructionRules
{
    public bool RequireNonEmptySixConnected { get; }
    public bool RequireDeclaredSize { get; }
    public int ContactAnchorCount { get; }
    public bool ContactsMustBeDistinct { get; }
    public bool ContactsMustBindExistingParts { get; }

    public ConstructionRules(
        bool requireNonEmptySixConnected,
        bool requireDeclaredSize,
        int contactAnchorCount,
        bool contactsMustBeDistinct,
        bool contactsMustBindExistingParts)
    {
        if (contactAnchorCount < 0)
            throw new ArgumentOutOfRangeException(nameof(contactAnchorCount));
        RequireNonEmptySixConnected = requireNonEmptySixConnected;
        RequireDeclaredSize = requireDeclaredSize;
        ContactAnchorCount = contactAnchorCount;
        ContactsMustBeDistinct = contactsMustBeDistinct;
        ContactsMustBindExistingParts = contactsMustBindExistingParts;
    }
}

public sealed class IdentityRetentionRules
{
    public string RequiredCoreTag { get; }

    public IdentityRetentionRules(string requiredCoreTag)
    {
        RequiredCoreTag = VoxelDefinition.NormalizeTag(requiredCoreTag, nameof(requiredCoreTag));
    }
}

public sealed class CompositeArchetype
{
    public CompositeArchetypeId Id { get; }
    public ConstructionRules Construction { get; }
    public IdentityRetentionRules IdentityRetention { get; }

    public CompositeArchetype(
        CompositeArchetypeId id,
        ConstructionRules construction,
        IdentityRetentionRules identityRetention)
    {
        ArgumentNullException.ThrowIfNull(construction);
        ArgumentNullException.ThrowIfNull(identityRetention);
        Id = id;
        Construction = construction;
        IdentityRetention = identityRetention;
    }
}

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

public sealed class CompositeContactAnchor
{
    public double X { get; }
    public double Y { get; }
    public double Z { get; }
    public LocalVoxelCoord BoundCell { get; }

    public CompositeContactAnchor(double x, double y, double z, LocalVoxelCoord boundCell)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z))
            throw new ArgumentOutOfRangeException(nameof(x), "Contact coordinates must be finite.");
        X = x;
        Y = y;
        Z = z;
        BoundCell = boundCell;
    }
}

public sealed class CompositeDefinition
{
    public CompositeDefinitionId Id { get; }
    public string Name { get; }
    public string Description { get; }
    public CompositeArchetypeId ArchetypeId { get; }
    public VehicleSizeClass SizeClass { get; }
    public IReadOnlyList<CompositeVoxelSpec> Voxels { get; }
    public IReadOnlyList<CompositeContactAnchor> Contacts { get; }

    public CompositeDefinition(
        CompositeDefinitionId id,
        string name,
        string description,
        CompositeArchetypeId archetypeId,
        VehicleSizeClass sizeClass,
        IReadOnlyList<CompositeVoxelSpec> voxels,
        IReadOnlyList<CompositeContactAnchor> contacts)
    {
        ArgumentNullException.ThrowIfNull(voxels);
        ArgumentNullException.ThrowIfNull(contacts);
        if (!Enum.IsDefined(sizeClass))
            throw new ArgumentOutOfRangeException(nameof(sizeClass), sizeClass, "Unknown vehicle size class.");

        Id = id;
        Name = VoxelDefinition.NormalizeDisplayText(name, VoxelDefinition.NameMinLength, VoxelDefinition.NameMaxLength, nameof(name));
        Description = VoxelDefinition.NormalizeDisplayText(
            description,
            VoxelDefinition.DescriptionMinLength,
            VoxelDefinition.DescriptionMaxLength,
            nameof(description));
        ArchetypeId = archetypeId;
        SizeClass = sizeClass;
        Voxels = new ReadOnlyCollection<CompositeVoxelSpec>(
            voxels.OrderBy(item => item.Cell).ToArray());
        Contacts = new ReadOnlyCollection<CompositeContactAnchor>(contacts.ToArray());
    }
}
