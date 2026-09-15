using Train.Content;
using Train.Domain.Composites;
using Train.Domain.Rails;
using Train.Domain.Simulation;

namespace Train.Domain.Voxels;

public sealed partial class VoxelDevelopmentSession
{
    private readonly VoxelCatalog _catalog;
    private readonly CompositeCatalog _composites;
    private readonly IWorldSaveStore? _store;
    private readonly Action<VoxelCatalogChange> _onCatalogChanged;
    private readonly Action<CompositeCatalogChange> _onCompositesChanged;
    private readonly List<(WorldVoxelCoord Position, bool OccupancyChanged)> _pendingChanges = new();
    private readonly List<WorldVoxelCoord> _pendingCollisionChanges = new();
    private readonly List<CompositeInstance> _vehicles = new();
    private readonly PhysicalRailState _rails = new();
    private readonly List<ChunkCoord> _pendingTrackChunks = new();
    private bool _rebuildAll = true;
    private bool _rebuildAllCollision = true;
    private bool _rebuildComposites = true;
    private bool _rebuildTracks = true;

    public VoxelWorldIdentity Identity { get; }
    public VoxelWorld World { get; private set; }
    public VoxelWorldCommands Commands { get; private set; }
    public Guid? SaveId { get; private set; }
    public string DisplayName { get; private set; } = WorldSaveDocument.DefaultDisplayName;
    public int DefinitionCount => _catalog.Current?.Entries.Count ?? 0;
    public ulong NextEntityId { get; private set; } = 1;
    public ulong TransactionCount { get; private set; }
    public ulong SimulationStep { get; private set; }
    public bool SimulationPaused { get; private set; }
    public IReadOnlyList<CompositeInstance> Vehicles => _vehicles;
    public PhysicalRailState Rails => _rails;
    public bool HasRailway => _rails.HasRailway;
    internal CompositeCatalogSnapshot CurrentComposites => _composites.Current;

    public event Action? Changed;

    public VoxelDevelopmentSession(
        VoxelCatalog catalog,
        VoxelWorldIdentity? identity = null,
        IWorldSaveStore? store = null,
        CompositeCatalog? composites = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _catalog = catalog;
        _composites = composites ?? new CompositeCatalog();
        _store = store;
        Identity = identity ?? VoxelWorldIdentity.Slice1;
        World = CreateWorld();
        Commands = new VoxelWorldCommands(World, _catalog);
        _onCatalogChanged = _ => MarkCatalogDirty();
        _onCompositesChanged = _ => MarkCatalogDirty();
        _catalog.Changed += _onCatalogChanged;
        _composites.Changed += _onCompositesChanged;
    }

    public void Detach()
    {
        _catalog.Changed -= _onCatalogChanged;
        _composites.Changed -= _onCompositesChanged;
    }

    internal void ApplyContentReload(
        IReadOnlyList<(WorldVoxelCoord Position, WorldVoxel Voxel)> worldReplacements,
        IReadOnlyList<CompositeInstance>? vehicles)
    {
        ArgumentNullException.ThrowIfNull(worldReplacements);
        if (worldReplacements.Count > 0)
            World.ReplaceOccupied(worldReplacements);
        if (vehicles is not null)
        {
            _vehicles.Clear();
            _vehicles.AddRange(vehicles);
            _vehicles.Sort((left, right) => left.Id.Value.CompareTo(right.Id.Value));
        }

        AdaptAssembliesToLiveCatalog();
    }

    private void MarkCatalogDirty()
    {
        AdaptAssembliesToLiveCatalog();
        _rebuildAll = true;
        _rebuildAllCollision = true;
        _rebuildComposites = true;
        _rebuildTracks = true;
        Changed?.Invoke();
    }

    public void Reset()
    {
        World = CreateWorld();
        Commands = new VoxelWorldCommands(World, _catalog);
        _vehicles.Clear();
        _rails.Clear();
        _assemblies.Clear();
        NextEntityId = 1;
        TransactionCount = 0;
        ResetMotionState();
        _pendingChanges.Clear();
        _pendingCollisionChanges.Clear();
        _pendingTrackChunks.Clear();
        _rebuildAll = true;
        _rebuildAllCollision = true;
        _rebuildComposites = true;
        _rebuildTracks = true;
        Changed?.Invoke();
    }

    public WorldCommandResult Add(WorldVoxelCoord position, VoxelDefinitionId definitionId, VoxelOrientation orientation)
    {
        var result = Commands.Add(position, definitionId, orientation);
        if (result.Succeeded)
            NoteChange(position, occupancyChanged: true);
        return result;
    }

    public WorldCommandResult Remove(WorldVoxelCoord position)
    {
        var result = Commands.Remove(position);
        if (result.Succeeded)
            NoteChange(position, occupancyChanged: true);
        return result;
    }

    public WorldDamageResult Damage(WorldVoxelCoord position, int amount) =>
        Damage(new[] { new VoxelDamageTarget(position, amount) });

    public WorldDamageResult Damage(IReadOnlyList<VoxelDamageTarget> targets)
    {
        var result = Commands.Damage(targets);
        if (result.Succeeded)
        {
            foreach (var target in result.Targets)
                NoteChange(target.Position, occupancyChanged: target.Status == WorldCommandStatus.Removed);
        }

        return result;
    }

    public CompositeDamageResult Damage(EntityId vehicleId, LocalVoxelCoord cell, int amount) =>
        Damage(new[] { new CompositeDamageTarget(vehicleId, cell, amount) });

    public CompositeDamageResult Damage(IReadOnlyList<CompositeDamageTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        if (targets.Count == 0)
            return new CompositeDamageResult(CompositeDamageStatus.InvalidDamage);

        foreach (var target in targets)
        {
            if (target.Amount <= 0)
                return new CompositeDamageResult(CompositeDamageStatus.InvalidDamage, target.VehicleId);
            var vehicle = _vehicles.FirstOrDefault(item => item.Id == target.VehicleId);
            if (vehicle is null)
                return new CompositeDamageResult(CompositeDamageStatus.UnknownVehicle, target.VehicleId);
            if (vehicle.PartAt(target.Cell) is null)
                return new CompositeDamageResult(CompositeDamageStatus.Empty, target.VehicleId);
        }

        var tracked = targets.Select(target =>
        {
            var part = _vehicles.First(item => item.Id == target.VehicleId).PartAt(target.Cell)!;
            return (Target: target, PartId: part.Id);
        }).ToArray();
        if (!CanCommitTransaction())
            return new CompositeDamageResult(CompositeDamageStatus.TransactionExhausted, targets[0].VehicleId);
        var affected = targets.Select(target => target.VehicleId).Distinct().OrderBy(id => id.Value).ToArray();
        var plans = affected.Select(vehicleId => PlanVehicleDamage(
            _vehicles.First(vehicle => vehicle.Id == vehicleId),
            targets.Where(target => target.VehicleId == vehicleId).ToArray())).ToArray();
        var requiredIds = plans.Sum(plan => (long)Math.Max(0, plan.Survivors.Count - 1));
        if (!CanAllocateEntityIds((ulong)requiredIds))
            return new CompositeDamageResult(CompositeDamageStatus.EntityIdExhausted, targets[0].VehicleId);
        CommitTransaction();

        var allocatedFrom = NextEntityId;
        var reserved = new HashSet<WorldVoxelCoord>();
        foreach (var plan in plans)
            SettleVehicleDamage(plan, reserved);

        var outcomes = new CompositeDamageTargetResult[targets.Count];
        for (var index = 0; index < targets.Count; index++)
            outcomes[index] = DescribeSettledTarget(tracked[index].Target, tracked[index].PartId);

        var overall = _vehicles.Any(vehicle =>
                affected.Contains(vehicle.Id) ||
                (vehicle.Id.Value >= allocatedFrom && vehicle.Id.Value < NextEntityId))
            ? CompositeDamageStatus.Damaged
            : CompositeDamageStatus.Removed;

        DissolveBrokenAssemblies();
        _rebuildComposites = true;
        Changed?.Invoke();
        return new CompositeDamageResult(overall, targets[0].VehicleId, outcomes);
    }

    public RailEditResult ConstructRails(RailConstructionPlan? plan = null)
    {
        plan ??= StraightRailFixture.CreateFixturePlan();
        if (_catalog.Current is null)
            return new RailEditResult(RailEditStatus.UnknownDefinition);

        var preparedStatus = RailEditTransaction.TryPrepare(plan, World, _catalog.Current, _rails, out var prepared);
        if (preparedStatus != RailEditStatus.Committed)
            return new RailEditResult(preparedStatus);
        if (!CanCommitTransaction())
            return new RailEditResult(RailEditStatus.TransactionExhausted);
        if (!CanAllocateEntityIds((ulong)prepared.RequiredEntityIds))
            return new RailEditResult(RailEditStatus.EntityIdExhausted);

        foreach (var cell in prepared.CellsToAdd)
        {
            var added = Commands.Add(cell, StraightRailFixture.RailDefinitionId, VoxelOrientation.Identity);
            if (!added.Succeeded)
                throw new InvalidOperationException($"Prepared rail cell {cell} could not be added: {added.Status}.");
            NoteChange(cell, occupancyChanged: true, publish: false);
            MarkTrackChunk(cell);
        }

        var firstId = NextEntityId;
        NextEntityId += (ulong)prepared.RequiredEntityIds;
        CommitTransaction();
        _rails.MergeCommitted(RailEditTransaction.Commit(prepared, plan, firstId));
        Changed?.Invoke();
        return new RailEditResult(RailEditStatus.Committed, _rails.Segments.Select(segment => segment.Id).ToArray());
    }

    public RailRepairResult RepairRails(IReadOnlyList<WorldVoxelCoord> cells)
    {
        ArgumentNullException.ThrowIfNull(cells);
        if (cells.Count == 0)
            return new RailRepairResult(RailRepairStatus.InvalidRepair, default);

        foreach (var cell in cells)
        {
            var status = _rails.ValidateRepair(cell, World, out _);
            if (status != RailRepairStatus.Repaired)
                return new RailRepairResult(status, cell);
        }

        if (!CanCommitTransaction())
            return new RailRepairResult(RailRepairStatus.TransactionExhausted, cells[0]);

        if (_catalog.Current is null ||
            !_catalog.Current.TryGet(StraightRailFixture.RailDefinitionId, out var entry))
            return new RailRepairResult(RailRepairStatus.Conflict, cells[0]);

        CommitTransaction();
        var restored = new List<WorldVoxelCoord>();
        foreach (var cell in cells)
        {
            var query = World.Query(cell);
            var voxel = new WorldVoxel(
                StraightRailFixture.RailDefinitionId,
                entry.Definition.MaxDurability,
                VoxelOrientation.Identity);
            if (query.IsOccupied)
            {
                if (!World.TryReplace(cell, voxel))
                    throw new InvalidOperationException($"Could not restore rail cell {cell}.");
                NoteChange(cell, occupancyChanged: false, publish: false);
            }
            else
            {
                if (!World.TryAdd(cell, voxel))
                    throw new InvalidOperationException($"Could not restore missing rail cell {cell}.");
                NoteChange(cell, occupancyChanged: true, publish: false);
            }

            _rails.NoteRestored(cell);
            restored.Add(cell);
            MarkTrackChunk(cell);
        }

        Changed?.Invoke();
        return new RailRepairResult(RailRepairStatus.Repaired, cells[0], restored);
    }

    public RailQuery QueryRails() => _rails.Query(World);

    public DerivedRailGraph QueryDerivedRailGraph() => _rails.QueryDerivedGraph();

    public void RebuildDerivedRailGraph() => _rails.RebuildDerivedGraph();

    public TrackRenderModel BuildTrackRenderModel() =>
        TrackRenderModel.From(_rails, World, World.ChunkSize);

    public RailAuthoritySnapshot CaptureRailAuthority() =>
        _rails.Capture(World, NextEntityId, TransactionCount);

    public WorldVoxelQuery Query(WorldVoxelCoord position) => Commands.Query(position);

    public VoxelCollisionQueryResult QueryCollision(WorldVoxelCoord cell) => Commands.QueryCollision(cell);

    public VoxelCollisionQueryResult QueryCollision(
        double minX,
        double minY,
        double minZ,
        double maxX,
        double maxY,
        double maxZ) =>
        Commands.QueryCollision(minX, minY, minZ, maxX, maxY, maxZ);

    public VoxelCollisionQueryResult QueryCollision(VoxelCollisionBox probe) => Commands.QueryCollision(probe);

    public CompositeManufactureResult Manufacture(CompositeDefinitionId definitionId, CompositeTransform? transform = null)
    {
        var voxels = _catalog.Current ?? throw new InvalidOperationException("Voxel content must be loaded before manufacturing.");
        if (!_composites.Current.TryGet(definitionId, out var compositeEntry))
            return new CompositeManufactureResult(ConstructionStatus.UnknownDefinition);

        var definition = compositeEntry.Definition;
        if (!_composites.Current.TryGet(definition.ArchetypeId, out var archetypeEntry))
            return new CompositeManufactureResult(ConstructionStatus.UnknownArchetype);

        var pose = transform ?? HollowShellFixture.DefaultTransform;
        var status = ConstructionValidator.Validate(definition, archetypeEntry.Archetype, voxels, pose);
        if (status != ConstructionStatus.Valid)
            return new CompositeManufactureResult(status);

        if (!CanCommitTransaction())
            return new CompositeManufactureResult(ConstructionStatus.TransactionExhausted);
        if (!CanAllocateEntityIds(1UL + (ulong)definition.Voxels.Count))
            return new CompositeManufactureResult(ConstructionStatus.EntityIdExhausted);

        var next = NextEntityId;
        var vehicleId = new EntityId(next++);
        var parts = new List<CompositePart>(definition.Voxels.Count);
        foreach (var spec in definition.Voxels.OrderBy(item => item.Cell))
        {
            if (!voxels.TryGet(spec.DefinitionId, out var voxelEntry))
                return new CompositeManufactureResult(ConstructionStatus.UnknownVoxel);
            parts.Add(new CompositePart(
                new EntityId(next++),
                spec.Cell,
                spec.DefinitionId,
                spec.Orientation,
                voxelEntry.Definition.MaxDurability,
                voxelEntry.SemanticRevision));
        }

        var instance = new CompositeInstance(
            vehicleId,
            definition.Id,
            compositeEntry.SemanticRevision,
            definition.ArchetypeId,
            archetypeEntry.SemanticRevision,
            definition.SizeClass,
            pose,
            parts,
            definition.Contacts);
        _vehicles.Add(instance);
        NextEntityId = next;
        CommitTransaction();
        _rebuildComposites = true;
        Changed?.Invoke();
        return new CompositeManufactureResult(ConstructionStatus.Valid, vehicleId, instance.Parts);
    }

    public CompositeRefitResult Refit(EntityId vehicleId, CompositeRefitOperation operation) =>
        Refit(vehicleId, new[] { operation });

    public CompositeRefitResult Refit(EntityId vehicleId, IReadOnlyList<CompositeRefitOperation> operations)
    {
        ArgumentNullException.ThrowIfNull(operations);
        var index = _vehicles.FindIndex(item => item.Id == vehicleId);
        if (index < 0)
            return new CompositeRefitResult(ConstructionStatus.UnknownVehicle, vehicleId);

        var voxels = _catalog.Current ?? throw new InvalidOperationException("Voxel content must be loaded before refitting.");
        var status = CompositeRefitPlanner.TryPlan(_vehicles[index], operations, voxels, _composites.Current, out var planned);
        if (status != ConstructionStatus.Valid)
            return new CompositeRefitResult(status, vehicleId);

        var current = _vehicles[index];
        if (!TryPublishedOrSaveLocalNames(current, out var sourceName, out var sourceDescription))
            return new CompositeRefitResult(ConstructionStatus.UnknownDefinition, vehicleId);
        if (!CanCommitTransaction())
            return new CompositeRefitResult(ConstructionStatus.TransactionExhausted, vehicleId);
        if (!CanAllocateEntityIds((ulong)planned.Count(part => part.Allocate)))
            return new CompositeRefitResult(ConstructionStatus.EntityIdExhausted, vehicleId);

        var next = NextEntityId;
        var allocated = new List<EntityId>();
        var parts = new List<CompositePart>(planned.Count);
        foreach (var plannedPart in planned)
        {
            if (plannedPart.Allocate)
            {
                var id = new EntityId(next++);
                allocated.Add(id);
                parts.Add(new CompositePart(
                    id,
                    plannedPart.Cell,
                    plannedPart.DefinitionId,
                    plannedPart.Orientation,
                    plannedPart.CurrentDurability,
                    plannedPart.LastUsedRevision));
                continue;
            }

            parts.Add(new CompositePart(
                plannedPart.ExistingId!.Value,
                plannedPart.Cell,
                plannedPart.DefinitionId,
                plannedPart.Orientation,
                plannedPart.CurrentDurability,
                plannedPart.LastUsedRevision));
        }

        var saveLocal = new CompositeDefinition(
            current.DefinitionId,
            sourceName,
            sourceDescription,
            current.ArchetypeId,
            current.SizeClass,
            parts.Select(part => new CompositeVoxelSpec(part.Cell, part.DefinitionId, part.Orientation)).ToArray(),
            current.Contacts);
        var saveLocalRevision = CompositeRevisions.ComputeSemanticRevision(saveLocal);
        var instance = new CompositeInstance(
            current.Id,
            current.DefinitionId,
            saveLocalRevision,
            current.ArchetypeId,
            current.LastUsedArchetypeRevision,
            current.SizeClass,
            current.Transform,
            parts,
            current.Contacts,
            saveLocalRevision,
            saveLocal);
        _vehicles[index] = instance;
        NextEntityId = next;
        CommitTransaction();
        DissolveBrokenAssemblies();
        _rebuildComposites = true;
        Changed?.Invoke();
        return new CompositeRefitResult(ConstructionStatus.Valid, vehicleId, instance.Parts, allocated);
    }

    public CompositeQuery? QueryComposite(EntityId id)
    {
        var instance = _vehicles.FirstOrDefault(item => item.Id == id);
        return instance is null ? null : ToQuery(instance);
    }

    public IReadOnlyList<CompositeQuery> QueryComposites() =>
        _vehicles.OrderBy(item => item.Id.Value).Select(ToQuery).ToArray();

    public IReadOnlyList<CompositeDamagedPartQuery> QueryDamagedParts()
    {
        var voxels = _catalog.Current ?? throw new InvalidOperationException("Voxel content must be loaded.");
        var damaged = new List<CompositeDamagedPartQuery>();
        foreach (var vehicle in _vehicles.OrderBy(item => item.Id.Value))
        {
            foreach (var part in vehicle.Parts)
            {
                if (!voxels.TryGet(part.DefinitionId, out var entry))
                    continue;
                if (part.CurrentDurability >= entry.Definition.MaxDurability)
                    continue;
                damaged.Add(new CompositeDamagedPartQuery(
                    vehicle.Id,
                    part.Id,
                    part.Cell,
                    part.DefinitionId,
                    part.Orientation,
                    part.CurrentDurability,
                    entry.Definition.MaxDurability));
            }
        }

        return damaged;
    }

    public CompositeAuthoritySnapshot CaptureAuthority()
    {
        var world = World.OccupiedVoxels()
            .OrderBy(item => item.Key.X)
            .ThenBy(item => item.Key.Y)
            .ThenBy(item => item.Key.Z)
            .Select(item => (item.Key, item.Value.DefinitionId, item.Value.CurrentDurability, item.Value.Orientation.ToString(), item.Value.RuntimeId?.Value))
            .ToArray();
        return new CompositeAuthoritySnapshot(
            World.VoxelCount,
            world,
            QueryComposites(),
            NextEntityId,
            TransactionCount);
    }

    public bool TakePendingCompositeVisualRebuild()
    {
        var dirty = _rebuildComposites;
        _rebuildComposites = false;
        return dirty;
    }

    public WorldSaveResult Save(string? displayName = null)
    {
        if (_store is null || _catalog.Current is null)
            return new WorldSaveResult(WorldSaveStatus.Unavailable);

        var name = displayName ?? DisplayName;
        if (!WorldSaveDocument.TryNormalizeDisplayName(name, out var normalized))
            return new WorldSaveResult(WorldSaveStatus.InvalidName, SaveId, DisplayName);

        var id = SaveId ?? Guid.NewGuid();
        if (!WorldSaveCodec.TryCapture(
                World,
                _catalog.Current,
                id,
                normalized,
                _vehicles,
                NextEntityId,
                TransactionCount,
                out var document,
                out var captureStatus,
                _rails,
                _assemblies,
                SimulationStep,
                QueryScheduledTrainCommands(),
                ScheduledCommandSequence))
            return new WorldSaveResult(captureStatus, SaveId, DisplayName);

        var json = WorldSaveCodec.Serialize(document);
        if (!_store.TryWrite(id, json, out var writeStatus))
            return new WorldSaveResult(writeStatus, SaveId, DisplayName);

        SaveId = id;
        DisplayName = normalized;
        return new WorldSaveResult(WorldSaveStatus.Saved, SaveId, DisplayName);
    }

    public WorldSaveResult Load(Guid saveId)
    {
        if (_store is null || _catalog.Current is null)
            return new WorldSaveResult(WorldSaveStatus.Unavailable);

        if (!_store.TryRead(saveId, out var json, out var readStatus))
            return new WorldSaveResult(readStatus, SaveId, DisplayName);
        if (!WorldSaveCodec.TryDeserialize(json, out var document, out var parseStatus) ||
            (parseStatus is WorldSaveStatus.Loaded && document is null))
            return new WorldSaveResult(
                parseStatus is WorldSaveStatus.Loaded ? WorldSaveStatus.UnknownFormat : parseStatus,
                SaveId,
                DisplayName);
        if (document.Id != saveId)
            return new WorldSaveResult(WorldSaveStatus.MismatchedId, SaveId, DisplayName);
        if (!WorldSaveRestorer.TryBuild(
                _catalog.Current,
                _composites.Current,
                document,
                Identity,
                out var restore,
                out var buildStatus))
            return new WorldSaveResult(buildStatus, SaveId, DisplayName);

        ReplaceSession(restore);
        SaveId = document.Id;
        DisplayName = document.DisplayName;
        return new WorldSaveResult(WorldSaveStatus.Loaded, SaveId, DisplayName);
    }

    public IReadOnlyList<ChunkCoord> TakePendingDirtyChunks()
    {
        var dirty = new SortedSet<ChunkCoord>();
        if (_rebuildAll)
        {
            foreach (var chunk in VoxelChunks.All(Identity))
                dirty.Add(chunk);
        }
        else
        {
            foreach (var (position, occupancyChanged) in _pendingChanges)
            {
                var chunks = occupancyChanged
                    ? VoxelChunks.AffectedByOccupancy(position, Identity)
                    : VoxelChunks.AffectedByDurability(position, Identity);
                foreach (var chunk in chunks)
                    dirty.Add(chunk);
            }
        }

        _pendingChanges.Clear();
        _rebuildAll = false;
        return dirty.ToArray();
    }

    public IReadOnlyList<ChunkCoord> TakePendingTrackChunks()
    {
        if (_rebuildTracks)
        {
            _rebuildTracks = false;
            _pendingTrackChunks.Clear();
            return VoxelChunks.All(Identity);
        }

        var dirty = _pendingTrackChunks
            .Distinct()
            .OrderBy(chunk => chunk)
            .ToArray();
        _pendingTrackChunks.Clear();
        return dirty;
    }

    public IReadOnlyList<ChunkCoord> TakePendingCollisionDirtyChunks()
    {
        var dirty = new SortedSet<ChunkCoord>();
        if (_rebuildAllCollision)
        {
            foreach (var chunk in VoxelChunks.All(Identity))
                dirty.Add(chunk);
        }
        else
        {
            foreach (var position in _pendingCollisionChanges)
            {
                foreach (var chunk in VoxelChunks.AffectedByCollision(position, Identity))
                    dirty.Add(chunk);
            }
        }

        _pendingCollisionChanges.Clear();
        _rebuildAllCollision = false;
        return dirty.ToArray();
    }

    private void NoteChange(WorldVoxelCoord position, bool occupancyChanged, bool publish = true)
    {
        _pendingChanges.Add((position, occupancyChanged));
        if (occupancyChanged)
            _pendingCollisionChanges.Add(position);
        _rails.NoteWorldChange(position, occupancyChanged);
        if (_rails.Occupies(position))
            MarkTrackChunk(position);
        if (publish)
            Changed?.Invoke();
    }

    private void MarkTrackChunk(WorldVoxelCoord position) =>
        _pendingTrackChunks.Add(VoxelChunks.Of(position, World.ChunkSize));

    private bool CanCommitTransaction() => TransactionCount < ulong.MaxValue - 1;

    // Keep MaxValue as an exhausted cursor: saves require the next ID to exceed every allocated ID.
    private bool CanAllocateEntityIds(ulong count) => NextEntityId != 0 && count <= ulong.MaxValue - NextEntityId;

    private void CommitTransaction()
    {
        if (TransactionCount >= ulong.MaxValue - 1)
            throw new InvalidOperationException("Transaction count is exhausted.");
        TransactionCount++;
    }

    private bool TryPublishedOrSaveLocalNames(
        CompositeInstance vehicle,
        out string name,
        out string description)
    {
        if (_composites.Current.TryGet(vehicle.DefinitionId, out var published))
        {
            name = published.Definition.Name;
            description = published.Definition.Description;
            return true;
        }

        if (vehicle.SaveLocalDefinition is { } local)
        {
            name = local.Name;
            description = local.Description;
            return true;
        }

        name = "";
        description = "";
        return false;
    }

    private void ReplaceSession(WorldSaveRestore restore)
    {
        World = restore.World;
        Commands = new VoxelWorldCommands(World, _catalog);
        _vehicles.Clear();
        _vehicles.AddRange(restore.Vehicles);
        _vehicles.Sort((left, right) => left.Id.Value.CompareTo(right.Id.Value));
        _assemblies.Clear();
        _assemblies.AddRange(restore.Assemblies);
        NextEntityId = restore.NextEntityId;
        TransactionCount = restore.TransactionCount;
        SimulationStep = restore.SimulationStep;
        SimulationPaused = false;
        _scheduled.Clear();
        foreach (var command in restore.ScheduledCommands)
            _scheduled.Add(new ScheduledTrainCommand(command.AtStep, command.Start, command.Sequence));
        _scheduledSequence = restore.ScheduledSequence;
        _hostScheduler.Reset();
        _rails.Clear();
        if (restore.Rails is not null)
        {
            _rails.RestoreFrom(
                restore.Rails.Segments,
                restore.Rails.Connections,
                restore.Rails.OccupancyCells,
                restore.Rails.SleeperCount,
                World);
        }

        AdaptAssembliesToLiveCatalog();
        _pendingChanges.Clear();
        _pendingCollisionChanges.Clear();
        _pendingTrackChunks.Clear();
        _rebuildAll = true;
        _rebuildAllCollision = true;
        _rebuildComposites = true;
        _rebuildTracks = true;
        Changed?.Invoke();
    }

    private sealed record VehicleDamagePlan(
        CompositeInstance Vehicle,
        IReadOnlyList<(IReadOnlyList<CompositePart> Parts, long Value, ulong Candidate)> Survivors,
        IReadOnlyList<IReadOnlyList<CompositePart>> Fragments);

    private VehicleDamagePlan PlanVehicleDamage(
        CompositeInstance vehicle,
        IReadOnlyList<CompositeDamageTarget> targets)
    {
        var remaining = vehicle.Parts.ToDictionary(part => part.Cell);
        var totals = new Dictionary<LocalVoxelCoord, long>();
        foreach (var target in targets)
            totals[target.Cell] = totals.GetValueOrDefault(target.Cell) + target.Amount;

        foreach (var (cell, amount) in totals
            .OrderBy(pair => pair.Key))
        {
            var part = remaining[cell];
            var nextDurability = part.CurrentDurability - amount;
            if (nextDurability > 0)
            {
                remaining[cell] = new CompositePart(
                    part.Id,
                    part.Cell,
                    part.DefinitionId,
                    part.Orientation,
                    (int)nextDurability,
                    part.LastUsedRevision);
            }
            else
            {
                remaining.Remove(cell);
            }
        }

        var voxels = _catalog.Current ?? throw new InvalidOperationException("Voxel content must be loaded.");
        _composites.Current.TryGet(vehicle.ArchetypeId, out var archetypeEntry);
        var components = VoxelConnectivity.Components(remaining.Values.ToArray(), part => part.Cell);
        var survivors = new List<(IReadOnlyList<CompositePart> Parts, long Value, ulong Candidate)>();
        var fragments = new List<IReadOnlyList<CompositePart>>();
        foreach (var component in components)
        {
            if (RetainsIdentity(component, archetypeEntry?.Archetype, voxels))
            {
                survivors.Add((component, ValueOf(component, voxels), component.Min(part => part.Id.Value)));
            }
            else
            {
                fragments.Add(component);
            }
        }

        return new VehicleDamagePlan(vehicle, survivors, fragments);
    }

    private void SettleVehicleDamage(VehicleDamagePlan plan, HashSet<WorldVoxelCoord> reserved)
    {
        var vehicle = plan.Vehicle;
        var survivors = plan.Survivors;
        var replacements = new List<CompositeInstance>();
        if (survivors.Count == 1)
        {
            replacements.Add(Successor(vehicle, vehicle.Id, survivors[0].Parts));
        }
        else if (survivors.Count > 1)
        {
            var maxValue = survivors.Max(item => item.Value);
            var primaryCandidate = SplitMix64.SelectPrimaryCandidate(
                Identity.Seed,
                TransactionCount,
                survivors.Where(item => item.Value == maxValue).Select(item => item.Candidate).ToArray());
            var primary = survivors.First(item => item.Value == maxValue && item.Candidate == primaryCandidate);
            replacements.Add(Successor(vehicle, vehicle.Id, primary.Parts));
            foreach (var secondary in survivors.Where(item => item.Candidate != primary.Candidate).OrderBy(item => item.Candidate))
                replacements.Add(Successor(vehicle, new EntityId(NextEntityId++), secondary.Parts));
        }

        var blockedByVehicles = OccupiedByOtherVehicles(vehicle.Id, replacements);
        foreach (var fragment in plan.Fragments.OrderBy(item => item.Min(part => part.Id.Value)))
            TryPlaceFragment(fragment, vehicle.Transform, reserved, blockedByVehicles);

        _vehicles.Remove(vehicle);
        _vehicles.AddRange(replacements);
        _vehicles.Sort((left, right) => left.Id.Value.CompareTo(right.Id.Value));
    }

    private CompositeDamageTargetResult DescribeSettledTarget(CompositeDamageTarget target, EntityId partId)
    {
        foreach (var vehicle in _vehicles)
        {
            var part = vehicle.Parts.FirstOrDefault(item => item.Id == partId);
            if (part is null)
                continue;
            return new CompositeDamageTargetResult(
                CompositeDamageStatus.Damaged,
                vehicle.Id,
                target.Cell,
                target.Amount,
                part.CurrentDurability,
                partId);
        }

        foreach (var occupied in World.OccupiedVoxels())
        {
            if (occupied.Value.RuntimeId != partId)
                continue;
            return new CompositeDamageTargetResult(
                CompositeDamageStatus.Damaged,
                null,
                target.Cell,
                target.Amount,
                occupied.Value.CurrentDurability,
                partId,
                occupied.Key);
        }

        return new CompositeDamageTargetResult(
            CompositeDamageStatus.Removed,
            null,
            target.Cell,
            target.Amount,
            0,
            partId);
    }

    private static bool RetainsIdentity(
        IReadOnlyList<CompositePart> parts,
        CompositeArchetype? archetype,
        VoxelCatalogSnapshot voxels)
    {
        return archetype is not null &&
               ConstructionValidator.EvaluateIdentity(
                   parts.Select(part => part.DefinitionId),
                   archetype,
                   voxels) == IdentityRetentionStatus.Retained;
    }

    private static long ValueOf(IReadOnlyList<CompositePart> parts, VoxelCatalogSnapshot voxels)
    {
        long sum = 0;
        foreach (var part in parts)
        {
            if (voxels.TryGet(part.DefinitionId, out var entry))
                sum += entry.Definition.Value;
        }

        return sum;
    }

    private static CompositeInstance Successor(
        CompositeInstance source,
        EntityId id,
        IReadOnlyList<CompositePart> parts)
    {
        var contacts = source.Contacts
            .Where(contact => parts.Any(part => part.Cell == contact.BoundCell))
            .ToArray();
        var lastUsedRevision = source.LastUsedRevision;
        string? saveLocalRevision = null;
        CompositeDefinition? saveLocal = null;
        if (source.SaveLocalDefinition is { } previous)
        {
            saveLocal = new CompositeDefinition(
                previous.Id,
                previous.Name,
                previous.Description,
                previous.ArchetypeId,
                previous.SizeClass,
                parts.Select(part => new CompositeVoxelSpec(part.Cell, part.DefinitionId, part.Orientation)).ToArray(),
                contacts);
            saveLocalRevision = CompositeRevisions.ComputeSemanticRevision(saveLocal);
            lastUsedRevision = saveLocalRevision;
        }

        return new CompositeInstance(
            id,
            source.DefinitionId,
            lastUsedRevision,
            source.ArchetypeId,
            source.LastUsedArchetypeRevision,
            source.SizeClass,
            source.Transform,
            parts,
            contacts,
            saveLocalRevision,
            saveLocal);
    }

    private HashSet<WorldVoxelCoord> OccupiedByOtherVehicles(
        EntityId except,
        IEnumerable<CompositeInstance> extras)
    {
        var occupied = new HashSet<WorldVoxelCoord>();
        foreach (var vehicle in _vehicles)
        {
            if (vehicle.Id == except)
                continue;
            AddSnappedOccupancy(occupied, vehicle);
        }

        foreach (var vehicle in extras)
            AddSnappedOccupancy(occupied, vehicle);
        return occupied;
    }

    private static void AddSnappedOccupancy(HashSet<WorldVoxelCoord> occupied, CompositeInstance vehicle)
    {
        foreach (var cell in FragmentGridSnap.TargetCells(
            vehicle.Transform,
            vehicle.Parts.Select(part => part.Cell).ToArray(),
            new WorldVoxelCoord(0, 0, 0)))
        {
            occupied.Add(cell);
        }
    }

    private void TryPlaceFragment(
        IReadOnlyList<CompositePart> parts,
        CompositeTransform transform,
        HashSet<WorldVoxelCoord> reserved,
        HashSet<WorldVoxelCoord> blockedByVehicles)
    {
        var cells = parts.Select(part => part.Cell).ToArray();
        var orientation = FragmentGridSnap.SnapRotation(transform.Orientation);
        foreach (var offset in FragmentGridSnap.SearchOffsets)
        {
            var targets = FragmentGridSnap.TargetCells(transform, cells, offset);
            if (targets.Distinct().Count() != targets.Count)
                continue;
            if (targets.Any(cell =>
                    !World.Bounds.Contains(cell) ||
                    World.Query(cell).IsOccupied ||
                    reserved.Contains(cell) ||
                    blockedByVehicles.Contains(cell)))
                continue;

            for (var index = 0; index < parts.Count; index++)
            {
                var part = parts[index];
                var placed = new WorldVoxel(
                    part.DefinitionId,
                    part.CurrentDurability,
                    FragmentGridSnap.Compose(orientation, part.Orientation),
                    part.Id);
                if (!World.TryAdd(targets[index], placed))
                    throw new InvalidOperationException("Fragment placement passed checks but failed to transfer ownership.");
                reserved.Add(targets[index]);
                NoteChange(targets[index], occupancyChanged: true, publish: false);
            }

            return;
        }
    }

    private CompositeQuery ToQuery(CompositeInstance instance)
    {
        var voxels = _catalog.Current ?? throw new InvalidOperationException("Voxel content must be loaded.");
        _composites.Current.TryGet(instance.ArchetypeId, out var archetypeEntry);
        var construction = archetypeEntry is null
            ? ConstructionStatus.UnknownArchetype
            : ConstructionValidator.Validate(
                new CompositeDefinition(
                    instance.DefinitionId,
                    "query",
                    "query",
                    instance.ArchetypeId,
                    instance.SizeClass,
                    instance.Parts.Select(part => new CompositeVoxelSpec(part.Cell, part.DefinitionId, part.Orientation)).ToArray(),
                    instance.Contacts),
                archetypeEntry.Archetype,
                voxels,
                instance.Transform);
        var identity = archetypeEntry is null
            ? IdentityRetentionStatus.MissingCoreTag
            : ConstructionValidator.EvaluateIdentity(instance, archetypeEntry.Archetype, voxels);
        var boundContacts = instance.Contacts.Count(contact => instance.PartAt(contact.BoundCell) is not null);
        var capabilities = TrainAssemblyRules.LiveCapabilities(instance, voxels);
        var operational = TrainAssemblyRules.EvaluateOperational(boundContacts, capabilities);
        var parts = instance.Parts
            .Select(part => new CompositePartQuery(part.Id, part.Cell, part.DefinitionId, part.Orientation, part.CurrentDurability))
            .ToArray();
        var contacts = instance.Contacts
            .Select(contact =>
            {
                var bound = instance.PartAt(contact.BoundCell);
                return bound is null
                    ? null
                    : new CompositeContactQuery(
                        contact.X,
                        contact.Y,
                        contact.Z,
                        contact.BoundCell,
                        bound.Id);
            })
            .Where(contact => contact is not null)
            .Select(contact => contact!)
            .ToArray();
        return new CompositeQuery(
            instance.Id,
            instance.DefinitionId,
            instance.ArchetypeId,
            instance.SizeClass,
            instance.Transform,
            construction,
            identity,
            operational,
            capabilities,
            parts,
            contacts,
            VehicleEnvelope.TryFromContacts(instance.SizeClass, instance.Contacts, out var envelope) ? envelope : null,
            instance.LastUsedRevision,
            instance.SaveLocalRevision);
    }

    private VoxelWorld CreateWorld() => VoxelWorldFactory.Create(
        _catalog.Current ?? throw new InvalidOperationException("Voxel content must be loaded before entering the development scene."),
        Identity);
}
