using Train.Content;
using Train.Domain.Composites;
using Train.Domain.Rails;

namespace Train.Domain.Voxels;

public sealed partial class VoxelDevelopmentSession
{
    private readonly List<TrainAssembly> _assemblies = new();

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

        var requiredIds = next - NextEntityId + 1;
        if (!CanCommitTransaction())
            return new TrainAssemblyResult(TrainAssemblyStatus.TransactionExhausted);
        if (!CanAllocateEntityIds(requiredIds))
            return new TrainAssemblyResult(TrainAssemblyStatus.EntityIdExhausted);

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
        _assemblies.OrderBy(item => item.Id.Value).Select(ToQuery).ToArray();

    public TrainAssemblyQuery? QueryTrainAssembly(EntityId id)
    {
        var assembly = _assemblies.FirstOrDefault(item => item.Id == id);
        return assembly is null ? null : ToQuery(assembly);
    }

    public TrainAuthoritySnapshot CaptureTrainAuthority() =>
        new(CaptureAuthority(), CaptureRailAuthority(), QueryTrainAssemblies());

    private void DissolveBrokenAssemblies()
    {
        _assemblies.RemoveAll(assembly =>
            assembly.Connections.Any(connection =>
                !HasLivePart(connection.FromVehicleId, connection.FromPartId) ||
                !HasLivePart(connection.ToVehicleId, connection.ToPartId)));
    }

    private bool HasLivePart(EntityId vehicleId, EntityId partId)
    {
        var vehicle = _vehicles.FirstOrDefault(item => item.Id == vehicleId);
        return vehicle?.PartById(partId) is not null;
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

    private TrainAssemblyQuery ToQuery(TrainAssembly assembly)
    {
        var poses = new List<TrainVehiclePoseQuery>(assembly.VehicleIds.Count);
        foreach (var vehicleId in assembly.VehicleIds)
        {
            var vehicle = _vehicles.First(item => item.Id == vehicleId);
            poses.Add(new TrainVehiclePoseQuery(
                vehicle.Id,
                vehicle.Transform,
                StraightTrainSolver.ToWorldContact(vehicle, 0),
                StraightTrainSolver.ToWorldContact(vehicle, 1)));
        }

        return new TrainAssemblyQuery(
            assembly.Id,
            assembly.ActiveLocomotiveId,
            assembly.Reference,
            assembly.VehicleIds,
            assembly.Connections,
            poses);
    }

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
