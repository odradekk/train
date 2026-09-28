using Train.Content;
using Train.Domain.Composites;
using Train.Domain.Rails;
using Train.Domain.Voxels;

namespace Train.Domain.Commands;

public enum WorldEditKind
{
    VoxelAdd = 0,
    VoxelRemove = 1,
    VoxelDamage = 2,
    VehicleManufacture = 3,
    VehicleDamage = 4,
    VehicleRefit = 5,
    RailConstruct = 6,
    RailRepair = 7,
    TrainAssemble = 8,
    TrainStart = 9,
    TrainStop = 10,
    ContentPublish = 11
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

/// <summary>One atomic rail construction; segment, volume, and connection order are semantic and preserved.</summary>
public sealed class RailConstructEdit : WorldEdit
{
    public RailConstructionPlan Plan { get; }

    public RailConstructEdit(RailConstructionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!IsEncodable(plan))
            throw new ArgumentOutOfRangeException(nameof(plan), "Rail plan values must be finite and rail ends defined.");
        Plan = new RailConstructionPlan(
            Array.AsReadOnly(plan.Segments.Select(segment => new PlannedRailSegment(
                segment.Alignment,
                Array.AsReadOnly(segment.RailVolumes.ToArray()),
                Array.AsReadOnly(segment.SleeperVolumes.ToArray()))).ToArray()),
            Array.AsReadOnly(plan.Connections.ToArray()));
    }

    public static bool IsEncodable(RailConstructionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        static bool Finite(RailVolumeBox box) =>
            double.IsFinite(box.MinX) && double.IsFinite(box.MinY) && double.IsFinite(box.MinZ) &&
            double.IsFinite(box.MaxX) && double.IsFinite(box.MaxY) && double.IsFinite(box.MaxZ);
        foreach (var segment in plan.Segments)
        {
            if (segment is null || segment.RailVolumes is null || segment.SleeperVolumes is null)
                return false;
            var alignment = segment.Alignment;
            if (!double.IsFinite(alignment.StartX) || !double.IsFinite(alignment.StartY) || !double.IsFinite(alignment.StartZ) ||
                !double.IsFinite(alignment.EndX) || !double.IsFinite(alignment.EndY) || !double.IsFinite(alignment.EndZ) ||
                !segment.RailVolumes.All(Finite) || !segment.SleeperVolumes.All(Finite))
                return false;
        }

        return plan.Connections.All(connection =>
            connection is not null && Enum.IsDefined(connection.FromEnd) && Enum.IsDefined(connection.ToEnd));
    }

    public override WorldEditKind Kind => WorldEditKind.RailConstruct;
    public override IEnumerable<EntityId> EntityReferences => Array.Empty<EntityId>();
}

/// <summary>One atomic in-place repair; the cell list keeps submission order, so the reported first cell is stable.</summary>
public sealed class RailRepairEdit : WorldEdit
{
    public IReadOnlyList<WorldVoxelCoord> Cells { get; }

    public RailRepairEdit(IReadOnlyList<WorldVoxelCoord> cells)
    {
        ArgumentNullException.ThrowIfNull(cells);
        Cells = Array.AsReadOnly(cells.ToArray());
    }

    public override WorldEditKind Kind => WorldEditKind.RailRepair;
    public override IEnumerable<EntityId> EntityReferences => Array.Empty<EntityId>();
}

/// <summary>Assembles a locomotive and a carriage from published definitions at explicit poses and coupler cells.</summary>
public sealed class TrainAssembleEdit : WorldEdit
{
    public CompositeDefinitionId LocomotiveId { get; }
    public CompositeTransform LocomotiveTransform { get; }
    public LocalVoxelCoord LocomotiveCouplerCell { get; }
    public CompositeDefinitionId CarriageId { get; }
    public CompositeTransform CarriageTransform { get; }
    public LocalVoxelCoord CarriageCouplerCell { get; }

    public TrainAssembleEdit(
        CompositeDefinitionId locomotiveId,
        CompositeTransform locomotiveTransform,
        LocalVoxelCoord locomotiveCouplerCell,
        CompositeDefinitionId carriageId,
        CompositeTransform carriageTransform,
        LocalVoxelCoord carriageCouplerCell)
    {
        if (locomotiveId.Namespace is null || carriageId.Namespace is null)
            throw new ArgumentException("Definition ids are required.");
        if (!locomotiveTransform.IsFinite || !carriageTransform.IsFinite)
            throw new ArgumentOutOfRangeException(nameof(locomotiveTransform), "Transform components must be finite.");
        LocomotiveId = locomotiveId;
        LocomotiveTransform = locomotiveTransform;
        LocomotiveCouplerCell = locomotiveCouplerCell;
        CarriageId = carriageId;
        CarriageTransform = carriageTransform;
        CarriageCouplerCell = carriageCouplerCell;
    }

    public static TrainAssembleEdit StaticFixture { get; } = new(
        StaticTrainFixture.LocomotiveId,
        StaticTrainFixture.LocomotiveTransform,
        StaticTrainFixture.LocomotiveRearCouplerCell,
        StaticTrainFixture.CarriageId,
        StaticTrainFixture.CarriageTransform,
        StaticTrainFixture.CarriageFrontCouplerCell);

    public override WorldEditKind Kind => WorldEditKind.TrainAssemble;
    public override IEnumerable<EntityId> EntityReferences => Array.Empty<EntityId>();
}

/// <summary>Starts one assembly along its alignment; start conditions are checked where the edit executes.</summary>
public sealed class TrainStartEdit : WorldEdit
{
    public EntityId AssemblyId { get; }

    public TrainStartEdit(EntityId assemblyId)
    {
        AssemblyId = RequireId(assemblyId, nameof(assemblyId));
    }

    public override WorldEditKind Kind => WorldEditKind.TrainStart;
    public override IEnumerable<EntityId> EntityReferences => new[] { AssemblyId };
}

public sealed class TrainStopEdit : WorldEdit
{
    public EntityId AssemblyId { get; }

    public TrainStopEdit(EntityId assemblyId)
    {
        AssemblyId = RequireId(assemblyId, nameof(assemblyId));
    }

    public override WorldEditKind Kind => WorldEditKind.TrainStop;
    public override IEnumerable<EntityId> EntityReferences => new[] { AssemblyId };
}

/// <summary>
/// A content publication that adapts the live world. Recorded by the content reload boundary, never submitted or
/// queued; the digest names the published voxel, archetype, and composite semantic revisions.
/// </summary>
public sealed class ContentPublishEdit : WorldEdit
{
    public const int DigestLength = 64;

    public string ContentDigest { get; }

    public ContentPublishEdit(string contentDigest)
    {
        if (!IsDigest(contentDigest))
            throw new ArgumentException("Content digest must be 64 upper-case hex characters.", nameof(contentDigest));
        ContentDigest = contentDigest;
    }

    public static ContentPublishEdit For(VoxelCatalogSnapshot voxels, CompositeCatalogSnapshot composites)
    {
        ArgumentNullException.ThrowIfNull(voxels);
        ArgumentNullException.ThrowIfNull(composites);
        var lines = voxels.Entries.Select(pair => $"voxel {pair.Key.Value} {pair.Value.SemanticRevision}")
            .Concat(composites.Archetypes.Select(pair => $"archetype {pair.Key.Value} {pair.Value.SemanticRevision}"))
            .Concat(composites.Composites.Select(pair => $"composite {pair.Key.Value} {pair.Value.SemanticRevision}"))
            .OrderBy(line => line, StringComparer.Ordinal);
        return new ContentPublishEdit(WorldEditCodec.Digest(string.Join('\n', lines)));
    }

    internal static bool IsDigest(string? text) =>
        text is { Length: DigestLength } && text.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');

    public override WorldEditKind Kind => WorldEditKind.ContentPublish;
    public override IEnumerable<EntityId> EntityReferences => Array.Empty<EntityId>();
}
