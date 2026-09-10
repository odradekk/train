using System.Collections.ObjectModel;
using Train.Domain.Voxels;

namespace Train.Domain.Composites;

public enum ConstructionStatus
{
    Valid = 0,
    Empty = 1,
    Disconnected = 2,
    DuplicateOccupancy = 3,
    UnknownSizeClass = 4,
    EnvelopeExceeded = 5,
    MissingContact = 6,
    DuplicateContact = 7,
    UnboundContact = 8,
    CoincidentContacts = 9,
    UnknownVoxel = 10,
    DisallowedOrientation = 11,
    NonFiniteTransform = 12,
    UnknownDefinition = 13,
    UnknownArchetype = 14
}

public enum IdentityRetentionStatus
{
    Retained = 0,
    MissingCoreTag = 1
}

public enum OperationalCondition
{
    Unevaluated = 0
}

public sealed class CompositePart
{
    public EntityId Id { get; }
    public LocalVoxelCoord Cell { get; }
    public VoxelDefinitionId DefinitionId { get; }
    public VoxelOrientation Orientation { get; }
    public int CurrentDurability { get; }
    public string LastUsedRevision { get; }

    public CompositePart(
        EntityId id,
        LocalVoxelCoord cell,
        VoxelDefinitionId definitionId,
        VoxelOrientation orientation,
        int currentDurability,
        string lastUsedRevision)
    {
        ArgumentException.ThrowIfNullOrEmpty(lastUsedRevision);
        if (currentDurability < 0)
            throw new ArgumentOutOfRangeException(nameof(currentDurability));
        Id = id;
        Cell = cell;
        DefinitionId = definitionId;
        Orientation = orientation;
        CurrentDurability = currentDurability;
        LastUsedRevision = lastUsedRevision;
    }
}

public sealed class CompositeInstance
{
    public EntityId Id { get; }
    public CompositeDefinitionId DefinitionId { get; }
    public string LastUsedRevision { get; }
    public CompositeArchetypeId ArchetypeId { get; }
    public string LastUsedArchetypeRevision { get; }
    public VehicleSizeClass SizeClass { get; }
    public CompositeTransform Transform { get; }
    public IReadOnlyList<CompositePart> Parts { get; }
    public IReadOnlyList<CompositeContactAnchor> Contacts { get; }

    public CompositeInstance(
        EntityId id,
        CompositeDefinitionId definitionId,
        string lastUsedRevision,
        CompositeArchetypeId archetypeId,
        string lastUsedArchetypeRevision,
        VehicleSizeClass sizeClass,
        CompositeTransform transform,
        IReadOnlyList<CompositePart> parts,
        IReadOnlyList<CompositeContactAnchor> contacts)
    {
        ArgumentException.ThrowIfNullOrEmpty(lastUsedRevision);
        ArgumentException.ThrowIfNullOrEmpty(lastUsedArchetypeRevision);
        ArgumentNullException.ThrowIfNull(parts);
        ArgumentNullException.ThrowIfNull(contacts);
        Id = id;
        DefinitionId = definitionId;
        LastUsedRevision = lastUsedRevision;
        ArchetypeId = archetypeId;
        LastUsedArchetypeRevision = lastUsedArchetypeRevision;
        SizeClass = sizeClass;
        Transform = transform;
        Parts = new ReadOnlyCollection<CompositePart>(parts.OrderBy(part => part.Cell).ToArray());
        Contacts = new ReadOnlyCollection<CompositeContactAnchor>(contacts.ToArray());
    }

    public CompositePart? PartAt(LocalVoxelCoord cell) =>
        Parts.FirstOrDefault(part => part.Cell == cell);
}

public sealed record CompositeManufactureResult(
    ConstructionStatus Status,
    EntityId? VehicleId = null,
    IReadOnlyList<CompositePart>? Parts = null)
{
    public bool Succeeded => Status == ConstructionStatus.Valid && VehicleId is not null;
}

public sealed record CompositePartQuery(
    EntityId Id,
    LocalVoxelCoord Cell,
    VoxelDefinitionId DefinitionId,
    VoxelOrientation Orientation,
    int CurrentDurability);

public sealed record CompositeContactQuery(
    double X,
    double Y,
    double Z,
    LocalVoxelCoord BoundCell,
    EntityId BoundPartId);

public sealed record CompositeQuery(
    EntityId Id,
    CompositeDefinitionId DefinitionId,
    CompositeArchetypeId ArchetypeId,
    VehicleSizeClass SizeClass,
    CompositeTransform Transform,
    ConstructionStatus Construction,
    IdentityRetentionStatus IdentityRetention,
    OperationalCondition OperationalCondition,
    IReadOnlyList<CompositePartQuery> Parts,
    IReadOnlyList<CompositeContactQuery> Contacts,
    VehicleEnvelopeBox? Envelope)
{
    public bool Equals(CompositeQuery? other) =>
        other is not null &&
        Id == other.Id &&
        DefinitionId == other.DefinitionId &&
        ArchetypeId == other.ArchetypeId &&
        SizeClass == other.SizeClass &&
        Transform == other.Transform &&
        Construction == other.Construction &&
        IdentityRetention == other.IdentityRetention &&
        OperationalCondition == other.OperationalCondition &&
        Envelope == other.Envelope &&
        Parts.SequenceEqual(other.Parts) &&
        Contacts.SequenceEqual(other.Contacts);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Id);
        hash.Add(DefinitionId);
        hash.Add(ArchetypeId);
        hash.Add(SizeClass);
        hash.Add(Transform);
        hash.Add(Construction);
        hash.Add(IdentityRetention);
        hash.Add(OperationalCondition);
        hash.Add(Envelope);
        foreach (var part in Parts)
            hash.Add(part);
        foreach (var contact in Contacts)
            hash.Add(contact);
        return hash.ToHashCode();
    }
}

public sealed record CompositeAuthoritySnapshot(
    int WorldVoxelCount,
    IReadOnlyList<(WorldVoxelCoord Position, VoxelDefinitionId DefinitionId, int Durability, string Orientation)> WorldVoxels,
    IReadOnlyList<CompositeQuery> Vehicles,
    ulong NextEntityId,
    ulong TransactionCount)
{
    public bool Equals(CompositeAuthoritySnapshot? other) =>
        other is not null &&
        WorldVoxelCount == other.WorldVoxelCount &&
        NextEntityId == other.NextEntityId &&
        TransactionCount == other.TransactionCount &&
        WorldVoxels.SequenceEqual(other.WorldVoxels) &&
        Vehicles.SequenceEqual(other.Vehicles);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(WorldVoxelCount);
        hash.Add(NextEntityId);
        hash.Add(TransactionCount);
        foreach (var voxel in WorldVoxels)
            hash.Add(voxel);
        foreach (var vehicle in Vehicles)
            hash.Add(vehicle);
        return hash.ToHashCode();
    }
}
