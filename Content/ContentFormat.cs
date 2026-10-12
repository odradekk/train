namespace Train.Content;

internal sealed class ContentPackageLoad(ContentPackage package)
{
    public ContentPackage Package { get; } = package;
    public DiagnosticBag Diagnostics { get; } = new(
        package.IsRequired ? ContentDiagnosticSeverity.Error : ContentDiagnosticSeverity.Warning);
}

internal abstract class ContentFormat
{
    public abstract string FileSuffix { get; }
    public abstract string LogKind { get; }
    public abstract int CountFor(ContentPackageLoad package);

    public abstract int ReadFile(IContentSource source, ContentPackageLoad package, string file, byte[] bytes);

    public abstract void Merge();

    public abstract bool HasChanges(ContentSnapshot previous);
}

internal abstract class ContentFormat<TId, TCandidate> : ContentFormat
    where TId : notnull
    where TCandidate : class
{
    private readonly List<(ContentPackageLoad Package, TCandidate Entry)> _parsed = new();
    protected Dictionary<TId, TCandidate> Candidates { get; } = new();
    protected Dictionary<TId, ContentPackageLoad> Owners { get; } = new();

    protected abstract TId Identity(TCandidate entry);

    protected abstract IReadOnlyList<TCandidate> Parse(
        IContentSource source, ContentPackageLoad package, string file, byte[] bytes);

    public sealed override int CountFor(ContentPackageLoad package) =>
        _parsed.Count(item => ReferenceEquals(item.Package, package));

    public sealed override int ReadFile(IContentSource source, ContentPackageLoad package, string file, byte[] bytes)
    {
        var entries = Parse(source, package, file, bytes);
        foreach (var entry in entries)
            _parsed.Add((package, entry));
        return entries.Count;
    }

    public sealed override void Merge()
    {
        Candidates.Clear();
        Owners.Clear();
        var groups = _parsed.Where(item => !item.Package.Diagnostics.Invalid)
            .GroupBy(item => Identity(item.Entry));
        foreach (var group in groups)
        {
            if (group.Count() > 1)
            {
                foreach (var owner in group.Select(item => item.Package).Distinct())
                {
                    owner.Diagnostics.Source = owner.Package.ResourcesRoot;
                    owner.Diagnostics.Reject(ContentDiagnosticCodes.DuplicateLocalId,
                        $"The {LogKind} identity '{group.Key}' is declared more than once.");
                }
                continue;
            }
            var item = group.Single();
            Candidates.Add(group.Key, item.Entry);
            Owners.Add(group.Key, item.Package);
        }
        // 冲突可能出现在包的后一个条目；此前暂时合并的同包条目也必须撤销。
        foreach (var id in Owners.Where(item => item.Value.Diagnostics.Invalid).Select(item => item.Key).ToArray())
        {
            Candidates.Remove(id);
            Owners.Remove(id);
        }
    }

    protected static bool HasChanges<TEntry>(
        IReadOnlyDictionary<TId, TEntry> entries, IReadOnlyDictionary<TId, TEntry> previous,
        Func<TEntry, string> fingerprint) =>
        previous.Count != entries.Count || entries.Any(item =>
            !previous.TryGetValue(item.Key, out var old) || fingerprint(old) != fingerprint(item.Value));
}
