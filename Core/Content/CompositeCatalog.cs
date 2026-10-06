// 属于内容层的目录模块。
// 保存蓝图与 Archetype 的发布快照，计算内容修订和目录差异，并校验候选引用。
using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text.Json;
using Train.Core.Content.Composites;
using Train.Core.Content.Voxels;

namespace Train.Core.Content;

/// <summary>表示内容目录中一个已校验的 Archetype，以及加载该定义时确定的来源和修订信息。</summary>
/// <remarks>定义校验和修订计算由内容加载器完成；条目构造函数只检查必填引用与修订字符串非空，不重新计算修订或校验其格式。</remarks>
/// <example>内置 Archetype 发布并按标识读取的用例见 <c>CompositeContentTests.Builtin_style_composites_commit_through_the_existing_reloader</c>。</example>
public sealed class CompositeArchetypeCatalogEntry
{
    /// <summary>获取已校验且不可变的 Archetype 定义。</summary>
    public CompositeArchetype Archetype { get; }
    /// <summary>获取定义所属的内容包和包内源文件。</summary>
    public ContentOrigin Origin { get; }
    /// <summary>获取 Archetype 身份、建造规则和身份保留标签规范编码的 SHA-256 大写十六进制修订。</summary>
    public string SemanticRevision { get; }
    /// <summary>获取用于判断发布变化的指纹；内容加载器当前直接使用语义修订。</summary>
    public string PublicationFingerprint { get; }

    /// <summary>创建一个已校验 Archetype 的目录条目。</summary>
    /// <param name="archetype">已校验的 Archetype 定义。</param>
    /// <param name="origin">定义所属的内容包和包内源文件。</param>
    /// <param name="semanticRevision">非空的语义修订字符串。</param>
    /// <param name="publicationFingerprint">非空的发布指纹。</param>
    /// <exception cref="ArgumentNullException"><paramref name="archetype"/>、<paramref name="origin"/>、<paramref name="semanticRevision"/> 或 <paramref name="publicationFingerprint"/> 为 <see langword="null"/>。</exception>
    /// <exception cref="ArgumentException"><paramref name="semanticRevision"/> 或 <paramref name="publicationFingerprint"/> 为空字符串。</exception>
    public CompositeArchetypeCatalogEntry(
        CompositeArchetype archetype,
        ContentOrigin origin,
        string semanticRevision,
        string publicationFingerprint)
    {
        ArgumentNullException.ThrowIfNull(archetype);
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentException.ThrowIfNullOrEmpty(semanticRevision);
        ArgumentException.ThrowIfNullOrEmpty(publicationFingerprint);
        Archetype = archetype;
        Origin = origin;
        SemanticRevision = semanticRevision;
        PublicationFingerprint = publicationFingerprint;
    }
}

/// <summary>表示内容目录中一个已校验的蓝图，以及加载该定义时确定的来源、显示名和修订信息。</summary>
/// <remarks>定义校验和修订计算由内容加载器完成；条目构造函数只检查必填引用与修订字符串非空，不重新计算修订或校验其格式。</remarks>
/// <example>玩家蓝图的文件显示名和 UUID 命名空间见 <c>CompositeContentTests.Player_blueprint_uses_file_uuid_namespace_and_in_file_display_name</c>。</example>
public sealed class CompositeCatalogEntry
{
    /// <summary>获取已规范化且不可变的蓝图定义。</summary>
    public CompositeDefinition Definition { get; }
    /// <summary>获取定义所属的内容包和包内源文件。</summary>
    public ContentOrigin Origin { get; }
    /// <summary>获取定义文件的显示名；文件未声明时为 <see langword="null"/>。</summary>
    public string? FileDisplayName { get; }
    /// <summary>获取身份、Archetype、尺寸档位、体素布局和接触点规范编码的 SHA-256 大写十六进制修订。</summary>
    public string SemanticRevision { get; }
    /// <summary>获取语义修订、蓝图名称、说明和文件显示名共同形成的 SHA-256 大写十六进制发布指纹。</summary>
    public string PublicationFingerprint { get; }

    /// <summary>创建一个已校验蓝图的目录条目。</summary>
    /// <param name="definition">已校验、已规范化的蓝图定义。</param>
    /// <param name="origin">定义所属的内容包和包内源文件。</param>
    /// <param name="fileDisplayName">定义文件声明的显示名；未声明时为 <see langword="null"/>。</param>
    /// <param name="semanticRevision">非空的语义修订字符串。</param>
    /// <param name="publicationFingerprint">非空的发布指纹。</param>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/>、<paramref name="origin"/>、<paramref name="semanticRevision"/> 或 <paramref name="publicationFingerprint"/> 为 <see langword="null"/>。</exception>
    /// <exception cref="ArgumentException"><paramref name="semanticRevision"/> 或 <paramref name="publicationFingerprint"/> 为空字符串。</exception>
    public CompositeCatalogEntry(
        CompositeDefinition definition,
        ContentOrigin origin,
        string? fileDisplayName,
        string semanticRevision,
        string publicationFingerprint)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentException.ThrowIfNullOrEmpty(semanticRevision);
        ArgumentException.ThrowIfNullOrEmpty(publicationFingerprint);
        Definition = definition;
        Origin = origin;
        FileDisplayName = fileDisplayName;
        SemanticRevision = semanticRevision;
        PublicationFingerprint = publicationFingerprint;
    }
}

/// <summary>表示某一目录世代完整且不可变的蓝图与 Archetype 集合。</summary>
/// <remarks>两个目录索引均冻结保存；世代由调用方提供，本类型不检查其取值范围或与其他快照的先后关系。</remarks>
/// <example>同一快照分别按蓝图和 Archetype 标识读取条目的用例见 <c>CompositeContentTests.Builtin_style_composites_commit_through_the_existing_reloader</c>。</example>
public sealed class CompositeCatalogSnapshot
{
    /// <summary>获取本快照的目录世代；内容加载器以零表示初始化的空目录，有变化的发布使世代递增。</summary>
    public long Generation { get; }
    /// <summary>获取按身份索引的全部 Archetype。</summary>
    public IReadOnlyDictionary<CompositeArchetypeId, CompositeArchetypeCatalogEntry> Archetypes { get; }
    /// <summary>获取按身份索引的全部蓝图。</summary>
    public IReadOnlyDictionary<CompositeDefinitionId, CompositeCatalogEntry> Composites { get; }

    /// <summary>创建目录快照，并分别按稳定标识冻结 Archetype 和蓝图条目。</summary>
    /// <param name="generation">本快照的目录世代。</param>
    /// <param name="archetypes">按 Archetype 标识索引的完整条目集合。</param>
    /// <param name="composites">按蓝图标识索引的完整条目集合。</param>
    /// <remarks>传入冻结字典时直接复用；其他字典复制后冻结，后续修改原字典不会改变快照。</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="archetypes"/> 或 <paramref name="composites"/> 为 <see langword="null"/>。</exception>
    public CompositeCatalogSnapshot(
        long generation,
        IReadOnlyDictionary<CompositeArchetypeId, CompositeArchetypeCatalogEntry> archetypes,
        IReadOnlyDictionary<CompositeDefinitionId, CompositeCatalogEntry> composites)
    {
        ArgumentNullException.ThrowIfNull(archetypes);
        ArgumentNullException.ThrowIfNull(composites);
        Generation = generation;
        Archetypes = archetypes as FrozenDictionary<CompositeArchetypeId, CompositeArchetypeCatalogEntry> ??
                     archetypes.ToFrozenDictionary();
        Composites = composites as FrozenDictionary<CompositeDefinitionId, CompositeCatalogEntry> ??
                     composites.ToFrozenDictionary();
    }

    /// <summary>获取世代为零的空目录快照。</summary>
    public static CompositeCatalogSnapshot Empty { get; } = new(
        0,
        FrozenDictionary<CompositeArchetypeId, CompositeArchetypeCatalogEntry>.Empty,
        FrozenDictionary<CompositeDefinitionId, CompositeCatalogEntry>.Empty);

    /// <summary>尝试按蓝图标识取得本世代的目录条目。</summary>
    /// <param name="id">要查找的蓝图标识。</param>
    /// <param name="entry">成功时为对应目录条目；失败时为 <see langword="null"/>。</param>
    /// <returns>存在对应标识时为 <see langword="true"/>；否则为 <see langword="false"/>。</returns>
    public bool TryGet(CompositeDefinitionId id, out CompositeCatalogEntry entry) =>
        Composites.TryGetValue(id, out entry!);

    /// <summary>尝试按 Archetype 标识取得本世代的目录条目。</summary>
    /// <param name="id">要查找的 Archetype 标识。</param>
    /// <param name="entry">成功时为对应目录条目；失败时为 <see langword="null"/>。</param>
    /// <returns>存在对应标识时为 <see langword="true"/>；否则为 <see langword="false"/>。</returns>
    public bool TryGet(CompositeArchetypeId id, out CompositeArchetypeCatalogEntry entry) =>
        Archetypes.TryGetValue(id, out entry!);
}

/// <summary>表示一次组合内容发布相对上一快照的蓝图与 Archetype 变化。</summary>
/// <remarks>六组标识通过只读接口提供，但直接引用构造时传入的集合；调用方须保证集合非 null，并在交给订阅者后保持内容稳定。</remarks>
/// <example>显示字段与语义字段分别触发发布变化的用例见 <c>CompositeContentTests.Semantic_revision_ignores_layout_and_excludes_display_and_target_revisions</c>。</example>
public sealed class CompositeCatalogChange
{
    /// <summary>获取此次变化对应的目标目录世代。</summary>
    public long Generation { get; }
    /// <summary>获取新增蓝图身份。</summary>
    public IReadOnlySet<CompositeDefinitionId> Added { get; }
    /// <summary>获取移除蓝图身份。</summary>
    public IReadOnlySet<CompositeDefinitionId> Removed { get; }
    /// <summary>获取发布指纹改变的既有蓝图身份。</summary>
    public IReadOnlySet<CompositeDefinitionId> Changed { get; }
    /// <summary>获取新增 Archetype 身份。</summary>
    public IReadOnlySet<CompositeArchetypeId> AddedArchetypes { get; }
    /// <summary>获取移除 Archetype 身份。</summary>
    public IReadOnlySet<CompositeArchetypeId> RemovedArchetypes { get; }
    /// <summary>获取发布指纹改变的既有 Archetype 身份。</summary>
    public IReadOnlySet<CompositeArchetypeId> ChangedArchetypes { get; }

    /// <summary>使用调用方提供的六组标识创建蓝图与 Archetype 目录变化。</summary>
    /// <param name="generation">变化应用后的目录世代。</param>
    /// <param name="added">新增的蓝图标识。</param>
    /// <param name="removed">移除的蓝图标识。</param>
    /// <param name="changed">身份仍存在但发布指纹改变的蓝图标识。</param>
    /// <param name="addedArchetypes">新增的 Archetype 标识。</param>
    /// <param name="removedArchetypes">移除的 Archetype 标识。</param>
    /// <param name="changedArchetypes">身份仍存在但发布指纹改变的 Archetype 标识。</param>
    /// <remarks>集合可以为空；本构造函数不检查参数、不复制或冻结集合，也不校验各组标识是否互斥。</remarks>
    public CompositeCatalogChange(
        long generation,
        IReadOnlySet<CompositeDefinitionId> added,
        IReadOnlySet<CompositeDefinitionId> removed,
        IReadOnlySet<CompositeDefinitionId> changed,
        IReadOnlySet<CompositeArchetypeId> addedArchetypes,
        IReadOnlySet<CompositeArchetypeId> removedArchetypes,
        IReadOnlySet<CompositeArchetypeId> changedArchetypes)
    {
        Generation = generation;
        Added = added;
        Removed = removed;
        Changed = changed;
        AddedArchetypes = addedArchetypes;
        RemovedArchetypes = removedArchetypes;
        ChangedArchetypes = changedArchetypes;
    }

    /// <summary>获取蓝图或 Archetype 是否至少存在一项新增、移除或发布指纹改变。</summary>
    public bool HasChanges =>
        Added.Count > 0 || Removed.Count > 0 || Changed.Count > 0 ||
        AddedArchetypes.Count > 0 || RemovedArchetypes.Count > 0 || ChangedArchetypes.Count > 0;
}

/// <summary>原子保存当前蓝图目录快照，并在成功发布变化后通知订阅方。</summary>
/// <remarks>候选校验、差异计算、发布与通知的时机由内容加载器负责；替换快照和通知订阅方是两个独立操作。</remarks>
/// <example>拒绝无效内容并保留当前目录世代的用例见 <c>CompositeContentTests.Missing_required_composite_fields_reject_built_in_and_keep_generation</c>。</example>
public sealed class CompositeCatalog
{
    private CompositeCatalogSnapshot _current = CompositeCatalogSnapshot.Empty;

    /// <summary>原子读取当前快照；首次发布前为世代零的空目录，始终不为 null。</summary>
    public CompositeCatalogSnapshot Current => Volatile.Read(ref _current);

    /// <summary>由内容加载器在发布有变化的新快照后，同步通知蓝图与 Archetype 的变化。</summary>
    /// <remarks>逐个调用通知开始时已登记的订阅者；订阅者异常交给故障回调，回调正常返回后继续通知其余订阅者。</remarks>
    public event Action<CompositeCatalogChange>? Changed;

    /// <summary>原子替换当前已发布快照。</summary>
    /// <param name="snapshot">要发布的完整蓝图与 Archetype 快照；其目录世代由调用方确定。</param>
    /// <remarks>快照替换后，之前取得的冻结快照仍可读取；变化通知由调用方另行调用 <see cref="NotifyChanged"/> 发出。</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> 为 <see langword="null"/>。</exception>
    internal void Publish(CompositeCatalogSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Volatile.Write(ref _current, snapshot);
    }

    /// <summary>依次通知本次通知开始时已登记的订阅方，并把每个订阅异常交给故障回调。</summary>
    /// <param name="change">本次发布的目录世代及蓝图、Archetype 变化集。</param>
    /// <param name="onFault">处理单个订阅者异常的回调；正常返回后继续后续通知。</param>
    /// <remarks>无订阅者时直接返回。若故障回调自身抛出异常，该异常向调用方传播并终止本次通知；本方法不修改当前快照。</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="change"/> 或 <paramref name="onFault"/> 为 <see langword="null"/>。</exception>
    internal void NotifyChanged(CompositeCatalogChange change, Action<Exception> onFault)
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(onFault);
        var handlers = Changed;
        if (handlers is null)
            return;
        foreach (Action<CompositeCatalogChange> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(change);
            }
            catch (Exception exception)
            {
                onFault(exception);
            }
        }
    }
}

/// <summary>把蓝图和 Archetype 定义编码为字段及集合顺序固定的 UTF-8 JSON，并计算语义修订与发布指纹。</summary>
/// <remarks>蓝图语义编码只记录引用的身份，不读取被引用体素或 Archetype 的修订；显示字段另行参与蓝图发布指纹。</remarks>
/// <example>文件排版和体素数组顺序不影响修订，显示字段只影响发布指纹；见 <c>CompositeContentTests.Semantic_revision_ignores_layout_and_excludes_display_and_target_revisions</c>。</example>
internal static class CompositeRevisions
{
    /// <summary>语义编码中 <c>encoding_version</c> 的固定值；当前规范编码使用版本 1，版本值本身参与哈希。</summary>
    /// <remarks>与定义文件的 <c>schema_version</c> 分开管理；改变规范编码规则时需同步调整，修订用途见 docs/adr/0005-resolve-current-definitions-and-separate-content-versions.md。</remarks>
    public const int SemanticEncodingVersion = 1;
    /// <summary>蓝图语义编码中 <c>kind</c> 字段的固定值 <c>composite</c>，用于区分不同种类的内容。</summary>
    public const string CompositeKind = "composite";
    /// <summary>Archetype 语义编码中 <c>kind</c> 字段的固定值 <c>composite_archetype</c>，用于区分不同种类的内容。</summary>
    public const string ArchetypeKind = "composite_archetype";

    /// <summary>计算蓝图定义的语义修订。</summary>
    /// <param name="definition">待编码的蓝图定义。</param>
    /// <returns>规范编码字节的 SHA-256 修订，为 64 字符的大写十六进制字符串。</returns>
    /// <remarks>编码包括版本、内容种类、身份、Archetype 标识、尺寸档位、按格坐标排序的体素定义标识、朝向与格坐标，以及按位置和绑定格排序的接触点位置与绑定格；名称、说明、文件显示名和引用目标的修订不参与。</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> 为 <see langword="null"/>。</exception>
    /// <example>蓝图尺寸档位从 small 改为 medium 会改变修订；见 <c>CompositeContentTests.Semantic_revision_ignores_layout_and_excludes_display_and_target_revisions</c>。</example>
    public static string ComputeSemanticRevision(CompositeDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return Sha256Hex(EncodeCompositeSemantic(definition));
    }

    /// <summary>计算 Archetype 定义的语义修订。</summary>
    /// <param name="archetype">待编码的 Archetype 定义。</param>
    /// <returns>规范编码字节的 SHA-256 修订，为 64 字符的大写十六进制字符串。</returns>
    /// <remarks>编码包括版本、内容种类、身份、非空六向连接要求、声明尺寸要求、接触点数量、接触点互异要求、接触点绑定已有部件要求和身份保留所需核心标签。</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="archetype"/> 为 <see langword="null"/>。</exception>
    /// <example>同一组规则在 JSON 中的字段顺序改变时，Archetype 修订保持不变；见 <c>CompositeContentTests.Semantic_revision_ignores_layout_and_excludes_display_and_target_revisions</c>。</example>
    public static string ComputeSemanticRevision(CompositeArchetype archetype)
    {
        ArgumentNullException.ThrowIfNull(archetype);
        return Sha256Hex(EncodeArchetypeSemantic(archetype));
    }

    /// <summary>计算蓝图一次完整发布的指纹。</summary>
    /// <param name="definition">待编码的蓝图定义。</param>
    /// <param name="fileDisplayName">定义文件的显示名；未声明时为 <see langword="null"/>。</param>
    /// <param name="semanticRevision">该定义已经计算出的非空语义修订。</param>
    /// <returns>语义修订、蓝图名称、说明和文件显示名规范编码后的 SHA-256 大写十六进制字符串。</returns>
    /// <remarks>文件显示名为 <see langword="null"/> 时按空字符串编码，因此这两种输入的发布指纹相同。</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> 或 <paramref name="semanticRevision"/> 为 <see langword="null"/>。</exception>
    /// <exception cref="ArgumentException"><paramref name="semanticRevision"/> 为空字符串。</exception>
    /// <example>只改蓝图名称、说明和文件显示名会使发布内容变化，语义修订保持不变；见 <c>CompositeContentTests.Semantic_revision_ignores_layout_and_excludes_display_and_target_revisions</c>。</example>
    public static string ComputePublicationFingerprint(
        CompositeDefinition definition,
        string? fileDisplayName,
        string semanticRevision)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrEmpty(semanticRevision);
        return Sha256Hex(EncodeCompositePublication(definition, fileDisplayName, semanticRevision));
    }

    /// <summary>取得 Archetype 一次完整发布的指纹。</summary>
    /// <param name="archetype">已计算语义修订的 Archetype 定义。</param>
    /// <param name="semanticRevision">该定义已经计算出的非空语义修订。</param>
    /// <returns><paramref name="semanticRevision"/> 本身；当前 Archetype 没有语义修订之外的发布字段。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="archetype"/> 或 <paramref name="semanticRevision"/> 为 <see langword="null"/>。</exception>
    /// <exception cref="ArgumentException"><paramref name="semanticRevision"/> 为空字符串。</exception>
    /// <example>同一 Archetype 在文件重排后修订和发布内容均不变；见 <c>CompositeContentTests.Semantic_revision_ignores_layout_and_excludes_display_and_target_revisions</c>。</example>
    public static string ComputePublicationFingerprint(CompositeArchetype archetype, string semanticRevision)
    {
        ArgumentNullException.ThrowIfNull(archetype);
        ArgumentException.ThrowIfNullOrEmpty(semanticRevision);
        return semanticRevision;
    }

    /// <summary>按固定字段顺序编码蓝图身份、Archetype、尺寸档位、体素布局和接触点，并规范化集合顺序。</summary>
    private static byte[] EncodeCompositeSemantic(CompositeDefinition definition)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WriteString("archetype", definition.ArchetypeId.Value);
            writer.WriteStartArray("contacts");
            foreach (var contact in definition.Contacts
                         .OrderBy(item => item.X)
                         .ThenBy(item => item.Y)
                         .ThenBy(item => item.Z)
                         .ThenBy(item => item.BoundCell))
            {
                writer.WriteStartObject();
                writer.WriteNumber("cell_x", contact.BoundCell.X);
                writer.WriteNumber("cell_y", contact.BoundCell.Y);
                writer.WriteNumber("cell_z", contact.BoundCell.Z);
                writer.WritePropertyName("x");
                writer.WriteRawValue(VoxelDefinition.FormatMass(contact.X));
                writer.WritePropertyName("y");
                writer.WriteRawValue(VoxelDefinition.FormatMass(contact.Y));
                writer.WritePropertyName("z");
                writer.WriteRawValue(VoxelDefinition.FormatMass(contact.Z));
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteNumber("encoding_version", SemanticEncodingVersion);
            writer.WriteString("id", definition.Id.Value);
            writer.WriteString("kind", CompositeKind);
            writer.WriteString("size_class", VehicleSize.ToWire(definition.SizeClass));
            writer.WriteStartArray("voxels");
            foreach (var voxel in definition.Voxels.OrderBy(item => item.Cell))
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
            writer.Flush();
        }

        return stream.ToArray();
    }

    /// <summary>按固定字段顺序编码 Archetype 身份、五项建造规则和身份保留所需核心标签。</summary>
    private static byte[] EncodeArchetypeSemantic(CompositeArchetype archetype)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("construction");
            writer.WriteNumber("contact_anchor_count", archetype.Construction.ContactAnchorCount);
            writer.WriteBoolean("contacts_must_be_distinct", archetype.Construction.ContactsMustBeDistinct);
            writer.WriteBoolean("contacts_must_bind_existing_parts", archetype.Construction.ContactsMustBindExistingParts);
            writer.WriteBoolean("require_declared_size", archetype.Construction.RequireDeclaredSize);
            writer.WriteBoolean("require_nonempty_six_connected", archetype.Construction.RequireNonEmptySixConnected);
            writer.WriteEndObject();
            writer.WriteNumber("encoding_version", SemanticEncodingVersion);
            writer.WriteString("id", archetype.Id.Value);
            writer.WriteStartObject("identity_retention");
            writer.WriteString("require_core_tag", archetype.IdentityRetention.RequiredCoreTag);
            writer.WriteEndObject();
            writer.WriteString("kind", ArchetypeKind);
            writer.WriteEndObject();
            writer.Flush();
        }

        return stream.ToArray();
    }

    /// <summary>编码蓝图名称、说明、文件显示名和语义修订组成的完整发布状态。</summary>
    private static byte[] EncodeCompositePublication(
        CompositeDefinition definition,
        string? fileDisplayName,
        string semanticRevision)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WriteString("description", definition.Description);
            writer.WriteString("display_name", fileDisplayName ?? "");
            writer.WriteString("name", definition.Name);
            writer.WriteString("semantic_revision", semanticRevision);
            writer.WriteEndObject();
            writer.Flush();
        }

        return stream.ToArray();
    }

    /// <summary>计算字节序列的 SHA-256，并返回大写十六进制文本。</summary>
    private static string Sha256Hex(ReadOnlySpan<byte> bytes)
    {
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(bytes, hash);
        return Convert.ToHexString(hash);
    }
}

/// <summary>按身份和发布指纹比较两份蓝图及 Archetype 目录，计算各自的新增、移除和改变集合。</summary>
/// <example>仅修改显示字段也属于发布变化，体素数组重排则不产生变化；见 <c>CompositeContentTests.Semantic_revision_ignores_layout_and_excludes_display_and_target_revisions</c>。</example>
internal static class CompositeCatalogDiff
{
    /// <summary>以发布指纹分别比较前后蓝图与 Archetype 快照，并把身份分类为新增、移除或改变。</summary>
    /// <param name="generation">结果变化集对应的目标目录世代。</param>
    /// <param name="previous">上一目录快照；为 null 时，两个候选目录的全部标识都归入新增集合。</param>
    /// <param name="nextArchetypes">按身份索引的完整候选 Archetype 目录，不能为 null。</param>
    /// <param name="nextComposites">按身份索引的完整候选蓝图目录，不能为 null。</param>
    /// <returns>包含六组新建标识集合的目录差异；所有集合都可以为空。</returns>
    /// <remarks>发布指纹按区分大小写的精确值比较；来源和世代本身不参与差异判断。计算不修改输入目录。</remarks>
    public static CompositeCatalogChange Compute(
        long generation,
        CompositeCatalogSnapshot? previous,
        IReadOnlyDictionary<CompositeArchetypeId, CompositeArchetypeCatalogEntry> nextArchetypes,
        IReadOnlyDictionary<CompositeDefinitionId, CompositeCatalogEntry> nextComposites)
    {
        var added = new HashSet<CompositeDefinitionId>();
        var removed = new HashSet<CompositeDefinitionId>();
        var changed = new HashSet<CompositeDefinitionId>();
        var addedArchetypes = new HashSet<CompositeArchetypeId>();
        var removedArchetypes = new HashSet<CompositeArchetypeId>();
        var changedArchetypes = new HashSet<CompositeArchetypeId>();
        var previousComposites = previous?.Composites;
        var previousArchetypes = previous?.Archetypes;

        if (previousComposites is null)
            added.UnionWith(nextComposites.Keys);
        else
        {
            foreach (var (id, entry) in nextComposites)
            {
                if (!previousComposites.TryGetValue(id, out var prior))
                    added.Add(id);
                else if (!string.Equals(prior.PublicationFingerprint, entry.PublicationFingerprint, StringComparison.Ordinal))
                    changed.Add(id);
            }

            foreach (var id in previousComposites.Keys)
            {
                if (!nextComposites.ContainsKey(id))
                    removed.Add(id);
            }
        }

        if (previousArchetypes is null)
            addedArchetypes.UnionWith(nextArchetypes.Keys);
        else
        {
            foreach (var (id, entry) in nextArchetypes)
            {
                if (!previousArchetypes.TryGetValue(id, out var prior))
                    addedArchetypes.Add(id);
                else if (!string.Equals(prior.PublicationFingerprint, entry.PublicationFingerprint, StringComparison.Ordinal))
                    changedArchetypes.Add(id);
            }

            foreach (var id in previousArchetypes.Keys)
            {
                if (!nextArchetypes.ContainsKey(id))
                    removedArchetypes.Add(id);
            }
        }

        return new CompositeCatalogChange(
            generation,
            added,
            removed,
            changed,
            addedArchetypes,
            removedArchetypes,
            changedArchetypes);
    }
}

/// <summary>检查候选蓝图对 Archetype 和体素定义的引用，并按内容命名空间剔除引用不完整的可选内容。</summary>
/// <example>引用缺失体素的 Mod 蓝图被隔离，内置蓝图仍可发布；见 <c>CompositeContentTests.Candidate_drops_a_mod_whose_composite_references_are_unresolved</c>。</example>
internal static class CompositeReferenceValidator
{
    /// <summary>校验候选蓝图引用；内置引用失败时拒绝发布，其他来源失败时移除同命名空间的全部候选内容后重试。</summary>
    /// <param name="voxels">可就地移除条目的候选体素目录。</param>
    /// <param name="archetypes">可就地移除条目的候选 Archetype 目录。</param>
    /// <param name="composites">可就地移除条目的候选蓝图目录。</param>
    /// <returns>允许或拒绝候选发布的决定，以及发现的引用诊断。</returns>
    /// <remarks>内置蓝图缺少引用时追加错误并拒绝发布；Mod 或玩家蓝图缺少引用时追加警告，并从三个候选目录移除其命名空间。移除可能使其他蓝图失去引用，因此重复检查直到没有新的失败；拒绝时也不恢复此前已移除的候选条目。</remarks>
    /// <exception cref="ArgumentNullException">任一候选目录为 <see langword="null"/>。</exception>
    public static ContentPreCommitDecision Filter(
        IDictionary<VoxelDefinitionId, VoxelCatalogEntry> voxels,
        IDictionary<CompositeArchetypeId, CompositeArchetypeCatalogEntry> archetypes,
        IDictionary<CompositeDefinitionId, CompositeCatalogEntry> composites)
    {
        ArgumentNullException.ThrowIfNull(voxels);
        ArgumentNullException.ThrowIfNull(archetypes);
        ArgumentNullException.ThrowIfNull(composites);
        var diagnostics = new List<ContentDiagnostic>();
        while (true)
        {
            var failedMods = new HashSet<string>(StringComparer.Ordinal);
            var failedPlayers = new HashSet<string>(StringComparer.Ordinal);
            var rejectBuiltIn = false;
            foreach (var composite in composites.Values.ToArray())
            {
                if (CompositeReferencesAreValid(voxels, archetypes, composite, diagnostics))
                    continue;
                if (composite.Origin.Namespace == VoxelDefinitionId.BuiltInNamespace)
                    rejectBuiltIn = true;
                else if (Guid.TryParseExact(composite.Origin.Namespace, "D", out _))
                    failedPlayers.Add(composite.Origin.Namespace);
                else
                    failedMods.Add(composite.Origin.Namespace);
            }

            if (rejectBuiltIn)
                return ContentPreCommitDecision.Reject(diagnostics);
            if (failedMods.Count == 0 && failedPlayers.Count == 0)
                return diagnostics.Count == 0
                    ? ContentPreCommitDecision.Allow()
                    : ContentPreCommitDecision.Allow(diagnostics);

            foreach (var ns in failedMods.Concat(failedPlayers))
                DropNamespace(ns, voxels, archetypes, composites);
        }
    }

    /// <summary>检查蓝图引用的 Archetype 和全部体素；为每个缺失引用追加诊断，内置来源记为错误，其他来源记为警告。</summary>
    private static bool CompositeReferencesAreValid(
        IDictionary<VoxelDefinitionId, VoxelCatalogEntry> voxels,
        IDictionary<CompositeArchetypeId, CompositeArchetypeCatalogEntry> archetypes,
        CompositeCatalogEntry composite,
        List<ContentDiagnostic> diagnostics)
    {
        var valid = true;
        var severity = composite.Origin.Namespace == VoxelDefinitionId.BuiltInNamespace
            ? ContentDiagnosticSeverity.Error
            : ContentDiagnosticSeverity.Warning;
        if (!archetypes.ContainsKey(composite.Definition.ArchetypeId))
        {
            valid = false;
            diagnostics.Add(new ContentDiagnostic(
                severity,
                ContentDiagnosticCodes.UnresolvedArchetypeReference,
                "A composite references an archetype missing from the candidate catalog.",
                composite.Origin.SourceFile,
                composite.Definition.ArchetypeId.Value));
        }

        foreach (var voxel in composite.Definition.Voxels)
        {
            if (voxels.ContainsKey(voxel.DefinitionId))
                continue;
            valid = false;
            diagnostics.Add(new ContentDiagnostic(
                severity,
                ContentDiagnosticCodes.UnresolvedVoxelReference,
                "A composite references a voxel missing from the candidate catalog.",
                composite.Origin.SourceFile,
                voxel.DefinitionId.Value));
        }

        return valid;
    }

    /// <summary>从体素、Archetype 和蓝图候选目录移除指定命名空间的全部内容。</summary>
    private static void DropNamespace(
        string ns,
        IDictionary<VoxelDefinitionId, VoxelCatalogEntry> voxels,
        IDictionary<CompositeArchetypeId, CompositeArchetypeCatalogEntry> archetypes,
        IDictionary<CompositeDefinitionId, CompositeCatalogEntry> composites)
    {
        foreach (var id in voxels.Keys.Where(id => id.Namespace == ns).ToArray())
            voxels.Remove(id);
        foreach (var id in archetypes.Keys.Where(id => id.Namespace == ns).ToArray())
            archetypes.Remove(id);
        foreach (var id in composites.Keys.Where(id => id.Namespace == ns).ToArray())
            composites.Remove(id);
    }
}
