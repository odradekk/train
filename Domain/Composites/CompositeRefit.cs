using Train.Content;
using Train.Domain.Voxels;

namespace Train.Domain.Composites;

public enum CompositeRefitKind
{
    Add = 0,
    Remove = 1,
    Move = 2,
    Rotate = 3,
    Replace = 4
}

public sealed class CompositeRefitOperation
{
    public CompositeRefitKind Kind { get; }
    public EntityId? PartId { get; }
    public LocalVoxelCoord? Cell { get; }
    public VoxelDefinitionId? DefinitionId { get; }
    public VoxelOrientation? Orientation { get; }

    private CompositeRefitOperation(
        CompositeRefitKind kind,
        EntityId? partId,
        LocalVoxelCoord? cell,
        VoxelDefinitionId? definitionId,
        VoxelOrientation? orientation)
    {
        Kind = kind;
        PartId = partId;
        Cell = cell;
        DefinitionId = definitionId;
        Orientation = orientation;
    }

    public static CompositeRefitOperation Add(
        LocalVoxelCoord cell,
        VoxelDefinitionId definitionId,
        VoxelOrientation orientation) =>
        new(CompositeRefitKind.Add, null, cell, definitionId, orientation);

    public static CompositeRefitOperation Remove(EntityId partId) =>
        new(CompositeRefitKind.Remove, partId, null, null, null);

    public static CompositeRefitOperation Move(EntityId partId, LocalVoxelCoord destination) =>
        new(CompositeRefitKind.Move, partId, destination, null, null);

    public static CompositeRefitOperation Rotate(EntityId partId, VoxelOrientation orientation) =>
        new(CompositeRefitKind.Rotate, partId, null, null, orientation);

    public static CompositeRefitOperation Replace(
        LocalVoxelCoord cell,
        VoxelDefinitionId definitionId,
        VoxelOrientation orientation) =>
        new(CompositeRefitKind.Replace, null, cell, definitionId, orientation);
}

public sealed record CompositeRefitResult(
    ConstructionStatus Status,
    EntityId? VehicleId = null,
    IReadOnlyList<CompositePart>? Parts = null,
    IReadOnlyList<EntityId>? AllocatedIds = null)
{
    public bool Succeeded => Status == ConstructionStatus.Valid && VehicleId is not null;
}

public sealed record HollowShellRefitTrace(
    EntityId VehicleId,
    EntityId P,
    EntityId Q,
    EntityId R,
    CompositeAuthoritySnapshot Success);

public static class HollowShellRefit
{
    public const string MarkerLocalId = "refit-marker";
    public const string ReplacementLocalId = "refit-replacement";

    public static VoxelDefinitionId MarkerId { get; } = new("core", MarkerLocalId);
    public static VoxelDefinitionId ReplacementId { get; } = new("core", ReplacementLocalId);
    public static VoxelDefinitionId LockedId { get; } = new("core", "fixed");
    public static VoxelOrientation MarkerOrientation { get; } = new(VoxelAxis.NegativeZ, VoxelAxis.PositiveY);
    public static LocalVoxelCoord FirstAdd { get; } = new(2, 1, 0);
    public static LocalVoxelCoord Moved { get; } = new(3, 1, 0);
    public static LocalVoxelCoord SecondAdd { get; } = new(4, 1, 0);
    public static LocalVoxelCoord Occupied { get; } = new(0, 1, 0);
    public static LocalVoxelCoord Disconnected { get; } = new(3, 2, 1);
    public static LocalVoxelCoord OutOfEnvelope { get; } = new(8, 1, 0);
    public static LocalVoxelCoord LockedCell { get; } = new(5, 1, 0);

    public static HollowShellRefitTrace ApplySuccess(VoxelDevelopmentSession session, EntityId vehicleId)
    {
        ArgumentNullException.ThrowIfNull(session);
        var added = Require(session.Refit(vehicleId, CompositeRefitOperation.Add(FirstAdd, MarkerId, VoxelOrientation.Identity)));
        var p = SingleAllocated(added);
        RequireDamage(session.Damage(vehicleId, FirstAdd, 25), 75);
        Require(session.Refit(vehicleId, CompositeRefitOperation.Move(p, Moved)));
        Require(session.Refit(vehicleId, CompositeRefitOperation.Rotate(p, MarkerOrientation)));
        var addedQ = Require(session.Refit(vehicleId, CompositeRefitOperation.Add(SecondAdd, MarkerId, VoxelOrientation.Identity)));
        var q = SingleAllocated(addedQ);
        Require(session.Refit(vehicleId, new[]
        {
            CompositeRefitOperation.Move(p, SecondAdd),
            CompositeRefitOperation.Move(q, Moved)
        }));
        var replaced = Require(session.Refit(
            vehicleId,
            CompositeRefitOperation.Replace(Moved, ReplacementId, VoxelOrientation.Identity)));
        var r = SingleAllocated(replaced);
        Require(session.Refit(vehicleId, CompositeRefitOperation.Remove(r)));
        return new HollowShellRefitTrace(vehicleId, p, q, r, session.CaptureAuthority());
    }

    public static int RunMoveReturnPairs(VoxelDevelopmentSession session, EntityId vehicleId, EntityId partId, int pairs)
    {
        ArgumentNullException.ThrowIfNull(session);
        for (var index = 0; index < pairs; index++)
        {
            Require(session.Refit(vehicleId, CompositeRefitOperation.Move(partId, Moved)));
            Require(session.Refit(vehicleId, CompositeRefitOperation.Move(partId, SecondAdd)));
        }

        return pairs;
    }

    private static EntityId SingleAllocated(CompositeRefitResult result)
    {
        if (result.AllocatedIds is not { Count: 1 })
            throw new InvalidOperationException("Official hollow-shell refit did not return exactly one allocated id.");
        return result.AllocatedIds[0];
    }

    private static CompositeRefitResult Require(CompositeRefitResult result)
    {
        if (!result.Succeeded)
            throw new InvalidOperationException($"Official hollow-shell refit failed: {result.Status}.");
        return result;
    }

    private static void RequireDamage(CompositeDamageResult result, int remaining)
    {
        if (!result.Succeeded || result.Targets is null || result.Targets[0].RemainingDurability != remaining)
            throw new InvalidOperationException($"Official hollow-shell refit damage failed: {result.Status}.");
    }
}

internal sealed class PlannedRefitPart
{
    public EntityId? ExistingId { get; }
    public LocalVoxelCoord Cell { get; }
    public VoxelDefinitionId DefinitionId { get; }
    public VoxelOrientation Orientation { get; }
    public int CurrentDurability { get; }
    public string LastUsedRevision { get; }
    public bool Allocate { get; }

    public PlannedRefitPart(
        EntityId? existingId,
        LocalVoxelCoord cell,
        VoxelDefinitionId definitionId,
        VoxelOrientation orientation,
        int currentDurability,
        string lastUsedRevision,
        bool allocate)
    {
        ExistingId = existingId;
        Cell = cell;
        DefinitionId = definitionId;
        Orientation = orientation;
        CurrentDurability = currentDurability;
        LastUsedRevision = lastUsedRevision;
        Allocate = allocate;
    }
}

internal static class CompositeRefitPlanner
{
    public static ConstructionStatus TryPlan(
        CompositeInstance vehicle,
        IReadOnlyList<CompositeRefitOperation> operations,
        VoxelCatalogSnapshot voxels,
        CompositeCatalogSnapshot composites,
        out IReadOnlyList<PlannedRefitPart> planned)
    {
        planned = Array.Empty<PlannedRefitPart>();
        if (operations.Count == 0)
            return ConstructionStatus.Empty;

        var originals = vehicle.Parts.ToDictionary(part => part.Id);
        var occupancy = vehicle.Parts.ToDictionary(part => part.Cell, part => part.Id);
        var orientations = vehicle.Parts.ToDictionary(part => part.Id, part => part.Orientation);
        var lifted = new HashSet<EntityId>();
        var removed = new HashSet<EntityId>();
        var landings = new List<Landing>();

        foreach (var operation in operations)
        {
            switch (operation.Kind)
            {
                case CompositeRefitKind.Add:
                    if (operation.Cell is not { } addCell ||
                        operation.DefinitionId is not { } addDefinition ||
                        operation.Orientation is not { } addOrientation)
                        return ConstructionStatus.UnknownVoxel;
                    landings.Add(Landing.Create(null, addCell, addDefinition, addOrientation));
                    break;
                case CompositeRefitKind.Remove:
                    if (operation.PartId is not { } removeId || !originals.ContainsKey(removeId))
                        return ConstructionStatus.UnknownPart;
                    if (!lifted.Add(removeId))
                        return ConstructionStatus.UnknownPart;
                    removed.Add(removeId);
                    break;
                case CompositeRefitKind.Move:
                    if (operation.PartId is not { } moveId || !originals.ContainsKey(moveId) || operation.Cell is not { } dest)
                        return ConstructionStatus.UnknownPart;
                    if (removed.Contains(moveId) || !lifted.Add(moveId))
                        return ConstructionStatus.UnknownPart;
                    landings.Add(Landing.Create(moveId, dest, null, null));
                    break;
                case CompositeRefitKind.Rotate:
                    if (operation.PartId is not { } rotateId || !originals.ContainsKey(rotateId) || operation.Orientation is not { } rotated)
                        return ConstructionStatus.UnknownPart;
                    if (removed.Contains(rotateId))
                        return ConstructionStatus.UnknownPart;
                    orientations[rotateId] = rotated;
                    break;
                case CompositeRefitKind.Replace:
                    if (operation.Cell is not { } replaceCell ||
                        operation.DefinitionId is not { } replaceDefinition ||
                        operation.Orientation is not { } replaceOrientation)
                        return ConstructionStatus.Empty;
                    if (!occupancy.TryGetValue(replaceCell, out var oldId) || !originals.ContainsKey(oldId))
                        return ConstructionStatus.Empty;
                    if (!lifted.Add(oldId))
                        return ConstructionStatus.UnknownPart;
                    removed.Add(oldId);
                    landings.Add(Landing.Create(null, replaceCell, replaceDefinition, replaceOrientation));
                    break;
                default:
                    return ConstructionStatus.UnknownPart;
            }
        }

        foreach (var id in lifted)
            occupancy.Remove(originals[id].Cell);

        var placed = new Dictionary<LocalVoxelCoord, PlannedRefitPart>();
        foreach (var part in vehicle.Parts)
        {
            if (lifted.Contains(part.Id) || removed.Contains(part.Id))
                continue;
            placed[part.Cell] = new PlannedRefitPart(
                part.Id,
                part.Cell,
                part.DefinitionId,
                orientations[part.Id],
                part.CurrentDurability,
                part.LastUsedRevision,
                allocate: false);
        }

        foreach (var landing in landings)
        {
            if (placed.ContainsKey(landing.Cell))
                return ConstructionStatus.DuplicateOccupancy;
            if (landing.ExistingId is { } existing)
            {
                var source = originals[existing];
                placed[landing.Cell] = new PlannedRefitPart(
                    existing,
                    landing.Cell,
                    source.DefinitionId,
                    orientations[existing],
                    source.CurrentDurability,
                    source.LastUsedRevision,
                    allocate: false);
                continue;
            }

            if (!voxels.TryGet(landing.DefinitionId!.Value, out var entry))
                return ConstructionStatus.UnknownVoxel;
            placed[landing.Cell] = new PlannedRefitPart(
                null,
                landing.Cell,
                landing.DefinitionId.Value,
                landing.Orientation!.Value,
                entry.Definition.MaxDurability,
                entry.SemanticRevision,
                allocate: true);
        }

        if (!composites.TryGet(vehicle.DefinitionId, out var published) ||
            !composites.TryGet(vehicle.ArchetypeId, out var archetypeEntry))
            return ConstructionStatus.UnknownDefinition;

        var ordered = new List<PlannedRefitPart>();
        foreach (var part in placed.Values.Where(item => !item.Allocate).OrderBy(item => item.Cell))
            ordered.Add(part);
        foreach (var landing in landings.Where(item => item.ExistingId is null))
            ordered.Add(placed[landing.Cell]);

        var status = ConstructionValidator.Validate(
            new CompositeDefinition(
                vehicle.DefinitionId,
                published.Definition.Name,
                published.Definition.Description,
                vehicle.ArchetypeId,
                vehicle.SizeClass,
                ordered.Select(part => new CompositeVoxelSpec(part.Cell, part.DefinitionId, part.Orientation)).ToArray(),
                vehicle.Contacts),
            archetypeEntry.Archetype,
            voxels,
            vehicle.Transform);
        if (status != ConstructionStatus.Valid)
            return status;

        planned = ordered;
        return ConstructionStatus.Valid;
    }

    private readonly record struct Landing(
        EntityId? ExistingId,
        LocalVoxelCoord Cell,
        VoxelDefinitionId? DefinitionId,
        VoxelOrientation? Orientation)
    {
        public static Landing Create(
            EntityId? existingId,
            LocalVoxelCoord cell,
            VoxelDefinitionId? definitionId,
            VoxelOrientation? orientation) =>
            new(existingId, cell, definitionId, orientation);
    }
}
