using System.Collections.Frozen;
using Train.Domain.Voxels;
using Train.Domain.Settlements;
using Train.Logging;

namespace Train.Content;

public enum ContentReloadStatus
{
    Committed = 0,
    CommittedWithWarnings = 1,
    Rejected = 2
}

public sealed class ContentReloadResult
{
    public ContentReloadStatus Status { get; }
    public bool Changed { get; }
    public long CatalogVersion { get; }
    public int ScannedFileCount { get; }
    public int AcceptedDefinitionCount { get; }
    public int WarningCount { get; }
    public int ErrorCount { get; }
    public IReadOnlyList<ContentDiagnostic> Diagnostics { get; }
    public IReadOnlySet<VoxelDefinitionId> Added { get; }
    public IReadOnlySet<VoxelDefinitionId> Removed { get; }
    public IReadOnlySet<VoxelDefinitionId> ChangedIds { get; }
    public IReadOnlySet<SettlementId> AddedSettlements { get; }
    public IReadOnlySet<SettlementId> RemovedSettlements { get; }
    public IReadOnlySet<SettlementId> ChangedSettlementIds { get; }
    public int AcceptedSettlementCount { get; }

    public ContentReloadResult(
        ContentReloadStatus status,
        bool changed,
        long catalogVersion,
        int scannedFileCount,
        int acceptedDefinitionCount,
        IReadOnlyList<ContentDiagnostic> diagnostics,
        IReadOnlySet<VoxelDefinitionId> added,
        IReadOnlySet<VoxelDefinitionId> removed,
        IReadOnlySet<VoxelDefinitionId> changedIds,
        IReadOnlySet<SettlementId>? addedSettlements = null,
        IReadOnlySet<SettlementId>? removedSettlements = null,
        IReadOnlySet<SettlementId>? changedSettlementIds = null,
        int acceptedSettlementCount = 0)
    {
        Status = status;
        Changed = changed;
        CatalogVersion = catalogVersion;
        ScannedFileCount = scannedFileCount;
        AcceptedDefinitionCount = acceptedDefinitionCount;
        Diagnostics = ImmutableSets.Freeze(diagnostics);
        WarningCount = Diagnostics.Count(item => item.Severity == ContentDiagnosticSeverity.Warning);
        ErrorCount = Diagnostics.Count(item => item.Severity == ContentDiagnosticSeverity.Error);
        Added = added;
        Removed = removed;
        ChangedIds = changedIds;
        AddedSettlements = addedSettlements ?? ImmutableSets.EmptySettlements;
        RemovedSettlements = removedSettlements ?? ImmutableSets.EmptySettlements;
        ChangedSettlementIds = changedSettlementIds ?? ImmutableSets.EmptySettlements;
        AcceptedSettlementCount = acceptedSettlementCount;
    }

    public bool IsCommitted => Status is ContentReloadStatus.Committed or ContentReloadStatus.CommittedWithWarnings;
}

public sealed class ContentPreCommitDecision
{
    public bool CanCommit { get; }
    public IReadOnlyList<ContentDiagnostic> Diagnostics { get; }

    private ContentPreCommitDecision(bool canCommit, IReadOnlyList<ContentDiagnostic> diagnostics)
    {
        CanCommit = canCommit;
        Diagnostics = ImmutableSets.Freeze(diagnostics);
    }

    public static ContentPreCommitDecision Allow(IEnumerable<ContentDiagnostic>? warnings = null) =>
        new(true, warnings?.ToArray() ?? Array.Empty<ContentDiagnostic>());

    public static ContentPreCommitDecision Reject(IEnumerable<ContentDiagnostic> diagnostics) =>
        new(false, diagnostics.ToArray());
}

public interface IContentPreCommitValidator
{
    ContentPreCommitDecision Validate(VoxelCatalogSnapshot candidate);
}

public interface IContentWorldReloadAdapter
{
    ContentWorldReloadPlan Prepare(VoxelCatalogSnapshot candidate);
}

public sealed class ContentWorldReloadPlan
{
    public static ContentWorldReloadPlan None { get; } = Accept(null);

    public bool CanCommit { get; }
    public IReadOnlyList<ContentDiagnostic> Diagnostics { get; }

    private readonly Action? _apply;

    private ContentWorldReloadPlan(
        bool canCommit,
        IReadOnlyList<ContentDiagnostic> diagnostics,
        Action? apply)
    {
        CanCommit = canCommit;
        Diagnostics = ImmutableSets.Freeze(diagnostics);
        _apply = apply;
    }

    public static ContentWorldReloadPlan Accept(
        Action? apply,
        IEnumerable<ContentDiagnostic>? warnings = null) =>
        new(true, warnings?.ToArray() ?? Array.Empty<ContentDiagnostic>(), apply);

    public static ContentWorldReloadPlan Reject(IEnumerable<ContentDiagnostic> diagnostics) =>
        new(false, diagnostics.ToArray(), null);

    public void Apply() => _apply?.Invoke();
}

public sealed class ReferencedVoxelIdValidator : IContentPreCommitValidator
{
    private readonly Func<IReadOnlySet<VoxelDefinitionId>> _referencedIds;

    public ReferencedVoxelIdValidator(Func<IReadOnlySet<VoxelDefinitionId>> referencedIds)
    {
        ArgumentNullException.ThrowIfNull(referencedIds);
        _referencedIds = referencedIds;
    }

    public ContentPreCommitDecision Validate(VoxelCatalogSnapshot candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var diagnostics = new List<ContentDiagnostic>();
        foreach (var id in _referencedIds())
        {
            if (!candidate.TryGet(id, out var entry))
            {
                diagnostics.Add(new ContentDiagnostic(
                    ContentDiagnosticSeverity.Error,
                    ContentDiagnosticCodes.UnresolvedVoxelReference,
                    "A referenced voxel is missing from the candidate catalog.",
                    jsonPath: id.Value));
                continue;
            }

            if (!entry.Definition.HasNaturalResourceTag)
            {
                diagnostics.Add(new ContentDiagnostic(
                    ContentDiagnosticSeverity.Error,
                    ContentDiagnosticCodes.NaturalResourceTagRequired,
                    "A settlement natural resource id does not resolve to a natural_resource voxel.",
                    jsonPath: id.Value));
            }
        }

        return diagnostics.Count == 0
            ? ContentPreCommitDecision.Allow()
            : ContentPreCommitDecision.Reject(diagnostics);
    }

    public static ContentPreCommitDecision FilterSettlementReferences(
        IDictionary<VoxelDefinitionId, VoxelCatalogEntry> entries,
        IDictionary<SettlementId, SettlementCandidate> settlements)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(settlements);
        var diagnostics = new List<ContentDiagnostic>();
        // Drop an entire invalid mod, then re-check leftovers so a remaining package
        // cannot keep a reference into the excluded namespace.
        while (true)
        {
            var failedMods = new HashSet<string>(StringComparer.Ordinal);
            var rejectBuiltIn = false;
            foreach (var settlement in settlements.Values.ToArray())
            {
                if (SettlementReferencesAreValid(entries, settlement, diagnostics))
                    continue;
                if (settlement.Kind == ContentPackageKind.BuiltIn)
                    rejectBuiltIn = true;
                else
                    failedMods.Add(PackageNamespace(settlement.Id));
            }

            if (rejectBuiltIn)
                return ContentPreCommitDecision.Reject(diagnostics);
            if (failedMods.Count == 0)
                return diagnostics.Count == 0
                    ? ContentPreCommitDecision.Allow()
                    : ContentPreCommitDecision.Allow(diagnostics);

            foreach (var ns in failedMods)
            {
                foreach (var id in entries.Keys.Where(id => id.Namespace == ns).ToArray())
                    entries.Remove(id);
                foreach (var id in settlements.Keys.Where(id => id.Value.StartsWith(ns + ":", StringComparison.Ordinal)).ToArray())
                    settlements.Remove(id);
            }
        }
    }

    private static bool SettlementReferencesAreValid(
        IDictionary<VoxelDefinitionId, VoxelCatalogEntry> entries,
        SettlementCandidate settlement,
        List<ContentDiagnostic> diagnostics)
    {
        var valid = true;
        var severity = settlement.Kind == ContentPackageKind.BuiltIn
            ? ContentDiagnosticSeverity.Error
            : ContentDiagnosticSeverity.Warning;
        foreach (var resourceId in settlement.NaturalResourceIds)
        {
            if (!entries.TryGetValue(resourceId, out var entry))
            {
                valid = false;
                diagnostics.Add(new ContentDiagnostic(
                    severity,
                    ContentDiagnosticCodes.UnresolvedVoxelReference,
                    "A referenced voxel is missing from the candidate catalog.",
                    jsonPath: resourceId.Value));
                continue;
            }

            if (entry.Definition.HasNaturalResourceTag)
                continue;
            valid = false;
            diagnostics.Add(new ContentDiagnostic(
                severity,
                ContentDiagnosticCodes.NaturalResourceTagRequired,
                "A settlement natural resource id does not resolve to a natural_resource voxel.",
                jsonPath: resourceId.Value));
        }

        return valid;
    }

    private static string PackageNamespace(SettlementId id)
    {
        var separator = id.Value.LastIndexOf(':');
        return separator <= 0 ? id.Value : id.Value[..separator];
    }
}

public sealed class ContentReloader
{
    private readonly IContentSource _source;
    private readonly TrainLogger _logger;
    private readonly IContentPreCommitValidator? _validator;
    private IContentWorldReloadAdapter? _worldAdapter;
    private int _busy;

    public VoxelCatalog Catalog { get; }
    public SettlementRegistry Settlements { get; }
    public bool InitializationFailed { get; private set; }

    public ContentReloader(
        IContentSource source,
        TrainLogger logger,
        VoxelCatalog? catalog = null,
        IContentPreCommitValidator? validator = null,
        SettlementRegistry? settlements = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(logger);
        _source = source;
        _logger = logger;
        _validator = validator;
        Catalog = catalog ?? new VoxelCatalog();
        Settlements = settlements ?? new SettlementRegistry();
    }

    public void SetWorldReloadAdapter(IContentWorldReloadAdapter? adapter) =>
        _worldAdapter = adapter;

    public ContentReloadResult Reload()
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            var busy = Reject(
                Catalog.Current?.Generation ?? 0,
                scanned: 0,
                accepted: 0,
                acceptedSettlements: 0,
                new[]
                {
                    new ContentDiagnostic(
                        ContentDiagnosticSeverity.Error,
                        ContentDiagnosticCodes.ReloadAlreadyInProgress,
                        "A content reload is already in progress.")
                });
            LogFailure(busy);
            return busy;
        }

        try
        {
            return ReloadExclusive();
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    private ContentReloadResult ReloadExclusive()
    {
        _logger.Info(ContentLog.Category, ContentLog.ScanStartedEventId, "Scanning built-in voxel content.");
        var candidate = CatalogCandidateBuilder.Build(_source, _logger);
        var diagnostics = candidate.Diagnostics.ToList();

        if (!candidate.CanCommit)
        {
            var rejected = Reject(Catalog.Current?.Generation ?? 0, candidate.ScannedFileCount, 0, 0, diagnostics);
            if (Catalog.Current is null)
                InitializationFailed = true;
            LogFailure(rejected);
            return rejected;
        }

        if (Catalog.Current is null && candidate.Entries.Count == 0)
        {
            diagnostics.Add(new ContentDiagnostic(
                ContentDiagnosticSeverity.Error,
                ContentDiagnosticCodes.EmptyInitialCatalog,
                "The initial built-in catalog must contain at least one voxel definition."));
            var rejected = Reject(0, candidate.ScannedFileCount, 0, 0, diagnostics);
            InitializationFailed = true;
            LogFailure(rejected);
            return rejected;
        }

        var entries = new Dictionary<VoxelDefinitionId, VoxelCatalogEntry>(candidate.Entries);
        var settlements = new Dictionary<SettlementId, SettlementCandidate>(candidate.Settlements);
        var references = ReferencedVoxelIdValidator.FilterSettlementReferences(entries, settlements);
        diagnostics.AddRange(references.Diagnostics);
        if (!references.CanCommit)
        {
            var rejected = Reject(Catalog.Current?.Generation ?? 0, candidate.ScannedFileCount, candidate.Entries.Count, 0, diagnostics);
            if (Catalog.Current is null)
                InitializationFailed = true;
            LogFailure(rejected);
            return rejected;
        }

        var nextGeneration = (Catalog.Current?.Generation ?? 0) + 1;
        var snapshot = new VoxelCatalogSnapshot(nextGeneration, entries);

        if (!Settlements.HasCommitted && settlements.Count == 0)
        {
            diagnostics.Add(new ContentDiagnostic(
                ContentDiagnosticSeverity.Error,
                ContentDiagnosticCodes.EmptyInitialSettlements,
                "The initial built-in package must contain at least one settlement."));
            var rejected = Reject(Catalog.Current?.Generation ?? 0, candidate.ScannedFileCount, candidate.Entries.Count, 0, diagnostics);
            InitializationFailed = true;
            LogFailure(rejected);
            return rejected;
        }

        var decision = _validator?.Validate(snapshot) ?? ContentPreCommitDecision.Allow();
        diagnostics.AddRange(decision.Diagnostics);

        if (!decision.CanCommit)
        {
            var rejected = Reject(Catalog.Current?.Generation ?? 0, candidate.ScannedFileCount, candidate.Entries.Count, settlements.Count, diagnostics);
            if (Catalog.Current is null)
                InitializationFailed = true;
            LogFailure(rejected);
            return rejected;
        }

        var worldPlan = _worldAdapter?.Prepare(snapshot) ?? ContentWorldReloadPlan.None;
        diagnostics.AddRange(worldPlan.Diagnostics);
        if (!worldPlan.CanCommit)
        {
            var rejected = Reject(Catalog.Current?.Generation ?? 0, candidate.ScannedFileCount, candidate.Entries.Count, settlements.Count, diagnostics);
            if (Catalog.Current is null)
                InitializationFailed = true;
            LogFailure(rejected);
            return rejected;
        }

        var change = CatalogDiff.Compute(snapshot.Generation, Catalog.Current, entries);
        var settlementChange = SettlementDiff.Compute(Settlements, settlements);
        var status = diagnostics.Any(item => item.Severity == ContentDiagnosticSeverity.Warning)
            ? ContentReloadStatus.CommittedWithWarnings
            : ContentReloadStatus.Committed;
        var voxelsChanged = CatalogDiff.HasChanges(change);
        var settlementsChanged = SettlementDiff.HasChanges(settlementChange);

        if (!voxelsChanged && !settlementsChanged)
        {
            var unchanged = new ContentReloadResult(
                status,
                changed: false,
                Catalog.Current?.Generation ?? 0,
                candidate.ScannedFileCount,
                entries.Count,
                diagnostics,
                ImmutableSets.Empty,
                ImmutableSets.Empty,
                ImmutableSets.Empty,
                ImmutableSets.EmptySettlements,
                ImmutableSets.EmptySettlements,
                ImmutableSets.EmptySettlements,
                settlements.Count);
            InitializationFailed = false;
            LogSummary(unchanged);
            return unchanged;
        }

        worldPlan.Apply();
        if (voxelsChanged)
            Catalog.Publish(snapshot);
        if (settlementsChanged || !Settlements.HasCommitted)
            Settlements.Apply(settlements.Values.ToArray());
        InitializationFailed = false;
        if (voxelsChanged)
            Catalog.NotifyChanged(change, LogNotifyFailure);
        var committed = new ContentReloadResult(
            status,
            changed: true,
            voxelsChanged ? snapshot.Generation : Catalog.Current?.Generation ?? 0,
            candidate.ScannedFileCount,
            entries.Count,
            diagnostics,
            voxelsChanged ? change.Added : ImmutableSets.Empty,
            voxelsChanged ? change.Removed : ImmutableSets.Empty,
            voxelsChanged ? change.Changed : ImmutableSets.Empty,
            settlementChange.Added,
            settlementChange.Removed,
            settlementChange.Changed,
            settlements.Count);
        LogSummary(committed);
        return committed;
    }

    private static ContentReloadResult Reject(
        long version,
        int scanned,
        int accepted,
        int acceptedSettlements,
        IReadOnlyList<ContentDiagnostic> diagnostics) =>
        new(
            ContentReloadStatus.Rejected,
            changed: false,
            version,
            scanned,
            accepted,
            diagnostics,
            ImmutableSets.Empty,
            ImmutableSets.Empty,
            ImmutableSets.Empty,
            ImmutableSets.EmptySettlements,
            ImmutableSets.EmptySettlements,
            ImmutableSets.EmptySettlements,
            acceptedSettlements);

    private void LogSummary(ContentReloadResult result)
    {
        _logger.Info(
            ContentLog.Category,
            ContentLog.ReloadSummaryEventId,
            $"Content reload finished: {result.ScannedFileCount} files scanned, {result.AcceptedDefinitionCount} accepted, {result.WarningCount + result.ErrorCount} failed.",
            new Dictionary<string, object?>
            {
                ["status"] = result.Status.ToString(),
                ["changed"] = result.Changed,
                ["catalog_version"] = result.CatalogVersion,
                ["scanned_files"] = result.ScannedFileCount,
                ["accepted"] = result.AcceptedDefinitionCount,
                ["warning_count"] = result.WarningCount,
                ["error_count"] = result.ErrorCount,
                ["added"] = result.Added.Select(id => id.Value).ToArray(),
                ["removed"] = result.Removed.Select(id => id.Value).ToArray(),
                ["changed_ids"] = result.ChangedIds.Select(id => id.Value).ToArray(),
                ["settlements_accepted"] = result.AcceptedSettlementCount,
                ["settlements_added"] = result.AddedSettlements.Select(id => id.Value).ToArray(),
                ["settlements_removed"] = result.RemovedSettlements.Select(id => id.Value).ToArray(),
                ["settlements_changed"] = result.ChangedSettlementIds.Select(id => id.Value).ToArray()
            });
    }

    private void LogFailure(ContentReloadResult result)
    {
        _logger.Error(
            ContentLog.Category,
            ContentLog.ReloadFailedEventId,
            "Content catalog was not committed.",
            new Dictionary<string, object?>
            {
                ["error_count"] = result.ErrorCount,
                ["warning_count"] = result.WarningCount,
                ["scanned_files"] = result.ScannedFileCount
            });
        LogSummary(result);
    }

    private void LogNotifyFailure(Exception exception) =>
        _logger.Error(
            ContentLog.Category,
            ContentLog.NotifyFailedEventId,
            "A catalog change subscriber failed after content was committed.",
            new Dictionary<string, object?>
            {
                ["exception"] = exception.GetType().FullName,
                ["message"] = exception.Message
            });
}

internal sealed class CatalogCandidate
{
    public IReadOnlyDictionary<VoxelDefinitionId, VoxelCatalogEntry> Entries { get; }
    public IReadOnlyDictionary<SettlementId, SettlementCandidate> Settlements { get; }
    public IReadOnlyList<ContentDiagnostic> Diagnostics { get; }
    public int ScannedFileCount { get; }
    public bool CanCommit { get; }
    public bool PackagesInvalid { get; }

    public CatalogCandidate(
        IReadOnlyDictionary<VoxelDefinitionId, VoxelCatalogEntry> entries,
        IReadOnlyDictionary<SettlementId, SettlementCandidate> settlements,
        IReadOnlyList<ContentDiagnostic> diagnostics,
        int scannedFileCount,
        bool canCommit,
        bool packagesInvalid)
    {
        Entries = entries;
        Settlements = settlements;
        Diagnostics = diagnostics;
        ScannedFileCount = scannedFileCount;
        CanCommit = canCommit;
        PackagesInvalid = packagesInvalid;
    }
}

internal static class SettlementDiff
{
    public static (IReadOnlySet<SettlementId> Added, IReadOnlySet<SettlementId> Removed, IReadOnlySet<SettlementId> Changed)
        Compute(SettlementRegistry registry, IReadOnlyDictionary<SettlementId, SettlementCandidate> next)
    {
        var added = new HashSet<SettlementId>();
        var removed = new HashSet<SettlementId>();
        var changed = new HashSet<SettlementId>();
        if (!registry.HasCommitted)
        {
            added.UnionWith(next.Keys);
            return (added, removed, changed);
        }

        foreach (var (id, candidate) in next)
        {
            if (!registry.TryGet(id, out var existing))
                added.Add(id);
            else if (!candidate.Matches(existing))
                changed.Add(id);
        }

        foreach (var item in registry.Items)
        {
            if (!next.ContainsKey(item.Id))
                removed.Add(item.Id);
        }

        return (added, removed, changed);
    }

    public static bool HasChanges(
        (IReadOnlySet<SettlementId> Added, IReadOnlySet<SettlementId> Removed, IReadOnlySet<SettlementId> Changed) change) =>
        change.Added.Count > 0 || change.Removed.Count > 0 || change.Changed.Count > 0;
}

internal static class CatalogCandidateBuilder
{
    public static CatalogCandidate Build(IContentSource source, TrainLogger logger)
    {
        var packages = source.ListPackages();
        var diagnostics = new List<ContentDiagnostic>();
        var accepted = new Dictionary<VoxelDefinitionId, VoxelCatalogEntry>();
        var acceptedSettlements = new Dictionary<SettlementId, SettlementCandidate>();
        var occupied = new Dictionary<MapPosition, SettlementId>();
        var scanned = 0;
        var requiredInvalid = false;
        var isolatedCount = 0;
        var acceptedMods = 0;

        foreach (var diagnostic in source.ListScanDiagnostics())
        {
            LogDiagnostic(logger, diagnostic);
            diagnostics.Add(diagnostic);
        }

        if (packages.Count == 0 || packages.All(package => package.Kind != ContentPackageKind.BuiltIn))
        {
            diagnostics.Add(new ContentDiagnostic(
                ContentDiagnosticSeverity.Error,
                ContentDiagnosticCodes.BuiltInPackageUnavailable,
                "The built-in content package was not provided."));
            return new CatalogCandidate(accepted, acceptedSettlements, diagnostics, 0, false, true);
        }

        var duplicateModIds = packages
            .Where(package => package.Kind == ContentPackageKind.Mod)
            .GroupBy(package => package.Namespace, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var package in packages)
        {
            if (package.Kind == ContentPackageKind.Mod && duplicateModIds.Contains(package.Namespace))
            {
                var duplicate = new ContentDiagnostic(
                    ContentDiagnosticSeverity.Warning,
                    ContentDiagnosticCodes.DuplicateModUuid,
                    "Duplicate mod UUID disables every package using that UUID.",
                    package.Source ?? package.Namespace,
                    package.Namespace);
                LogDiagnostic(logger, duplicate);
                diagnostics.Add(duplicate);
                isolatedCount++;
                continue;
            }

            var bag = new DiagnosticBag();
            var packageContent = BuildPackage(source, package, bag, logger, ref scanned);
            if (package.Kind == ContentPackageKind.Mod && packageContent.Voxels.Count > ContentLimits.MaxVoxelsPerMod)
            {
                bag.Warn(ContentDiagnosticCodes.ModVoxelLimitExceeded, "A mod exceeds the 1000-voxel package limit.");
                packageContent = new PackageContent(
                    new Dictionary<VoxelDefinitionId, VoxelCatalogEntry>(),
                    new Dictionary<SettlementId, SettlementCandidate>());
            }

            foreach (var diagnostic in bag.Items)
                LogDiagnostic(logger, diagnostic);
            diagnostics.AddRange(bag.Items);
            if (!package.Available || bag.Invalid)
            {
                if (package.IsRequired)
                    requiredInvalid = true;
                else
                    isolatedCount++;
                continue;
            }

            if (package.Kind == ContentPackageKind.Mod)
                acceptedMods++;

            if (packageContent.Voxels.Count == 0 && packageContent.Settlements.Count == 0)
            {
                logger.Debug(
                    ContentLog.Category,
                    ContentLog.PackageEmptyEventId,
                    "Content package contains no voxels.",
                    new Dictionary<string, object?> { ["package"] = package.Namespace });
            }

            foreach (var (id, entry) in packageContent.Voxels)
                accepted[id] = entry;
            foreach (var (id, settlement) in packageContent.Settlements)
            {
                if (occupied.TryGetValue(settlement.Position, out var prior))
                {
                    var conflict = new ContentDiagnostic(
                        ContentDiagnosticSeverity.Warning,
                        ContentDiagnosticCodes.DuplicatePosition,
                        "A later settlement occupies an already claimed map position.",
                        settlement.Id.Value,
                        prior.Value);
                    LogDiagnostic(logger, conflict);
                    diagnostics.Add(conflict);
                    continue;
                }

                occupied[settlement.Position] = id;
                acceptedSettlements[id] = settlement;
            }
        }

        var modCount = packages.Count(package => package.Kind == ContentPackageKind.Mod);
        if (modCount > 0 || source.ListScanDiagnostics().Count > 0)
        {
            logger.Info(
                ContentLog.Category,
                ContentLog.ModsScanSummaryEventId,
                "Mod scan finished.",
                new Dictionary<string, object?>
                {
                    ["candidate_count"] = modCount,
                    ["accepted_count"] = acceptedMods,
                    ["isolated_count"] = isolatedCount
                });
        }

        if (accepted.Count > ContentLimits.MaxVoxels)
        {
            diagnostics.Add(new ContentDiagnostic(
                ContentDiagnosticSeverity.Error,
                ContentDiagnosticCodes.GlobalVoxelLimitExceeded,
                "The catalog exceeds the global voxel limit."));
            return new CatalogCandidate(accepted, acceptedSettlements, diagnostics, scanned, false, true);
        }

        return new CatalogCandidate(
            accepted.ToFrozenDictionary(),
            acceptedSettlements,
            diagnostics,
            scanned,
            canCommit: !requiredInvalid,
            requiredInvalid);
    }

    private sealed class PackageContent
    {
        public Dictionary<VoxelDefinitionId, VoxelCatalogEntry> Voxels { get; }
        public Dictionary<SettlementId, SettlementCandidate> Settlements { get; }

        public PackageContent(
            Dictionary<VoxelDefinitionId, VoxelCatalogEntry> voxels,
            Dictionary<SettlementId, SettlementCandidate> settlements)
        {
            Voxels = voxels;
            Settlements = settlements;
        }
    }

    private static PackageContent BuildPackage(
        IContentSource source,
        ContentPackage package,
        DiagnosticBag bag,
        TrainLogger logger,
        ref int scanned)
    {
        var accepted = new Dictionary<VoxelDefinitionId, VoxelCatalogEntry>();
        var settlements = new Dictionary<SettlementId, SettlementCandidate>();
        if (!package.Available)
        {
            if (package.IsRequired)
                bag.Error(ContentDiagnosticCodes.BuiltInPackageUnavailable, "The built-in content package is unavailable.");
            else
                bag.Warn(ContentDiagnosticCodes.PackageInvalid, "Mod package is unavailable.");
            return new PackageContent(accepted, settlements);
        }

        var discovery = source.ListVoxelFiles(package);
        var settlementDiscovery = source.ListSettlementFiles(package);
        scanned += discovery.Files.Count + settlementDiscovery.Files.Count;
        foreach (var diagnostic in discovery.Diagnostics.Concat(settlementDiscovery.Diagnostics))
            bag.Add(
                package.IsRequired ? diagnostic.Severity : ContentDiagnosticSeverity.Warning,
                diagnostic.Code,
                diagnostic.Message,
                diagnostic.JsonPath ?? diagnostic.Source);
        if (discovery.Invalid || settlementDiscovery.Invalid)
            bag.MarkInvalid();
        var parsed = new List<ParsedVoxel>();
        var acceptedByFile = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var file in discovery.Files)
            acceptedByFile[file] = 0;
        foreach (var file in discovery.Files)
        {
            bag.Source = file;
            var read = source.ReadFile(package, file, ContentLimits.JsonFileMaxBytes);
            if (read.UnsafeLink)
            {
                if (package.IsRequired)
                    bag.Error(ContentDiagnosticCodes.UnsafeLinkedPath, "Linked or reparse-point content is not allowed.", file);
                else
                    bag.Warn(ContentDiagnosticCodes.UnsafeLinkedPath, "Linked or reparse-point content is not allowed.", file);
                continue;
            }

            if (read.TooLarge)
            {
                bag.Warn(ContentDiagnosticCodes.FileTooLarge, "Voxel JSON exceeds the 1 MiB limit.");
                continue;
            }

            if (read.Bytes is null)
            {
                bag.Warn(ContentDiagnosticCodes.InvalidJson, "Voxel file could not be read.");
                continue;
            }

            parsed.AddRange(VoxelsJsonParser.Parse(read.Bytes, file, bag));
        }

        var duplicates = parsed
            .GroupBy(item => item.LocalId, StringComparer.Ordinal)
            .Where(group => group.Count() > 1);
        var duplicateIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in duplicates)
        {
            duplicateIds.Add(group.Key);
            bag.Source = group.First().SourceFile;
            bag.Warn(ContentDiagnosticCodes.DuplicateLocalId, "Duplicate local id in the package.", group.Key);
        }

        foreach (var item in parsed)
        {
            if (duplicateIds.Contains(item.LocalId))
                continue;
            bag.Source = item.SourceFile;
            if (!TryAccept(source, package, item, bag, logger, out var entry))
                continue;
            accepted[entry.Definition.Id] = entry;
            acceptedByFile[item.SourceFile] = acceptedByFile.GetValueOrDefault(item.SourceFile) + 1;
        }

        foreach (var file in discovery.Files)
            LogFileScanned(logger, package, file, "voxels", acceptedByFile[file]);

        AcceptSettlements(source, package, settlementDiscovery, bag, logger, settlements);
        return new PackageContent(accepted, settlements);
    }

    private static void AcceptSettlements(
        IContentSource source,
        ContentPackage package,
        ContentFileDiscovery discovery,
        DiagnosticBag bag,
        TrainLogger logger,
        Dictionary<SettlementId, SettlementCandidate> accepted)
    {
        var parsed = new List<ParsedSettlement>();
        var acceptedByFile = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var file in discovery.Files)
            acceptedByFile[file] = 0;
        foreach (var file in discovery.Files)
        {
            bag.Source = file;
            var read = source.ReadFile(package, file, ContentLimits.JsonFileMaxBytes);
            if (read.UnsafeLink)
            {
                if (package.IsRequired)
                    bag.Error(ContentDiagnosticCodes.UnsafeLinkedPath, "Linked or reparse-point content is not allowed.", file);
                else
                    bag.Warn(ContentDiagnosticCodes.UnsafeLinkedPath, "Linked or reparse-point content is not allowed.", file);
                continue;
            }

            if (read.TooLarge)
            {
                bag.Warn(ContentDiagnosticCodes.FileTooLarge, "Settlements JSON exceeds the 1 MiB limit.");
                continue;
            }

            if (read.Bytes is null)
            {
                bag.Warn(ContentDiagnosticCodes.InvalidJson, "Settlements file could not be read.");
                continue;
            }

            parsed.AddRange(SettlementsJsonParser.Parse(read.Bytes, package.Namespace, file, bag));
        }

        var duplicates = parsed
            .GroupBy(item => item.LocalId, StringComparer.Ordinal)
            .Where(group => group.Count() > 1);
        var duplicateIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in duplicates)
        {
            duplicateIds.Add(group.Key);
            bag.Source = group.First().SourceFile;
            bag.WarnSoft(ContentDiagnosticCodes.DuplicateLocalId, "Duplicate local id in the package.", group.Key);
        }

        var occupied = new HashSet<MapPosition>();
        foreach (var item in parsed)
        {
            if (duplicateIds.Contains(item.LocalId))
                continue;
            bag.Source = item.SourceFile;
            if (!occupied.Add(item.Position))
            {
                bag.WarnSoft(ContentDiagnosticCodes.DuplicatePosition, "A later settlement occupies an already claimed map position.", item.LocalId);
                continue;
            }

            var id = new SettlementId($"{package.Namespace}:{item.LocalId}");
            accepted[id] = new SettlementCandidate(
                id,
                new SettlementTypeId(item.Type),
                item.Name,
                item.Position,
                item.Population,
                item.NaturalResources.ToHashSet(),
                package.Kind);
            acceptedByFile[item.SourceFile] = acceptedByFile.GetValueOrDefault(item.SourceFile) + 1;
            logger.Debug(
                ContentLog.Category,
                ContentLog.DefinitionAcceptedEventId,
                "Accepted settlement definition.",
                new Dictionary<string, object?>
                {
                    ["settlement_id"] = id.Value,
                    ["source"] = item.SourceFile
                });
        }

        foreach (var file in discovery.Files)
            LogFileScanned(logger, package, file, "settlements", acceptedByFile[file]);
    }

    private static bool TryAccept(
        IContentSource source,
        ContentPackage package,
        ParsedVoxel parsed,
        DiagnosticBag bag,
        TrainLogger logger,
        out VoxelCatalogEntry entry)
    {
        entry = null!;
        if (!TryLoadPresentationAssets(source, package, parsed.Presentation, bag, out var assetRevisions, out var images))
            return false;

        var id = new VoxelDefinitionId(package.Namespace, parsed.LocalId);
        var definition = new VoxelDefinition(
            id,
            parsed.Name,
            parsed.Description,
            parsed.Value,
            parsed.Mass,
            parsed.MaxDurability,
            parsed.Tags,
            parsed.Behaviors,
            parsed.AllowedOrientations,
            parsed.Presentation,
            parsed.Collision);
        var origin = new ContentOrigin(package.Namespace, parsed.SourceFile);
        var semantic = ContentRevisions.ComputeSemanticRevision(definition);
        var publication = ContentRevisions.ComputePublicationFingerprint(definition, semantic, assetRevisions);
        entry = new VoxelCatalogEntry(definition, origin, semantic, assetRevisions, publication, images);
        logger.Debug(
            ContentLog.Category,
            ContentLog.DefinitionAcceptedEventId,
            "Accepted voxel definition.",
            new Dictionary<string, object?>
            {
                ["voxel_id"] = id.Value,
                ["source"] = parsed.SourceFile,
                ["semantic_revision"] = semantic
            });
        return true;
    }

    private static bool TryLoadPresentationAssets(
        IContentSource source,
        ContentPackage package,
        VoxelPresentation visual,
        DiagnosticBag bag,
        out Dictionary<string, string> revisions,
        out Dictionary<string, DecodedImage> images)
    {
        revisions = new Dictionary<string, string>(StringComparer.Ordinal);
        images = new Dictionary<string, DecodedImage>(StringComparer.Ordinal);
        var paths = new[] { visual.IconPath, visual.TopPath, visual.SidePath, visual.BottomPath };
        foreach (var path in paths.Distinct(StringComparer.Ordinal))
        {
            var read = source.ReadFile(package, path, ContentLimits.PngMaxBytes);
            if (read.UnsafeLink)
            {
                if (package.IsRequired)
                    bag.Error(ContentDiagnosticCodes.UnsafeLinkedPath, "Linked or reparse-point content is not allowed.", path);
                else
                    bag.Warn(ContentDiagnosticCodes.UnsafeLinkedPath, "Linked or reparse-point content is not allowed.", path);
                return false;
            }

            if (read.TooLarge)
            {
                bag.Warn(ContentDiagnosticCodes.ImageTooLarge, "PNG exceeds the size limit.", path);
                return false;
            }

            if (read.Bytes is null)
            {
                bag.Warn(ContentDiagnosticCodes.ImageInvalid, "Asset bytes could not be read.", path);
                return false;
            }

            var decoded = source.DecodePng(package, path, read.Bytes);
            if (decoded.UnsafeLink)
            {
                if (package.IsRequired)
                    bag.Error(ContentDiagnosticCodes.UnsafeLinkedPath, "Linked or reparse-point content is not allowed.", path);
                else
                    bag.Warn(ContentDiagnosticCodes.UnsafeLinkedPath, "Linked or reparse-point content is not allowed.", path);
                return false;
            }

            if (decoded.TooLarge)
            {
                bag.Warn(ContentDiagnosticCodes.ImageTooLarge, "PNG exceeds the size limit.", path);
                return false;
            }

            if (decoded.Failed || decoded.Image is null)
            {
                bag.Warn(ContentDiagnosticCodes.ImageInvalid, "PNG could not be decoded.", path);
                return false;
            }

            if (decoded.Image.Width is < 1 or > ContentLimits.ImageMaxDimension ||
                decoded.Image.Height is < 1 or > ContentLimits.ImageMaxDimension)
            {
                bag.Warn(ContentDiagnosticCodes.ImageTooLarge, "Decoded image exceeds the dimension limit.", path);
                return false;
            }

            images[path] = decoded.Image;
            revisions[path] = ContentRevisions.ComputeAssetRevision(read.Bytes);
        }

        if (!IsSquare(images[visual.IconPath]))
        {
            bag.Warn(ContentDiagnosticCodes.ImageInvalid, "Icon must be square.", visual.IconPath);
            return false;
        }

        var top = images[visual.TopPath];
        var side = images[visual.SidePath];
        var bottom = images[visual.BottomPath];
        if (!IsSquare(top) || !IsSquare(side) || !IsSquare(bottom) ||
            !IsPowerOfTwo(top.Width) || !IsPowerOfTwo(side.Width) || !IsPowerOfTwo(bottom.Width) ||
            top.Width != side.Width || side.Width != bottom.Width)
        {
            bag.Warn(ContentDiagnosticCodes.ImageInvalid, "Voxel faces must be equal power-of-two squares.", visual.TopPath);
            return false;
        }

        return true;
    }

    private static bool IsSquare(DecodedImage image) => image.Width == image.Height && image.Width > 0;

    private static bool IsPowerOfTwo(int value) => value > 0 && (value & (value - 1)) == 0;

    private static void LogFileScanned(
        TrainLogger logger,
        ContentPackage package,
        string source,
        string kind,
        int accepted) =>
        logger.Debug(
            ContentLog.Category,
            ContentLog.FileScannedEventId,
            "Content file scanned.",
            new Dictionary<string, object?>
            {
                ["source"] = source,
                ["package"] = package.Namespace,
                ["kind"] = kind,
                ["accepted"] = accepted
            });

    private static void LogDiagnostic(TrainLogger logger, ContentDiagnostic diagnostic)
    {
        var attributes = new Dictionary<string, object?>
        {
            ["code"] = diagnostic.Code,
            ["source"] = diagnostic.Source,
            ["json_path"] = diagnostic.JsonPath
        };
        if (diagnostic.Severity == ContentDiagnosticSeverity.Error)
        {
            logger.Error(ContentLog.Category, ContentLog.InvalidEventId, diagnostic.Message, attributes);
            return;
        }

        logger.Warning(ContentLog.Category, ContentLog.InvalidEventId, diagnostic.Message, attributes);
    }
}
