// 属于内容加载模块。
// 共用定义收集、身份冲突、包归属和发布比较，具体格式负责解析与引用规则。
namespace Train.Core.Content;

/// <summary>表示一次加载中一个包及其有上限的诊断；Invalid 是整个包的隔离标记。</summary>
/// <remarks>同一包的所有格式共用诊断容器，条目的归属始终指向本对象，而非仅用命名空间推断。</remarks>
internal sealed class ContentPackageLoad(ContentPackage package)
{
    public ContentPackage Package { get; } = package;
    public DiagnosticBag Diagnostics { get; } = new();
}

/// <summary>内容格式参加共同加载流程所需的内部操作；不向调用方开放注册或扩展。</summary>
internal abstract class ContentFormat
{
    public abstract string FileSuffix { get; }
    public abstract string LogKind { get; }
    public abstract int CountFor(ContentPackageLoad package);

    /// <summary>判断文件是否属于本领域格式及包允许的声明来源。</summary>
    public virtual bool Accepts(ContentPackage package, string file) =>
        file.EndsWith(FileSuffix, StringComparison.Ordinal);

    /// <summary>解析一份已读取的 JSON，保存候选条目及真实所属包，返回本文件接受的条目数。</summary>
    public abstract int ReadFile(IContentSource source, ContentPackageLoad package, string file, byte[] bytes);

    /// <summary>合并仍有效的包；相同身份的全部声明使其所属包无效，不选择覆盖者。</summary>
    public abstract void Merge();

    /// <summary>以本轮合并结果校验引用和计算派生数据，失败记录在实际所属包。</summary>
    public abstract void ResolveReferences();

    /// <summary>按完整发布指纹判断本格式是否与已发布快照不同。</summary>
    public abstract bool HasChanges(ContentSnapshot previous);
}

/// <summary>按强类型身份收集一种格式的有限条目，并共用整包隔离与指纹比较。</summary>
/// <remarks>Parsed 保留包归属；Entries 和 Owners 只包含尚有效包的合并结果，每次隔离后一起重建。</remarks>
internal abstract class ContentFormat<TId, TEntry> : ContentFormat
    where TId : notnull
    where TEntry : class
{
    private readonly List<(ContentPackageLoad Package, TEntry Entry)> _parsed = new();
    public Dictionary<TId, TEntry> Entries { get; } = new();
    protected Dictionary<TId, ContentPackageLoad> Owners { get; } = new();

    protected abstract TId Identity(TEntry entry);
    protected abstract string Fingerprint(TEntry entry);

    /// <summary>解析并校验本格式的文件；失败归入该包的共享诊断。</summary>
    protected abstract IReadOnlyList<TEntry> Parse(
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
        Entries.Clear();
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
                    owner.Diagnostics.Warn(ContentDiagnosticCodes.DuplicateLocalId,
                        $"The {LogKind} identity '{group.Key}' is declared more than once.");
                }
                continue;
            }
            var item = group.Single();
            Entries.Add(group.Key, item.Entry);
            Owners.Add(group.Key, item.Package);
        }
        // 冲突可能出现在包的后一个条目；此前暂时合并的同包条目也必须撤销。
        foreach (var id in Owners.Where(item => item.Value.Diagnostics.Invalid).Select(item => item.Key).ToArray())
        {
            Entries.Remove(id);
            Owners.Remove(id);
        }
    }

    /// <summary>比较强类型身份和发布指纹；来源位置与遍历顺序不影响有效内容。</summary>
    protected bool HasChanges(IReadOnlyDictionary<TId, TEntry> previous) =>
        previous.Count != Entries.Count || Entries.Any(item =>
            !previous.TryGetValue(item.Key, out var old) || Fingerprint(old) != Fingerprint(item.Value));
}
