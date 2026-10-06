using Train.Core.Content.Composites;
using Train.Core.Content;
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

        return LegacyBehaviorCapabilities.CapabilitiesFrom(behaviors);
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
            IsReservedCell(carriage, carriageCoupler) ||
            !StraightTrainSolver.TryValidateCouplerConnection(
                locomotive,
                carriage,
                locoCoupler,
                carriageCoupler,
                out _))
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
        var contacts = new[] { locoFront, locoRear, carriageFront, carriageRear };
        foreach (var contact in contacts)
        {
            if (!StraightTrainSolver.TrySupportingSegment(rails, world, contact, out _))
                return TrainAssemblyStatus.InsufficientSupport;
        }

        if (!StraightTrainSolver.TryResolveReference(
                rails,
                world,
                locoFront,
                RailHeading.AlongAlignment,
                out reference))
            return TrainAssemblyStatus.InsufficientSupport;
        if (!StraightTrainSolver.HasContinuousSupport(rails, world, contacts, reference))
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
}
