using Train.Content;
using Train.Domain.Behaviors;
using Train.Domain.Composites;
using Train.Domain.Rails;
using Train.Domain.Simulation;

namespace Train.Domain.Voxels;

public sealed partial class VoxelDevelopmentSession
{
    private readonly List<TrainAssembly> _assemblies = new();
    private readonly List<ScheduledTrainCommand> _scheduled = new();
    private readonly SimulationScheduler _hostScheduler = new();
    private ulong _scheduledSequence;

    public ulong ScheduledCommandSequence => _scheduledSequence;

    private readonly record struct ScheduledTrainCommand(
        ulong AtStep,
        bool Start,
        ulong Sequence,
        EntityId AssemblyId);

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
        NoteAssemblyGraphCommit();
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
        NoteAssemblyGraphCommit();
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
        new(CaptureAuthority(), CaptureRailAuthority(), QueryTrainAssemblies(), SimulationStep, QueryScheduledTrainCommands());

    public IReadOnlyList<ScheduledTrainCommandQuery> QueryScheduledTrainCommands() =>
        _scheduled
            .OrderBy(item => item.Sequence)
            .Select(item => new ScheduledTrainCommandQuery(item.AtStep, item.Start, item.Sequence, item.AssemblyId))
            .ToArray();

    public ulong CollectHostSteps(double displayDelta) =>
        _hostScheduler.CollectSteps(displayDelta, SimulationPaused);

    public TrainMotionResult StartTrain(RailHeading heading, ulong? atStep = null, EntityId? assemblyId = null)
    {
        if (heading == RailHeading.AgainstAlignment)
            return RejectMotion(TrainMotionStatus.ReverseUnsupported);
        if (!HasRailway)
            return RejectMotion(TrainMotionStatus.NoRailway);
        if (!TryResolveCommandTarget(assemblyId, out var target, out var status))
            return RejectMotion(status);

        return AcceptTrainCommand(atStep ?? SimulationStep, start: true, target.Id);
    }

    public TrainMotionResult StopTrain(ulong? atStep = null, EntityId? assemblyId = null)
    {
        if (!TryResolveCommandTarget(assemblyId, out var target, out var status))
            return RejectMotion(status);

        return AcceptTrainCommand(atStep ?? SimulationStep, start: false, target.Id);
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
            if (!TryApplyScheduledCommands(SimulationStep, out var scheduledStatus))
            {
                RestoreMotionSnapshot(snapshot);
                return RejectMotion(scheduledStatus);
            }

            if (!TryAdvanceAllAssemblies(out var status))
            {
                RestoreMotionSnapshot(snapshot);
                return RejectMotion(status);
            }

            SimulationStep++;
            if (SimulationStep == ulong.MaxValue &&
                !TryApplyScheduledCommands(SimulationStep, out scheduledStatus))
            {
                RestoreMotionSnapshot(snapshot);
                return RejectMotion(scheduledStatus);
            }
        }

        Changed?.Invoke();
        return new TrainMotionResult(TrainMotionStatus.Advanced, SimulationStep, steps);
    }

    private bool TryResolveCommandTarget(EntityId? assemblyId, out TrainAssembly target, out TrainMotionStatus status)
    {
        target = null!;
        status = TrainMotionStatus.Started;
        if (_assemblies.Count == 0)
        {
            status = TrainMotionStatus.NoAssembly;
            return false;
        }

        if (assemblyId is { } id)
        {
            var found = _assemblies.FirstOrDefault(item => item.Id == id);
            if (found is null)
            {
                status = TrainMotionStatus.UnknownAssembly;
                return false;
            }

            target = found;
            return true;
        }

        if (_assemblies.Count > 1)
        {
            status = TrainMotionStatus.UnknownAssembly;
            return false;
        }

        target = _assemblies[0];
        return true;
    }

    private IReadOnlyList<CompositeInstance> ProjectDamagedVehicles(
        IReadOnlyList<VehicleDamagePlan> plans,
        ref ulong next,
        ulong transactionCount)
    {
        var replacements = _vehicles.ToDictionary(item => item.Id);
        foreach (var plan in plans.OrderBy(item => item.Vehicle.Id.Value))
        {
            replacements.Remove(plan.Vehicle.Id);
            foreach (var successor in MaterializeDamageReplacements(plan, ref next, transactionCount))
                replacements[successor.Id] = successor;
        }

        return replacements.Values.OrderBy(item => item.Id.Value).ToArray();
    }

    private List<CompositeInstance> MaterializeDamageReplacements(
        VehicleDamagePlan plan,
        ref ulong next,
        ulong transactionCount)
    {
        var replacements = new List<CompositeInstance>();
        var survivors = plan.Survivors;
        if (survivors.Count == 1)
        {
            replacements.Add(Successor(plan.Vehicle, plan.Vehicle.Id, survivors[0].Parts));
        }
        else if (survivors.Count > 1)
        {
            var maxValue = survivors.Max(item => item.Value);
            var primaryCandidate = SplitMix64.SelectPrimaryCandidate(
                Identity.Seed,
                transactionCount,
                survivors.Where(item => item.Value == maxValue).Select(item => item.Candidate).ToArray());
            var primary = survivors.First(item => item.Value == maxValue && item.Candidate == primaryCandidate);
            replacements.Add(Successor(plan.Vehicle, plan.Vehicle.Id, primary.Parts));
            foreach (var secondary in survivors.Where(item => item.Candidate != primary.Candidate).OrderBy(item => item.Candidate))
                replacements.Add(Successor(plan.Vehicle, new EntityId(next++), secondary.Parts));
        }

        return replacements;
    }

    private bool TryPlanAssemblyRebuild(
        IReadOnlyList<CompositeInstance> vehicles,
        ref ulong next,
        out IReadOnlyList<TrainAssembly> planned)
    {
        planned = Array.Empty<TrainAssembly>();
        var vehicleById = vehicles.ToDictionary(item => item.Id);
        var partOwner = new Dictionary<EntityId, EntityId>();
        foreach (var vehicle in vehicles)
        {
            foreach (var part in vehicle.Parts)
                partOwner[part.Id] = vehicle.Id;
        }

        var connections = new List<CouplerConnectionQuery>();
        foreach (var connection in _assemblies.SelectMany(item => item.Connections))
        {
            if (!partOwner.TryGetValue(connection.FromPartId, out var fromId) ||
                !partOwner.TryGetValue(connection.ToPartId, out var toId) ||
                fromId == toId)
                continue;
            connections.Add(connection with { FromVehicleId = fromId, ToVehicleId = toId });
        }

        var parent = vehicles.ToDictionary(item => item.Id, item => item.Id);
        EntityId Find(EntityId id)
        {
            while (parent[id] != id)
                id = parent[id] = parent[parent[id]];
            return id;
        }

        void Union(EntityId left, EntityId right)
        {
            var a = Find(left);
            var b = Find(right);
            if (a.Value <= b.Value)
                parent[b] = a;
            else
                parent[a] = b;
        }

        foreach (var connection in connections)
            Union(connection.FromVehicleId, connection.ToVehicleId);

        var groups = vehicles
            .GroupBy(item => Find(item.Id))
            .OrderBy(group => group.Key.Value)
            .ToArray();
        var originAssembly = _vehicles.ToDictionary(
            item => item.Id,
            item => _assemblies.FirstOrDefault(assembly => assembly.VehicleIds.Contains(item.Id)));
        var items = new List<TrainAssembly>();
        foreach (var group in groups)
        {
            var members = group.OrderBy(item => item.Id.Value).ToArray();
            if (members.Any(member => !IsRailEligible(member)))
                continue;
            var memberIds = members.Select(item => item.Id).ToArray();
            var liveConnections = connections
                .Where(item => memberIds.Contains(item.FromVehicleId) && memberIds.Contains(item.ToVehicleId))
                .OrderBy(item => item.FromVehicleId.Value)
                .ThenBy(item => item.ToVehicleId.Value)
                .ToArray();
            if (members.Length == 2 && liveConnections.Length != 1)
                continue;
            if (members.Length > 2 && liveConnections.Length != members.Length - 1)
                continue;
            if (!TryAssemblyContacts(members, out var contacts) ||
                !StraightTrainSolver.TryToWorldContact(members[0], 0, out var front) ||
                !StraightTrainSolver.TryResolveReference(
                    _rails,
                    World,
                    (front.X, front.Y, front.Z),
                    RailHeading.AlongAlignment,
                    out var reference))
                continue;
            if (!StraightTrainSolver.HasContinuousSupport(_rails, World, contacts, reference))
                continue;

            var parents = members
                .Select(member => originAssembly.GetValueOrDefault(OriginVehicleId(member, _vehicles)))
                .OfType<TrainAssembly>()
                .Distinct()
                .OrderBy(item => item.Id.Value)
                .ToArray();
            var inherited = parents.FirstOrDefault(parentAssembly =>
                parentAssembly.ActiveLocomotiveId is { } locoId &&
                memberIds.Contains(locoId));
            EntityId assemblyId;
            if (inherited is not null)
            {
                assemblyId = inherited.Id;
            }
            else
            {
                if (next == 0 || next == ulong.MaxValue)
                    return false;
                assemblyId = new EntityId(next++);
            }

            var source = inherited ?? parents.FirstOrDefault();
            var voxels = _catalog.Current ?? throw new InvalidOperationException("Voxel content must be loaded before rebuilding assemblies.");
            EntityId? activeLoco = null;
            var running = false;
            if (source?.ActiveLocomotiveId is { } sourceLoco &&
                memberIds.Contains(sourceLoco) &&
                vehicleById.TryGetValue(sourceLoco, out var loco) &&
                StraightTrainMotion.TryResolvePropulsion(loco, voxels, out _))
            {
                activeLoco = sourceLoco;
                running = source.Running;
            }

            var orderedIds = inherited is not null
                ? inherited.VehicleIds.Where(memberIds.Contains).Concat(memberIds.Where(id => !inherited.VehicleIds.Contains(id))).ToArray()
                : memberIds;
            items.Add(new TrainAssembly(
                assemblyId,
                activeLoco,
                reference,
                orderedIds,
                liveConnections,
                source?.Speed ?? 0,
                running));
        }

        planned = items.OrderBy(item => item.Id.Value).ToArray();
        return true;
    }

    private static EntityId OriginVehicleId(CompositeInstance vehicle, IReadOnlyList<CompositeInstance> previous)
    {
        if (previous.Any(item => item.Id == vehicle.Id))
            return vehicle.Id;
        return previous
            .Where(item => item.Parts.Any(part => vehicle.Parts.Any(candidate => candidate.Id == part.Id)))
            .Select(item => item.Id)
            .DefaultIfEmpty(vehicle.Id)
            .First();
    }

    private bool IsRailEligible(CompositeInstance vehicle) =>
        StraightTrainSolver.TryCollectLiveContacts(vehicle, out _, out _, out _);

    private void ApplyPlannedAssemblies(IReadOnlyList<TrainAssembly> planned, ulong next)
    {
        _assemblies.Clear();
        _assemblies.AddRange(planned);
        NextEntityId = next;
        NoteAssemblyGraphCommit();
    }

    private void NoteAssemblyGraphCommit()
    {
        if (AssemblyGraphGeneration < ulong.MaxValue)
            AssemblyGraphGeneration++;
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
        _scheduled.Clear();
        _scheduledSequence = 0;
        _hostScheduler.Reset();
    }

    private TrainMotionResult AcceptTrainCommand(ulong atStep, bool start, EntityId assemblyId)
    {
        if (atStep < SimulationStep)
            return RejectMotion(TrainMotionStatus.PastStep, atStep);
        if (start && !CanStartNow(assemblyId, out var status))
            return RejectMotion(status, atStep);
        if (!CanCommitTransaction() || _scheduledSequence == ulong.MaxValue)
            return RejectMotion(TrainMotionStatus.TransactionExhausted, atStep);

        var snapshot = CaptureMotionSnapshot();
        CommitTransaction();
        _scheduled.Add(new ScheduledTrainCommand(atStep, start, _scheduledSequence++, assemblyId));
        if (atStep == SimulationStep && !TryApplyScheduledCommands(SimulationStep, out var applyStatus))
        {
            RestoreMotionSnapshot(snapshot);
            return RejectMotion(applyStatus, atStep);
        }

        Changed?.Invoke();
        return new TrainMotionResult(
            start ? TrainMotionStatus.Started : TrainMotionStatus.Stopped,
            SimulationStep,
            AtStep: atStep);
    }

    private bool TryApplyScheduledCommands(ulong step, out TrainMotionStatus status)
    {
        status = TrainMotionStatus.Advanced;
        var due = _scheduled.Where(item => item.AtStep == step).OrderBy(item => item.Sequence).ToArray();
        foreach (var command in due)
        {
            _scheduled.Remove(command);
            var index = _assemblies.FindIndex(item => item.Id == command.AssemblyId);
            if (index < 0)
                continue;
            if (command.Start)
            {
                if (!CanStartNow(command.AssemblyId, out status) && !_assemblies[index].Running)
                    return false;
                ApplyStartCore(index);
            }
            else
                ApplyStopCore(index);
        }

        return true;
    }

    private void ApplyStartCore(int index)
    {
        var assembly = _assemblies[index];
        _assemblies[index] = assembly.WithMotion(assembly.Speed, running: true);
    }

    private void ApplyStopCore(int index) =>
        _assemblies[index] = _assemblies[index].WithMotion(0, running: false);

    private bool CanStartNow(EntityId assemblyId, out TrainMotionStatus status)
    {
        status = TrainMotionStatus.Started;
        if (!HasRailway)
        {
            status = TrainMotionStatus.NoRailway;
            return false;
        }

        var assembly = _assemblies.FirstOrDefault(item => item.Id == assemblyId);
        if (assembly is null)
        {
            status = TrainMotionStatus.UnknownAssembly;
            return false;
        }

        if (assembly.ActiveLocomotiveId is not { } locoId)
        {
            status = TrainMotionStatus.MissingPropulsion;
            return false;
        }

        var voxels = _catalog.Current ?? throw new InvalidOperationException("Voxel content must be loaded before starting a train.");
        var locomotive = _vehicles.FirstOrDefault(item => item.Id == locoId);
        if (locomotive is null)
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

        if (assembly.Connections.Any(connection =>
                !HasLivePart(connection.FromVehicleId, connection.FromPartId) ||
                !HasLivePart(connection.ToVehicleId, connection.ToPartId)))
        {
            status = TrainMotionStatus.VehicleConstraint;
            return false;
        }

        var members = assembly.VehicleIds
            .Select(id => _vehicles.FirstOrDefault(item => item.Id == id))
            .OfType<CompositeInstance>()
            .ToArray();
        if (members.Length != assembly.VehicleIds.Count ||
            !TryAssemblyContacts(members, out var contacts))
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

    private bool TryAdvanceAllAssemblies(out TrainMotionStatus status)
    {
        status = TrainMotionStatus.Advanced;
        var voxels = _catalog.Current ?? throw new InvalidOperationException("Voxel content must be loaded before advancing simulation.");
        foreach (var assembly in _assemblies.OrderBy(item => item.Id.Value).ToArray())
        {
            var members = assembly.VehicleIds
                .Select(id => _vehicles.FirstOrDefault(item => item.Id == id))
                .OfType<CompositeInstance>()
                .ToArray();
            if (members.Length != assembly.VehicleIds.Count)
            {
                status = TrainMotionStatus.NoAssembly;
                return false;
            }

            if (!StraightTrainMotion.TryAdvanceAssembly(
                    assembly,
                    members,
                    _rails,
                    World,
                    voxels,
                    out var updated,
                    out var moved,
                    out status))
                return false;

            for (var index = 0; index < moved.Count; index++)
                ReplaceVehicle(moved[index]);
            var assemblyIndex = _assemblies.FindIndex(item => item.Id == assembly.Id);
            _assemblies[assemblyIndex] = updated;
        }

        return true;
    }

    private MotionSnapshot CaptureMotionSnapshot() =>
        new(
            SimulationStep,
            _vehicles.ToArray(),
            _assemblies.ToArray(),
            _scheduled.ToArray(),
            _scheduledSequence,
            TransactionCount);

    private void RestoreMotionSnapshot(MotionSnapshot snapshot)
    {
        SimulationStep = snapshot.Step;
        _vehicles.Clear();
        _vehicles.AddRange(snapshot.Vehicles);
        _assemblies.Clear();
        _assemblies.AddRange(snapshot.Assemblies);
        _scheduled.Clear();
        _scheduled.AddRange(snapshot.Scheduled);
        _scheduledSequence = snapshot.ScheduledSequence;
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
            var members = assembly.VehicleIds
                .Select(id => _vehicles.FirstOrDefault(item => item.Id == id))
                .OfType<CompositeInstance>()
                .ToArray();
            if (assembly.Running &&
                assembly.ActiveLocomotiveId is { } locoId)
            {
                var locomotive = members.FirstOrDefault(item => item.Id == locoId);
                if (locomotive is null || !StraightTrainMotion.TryResolvePropulsion(locomotive, voxels, out var propulsion))
                    continue;
                var speed = Math.Min(assembly.Speed, propulsion.TargetSpeed);
                if (speed != assembly.Speed)
                    _assemblies[index] = assembly.WithMotion(speed, assembly.Running);
                continue;
            }

            if (assembly.Speed > 0 && !StraightTrainMotion.TryResolveTrackContact(members, voxels, out _))
                continue;
        }
    }

    private TrainMotionResult RejectMotion(TrainMotionStatus status, ulong? atStep = null) =>
        new(status, SimulationStep, AtStep: atStep ?? SimulationStep);

    private static bool TryAssemblyContacts(
        IReadOnlyList<CompositeInstance> members,
        out (double X, double Y, double Z)[] contacts)
    {
        var collected = new List<(double X, double Y, double Z)>();
        foreach (var member in members)
        {
            if (!StraightTrainSolver.TryCollectLiveContacts(member, out var vehicleContacts, out _, out _))
            {
                contacts = Array.Empty<(double X, double Y, double Z)>();
                return false;
            }

            collected.AddRange(vehicleContacts);
        }

        contacts = collected.ToArray();
        return contacts.Length > 0;
    }

    private sealed record MotionSnapshot(
        ulong Step,
        IReadOnlyList<CompositeInstance> Vehicles,
        IReadOnlyList<TrainAssembly> Assemblies,
        IReadOnlyList<ScheduledTrainCommand> Scheduled,
        ulong ScheduledSequence,
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
