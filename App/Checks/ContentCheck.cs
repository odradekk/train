
using Godot;
using Train.Content;
using Train.Content.Markets;
using Train.Content.Settlements;
using Train.Content.Voxels;
using Train.App.Logging;

namespace Train.App.Checks;

internal sealed class ContentCheck : IContentSource
{
    private const double InitialPriceSensitivity = 0.5;
    private const double AddedPriceSensitivity = 0.75;
    private const int InitialSupplyCapacity = 2;
    private const int AddedSupplyCapacity = 3;

    private readonly GodotContentSource _initial;
    private readonly GodotContentSource _added;
    private GodotContentSource _current;
    private bool _awaitingReload;
    private ContentSnapshot? _initialSnapshot;
    private static readonly VoxelDefinitionId ProbeId = new("core", "smoke-ore");
    private static readonly VoxelDefinitionId MarketGoodId = StartupChecks.GrainId;
    private static readonly MarketTableDefinitionId MarketId = new("core", "smoke-prices");
    private static readonly SettlementTypeDefinitionId TypeId = new("core", "smoke-settlement");

    public ContentCheck(TrainLogger logger, string userData, string contentRoot)
    {
        _initial = new GodotContentSource(logger, userData, Path.Combine(contentRoot, "initial"), false);
        _added = new GodotContentSource(logger, userData, Path.Combine(contentRoot, "added"), false);
        _current = _initial;
    }

    public bool? Advance(ContentReloader reloader, ContentReloadResult lastReload, Action reload)
    {
        if (!_awaitingReload)
        {
            var initialSnapshot = reloader.Current;
            var initialOk = StartupChecks.ReportCatalog(initialSnapshot, lastReload, "CONTENT");
            initialOk &= StartupChecks.Report(InputMap.HasAction(App.ReloadContentAction), "CONTENT_RELOAD_ACTION_PRESENT");
            initialOk &= StartupChecks.Report(StartupChecks.CanReadResource(StartupChecks.VoxelJson), "CONTENT_VFS_JSON_READABLE");
            initialOk &= StartupChecks.Report(StartupChecks.CanReadResource(StartupChecks.IconPng), "CONTENT_VFS_PNG_READABLE");
            initialOk &= StartupChecks.Report(
                HasMarketAndTypeDefinitions(initialSnapshot, InitialPriceSensitivity, InitialSupplyCapacity),
                "CONTENT_MARKET_SETTLEMENT_READY");
            if (!initialOk || initialSnapshot is null || initialSnapshot.Voxels.ContainsKey(ProbeId))
                return false;
            _initialSnapshot = initialSnapshot;
            _current = _added;
            _awaitingReload = true;
            if (OS.IsDebugBuild())
            {
                Input.ParseInputEvent(new InputEventAction { Action = App.ReloadContentAction, Pressed = true });
                Input.FlushBufferedEvents();
            }
            else
                reload();
            return null;
        }

        var addedSnapshot = reloader.Current;
        var added = lastReload.IsCommitted &&
            _initialSnapshot is { } beforeAddition &&
            addedSnapshot is { } currentSnapshot &&
            currentSnapshot.Generation == beforeAddition.Generation + 1 &&
            !beforeAddition.Voxels.ContainsKey(ProbeId) &&
            currentSnapshot.Voxels.ContainsKey(ProbeId) &&
            HasMarketAndTypeDefinitions(beforeAddition, InitialPriceSensitivity, InitialSupplyCapacity) &&
            HasMarketAndTypeDefinitions(currentSnapshot, AddedPriceSensitivity, AddedSupplyCapacity);
        if (!StartupChecks.Report(added, "CONTENT_SMOKE_RELOADED"))
            return false;
        if (OS.IsDebugBuild())
            GD.Print("CONTENT_SMOKE_INPUT_RELOADED");
        _current = _initial;
        var beforeRemovalSnapshot = addedSnapshot;
        var removed = reloader.Reload();
        var afterRemovalSnapshot = reloader.Current;
        return StartupChecks.Report(removed.IsCommitted &&
            beforeRemovalSnapshot is { } before &&
            afterRemovalSnapshot is { } after &&
            after.Generation == before.Generation + 1 &&
            before.Voxels.ContainsKey(ProbeId) &&
            !after.Voxels.ContainsKey(ProbeId) &&
            HasMarketAndTypeDefinitions(before, AddedPriceSensitivity, AddedSupplyCapacity) &&
            HasMarketAndTypeDefinitions(after, InitialPriceSensitivity, InitialSupplyCapacity), "CONTENT_SMOKE_REMOVED");
    }

    private static bool HasMarketAndTypeDefinitions(ContentSnapshot? snapshot, double sensitivity, int capacity) =>
        snapshot is not null && snapshot.MarketTables.Count == 1 && snapshot.SettlementTypes.Count == 1 &&
        snapshot.MarketTables.TryGetValue(MarketId, out var market) && market.Definition.Goods.Count == 1 &&
        market.Definition.Goods.TryGetValue(MarketGoodId, out var price) &&
        price.Sensitivity == sensitivity && price.MinPrice == 1 && price.MaxPrice == 1000 &&
        snapshot.SettlementTypes.TryGetValue(TypeId, out var type) && type.Definition.Supply.Count == 1 &&
        type.Definition.Supply.TryGetValue(MarketGoodId, out var supply) &&
        supply.Capacity == capacity && supply.RestorePerTick == 1 && type.Definition.Demand.Count == 0 &&
        type.Definition.PreferredTerrain.Count == 0 && type.Definition.Weight == 1 &&
        type.Definition.Population.Min == 1 && type.Definition.Population.Max == 1 && type.Definition.InitialPlatformCount == 0;

    public IReadOnlyList<ContentPackage> ListPackages() => _current.ListPackages();
    public IReadOnlyList<ContentDiagnostic> ListScanDiagnostics() => _current.ListScanDiagnostics();
    public ContentFileDiscovery ListDefinitionFiles(ContentPackage package) => _current.ListDefinitionFiles(package);
    public ContentBytesResult ReadFile(ContentPackage package, string normalizedRelativePath, int maxBytes) =>
        _current.ReadFile(package, normalizedRelativePath, maxBytes);
    public ImageDecodeResult DecodePng(ContentPackage package, string normalizedRelativePath) =>
        _current.DecodePng(package, normalizedRelativePath);
    public ImageDecodeResult DecodePng(ContentPackage package, string normalizedRelativePath, byte[] rawBytes) =>
        _current.DecodePng(package, normalizedRelativePath, rawBytes);
}
