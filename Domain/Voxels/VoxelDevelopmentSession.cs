using Train.Content;

namespace Train.Domain.Voxels;

public sealed class VoxelDevelopmentSession
{
    private readonly VoxelCatalog _catalog;
    private readonly Action<VoxelCatalogChange> _onCatalogChanged;
    private readonly List<(WorldVoxelCoord Position, bool OccupancyChanged)> _pendingChanges = new();
    private bool _rebuildAll = true;

    public VoxelWorldIdentity Identity { get; }
    public VoxelWorld World { get; private set; }
    public VoxelWorldCommands Commands { get; private set; }
    public int DefinitionCount => _catalog.Current?.Entries.Count ?? 0;

    public event Action? Changed;

    public VoxelDevelopmentSession(VoxelCatalog catalog, VoxelWorldIdentity? identity = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _catalog = catalog;
        Identity = identity ?? VoxelWorldIdentity.Slice1;
        World = CreateWorld();
        Commands = new VoxelWorldCommands(World, _catalog);
        _onCatalogChanged = _ =>
        {
            _rebuildAll = true;
            Changed?.Invoke();
        };
        _catalog.Changed += _onCatalogChanged;
    }

    public void Detach() => _catalog.Changed -= _onCatalogChanged;

    public void Reset()
    {
        World = CreateWorld();
        Commands = new VoxelWorldCommands(World, _catalog);
        _pendingChanges.Clear();
        _rebuildAll = true;
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

    private void NoteChange(WorldVoxelCoord position, bool occupancyChanged)
    {
        _pendingChanges.Add((position, occupancyChanged));
        Changed?.Invoke();
    }

    private VoxelWorld CreateWorld() => VoxelWorldFactory.Create(
        _catalog.Current ?? throw new InvalidOperationException("Voxel content must be loaded before entering the development scene."),
        Identity);
}
