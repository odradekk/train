using Train.Domain.Composites;
using Train.Domain.Voxels;

namespace Train.Domain.Commands;

public enum WorldEditKind
{
    VoxelAdd = 0,
    VoxelRemove = 1,
    VoxelDamage = 2,
    VehicleManufacture = 3,
    VehicleDamage = 4,
    VehicleRefit = 5
}

/// <summary>
/// A parsed and validated world modification. Equality is canonical-encoding equality; construction rejects
/// values the encoding cannot represent.
/// </summary>
public abstract class WorldEdit : IEquatable<WorldEdit>
{
    private string? _canonical;

    private protected WorldEdit()
    {
    }

    public abstract WorldEditKind Kind { get; }

    public string WireKind => WorldEditCodec.WireKind(Kind);

    public string Canonical => _canonical ??= WorldEditCodec.Encode(this);

    public string Digest => WorldEditCodec.Digest(Canonical);

    /// <summary>Runtime entity ids named by the payload; restore rejects ids that were never allocated.</summary>
    public abstract IEnumerable<EntityId> EntityReferences { get; }

    public bool Equals(WorldEdit? other) =>
        other is not null && string.Equals(Canonical, other.Canonical, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is WorldEdit other && Equals(other);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Canonical);

    public override string ToString() => Canonical;

    private protected static VoxelDefinitionId RequireId(VoxelDefinitionId id, string name) =>
        id.Namespace is null ? throw new ArgumentException("Definition id is required.", name) : id;

    private protected static EntityId RequireId(EntityId id, string name) =>
        id.Value == 0 ? throw new ArgumentException("Runtime entity id is required.", name) : id;

    private protected static VoxelOrientation RequireOrientation(VoxelOrientation orientation, string name) =>
        orientation.IsOrthogonal ? orientation : throw new ArgumentException("Orientation must be orthogonal.", name);
}

public sealed class VoxelAddEdit : WorldEdit
{
    public WorldVoxelCoord Position { get; }
    public VoxelDefinitionId DefinitionId { get; }
    public VoxelOrientation Orientation { get; }

    public VoxelAddEdit(WorldVoxelCoord position, VoxelDefinitionId definitionId, VoxelOrientation orientation)
    {
        Position = position;
        DefinitionId = RequireId(definitionId, nameof(definitionId));
        Orientation = RequireOrientation(orientation, nameof(orientation));
    }

    public override WorldEditKind Kind => WorldEditKind.VoxelAdd;
    public override IEnumerable<EntityId> EntityReferences => Array.Empty<EntityId>();
}

public sealed class VoxelRemoveEdit : WorldEdit
{
    public WorldVoxelCoord Position { get; }

    public VoxelRemoveEdit(WorldVoxelCoord position)
    {
        Position = position;
    }

    public override WorldEditKind Kind => WorldEditKind.VoxelRemove;
    public override IEnumerable<EntityId> EntityReferences => Array.Empty<EntityId>();
}

/// <summary>One damage event; targets are an unordered multiset, stored sorted by position then amount.</summary>
public sealed class VoxelDamageEdit : WorldEdit
{
    public IReadOnlyList<VoxelDamageTarget> Targets { get; }

    /// <summary>For each canonical target, its index in the list the caller supplied.</summary>
    internal IReadOnlyList<int> SourceOrder { get; }

    public VoxelDamageEdit(IReadOnlyList<VoxelDamageTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        SourceOrder = Enumerable.Range(0, targets.Count)
            .OrderBy(index => targets[index].Position.X)
            .ThenBy(index => targets[index].Position.Y)
            .ThenBy(index => targets[index].Position.Z)
            .ThenBy(index => targets[index].Amount)
            .ToArray();
        Targets = Array.AsReadOnly(SourceOrder.Select(index => targets[index]).ToArray());
    }

    /// <summary>Maps per-target outcomes back to the order the caller listed its targets.</summary>
    public WorldDamageResult ToCallerOrder(WorldDamageResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Targets.Count != Targets.Count || Targets.Count == 0)
            return result;
        var ordered = new WorldDamageTargetResult[Targets.Count];
        for (var index = 0; index < ordered.Length; index++)
            ordered[SourceOrder[index]] = result.Targets[index];
        return result with { Position = ordered[0].Position, Targets = ordered };
    }

    public override WorldEditKind Kind => WorldEditKind.VoxelDamage;
    public override IEnumerable<EntityId> EntityReferences => Array.Empty<EntityId>();
}

/// <summary>Manufacture always carries the explicit pose, so a default resolved by one entry cannot drift.</summary>
public sealed class VehicleManufactureEdit : WorldEdit
{
    public CompositeDefinitionId DefinitionId { get; }
    public CompositeTransform Transform { get; }

    public VehicleManufactureEdit(CompositeDefinitionId definitionId, CompositeTransform transform)
    {
        if (definitionId.Namespace is null)
            throw new ArgumentException("Definition id is required.", nameof(definitionId));
        if (!transform.IsFinite)
            throw new ArgumentOutOfRangeException(nameof(transform), "Transform components must be finite.");
        DefinitionId = definitionId;
        Transform = transform;
    }

    public override WorldEditKind Kind => WorldEditKind.VehicleManufacture;
    public override IEnumerable<EntityId> EntityReferences => Array.Empty<EntityId>();
}

/// <summary>One damage event across vehicles; targets are stored sorted by vehicle, local cell, then amount.</summary>
public sealed class VehicleDamageEdit : WorldEdit
{
    public IReadOnlyList<CompositeDamageTarget> Targets { get; }

    /// <summary>For each canonical target, its index in the list the caller supplied.</summary>
    internal IReadOnlyList<int> SourceOrder { get; }

    public VehicleDamageEdit(IReadOnlyList<CompositeDamageTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        foreach (var target in targets)
            RequireId(target.VehicleId, nameof(targets));
        SourceOrder = Enumerable.Range(0, targets.Count)
            .OrderBy(index => targets[index].VehicleId.Value)
            .ThenBy(index => targets[index].Cell)
            .ThenBy(index => targets[index].Amount)
            .ToArray();
        Targets = Array.AsReadOnly(SourceOrder.Select(index => targets[index]).ToArray());
    }

    /// <summary>Maps per-target outcomes back to the order the caller listed its targets.</summary>
    public CompositeDamageResult ToCallerOrder(CompositeDamageResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Targets is not { } outcomes || outcomes.Count != Targets.Count || Targets.Count == 0)
            return result;
        var ordered = new CompositeDamageTargetResult[Targets.Count];
        for (var index = 0; index < ordered.Length; index++)
            ordered[SourceOrder[index]] = outcomes[index];
        return result with { VehicleId = ordered[0].VehicleId, Targets = ordered };
    }

    public override WorldEditKind Kind => WorldEditKind.VehicleDamage;
    public override IEnumerable<EntityId> EntityReferences => Targets.Select(target => target.VehicleId).Distinct();
}

/// <summary>One atomic refit; operation order is semantic (landing order) and is preserved.</summary>
public sealed class VehicleRefitEdit : WorldEdit
{
    public EntityId VehicleId { get; }
    public IReadOnlyList<CompositeRefitOperation> Operations { get; }

    public VehicleRefitEdit(EntityId vehicleId, IReadOnlyList<CompositeRefitOperation> operations)
    {
        ArgumentNullException.ThrowIfNull(operations);
        VehicleId = RequireId(vehicleId, nameof(vehicleId));
        foreach (var operation in operations)
        {
            ArgumentNullException.ThrowIfNull(operation, nameof(operations));
            if (operation.PartId is { } part)
                RequireId(part, nameof(operations));
            if (operation.DefinitionId is { } definition)
                RequireId(definition, nameof(operations));
            if (operation.Orientation is { } orientation)
                RequireOrientation(orientation, nameof(operations));
        }

        Operations = Array.AsReadOnly(operations.ToArray());
    }

    public override WorldEditKind Kind => WorldEditKind.VehicleRefit;

    public override IEnumerable<EntityId> EntityReferences =>
        new[] { VehicleId }
            .Concat(Operations.Where(operation => operation.PartId is not null).Select(operation => operation.PartId!.Value))
            .Distinct();
}
