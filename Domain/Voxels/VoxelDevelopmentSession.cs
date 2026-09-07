using Train.Content;

namespace Train.Domain.Voxels;

public sealed class VoxelDevelopmentSession
{
    private readonly VoxelCatalog _catalog;
    private readonly Action<VoxelCatalogChange> _onCatalogChanged;

    public VoxelWorld World { get; private set; }
    public VoxelWorldCommands Commands { get; private set; }
    public int DefinitionCount => _catalog.Current?.Entries.Count ?? 0;

    public event Action? Changed;

    public VoxelDevelopmentSession(VoxelCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _catalog = catalog;
        World = CreateWorld();
        Commands = new VoxelWorldCommands(World, _catalog);
        _onCatalogChanged = _ => Changed?.Invoke();
        _catalog.Changed += _onCatalogChanged;
    }

    public void Detach() => _catalog.Changed -= _onCatalogChanged;

    public void Reset()
    {
        World = CreateWorld();
        Commands = new VoxelWorldCommands(World, _catalog);
        Changed?.Invoke();
    }

    public WorldCommandResult Add(WorldVoxelCoord position, VoxelDefinitionId definitionId, VoxelOrientation orientation)
    {
        var result = Commands.Add(position, definitionId, orientation);
        if (result.Succeeded)
            Changed?.Invoke();
        return result;
    }

    public WorldCommandResult Remove(WorldVoxelCoord position)
    {
        var result = Commands.Remove(position);
        if (result.Succeeded)
            Changed?.Invoke();
        return result;
    }

    public WorldDamageResult Damage(WorldVoxelCoord position, int amount) =>
        Damage(new[] { new VoxelDamageTarget(position, amount) });

    public WorldDamageResult Damage(IReadOnlyList<VoxelDamageTarget> targets)
    {
        var result = Commands.Damage(targets);
        if (result.Succeeded)
            Changed?.Invoke();
        return result;
    }

    public WorldVoxelQuery Query(WorldVoxelCoord position) => Commands.Query(position);

    private VoxelWorld CreateWorld() => VoxelWorldFactory.CreateDevelopmentWorld(
        _catalog.Current ?? throw new InvalidOperationException("Voxel content must be loaded before entering the development scene."));
}
