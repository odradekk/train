using Train.Content;
using Train.Domain.Composites;

namespace Train.Domain.Voxels;

public sealed class VoxelDevelopmentSession
{
    private readonly VoxelCatalog _catalog;
    private readonly CompositeCatalog _composites;
    private readonly IWorldSaveStore? _store;
    private readonly Action<VoxelCatalogChange> _onCatalogChanged;
    private readonly List<(WorldVoxelCoord Position, bool OccupancyChanged)> _pendingChanges = new();
    private readonly List<WorldVoxelCoord> _pendingCollisionChanges = new();
    private readonly List<CompositeInstance> _vehicles = new();
    private bool _rebuildAll = true;
    private bool _rebuildAllCollision = true;
    private bool _rebuildComposites = true;

    public VoxelWorldIdentity Identity { get; }
    public VoxelWorld World { get; private set; }
    public VoxelWorldCommands Commands { get; private set; }
    public Guid? SaveId { get; private set; }
    public string DisplayName { get; private set; } = WorldSaveDocument.DefaultDisplayName;
    public int DefinitionCount => _catalog.Current?.Entries.Count ?? 0;
    public ulong NextEntityId { get; private set; } = 1;
    public ulong TransactionCount { get; private set; }
    public IReadOnlyList<CompositeInstance> Vehicles => _vehicles;
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
        _onCatalogChanged = _ =>
        {
            _rebuildAll = true;
            _rebuildAllCollision = true;
            _rebuildComposites = true;
            Changed?.Invoke();
        };
        _catalog.Changed += _onCatalogChanged;
    }

    public void Detach() => _catalog.Changed -= _onCatalogChanged;

    public void Reset()
    {
        World = CreateWorld();
        Commands = new VoxelWorldCommands(World, _catalog);
        _vehicles.Clear();
        NextEntityId = 1;
        TransactionCount = 0;
        _pendingChanges.Clear();
        _pendingCollisionChanges.Clear();
        _rebuildAll = true;
        _rebuildAllCollision = true;
        _rebuildComposites = true;
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
        TransactionCount++;
        _rebuildComposites = true;
        Changed?.Invoke();
        return new CompositeManufactureResult(ConstructionStatus.Valid, vehicleId, instance.Parts);
    }

    public CompositeQuery? QueryComposite(EntityId id)
    {
        var instance = _vehicles.FirstOrDefault(item => item.Id == id);
        return instance is null ? null : ToQuery(instance);
    }

    public IReadOnlyList<CompositeQuery> QueryComposites() =>
        _vehicles.Select(ToQuery).ToArray();

    public CompositeAuthoritySnapshot CaptureAuthority()
    {
        var world = World.OccupiedVoxels()
            .OrderBy(item => item.Key.X)
            .ThenBy(item => item.Key.Y)
            .ThenBy(item => item.Key.Z)
            .Select(item => (item.Key, item.Value.DefinitionId, item.Value.CurrentDurability, item.Value.Orientation.ToString()))
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
        if (_vehicles.Count > 0)
            return new WorldSaveResult(WorldSaveStatus.UnsupportedVehicles, SaveId, DisplayName);
        if (_store is null || _catalog.Current is null)
            return new WorldSaveResult(WorldSaveStatus.Unavailable);

        var name = displayName ?? DisplayName;
        if (!WorldSaveDocument.TryNormalizeDisplayName(name, out var normalized))
            return new WorldSaveResult(WorldSaveStatus.InvalidName, SaveId, DisplayName);

        var id = SaveId ?? Guid.NewGuid();
        if (!WorldSaveCodec.TryCapture(World, _catalog.Current, id, normalized, out var document, out var captureStatus))
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
        if (_vehicles.Count > 0)
            return new WorldSaveResult(WorldSaveStatus.UnsupportedVehicles, SaveId, DisplayName);
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
        if (!WorldSaveRestorer.TryBuild(_catalog.Current, document, Identity, out var candidate, out var buildStatus))
            return new WorldSaveResult(buildStatus, SaveId, DisplayName);

        ReplaceWorld(candidate);
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

    private void NoteChange(WorldVoxelCoord position, bool occupancyChanged)
    {
        _pendingChanges.Add((position, occupancyChanged));
        if (occupancyChanged)
            _pendingCollisionChanges.Add(position);
        Changed?.Invoke();
    }

    private void ReplaceWorld(VoxelWorld world)
    {
        World = world;
        Commands = new VoxelWorldCommands(World, _catalog);
        _vehicles.Clear();
        NextEntityId = 1;
        TransactionCount = 0;
        _pendingChanges.Clear();
        _pendingCollisionChanges.Clear();
        _rebuildAll = true;
        _rebuildAllCollision = true;
        _rebuildComposites = true;
        Changed?.Invoke();
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
        VehicleEnvelope.TryFromContacts(instance.SizeClass, instance.Contacts, out var envelope);
        var parts = instance.Parts
            .Select(part => new CompositePartQuery(part.Id, part.Cell, part.DefinitionId, part.Orientation, part.CurrentDurability))
            .ToArray();
        var contacts = instance.Contacts
            .Select(contact =>
            {
                var bound = instance.PartAt(contact.BoundCell)
                    ?? throw new InvalidOperationException(
                        $"Contact ({contact.X},{contact.Y},{contact.Z}) is not bound to a manufactured part.");
                return new CompositeContactQuery(
                    contact.X,
                    contact.Y,
                    contact.Z,
                    contact.BoundCell,
                    bound.Id);
            })
            .ToArray();
        return new CompositeQuery(
            instance.Id,
            instance.DefinitionId,
            instance.ArchetypeId,
            instance.SizeClass,
            instance.Transform,
            construction,
            identity,
            OperationalCondition.Unevaluated,
            parts,
            contacts,
            VehicleEnvelope.TryFromContacts(instance.SizeClass, instance.Contacts, out envelope) ? envelope : null);
    }

    private VoxelWorld CreateWorld() => VoxelWorldFactory.Create(
        _catalog.Current ?? throw new InvalidOperationException("Voxel content must be loaded before entering the development scene."),
        Identity);
}
