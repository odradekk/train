using Train.Content;
using Train.Domain.Behaviors;
using Train.Domain.Composites;
using Train.Domain.Voxels;

namespace Train.Domain.Rails;

public static class TrainAssemblyRules
{
    public static IReadOnlyList<string> LiveCapabilities(CompositeInstance vehicle, VoxelCatalogSnapshot voxels)
    {
        ArgumentNullException.ThrowIfNull(vehicle);
        ArgumentNullException.ThrowIfNull(voxels);
        var behaviors = new List<string>();
        foreach (var part in vehicle.Parts)
        {
            if (voxels.TryGet(part.DefinitionId, out var entry))
                behaviors.AddRange(entry.Definition.Behaviors);
        }

        return BehaviorRegistry.CapabilitiesFrom(behaviors);
    }

    public static OperationalCondition EvaluateOperational(int boundContacts, IReadOnlyList<string> capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        if (boundContacts < 2)
            return OperationalCondition.Disabled;
        if (!capabilities.Contains(CapabilityTypes.TrackContact))
            return OperationalCondition.Unevaluated;
        if (capabilities.Contains(CapabilityTypes.Propulsion) || capabilities.Contains(CapabilityTypes.Control))
        {
            return capabilities.Contains(CapabilityTypes.Propulsion) && capabilities.Contains(CapabilityTypes.Control)
                ? OperationalCondition.Operational
                : OperationalCondition.Disabled;
        }

        return OperationalCondition.Operational;
    }

    public static bool HasCapability(IReadOnlyList<string> capabilities, string capability) =>
        capabilities.Contains(capability);

    public static bool HasLocomotiveCapabilities(IReadOnlyList<string> capabilities) =>
        HasCapability(capabilities, CapabilityTypes.Propulsion) &&
        HasCapability(capabilities, CapabilityTypes.Control);

    public static TrainAssemblyStatus Validate(
        CompositeInstance locomotive,
        CompositeInstance carriage,
        TrainAssemblyRequest request,
        PhysicalRailState rails,
        VoxelWorld world,
        VoxelCatalogSnapshot voxels,
        bool alreadyAssembled,
        out CouplerConnectionQuery? connection,
        out RailPosition reference)
    {
        connection = null;
        reference = default;
        if (!rails.HasRailway)
            return TrainAssemblyStatus.NoRailway;
        if (alreadyAssembled)
            return TrainAssemblyStatus.AlreadyAssembled;
        if (locomotive.Contacts.Count(contact => locomotive.PartAt(contact.BoundCell) is not null) < 2 ||
            carriage.Contacts.Count(contact => carriage.PartAt(contact.BoundCell) is not null) < 2)
            return TrainAssemblyStatus.MissingContact;

        var locoCapabilities = LiveCapabilities(locomotive, voxels);
        var carriageCapabilities = LiveCapabilities(carriage, voxels);
        if (!HasCapability(locoCapabilities, CapabilityTypes.Propulsion))
            return TrainAssemblyStatus.MissingPropulsion;
        if (!HasCapability(locoCapabilities, CapabilityTypes.Control))
            return TrainAssemblyStatus.MissingControl;
        if (HasLocomotiveCapabilities(carriageCapabilities))
            return TrainAssemblyStatus.MultipleActiveLocomotives;
        if (!HasCapability(locoCapabilities, CapabilityTypes.TrackContact) ||
            !HasCapability(carriageCapabilities, CapabilityTypes.TrackContact))
            return TrainAssemblyStatus.MissingContact;

        var locoCoupler = request.LocomotiveCouplerCell ?? StaticTrainFixture.LocomotiveRearCouplerCell;
        var carriageCoupler = request.CarriageCouplerCell ?? StaticTrainFixture.CarriageFrontCouplerCell;
        if (locomotive.Id == carriage.Id ||
            IsReservedCell(locomotive, locoCoupler) ||
            IsReservedCell(carriage, carriageCoupler))
            return TrainAssemblyStatus.InvalidConnection;

        var fromPart = locomotive.PartAt(locoCoupler);
        var toPart = carriage.PartAt(carriageCoupler);
        if (fromPart is null || toPart is null)
            return TrainAssemblyStatus.MissingConnection;

        if (!StraightTrainSolver.TryValidatePoses(locomotive, carriage, out var poseStatus))
            return poseStatus;

        var locoFront = StraightTrainSolver.WorldContact(locomotive.Transform, locomotive.Contacts[0]);
        var locoRear = StraightTrainSolver.WorldContact(locomotive.Transform, locomotive.Contacts[1]);
        var carriageFront = StraightTrainSolver.WorldContact(carriage.Transform, carriage.Contacts[0]);
        var carriageRear = StraightTrainSolver.WorldContact(carriage.Transform, carriage.Contacts[1]);
        var contactXs = new[] { locoFront.X, locoRear.X, carriageFront.X, carriageRear.X };
        var supportSegments = new List<EntityId>();
        foreach (var worldX in contactXs)
        {
            if (!TrySupportingSegment(rails, world, worldX, out var segmentId))
                return TrainAssemblyStatus.InsufficientSupport;
            supportSegments.Add(segmentId);
        }

        if (!StraightTrainSolver.TryResolveReference(rails, locoFront.X, RailHeading.AlongAlignment, out reference))
            return TrainAssemblyStatus.InsufficientSupport;
        if (!AreOnOnePath(rails, supportSegments))
            return TrainAssemblyStatus.DisconnectedPath;

        connection = new CouplerConnectionQuery(
            locomotive.Id,
            fromPart.Id,
            locoCoupler,
            carriage.Id,
            toPart.Id,
            carriageCoupler);
        return TrainAssemblyStatus.Formed;
    }

    private static bool IsReservedCell(CompositeInstance vehicle, LocalVoxelCoord cell) =>
        vehicle.Contacts.Any(contact => contact.BoundCell == cell) ||
        cell == HollowShellFixture.CoreCell;

    private static bool TrySupportingSegment(
        PhysicalRailState rails,
        VoxelWorld world,
        double worldX,
        out EntityId segmentId)
    {
        foreach (var segment in rails.Segments)
        {
            if (rails.TraversableWorldX(segment.Id, world).Any(interval =>
                    interval.Start <= worldX && worldX <= interval.End))
            {
                segmentId = segment.Id;
                return true;
            }
        }

        segmentId = default;
        return false;
    }

    private static bool AreOnOnePath(PhysicalRailState rails, IReadOnlyList<EntityId> segmentIds)
    {
        var distinct = segmentIds.Distinct().ToArray();
        if (distinct.Length <= 1)
            return true;
        var seen = new HashSet<EntityId> { distinct[0] };
        var queue = new Queue<EntityId>();
        queue.Enqueue(distinct[0]);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var other in rails.Segments.Select(segment => segment.Id))
            {
                if (seen.Contains(other) || !rails.AreConnected(current, other))
                    continue;
                seen.Add(other);
                queue.Enqueue(other);
            }
        }

        return distinct.All(seen.Contains);
    }
}
