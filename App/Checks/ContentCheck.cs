// Train.App.Checks 读取运行器准备的两份内容，验证应用的重载入口。
// 切换当前读取来源模拟新增与移除，检查本身不创建或改写内容文件。

using Godot;
using Train.Core.Content;
using Train.Core.Content.Markets;
using Train.Core.Content.Settlements;
using Train.Core.Content.Voxels;
using Train.Core.Logging;

namespace Train.App.Checks;

/// <summary>在 initial 与 added 两份只读物理内容之间切换，观察同一重载器的目录发布。</summary>
/// <remarks>所有读取和 PNG 解码仍经过真实 GodotContentSource；切换仅影响下一次扫描。</remarks>
/// <example>added 中多一个 core:smoke-ore，重载后出现该定义，再切回 initial 后报告移除。</example>
internal sealed class ContentCheck : IContentSource
{
    private readonly GodotContentSource _initial;
    private readonly GodotContentSource _added;
    private GodotContentSource _current;
    // false 表示初始校验尚未完成，true 表示已切换来源并等待宿主的输入重载结果。
    private bool _awaitingReload;
    // 初始发布的快照，用于和后续重载快照比较探针成员。
    private ContentSnapshot? _initialSnapshot;
    /// <summary>运行器在 added 输入中追加的体素标识，用于观察新增与移除。</summary>
    private static readonly VoxelDefinitionId ProbeId = new("core", "smoke-ore");
    private static readonly VoxelDefinitionId MarketGoodId = new("core", "ferrous-ore");
    private static readonly MarketTableDefinitionId MarketId = new("core", "smoke-prices");
    private static readonly SettlementTypeDefinitionId TypeId = new("core", "smoke-settlement");

    /// <summary>保存两份内容根；输入不存在或无效时，由真实扫描和加载结果报告失败。</summary>
    /// <param name="logger">主线程上的应用日志会话。</param>
    /// <param name="userData">运行器分配的用户根，外部内容目录只读。</param>
    /// <param name="contentRoot">包含 initial 与 added 子目录的绝对物理路径。</param>
    public ContentCheck(TrainLogger logger, string userData, string contentRoot)
    {
        _initial = new GodotContentSource(logger, userData, Path.Combine(contentRoot, "initial"), false);
        _added = new GodotContentSource(logger, userData, Path.Combine(contentRoot, "added"), false);
        _current = _initial;
    }

    /// <summary>验证初始目录、经宿主入口新增定义，再验证移除；等待输入分发时返回 null。</summary>
    /// <param name="reloader">应用正在使用的同一内容重载器。</param>
    /// <param name="lastReload">宿主最近一次重载的发布结果。</param>
    /// <param name="reload">宿主正常使用的重载回调；发布构建无快捷键时直接调用。</param>
    /// <returns>等待下一帧时为 null；完成时表示三个阶段是否满足契约。</returns>
    public bool? Advance(ContentReloader reloader, ContentReloadResult lastReload, Action reload)
    {
        if (!_awaitingReload)
        {
            var initialSnapshot = reloader.Current;
            var initialOk = StartupChecks.ReportCatalog(initialSnapshot, lastReload, "CONTENT");
            initialOk &= StartupChecks.Report(InputMap.HasAction(App.ReloadContentAction), "CONTENT_RELOAD_ACTION_PRESENT");
            initialOk &= StartupChecks.Report(StartupChecks.CanReadResource(StartupChecks.VoxelJson), "CONTENT_VFS_JSON_READABLE");
            initialOk &= StartupChecks.Report(StartupChecks.CanReadResource(StartupChecks.IconPng), "CONTENT_VFS_PNG_READABLE");
            initialOk &= StartupChecks.Report(HasMarketAndTypeDefinitions(initialSnapshot, 0.5, 2), "CONTENT_MARKET_SETTLEMENT_READY");
            if (!initialOk || initialSnapshot is null || initialSnapshot.Voxels.ContainsKey(ProbeId))
                return false;
            _initialSnapshot = initialSnapshot;
            _current = _added;
            _awaitingReload = true;
            if (OS.IsDebugBuild())
            {
                // 经过 Godot 输入分发，验证宿主快捷键，而不是从检查直接调用重载器。
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
            HasMarketAndTypeDefinitions(beforeAddition, 0.5, 2) &&
            HasMarketAndTypeDefinitions(currentSnapshot, 0.75, 3);
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
            HasMarketAndTypeDefinitions(before, 0.75, 3) &&
            HasMarketAndTypeDefinitions(after, 0.5, 2), "CONTENT_SMOKE_REMOVED");
    }

    /// <summary>从一个完整快照读取物理夹具的价格和类型参数，确保新后缀经过真实内容源并一起发布。</summary>
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
