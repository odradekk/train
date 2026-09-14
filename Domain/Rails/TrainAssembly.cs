using System.Collections.ObjectModel;
using Train.Domain.Composites;
using Train.Domain.Voxels;

namespace Train.Domain.Rails;

public enum RailHeading
{
    AlongAlignment = 0,
    AgainstAlignment = 1
}

public readonly record struct RailPosition(EntityId SegmentId, double Distance, RailHeading Heading)
{
    public bool IsFinite => double.IsFinite(Distance);
}

public enum TrainAssemblyStatus
{
    Formed = 0,
    UnknownVehicle = 1,
    MissingConnection = 2,
    MissingPropulsion = 3,
    MissingControl = 4,
    MissingContact = 5,
    InsufficientSupport = 6,
    DisconnectedPath = 7,
    InvalidConnection = 8,
    MultipleActiveLocomotives = 9,
    VehicleConstraint = 10,
    AlreadyAssembled = 11,
    NoRailway = 12,
    TransactionExhausted = 13,
    EntityIdExhausted = 14
}

public sealed record TrainAssemblyRequest(
    EntityId LocomotiveId,
    EntityId CarriageId,
    LocalVoxelCoord? LocomotiveCouplerCell = null,
    LocalVoxelCoord? CarriageCouplerCell = null);

public sealed record TrainAssemblyResult(
    TrainAssemblyStatus Status,
    EntityId? AssemblyId = null)
{
    public bool Succeeded => Status == TrainAssemblyStatus.Formed && AssemblyId is not null;
}

public sealed record WorldContactQuery(
    double X,
    double Y,
    double Z,
    LocalVoxelCoord BoundCell,
    EntityId BoundPartId);

public sealed record CouplerConnectionQuery(
    EntityId FromVehicleId,
    EntityId FromPartId,
    LocalVoxelCoord FromCell,
    EntityId ToVehicleId,
    EntityId ToPartId,
    LocalVoxelCoord ToCell);

public sealed record TrainVehiclePoseQuery(
    EntityId VehicleId,
    CompositeTransform Transform,
    WorldContactQuery FrontContact,
    WorldContactQuery RearContact);

public sealed record TrainAssemblyQuery(
    EntityId Id,
    EntityId ActiveLocomotiveId,
    RailPosition Reference,
    IReadOnlyList<EntityId> VehicleIds,
    IReadOnlyList<CouplerConnectionQuery> Connections,
    IReadOnlyList<TrainVehiclePoseQuery> Vehicles,
    double Speed = 0,
    bool Running = false)
{
    public bool Equals(TrainAssemblyQuery? other) =>
        other is not null &&
        Id == other.Id &&
        ActiveLocomotiveId == other.ActiveLocomotiveId &&
        Reference == other.Reference &&
        VehicleIds.SequenceEqual(other.VehicleIds) &&
        Connections.SequenceEqual(other.Connections) &&
        Vehicles.SequenceEqual(other.Vehicles) &&
        Speed == other.Speed &&
        Running == other.Running;

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Id);
        hash.Add(ActiveLocomotiveId);
        hash.Add(Reference);
        foreach (var id in VehicleIds)
            hash.Add(id);
        foreach (var connection in Connections)
            hash.Add(connection);
        foreach (var vehicle in Vehicles)
            hash.Add(vehicle);
        hash.Add(Speed);
        hash.Add(Running);
        return hash.ToHashCode();
    }
}

public sealed record TrainAuthoritySnapshot(
    CompositeAuthoritySnapshot Composites,
    RailAuthoritySnapshot Rails,
    IReadOnlyList<TrainAssemblyQuery> Assemblies,
    ulong SimulationStep = 0,
    IReadOnlyList<ScheduledTrainCommandQuery>? ScheduledCommands = null)
{
    public IReadOnlyList<ScheduledTrainCommandQuery> PendingCommands =>
        ScheduledCommands ?? Array.Empty<ScheduledTrainCommandQuery>();

    public bool Equals(TrainAuthoritySnapshot? other) =>
        other is not null &&
        Composites == other.Composites &&
        Rails == other.Rails &&
        Assemblies.SequenceEqual(other.Assemblies) &&
        SimulationStep == other.SimulationStep &&
        PendingCommands.SequenceEqual(other.PendingCommands);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Composites);
        hash.Add(Rails);
        foreach (var assembly in Assemblies)
            hash.Add(assembly);
        hash.Add(SimulationStep);
        foreach (var command in PendingCommands)
            hash.Add(command);
        return hash.ToHashCode();
    }
}

public sealed class TrainAssembly
{
    public EntityId Id { get; }
    public EntityId ActiveLocomotiveId { get; }
    public RailPosition Reference { get; }
    public IReadOnlyList<EntityId> VehicleIds { get; }
    public IReadOnlyList<CouplerConnectionQuery> Connections { get; }
    public double Speed { get; }
    public bool Running { get; }

    public TrainAssembly(
        EntityId id,
        EntityId activeLocomotiveId,
        RailPosition reference,
        IReadOnlyList<EntityId> vehicleIds,
        IReadOnlyList<CouplerConnectionQuery> connections,
        double speed = 0,
        bool running = false)
    {
        ArgumentNullException.ThrowIfNull(vehicleIds);
        ArgumentNullException.ThrowIfNull(connections);
        if (!double.IsFinite(speed) || speed < 0)
            throw new ArgumentOutOfRangeException(nameof(speed), speed, "Speed must be finite and non-negative.");
        Id = id;
        ActiveLocomotiveId = activeLocomotiveId;
        Reference = reference;
        VehicleIds = new ReadOnlyCollection<EntityId>(vehicleIds.ToArray());
        Connections = new ReadOnlyCollection<CouplerConnectionQuery>(connections.ToArray());
        Speed = speed;
        Running = running;
    }

    public TrainAssembly WithMotion(double speed, bool running) =>
        new(Id, ActiveLocomotiveId, Reference, VehicleIds, Connections, speed, running);

    public TrainAssembly WithReferenceAndMotion(RailPosition reference, double speed, bool running) =>
        new(Id, ActiveLocomotiveId, reference, VehicleIds, Connections, speed, running);
}
