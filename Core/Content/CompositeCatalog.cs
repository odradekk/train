// 属于内容层的目录模块。
// 保存蓝图及设计属性快照，计算修订和发布差异，并校验候选引用。
using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text.Json;
using Train.Core.Content.Composites;
using Train.Core.Content.Voxels;

namespace Train.Core.Content;

/// <summary>表示一个蓝图的不可变定义、来源、修订和按当前体素定义计算的设计属性。</summary>
/// <example>部件数值变化后更新属性而保持蓝图语义修订，见 <c>CompositeContentTests</c>。</example>
public sealed class CompositeCatalogEntry
{
    /// <summary>获取已规范化的蓝图定义。</summary>
    public CompositeDefinition Definition { get; }
    /// <summary>获取所属内容包与包内源文件。</summary>
    public ContentOrigin Origin { get; }
    /// <summary>获取文件显示名；未声明时为 null。</summary>
    public string? FileDisplayName { get; }
    /// <summary>获取身份、尺寸档位和布局规范编码的 SHA-256 修订；不包含引用目标的修订。</summary>
    public string SemanticRevision { get; }
    /// <summary>获取定义、显示文本和设计属性共同形成的 SHA-256 发布指纹。</summary>
    public string PublicationFingerprint { get; }
    /// <summary>获取本快照使用的设计属性和 BOM；不包含实例状态。</summary>
    public DesignAttributes Attributes { get; }

    /// <summary>保存加载器校验和计算完成的蓝图发布条目，不重新计算修订或属性。</summary>
    /// <param name="definition">已规范化的蓝图定义。</param>
    /// <param name="origin">所属包与包内源文件。</param>
    /// <param name="fileDisplayName">文件显示名；未声明时为 null。</param>
    /// <param name="semanticRevision">非空的语义修订。</param>
    /// <param name="publicationFingerprint">非空的发布指纹。</param>
    /// <param name="attributes">按候选体素目录计算的不可变设计属性。</param>
    /// <exception cref="ArgumentNullException">必填引用为 null。</exception>
    /// <exception cref="ArgumentException">修订或指纹为空字符串。</exception>
    public CompositeCatalogEntry(
        CompositeDefinition definition, ContentOrigin origin, string? fileDisplayName,
        string semanticRevision, string publicationFingerprint, DesignAttributes attributes)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(attributes);
        ArgumentException.ThrowIfNullOrEmpty(semanticRevision);
        ArgumentException.ThrowIfNullOrEmpty(publicationFingerprint);
        Definition = definition;
        Origin = origin;
        FileDisplayName = fileDisplayName;
        SemanticRevision = semanticRevision;
        PublicationFingerprint = publicationFingerprint;
        Attributes = attributes;
    }
}

/// <summary>表示一个发布世代的完整蓝图集合；索引冻结，旧快照可继续读取。</summary>
/// <example>拒绝候选时保持原快照，见 <c>CompositeContentTests</c>。</example>
public sealed class CompositeCatalogSnapshot
{
    /// <summary>获取目录世代；0 表示尚未发布的空目录。</summary>
    public long Generation { get; }
    /// <summary>获取按稳定身份索引的全部蓝图。</summary>
    public IReadOnlyDictionary<CompositeDefinitionId, CompositeCatalogEntry> Composites { get; }

    /// <summary>冻结完整候选蓝图索引为一个发布快照。</summary>
    /// <param name="generation">由加载器确定的目录世代。</param>
    /// <param name="composites">按蓝图身份索引的完整条目集合。</param>
    /// <exception cref="ArgumentNullException">索引为 null。</exception>
    public CompositeCatalogSnapshot(long generation, IReadOnlyDictionary<CompositeDefinitionId, CompositeCatalogEntry> composites)
    {
        ArgumentNullException.ThrowIfNull(composites);
        Generation = generation;
        Composites = composites as FrozenDictionary<CompositeDefinitionId, CompositeCatalogEntry> ?? composites.ToFrozenDictionary();
    }

    /// <summary>获取初始化时的空目录。</summary>
    public static CompositeCatalogSnapshot Empty { get; } = new(0, FrozenDictionary<CompositeDefinitionId, CompositeCatalogEntry>.Empty);

    /// <summary>按稳定身份查找当前世代的蓝图。</summary>
    /// <param name="id">待查找的蓝图身份。</param>
    /// <param name="entry">成功时为条目，失败时为 null。</param>
    /// <returns>目录中是否存在对应蓝图。</returns>
    public bool TryGet(CompositeDefinitionId id, out CompositeCatalogEntry entry) => Composites.TryGetValue(id, out entry!);
}

/// <summary>表示一次蓝图发布的新增、移除和变化集合。</summary>
/// <remarks>集合由加载器提供并保持稳定；世代自身不参与差异判断。</remarks>
/// <example>仅设计属性变化也属于 Changed，见 <c>CompositeContentTests</c>。</example>
public sealed class CompositeCatalogChange
{
    /// <summary>获取发布后的目录世代。</summary>
    public long Generation { get; }
    /// <summary>获取新增蓝图身份。</summary>
    public IReadOnlySet<CompositeDefinitionId> Added { get; }
    /// <summary>获取移除蓝图身份。</summary>
    public IReadOnlySet<CompositeDefinitionId> Removed { get; }
    /// <summary>获取发布指纹变化的既有蓝图身份。</summary>
    public IReadOnlySet<CompositeDefinitionId> Changed { get; }
    /// <summary>获取是否存在任何目录变化。</summary>
    public bool HasChanges => Added.Count > 0 || Removed.Count > 0 || Changed.Count > 0;

    /// <summary>保存一次发布的世代与三个稳定变化集合。</summary>
    /// <param name="generation">发布后的目录世代。</param>
    /// <param name="added">新增身份。</param>
    /// <param name="removed">移除身份。</param>
    /// <param name="changed">发布指纹变化的既有身份。</param>
    public CompositeCatalogChange(long generation, IReadOnlySet<CompositeDefinitionId> added,
        IReadOnlySet<CompositeDefinitionId> removed, IReadOnlySet<CompositeDefinitionId> changed)
    {
        Generation = generation;
        Added = added;
        Removed = removed;
        Changed = changed;
    }
}

/// <summary>原子保存蓝图目录快照，在加载器发布后同步通知订阅者。</summary>
/// <remarks>快照替换与变化通知为两个独立操作，加载器决定其时机。</remarks>
/// <example>无效候选不替换快照，见 <c>CompositeContentTests</c>。</example>
public sealed class CompositeCatalog
{
    private CompositeCatalogSnapshot _current = CompositeCatalogSnapshot.Empty;
    /// <summary>原子读取当前快照；首次发布前为世代 0 的空目录。</summary>
    public CompositeCatalogSnapshot Current => Volatile.Read(ref _current);
    /// <summary>在有变化的发布后同步通知订阅者；单个订阅者故障不阻断其他订阅者。</summary>
    public event Action<CompositeCatalogChange>? Changed;

    /// <summary>替换当前快照，旧冻结快照仍可读取。</summary>
    internal void Publish(CompositeCatalogSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Volatile.Write(ref _current, snapshot);
    }

    /// <summary>通知本次调用开始时已登记的订阅者，把订阅异常交给故障回调。</summary>
    internal void NotifyChanged(CompositeCatalogChange change, Action<Exception> onFault)
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(onFault);
        var handlers = Changed;
        if (handlers is null)
            return;
        foreach (Action<CompositeCatalogChange> handler in handlers.GetInvocationList())
        {
            try { handler(change); }
            catch (Exception exception) { onFault(exception); }
        }
    }
}

/// <summary>规范编码蓝图定义与发布属性；引用目标的修订不进入蓝图自身修订。</summary>
internal static class CompositeRevisions
{
    /// <summary>规范编码版本；#222 删除接触点和 Archetype 后升级为 2，与文件 schema 独立。</summary>
    public const int SemanticEncodingVersion = 2;

    /// <summary>计算身份、尺寸档位及按坐标排序的布局的 SHA-256 修订。</summary>
    public static string ComputeSemanticRevision(CompositeDefinition definition)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("encoding_version", SemanticEncodingVersion);
            writer.WriteString("id", definition.Id.Value);
            writer.WriteString("kind", "composite");
            writer.WriteString("size_class", VehicleSize.ToWire(definition.SizeClass));
            writer.WriteStartArray("voxels");
            foreach (var voxel in definition.Voxels)
            {
                writer.WriteStartObject();
                writer.WriteString("definition", voxel.DefinitionId.Value);
                writer.WriteString("orientation", voxel.Orientation.ToString());
                writer.WriteNumber("x", voxel.Cell.X);
                writer.WriteNumber("y", voxel.Cell.Y);
                writer.WriteNumber("z", voxel.Cell.Z);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Sha256Hex(stream.ToArray());
    }

    /// <summary>计算显示字段、设计属性和自身语义修订的发布指纹。</summary>
    public static string ComputePublicationFingerprint(CompositeDefinition definition, string? fileDisplayName,
        string semanticRevision, DesignAttributes attributes)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("attributes");
            writer.WriteStartObject("bom");
            foreach (var (id, count) in attributes.Bom.OrderBy(item => item.Key.Value, StringComparer.Ordinal))
                writer.WriteNumber(id.Value, count);
            writer.WriteEndObject();
            writer.WriteNumber("bom_value", attributes.BomValue);
            writer.WriteNumber("cargo_capacity", attributes.CargoCapacity);
            writer.WriteBoolean("has_control", attributes.HasControl);
            writer.WriteNumber("mass", attributes.Mass);
            if (attributes.RatedSpeed is { } speed)
                writer.WriteNumber("rated_speed", speed);
            else
                writer.WriteNull("rated_speed");
            writer.WriteNumber("structural_durability_max", attributes.StructuralDurabilityMax);
            writer.WriteNumber("traction", attributes.Traction);
            writer.WriteEndObject();
            writer.WriteString("description", definition.Description);
            writer.WriteString("display_name", fileDisplayName ?? "");
            writer.WriteString("name", definition.Name);
            writer.WriteString("semantic_revision", semanticRevision);
            writer.WriteEndObject();
        }
        return Sha256Hex(stream.ToArray());
    }

    /// <summary>计算规范字节的 SHA-256 大写十六进制值。</summary>
    private static string Sha256Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}

/// <summary>按身份与发布指纹比较两个蓝图目录。</summary>
internal static class CompositeCatalogDiff
{
    /// <summary>计算候选相对旧快照的新增、移除和发布变化。</summary>
    public static CompositeCatalogChange Compute(long generation, CompositeCatalogSnapshot? previous,
        IReadOnlyDictionary<CompositeDefinitionId, CompositeCatalogEntry> next)
    {
        var added = new HashSet<CompositeDefinitionId>();
        var removed = new HashSet<CompositeDefinitionId>();
        var changed = new HashSet<CompositeDefinitionId>();
        foreach (var (id, entry) in next)
        {
            if (previous is null || !previous.Composites.TryGetValue(id, out var prior))
                added.Add(id);
            else if (!string.Equals(prior.PublicationFingerprint, entry.PublicationFingerprint, StringComparison.Ordinal))
                changed.Add(id);
        }
        if (previous is not null)
            removed.UnionWith(previous.Composites.Keys.Where(id => !next.ContainsKey(id)));
        return new CompositeCatalogChange(generation, added, removed, changed);
    }
}

/// <summary>解析候选蓝图的体素引用、检查允许朝向并计算设计属性；失败按包隔离。</summary>
internal static class CompositeReferenceValidator
{
    /// <summary>内置蓝图失败时拒绝候选；可选内容失败时删除其命名空间并重查级联引用。</summary>
    public static (bool CanCommit, IReadOnlyList<ContentDiagnostic> Diagnostics) Filter(
        IDictionary<VoxelDefinitionId, VoxelCatalogEntry> voxels,
        IDictionary<CompositeDefinitionId, CompositeCatalogEntry> composites)
    {
        ArgumentNullException.ThrowIfNull(voxels);
        ArgumentNullException.ThrowIfNull(composites);
        var diagnostics = new List<ContentDiagnostic>();
        while (true)
        {
            var failedNamespaces = new HashSet<string>(StringComparer.Ordinal);
            var rejectBuiltIn = false;
            foreach (var composite in composites.Values.ToArray())
            {
                if (TryCalculate(voxels, composite, diagnostics, out var attributes))
                {
                    composites[composite.Definition.Id] = new CompositeCatalogEntry(
                        composite.Definition, composite.Origin, composite.FileDisplayName, composite.SemanticRevision,
                        CompositeRevisions.ComputePublicationFingerprint(composite.Definition, composite.FileDisplayName,
                            composite.SemanticRevision, attributes), attributes);
                    continue;
                }
                if (composite.Origin.Namespace == VoxelDefinitionId.BuiltInNamespace)
                    rejectBuiltIn = true;
                else
                    failedNamespaces.Add(composite.Origin.Namespace);
            }
            if (rejectBuiltIn)
                return (false, diagnostics);
            if (failedNamespaces.Count == 0)
                return (true, diagnostics);
            foreach (var ns in failedNamespaces)
            {
                foreach (var id in voxels.Keys.Where(id => id.Namespace == ns).ToArray())
                    voxels.Remove(id);
                foreach (var id in composites.Keys.Where(id => id.Namespace == ns).ToArray())
                    composites.Remove(id);
            }
        }
    }

    /// <summary>解析蓝图布局至核心定义序列；引用、朝向或汇总溢出失败时追加来源诊断。</summary>
    private static bool TryCalculate(IDictionary<VoxelDefinitionId, VoxelCatalogEntry> voxels,
        CompositeCatalogEntry composite, List<ContentDiagnostic> diagnostics, out DesignAttributes attributes)
    {
        attributes = DesignAttributes.Empty;
        var severity = composite.Origin.Namespace == VoxelDefinitionId.BuiltInNamespace
            ? ContentDiagnosticSeverity.Error : ContentDiagnosticSeverity.Warning;
        var resolved = new List<ICoreVoxelDefinition>();
        var valid = true;
        foreach (var voxel in composite.Definition.Voxels)
        {
            if (!voxels.TryGetValue(voxel.DefinitionId, out var entry))
            {
                valid = false;
                diagnostics.Add(new ContentDiagnostic(severity, ContentDiagnosticCodes.UnresolvedVoxelReference,
                    "A composite references a voxel missing from the candidate catalog.", composite.Origin.SourceFile,
                    voxel.DefinitionId.Value));
                continue;
            }
            var allowed = entry.Definition.AllowedOrientations switch
            {
                VoxelOrientationConstraint.None => voxel.Orientation == VoxelOrientation.Identity,
                VoxelOrientationConstraint.FourWay => VoxelOrientation.FourWay.Contains(voxel.Orientation),
                VoxelOrientationConstraint.All24 => VoxelOrientation.All.Contains(voxel.Orientation),
                _ => false
            };
            if (!allowed)
            {
                valid = false;
                diagnostics.Add(new ContentDiagnostic(severity, ContentDiagnosticCodes.OrientationInvalid,
                    "A composite voxel uses an orientation disallowed by its definition.", composite.Origin.SourceFile,
                    voxel.DefinitionId.Value));
            }
            resolved.Add(entry.Definition);
        }
        if (!valid)
            return false;
        try
        {
            attributes = DesignAttributeCalculator.Calculate(resolved);
            return true;
        }
        catch (OverflowException)
        {
            diagnostics.Add(new ContentDiagnostic(severity, ContentDiagnosticCodes.LayoutInvalid,
                "Composite design attributes exceed the representable range.", composite.Origin.SourceFile,
                composite.Definition.Id.Value));
            return false;
        }
    }
}
