using Train.Content;
using Train.Domain.Behaviors;
using Train.Domain.Composites;
using Train.Domain.Rails;
using Train.Domain.Simulation;

namespace Train.Domain.Voxels;

public sealed partial class VoxelDevelopmentSession
{
    private readonly List<TrainAssembly> _assemblies = new();
    private readonly SimulationScheduler _hostScheduler = new();

    public TrainAssemblyResult AssembleStaticTrain()
    {
        if (!HasRailway)
            return new TrainAssemblyResult(TrainAssemblyStatus.NoRailway);
        if (_assemblies.Count > 0)
            return new TrainAssemblyResult(TrainAssemblyStatus.AlreadyAssembled);

        var voxels = _catalog.Current ?? throw new InvalidOperationException("Voxel content must be loaded before assembling.");
        if (!TryLoadPublished(StaticTrainFixture.LocomotiveId, StaticTrainFixture.LocomotiveTransform, voxels, out var preparedLoco, out var locoStatus) ||
            preparedLoco is null)
            return new TrainAssemblyResult(MapConstruction(locoStatus));
        if (!TryLoadPublished(StaticTrainFixture.CarriageId, StaticTrainFixture.CarriageTransform, voxels, out var preparedCarriage, out var carriageStatus) ||
            preparedCarriage is null)
            return new TrainAssemblyResult(MapConstruction(carriageStatus));

        var requiredIds = 1UL + (ulong)preparedLoco.Composite.Definition.Voxels.Count
            + 1UL + (ulong)preparedCarriage.Composite.Definition.Voxels.Count
            + 1UL;
        if (!CanCommitTransaction())
            return new TrainAssemblyResult(TrainAssemblyStatus.TransactionExhausted);
        if (!CanAllocateEntityIds(requiredIds))
            return new TrainAssemblyResult(TrainAssemblyStatus.EntityIdExhausted);

        var next = NextEntityId;
        var locomotive = Materialize(preparedLoco.Composite, preparedLoco.Archetype, voxels, preparedLoco.Transform, ref next);
        var carriage = Materialize(preparedCarriage.Composite, preparedCarriage.Archetype, voxels, preparedCarriage.Transform, ref next);
        var request = new TrainAssemblyRequest(
            locomotive.Id,
            carriage.Id,
            StaticTrainFixture.LocomotiveRearCouplerCell,
            StaticTrainFixture.CarriageFrontCouplerCell);
        var status = TrainAssemblyRules.Validate(
            locomotive,
            carriage,
            request,
            _rails,
            World,
            voxels,
            alreadyAssembled: false,
            out var connection,
            out var reference);
        if (status != TrainAssemblyStatus.Formed || connection is null)
            return new TrainAssemblyResult(status);

        var assemblyId = new EntityId(next++);
        _vehicles.Add(locomotive);
        _vehicles.Add(carriage);
        _assemblies.Add(new TrainAssembly(
            assemblyId,
            locomotive.Id,
            reference,
            new[] { locomotive.Id, carriage.Id },
            new[] { connection }));
        NextEntityId = next;
        CommitTransaction();
        _rebuildComposites = true;
        Changed?.Invoke();
        return new TrainAssemblyResult(TrainAssemblyStatus.Formed, assemblyId);
    }

    public TrainAssemblyResult AssembleTrain(TrainAssemblyRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var locomotive = _vehicles.FirstOrDefault(item => item.Id == request.LocomotiveId);
        var carriage = _vehicles.FirstOrDefault(item => item.Id == request.CarriageId);
        if (locomotive is null || carriage is null)
            return new TrainAssemblyResult(TrainAssemblyStatus.UnknownVehicle);

        var voxels = _catalog.Current ?? throw new InvalidOperationException("Voxel content must be loaded before assembling.");
        var posedLoco = locomotive.WithTransform(StaticTrainFixture.LocomotiveTransform);
        var posedCarriage = carriage.WithTransform(StaticTrainFixture.CarriageTransform);
        var status = TrainAssemblyRules.Validate(
            posedLoco,
            posedCarriage,
            request,
            _rails,
            World,
            voxels,
            alreadyAssembled: _assemblies.Count > 0,
            out var connection,
            out var reference);
        if (status != TrainAssemblyStatus.Formed || connection is null)
            return new TrainAssemblyResult(status);
        if (!CanCommitTransaction())
            return new TrainAssemblyResult(TrainAssemblyStatus.TransactionExhausted);
        if (!CanAllocateEntityIds(1))
            return new TrainAssemblyResult(TrainAssemblyStatus.EntityIdExhausted);

        ReplaceVehicle(posedLoco);
        ReplaceVehicle(posedCarriage);
        var assemblyId = new EntityId(NextEntityId++);
        _assemblies.Add(new TrainAssembly(
            assemblyId,
            posedLoco.Id,
            reference,
            new[] { posedLoco.Id, posedCarriage.Id },
            new[] { connection }));
        CommitTransaction();
        _rebuildComposites = true;
        Changed?.Invoke();
        return new TrainAssemblyResult(TrainAssemblyStatus.Formed, assemblyId);
    }

    public IReadOnlyList<TrainAssemblyQuery> QueryTrainAssemblies() =>
        _assemblies.OrderBy(item => item.Id.Value).Select(ToQuery).OfType<TrainAssemblyQuery>().ToArray();

    public TrainAssemblyQuery? QueryTrainAssembly(EntityId id)
    {
        var assembly = _assemblies.FirstOrDefault(item => item.Id == id);
        return assembly is null ? null : ToQuery(assembly);
    }

    public TrainAuthoritySnapshot CaptureTrainAuthority() =>
        new(CaptureAuthority(), CaptureRailAuthority(), QueryTrainAssemblies(), SimulationStep);

    public ulong CollectHostSteps(double displayDelta) =>
        _hostScheduler.CollectSteps(displayDelta, SimulationPaused);

    public TrainMotionResult StartTrain(RailHeading heading)
    {
        if (heading == RailHeading.AgainstAlignment)
            return RejectMotion(TrainMotionStatus.ReverseUnsupported);
        if (!HasRailway)
            return RejectMotion(TrainMotionStatus.NoRailway);
        if (_assemblies.Count == 0)
            return RejectMotion(TrainMotionStatus.NoAssembly);
        return ApplyStartNow();
    }

    public TrainMotionResult StopTrain()
    {
        if (_assemblies.Count == 0)
            return RejectMotion(TrainMotionStatus.NoAssembly);
        return ApplyStopNow();
    }

    public TrainMotionResult PauseSimulation()
    {
        SimulationPaused = true;
        Changed?.Invoke();
        return new TrainMotionResult(TrainMotionStatus.Paused, SimulationStep);
    }

    public TrainMotionResult ResumeSimulation()
    {
        SimulationPaused = false;
        Changed?.Invoke();
        return new TrainMotionResult(TrainMotionStatus.Resumed, SimulationStep);
    }

    public TrainMotionResult AdvanceSimulation(ulong steps)
    {
        if (steps == 0)
            return new TrainMotionResult(TrainMotionStatus.Advanced, SimulationStep, 0);
        if (steps > ulong.MaxValue - SimulationStep)
            return RejectMotion(TrainMotionStatus.StepOverflow);

        var snapshot = CaptureMotionSnapshot();
        for (ulong i = 0; i < steps; i++)
        {
            if (_assemblies.Count > 0 && !TryAdvanceOneAssembly(out var status))
            {
                RestoreMotionSnapshot(snapshot);
                return RejectMotion(status);
            }

            SimulationStep++;
        }

        Changed?.Invoke();
        return new TrainMotionResult(TrainMotionStatus.Advanced, SimulationStep, steps);
    }

    private void DissolveBrokenAssemblies()
    {
        _assemblies.RemoveAll(assembly =>
            assembly.Connections.Any(connection =>
                !HasLivePart(connection.FromVehicleId, connection.FromPartId) ||
                !HasLivePart(connection.ToVehicleId, connection.ToPartId)) ||
            assembly.VehicleIds.Any(id => !HasRequiredContacts(id)));
    }

    private bool HasLivePart(EntityId vehicleId, EntityId partId)
    {
        var vehicle = _vehicles.FirstOrDefault(item => item.Id == vehicleId);
        return vehicle?.PartById(partId) is not null;
    }

    private bool HasRequiredContacts(EntityId vehicleId)
    {
        var vehicle = _vehicles.FirstOrDefault(item => item.Id == vehicleId);
        return vehicle is not null &&
               vehicle.Contacts.Count(contact => vehicle.PartAt(contact.BoundCell) is not null) >= 2;
    }

    private bool TryLoadPublished(
        CompositeDefinitionId definitionId,
        CompositeTransform transform,
        VoxelCatalogSnapshot voxels,
        out PreparedVehicle? prepared,
        out ConstructionStatus status)
    {
        prepared = null;
        status = ConstructionStatus.UnknownDefinition;
        if (!_composites.Current.TryGet(definitionId, out var compositeEntry))
            return false;
        if (!_composites.Current.TryGet(compositeEntry.Definition.ArchetypeId, out var archetypeEntry))
        {
            status = ConstructionStatus.UnknownArchetype;
            return false;
        }

        status = ConstructionValidator.Validate(compositeEntry.Definition, archetypeEntry.Archetype, voxels, transform);
        if (status != ConstructionStatus.Valid)
            return false;
        prepared = new PreparedVehicle(compositeEntry, archetypeEntry, transform);
        return true;
    }

    private static CompositeInstance Materialize(
        CompositeCatalogEntry compositeEntry,
        CompositeArchetypeCatalogEntry archetypeEntry,
        VoxelCatalogSnapshot voxels,
        CompositeTransform transform,
        ref ulong next)
    {
        var definition = compositeEntry.Definition;
        var vehicleId = new EntityId(next++);
        var parts = new List<CompositePart>(definition.Voxels.Count);
        foreach (var spec in definition.Voxels.OrderBy(item => item.Cell))
        {
            if (!voxels.TryGet(spec.DefinitionId, out var voxelEntry))
                throw new InvalidOperationException($"Prepared vehicle is missing voxel {spec.DefinitionId}.");
            parts.Add(new CompositePart(
                new EntityId(next++),
                spec.Cell,
                spec.DefinitionId,
                spec.Orientation,
                voxelEntry.Definition.MaxDurability,
                voxelEntry.SemanticRevision));
        }

        return new CompositeInstance(
            vehicleId,
            definition.Id,
            compositeEntry.SemanticRevision,
            definition.ArchetypeId,
            archetypeEntry.SemanticRevision,
            definition.SizeClass,
            transform,
            parts,
            definition.Contacts);
    }

    private void ReplaceVehicle(CompositeInstance vehicle)
    {
        var index = _vehicles.FindIndex(item => item.Id == vehicle.Id);
        if (index < 0)
            throw new InvalidOperationException($"Vehicle {vehicle.Id} is missing during assembly.");
        _vehicles[index] = vehicle;
    }

    private TrainAssemblyQuery? ToQuery(TrainAssembly assembly)
    {
        var poses = new List<TrainVehiclePoseQuery>(assembly.VehicleIds.Count);
        foreach (var vehicleId in assembly.VehicleIds)
        {
            var vehicle = _vehicles.FirstOrDefault(item => item.Id == vehicleId);
            if (vehicle is null ||
                !StraightTrainSolver.TryToWorldContact(vehicle, 0, out var front) ||
                !StraightTrainSolver.TryToWorldContact(vehicle, 1, out var rear))
                return null;
            poses.Add(new TrainVehiclePoseQuery(vehicle.Id, vehicle.Transform, front, rear));
        }

        return new TrainAssemblyQuery(
            assembly.Id,
            assembly.ActiveLocomotiveId,
            assembly.Reference,
            assembly.VehicleIds,
            assembly.Connections,
            poses,
            assembly.Speed,
            assembly.Running);
    }

    private void ResetMotionState()
    {
        SimulationStep = 0;
        SimulationPaused = false;
        _hostScheduler.Reset();
    }

    private TrainMotionResult ApplyStartNow()
    {
        if (!CanStartNow(out var status))
            return RejectMotion(status);
        var assembly = _assemblies[0];
        if (assembly.Running)
            return new TrainMotionResult(TrainMotionStatus.Started, SimulationStep);
        if (!CanCommitTransaction())
            return RejectMotion(TrainMotionStatus.TransactionExhausted);
        CommitTransaction();
        ApplyStartCore();
        Changed?.Invoke();
        return new TrainMotionResult(TrainMotionStatus.Started, SimulationStep);
    }

    private TrainMotionResult ApplyStopNow()
    {
        var assembly = _assemblies[0];
        if (!assembly.Running && assembly.Speed == 0)
            return new TrainMotionResult(TrainMotionStatus.Stopped, SimulationStep);
        if (!CanCommitTransaction())
            return RejectMotion(TrainMotionStatus.TransactionExhausted);
        CommitTransaction();
        ApplyStopCore();
        Changed?.Invoke();
        return new TrainMotionResult(TrainMotionStatus.Stopped, SimulationStep);
    }

    private void ApplyStartCore()
    {
        var assembly = _assemblies[0];
        _assemblies[0] = assembly.WithMotion(assembly.Speed, running: true);
    }

    private void ApplyStopCore() =>
        _assemblies[0] = _assemblies[0].WithMotion(0, running: false);

    private bool CanStartNow(out TrainMotionStatus status)
    {
        status = TrainMotionStatus.Started;
        if (!HasRailway)
        {
            status = TrainMotionStatus.NoRailway;
            return false;
        }

        if (_assemblies.Count == 0)
        {
            status = TrainMotionStatus.NoAssembly;
            return false;
        }

        var voxels = _catalog.Current ?? throw new InvalidOperationException("Voxel content must be loaded before starting a train.");
        var assembly = _assemblies[0];
        var locomotive = _vehicles.FirstOrDefault(item => item.Id == assembly.ActiveLocomotiveId);
        var carriage = _vehicles.FirstOrDefault(item => item.Id == assembly.VehicleIds.FirstOrDefault(id => id != assembly.ActiveLocomotiveId));
        if (locomotive is null || carriage is null)
        {
            status = TrainMotionStatus.NoAssembly;
            return false;
        }

        var capabilities = TrainAssemblyRules.LiveCapabilities(locomotive, voxels);
        if (!TrainAssemblyRules.HasCapability(capabilities, CapabilityTypes.Propulsion))
        {
            status = TrainMotionStatus.MissingPropulsion;
            return false;
        }

        if (!TrainAssemblyRules.HasCapability(capabilities, CapabilityTypes.Control))
        {
            status = TrainMotionStatus.MissingControl;
            return false;
        }

        if (!StraightTrainMotion.TryResolvePropulsion(locomotive, voxels, out _))
        {
            status = TrainMotionStatus.MissingPropulsion;
            return false;
        }

        if (assembly.Connections.Count == 0 ||
            assembly.Connections.Any(connection =>
                !HasLivePart(connection.FromVehicleId, connection.FromPartId) ||
                !HasLivePart(connection.ToVehicleId, connection.ToPartId)))
        {
            status = TrainMotionStatus.VehicleConstraint;
            return false;
        }

        if (!TryAssemblyContacts(locomotive, carriage, out var contacts))
        {
            status = TrainMotionStatus.VehicleConstraint;
            return false;
        }

        if (!StraightTrainSolver.HasContinuousSupport(_rails, World, contacts, assembly.Reference))
        {
            status = TrainMotionStatus.InsufficientSupport;
            return false;
        }

        return true;
    }

    private bool TryAdvanceOneAssembly(out TrainMotionStatus status)
    {
        status = TrainMotionStatus.Advanced;
        var voxels = _catalog.Current ?? throw new InvalidOperationException("Voxel content must be loaded before advancing simulation.");
        var assembly = _assemblies[0];
        var locomotive = _vehicles.FirstOrDefault(item => item.Id == assembly.ActiveLocomotiveId);
        var carriage = _vehicles.FirstOrDefault(item => item.Id == assembly.VehicleIds.FirstOrDefault(id => id != assembly.ActiveLocomotiveId));
        if (locomotive is null || carriage is null)
        {
            status = TrainMotionStatus.NoAssembly;
            return false;
        }

        if (!StraightTrainMotion.TryAdvanceAssembly(
                assembly,
                locomotive,
                carriage,
                _rails,
                World,
                voxels,
                out var updated,
                out var newLoco,
                out var newCarriage,
                out status))
            return false;

        ReplaceVehicle(newLoco);
        ReplaceVehicle(newCarriage);
        _assemblies[0] = updated;
        return true;
    }

    private MotionSnapshot CaptureMotionSnapshot() =>
        new(
            SimulationStep,
            _vehicles.ToArray(),
            _assemblies.ToArray(),
            TransactionCount);

    private void RestoreMotionSnapshot(MotionSnapshot snapshot)
    {
        SimulationStep = snapshot.Step;
        _vehicles.Clear();
        _vehicles.AddRange(snapshot.Vehicles);
        _assemblies.Clear();
        _assemblies.AddRange(snapshot.Assemblies);
        TransactionCount = snapshot.TransactionCount;
    }

    private void AdaptAssembliesToLiveCatalog()
    {
        var voxels = _catalog.Current;
        if (voxels is null)
            return;
        for (var index = 0; index < _assemblies.Count; index++)
        {
            var assembly = _assemblies[index];
            var locomotive = _vehicles.FirstOrDefault(item => item.Id == assembly.ActiveLocomotiveId);
            if (locomotive is null || !StraightTrainMotion.TryResolvePropulsion(locomotive, voxels, out var propulsion))
                continue;
            var speed = Math.Min(assembly.Speed, propulsion.TargetSpeed);
            if (speed != assembly.Speed)
                _assemblies[index] = assembly.WithMotion(speed, assembly.Running);
        }
    }

    private TrainMotionResult RejectMotion(TrainMotionStatus status) =>
        new(status, SimulationStep);

    private static bool TryAssemblyContacts(
        CompositeInstance locomotive,
        CompositeInstance carriage,
        out (double X, double Y, double Z)[] contacts)
    {
        contacts = Array.Empty<(double X, double Y, double Z)>();
        if (!StraightTrainSolver.TryToWorldContact(locomotive, 0, out var locoFront) ||
            !StraightTrainSolver.TryToWorldContact(locomotive, 1, out var locoRear) ||
            !StraightTrainSolver.TryToWorldContact(carriage, 0, out var carriageFront) ||
            !StraightTrainSolver.TryToWorldContact(carriage, 1, out var carriageRear))
            return false;
        contacts =
        [
            (locoFront.X, locoFront.Y, locoFront.Z),
            (locoRear.X, locoRear.Y, locoRear.Z),
            (carriageFront.X, carriageFront.Y, carriageFront.Z),
            (carriageRear.X, carriageRear.Y, carriageRear.Z)
        ];
        return true;
    }

    private sealed record MotionSnapshot(
        ulong Step,
        IReadOnlyList<CompositeInstance> Vehicles,
        IReadOnlyList<TrainAssembly> Assemblies,
        ulong TransactionCount);

    private static TrainAssemblyStatus MapConstruction(ConstructionStatus status) => status switch
    {
        ConstructionStatus.UnknownDefinition => TrainAssemblyStatus.UnknownVehicle,
        ConstructionStatus.UnknownArchetype => TrainAssemblyStatus.UnknownVehicle,
        ConstructionStatus.MissingContact => TrainAssemblyStatus.MissingContact,
        ConstructionStatus.TransactionExhausted => TrainAssemblyStatus.TransactionExhausted,
        ConstructionStatus.EntityIdExhausted => TrainAssemblyStatus.EntityIdExhausted,
        _ => TrainAssemblyStatus.VehicleConstraint
    };

    private sealed record PreparedVehicle(
        CompositeCatalogEntry Composite,
        CompositeArchetypeCatalogEntry Archetype,
        CompositeTransform Transform);
}
