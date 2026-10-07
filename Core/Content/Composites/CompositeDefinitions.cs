// 属于内容层的蓝图定义模块。
// 表示蓝图、Archetype、体素布局与接触点的不可变内容数据。
using System.Collections.ObjectModel;
using Train.Core.Content.Voxels;

namespace Train.Core.Content.Composites;

/// <summary>表示一种蓝图 Archetype 对布局与接触点施加的构造约束。</summary>
/// <example>
/// 测试内存包中的车辆要求非空六连通布局、声明尺寸，并恰有两个不同且绑定已有部件的接触点；
/// 见 <c>CompositeContentTests.Builtin_style_composites_commit_through_the_existing_reloader</c>。
/// </example>
public sealed class ConstructionRules
{
    /// <summary>获取布局是否必须非空且所有体素以六邻接方式连通。</summary>
    public bool RequireNonEmptySixConnected { get; }
    /// <summary>获取布局是否必须位于蓝图声明的车辆尺寸档位包络内。</summary>
    public bool RequireDeclaredSize { get; }
    /// <summary>获取蓝图必须声明的接触点数量；<c>0</c> 表示不要求接触点。</summary>
    public int ContactAnchorCount { get; }
    /// <summary>获取接触点是否必须具有互不重复的位置。</summary>
    public bool ContactsMustBeDistinct { get; }
    /// <summary>获取每个接触点是否必须绑定蓝图布局中的现有体素格。</summary>
    public bool ContactsMustBindExistingParts { get; }

    /// <summary>创建一组蓝图构造约束。</summary>
    /// <param name="requireNonEmptySixConnected">布局是否必须非空且六连通。</param>
    /// <param name="requireDeclaredSize">布局是否必须符合声明的车辆尺寸档位。</param>
    /// <param name="contactAnchorCount">要求的接触点数量；必须大于或等于 <c>0</c>。</param>
    /// <param name="contactsMustBeDistinct">接触点是否必须互不重复。</param>
    /// <param name="contactsMustBindExistingParts">接触点是否必须绑定布局中的现有体素格。</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="contactAnchorCount"/> 小于 <c>0</c>。</exception>
    public ConstructionRules(
        bool requireNonEmptySixConnected,
        bool requireDeclaredSize,
        int contactAnchorCount,
        bool contactsMustBeDistinct,
        bool contactsMustBindExistingParts)
    {
        if (contactAnchorCount < 0)
            throw new ArgumentOutOfRangeException(nameof(contactAnchorCount));
        RequireNonEmptySixConnected = requireNonEmptySixConnected;
        RequireDeclaredSize = requireDeclaredSize;
        ContactAnchorCount = contactAnchorCount;
        ContactsMustBeDistinct = contactsMustBeDistinct;
        ContactsMustBindExistingParts = contactsMustBindExistingParts;
    }
}

/// <summary>表示蓝图实例保留身份所需的核心部件标签。</summary>
/// <example>测试内存包中的车辆使用 <c>identity_core</c>，见 <c>CompositeContentTests.Builtin_style_composites_commit_through_the_existing_reloader</c>。</example>
public sealed class IdentityRetentionRules
{
    /// <summary>获取身份核心体素必须具有的标签；值为 1–64 个小写 ASCII 字母、数字、下划线或连字符。</summary>
    public string RequiredCoreTag { get; }

    /// <summary>创建身份保留规则，并去除标签首尾空白。</summary>
    /// <param name="requiredCoreTag">身份核心标签；必须符合内容标签格式。</param>
    /// <exception cref="ArgumentNullException"><paramref name="requiredCoreTag"/> 为 <see langword="null"/>。</exception>
    /// <exception cref="ArgumentException"><paramref name="requiredCoreTag"/> 去除首尾空白后不符合内容标签格式。</exception>
    public IdentityRetentionRules(string requiredCoreTag)
    {
        RequiredCoreTag = VoxelDefinition.NormalizeTag(requiredCoreTag, nameof(requiredCoreTag));
    }
}

/// <summary>表示一类蓝图共同采用的构造约束和身份保留规则。</summary>
/// <example>测试内存包中 <c>core:dev-hollow-shell</c> Archetype 的加载结果见 <c>CompositeContentTests.Builtin_style_composites_commit_through_the_existing_reloader</c>。</example>
public sealed class CompositeArchetype
{
    /// <summary>获取 Archetype 的稳定身份。</summary>
    public CompositeArchetypeId Id { get; }
    /// <summary>获取蓝图布局与接触点必须满足的构造约束。</summary>
    public ConstructionRules Construction { get; }
    /// <summary>获取蓝图实例保留身份所需的核心部件规则。</summary>
    public IdentityRetentionRules IdentityRetention { get; }

    /// <summary>创建蓝图 Archetype。</summary>
    /// <param name="id">Archetype 的稳定身份。</param>
    /// <param name="construction">蓝图构造约束。</param>
    /// <param name="identityRetention">身份保留规则。</param>
    /// <exception cref="ArgumentNullException"><paramref name="construction"/> 或 <paramref name="identityRetention"/> 为 <see langword="null"/>。</exception>
    public CompositeArchetype(
        CompositeArchetypeId id,
        ConstructionRules construction,
        IdentityRetentionRules identityRetention)
    {
        ArgumentNullException.ThrowIfNull(construction);
        ArgumentNullException.ThrowIfNull(identityRetention);
        Id = id;
        Construction = construction;
        IdentityRetention = identityRetention;
    }
}

/// <summary>表示蓝图布局中一个体素格使用的体素定义与朝向。</summary>
/// <example><c>(1,0,0)</c> 处可放置朝向为 <c>px_py</c> 的 <c>core:identity-core</c>；见 <c>CompositeContentTests.Minimal_blueprint_parses_archetype_voxels_and_separate_contact_bindings</c>。</example>
public sealed class CompositeVoxelSpec
{
    /// <summary>获取体素格相对蓝图原点的整数坐标，单位为体素。</summary>
    public LocalVoxelCoord Cell { get; }
    /// <summary>获取该体素格引用的体素定义身份。</summary>
    public VoxelDefinitionId DefinitionId { get; }
    /// <summary>获取体素在蓝图局部坐标系中的离散朝向。</summary>
    public VoxelOrientation Orientation { get; }

    /// <summary>创建一个蓝图体素规格。</summary>
    /// <param name="cell">体素格相对蓝图原点的整数坐标，单位为体素。</param>
    /// <param name="definitionId">该体素格引用的体素定义身份。</param>
    /// <param name="orientation">体素在蓝图局部坐标系中的离散朝向。</param>
    public CompositeVoxelSpec(LocalVoxelCoord cell, VoxelDefinitionId definitionId, VoxelOrientation orientation)
    {
        Cell = cell;
        DefinitionId = definitionId;
        Orientation = orientation;
    }
}

/// <summary>表示蓝图中的一个连续坐标接触点及其所绑定的体素格。</summary>
/// <example>坐标 <c>(1,-0.75,1.5)</c> 可绑定到体素格 <c>(1,0,0)</c>；见 <c>CompositeContentTests.Minimal_blueprint_parses_archetype_voxels_and_separate_contact_bindings</c>。</example>
public sealed class CompositeContactAnchor
{
    /// <summary>获取接触点相对蓝图原点的 X 坐标，单位为体素。</summary>
    public double X { get; }
    /// <summary>获取接触点相对蓝图原点的 Y 坐标，单位为体素。</summary>
    public double Y { get; }
    /// <summary>获取接触点相对蓝图原点的 Z 坐标，单位为体素。</summary>
    public double Z { get; }
    /// <summary>获取接触点绑定的蓝图体素格。</summary>
    public LocalVoxelCoord BoundCell { get; }

    /// <summary>创建接触点；连续坐标与绑定体素格可以不同。</summary>
    /// <param name="x">接触点相对蓝图原点的 X 坐标，单位为体素；必须为有限数。</param>
    /// <param name="y">接触点相对蓝图原点的 Y 坐标，单位为体素；必须为有限数。</param>
    /// <param name="z">接触点相对蓝图原点的 Z 坐标，单位为体素；必须为有限数。</param>
    /// <param name="boundCell">接触点绑定的蓝图体素格。</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="x"/>、<paramref name="y"/> 或 <paramref name="z"/> 不是有限数。</exception>
    public CompositeContactAnchor(double x, double y, double z, LocalVoxelCoord boundCell)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z))
            throw new ArgumentOutOfRangeException(nameof(x), "Contact coordinates must be finite.");
        X = x;
        Y = y;
        Z = z;
        BoundCell = boundCell;
    }
}

/// <summary>表示一个固定蓝图版本的显示信息、车辆尺寸档位、体素布局与接触点。</summary>
/// <example>最小车辆蓝图的加载结果见 <c>CompositeContentTests.Minimal_blueprint_parses_archetype_voxels_and_separate_contact_bindings</c>。</example>
public sealed class CompositeDefinition
{
    /// <summary>获取蓝图定义的稳定身份。</summary>
    public CompositeDefinitionId Id { get; }
    /// <summary>获取去除首尾空白后的显示名称，长度为 1–100 个 Unicode 标量值。</summary>
    public string Name { get; }
    /// <summary>获取去除首尾空白后的说明，长度为 1–2000 个 Unicode 标量值。</summary>
    public string Description { get; }
    /// <summary>获取规定该蓝图构造与身份保留规则的 Archetype 身份。</summary>
    public CompositeArchetypeId ArchetypeId { get; }
    /// <summary>获取蓝图声明的单车尺寸包络档位。</summary>
    public VehicleSizeClass SizeClass { get; }
    /// <summary>获取按局部坐标确定性排序的体素布局只读副本。</summary>
    public IReadOnlyList<CompositeVoxelSpec> Voxels { get; }
    /// <summary>获取保持输入顺序的接触点只读副本。</summary>
    public IReadOnlyList<CompositeContactAnchor> Contacts { get; }

    /// <summary>创建蓝图定义，并规范化显示文本及复制布局数据。</summary>
    /// <param name="id">蓝图定义的稳定身份。</param>
    /// <param name="name">显示名称；去除首尾空白后须有 1–100 个 Unicode 标量值。</param>
    /// <param name="description">说明；去除首尾空白后须有 1–2000 个 Unicode 标量值。</param>
    /// <param name="archetypeId">规定构造与身份保留规则的 Archetype 身份。</param>
    /// <param name="sizeClass">单车尺寸包络档位。</param>
    /// <param name="voxels">蓝图体素布局；构造函数复制并按局部坐标排序。</param>
    /// <param name="contacts">蓝图接触点；构造函数按输入顺序复制。</param>
    /// <exception cref="ArgumentNullException"><paramref name="name"/>、<paramref name="description"/>、<paramref name="voxels"/> 或 <paramref name="contacts"/> 为 <see langword="null"/>。</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> 或 <paramref name="description"/> 去除首尾空白后的 Unicode 标量值数量超出允许范围。</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="sizeClass"/> 不是已定义的车辆尺寸档位。</exception>
    public CompositeDefinition(
        CompositeDefinitionId id,
        string name,
        string description,
        CompositeArchetypeId archetypeId,
        VehicleSizeClass sizeClass,
        IReadOnlyList<CompositeVoxelSpec> voxels,
        IReadOnlyList<CompositeContactAnchor> contacts)
    {
        ArgumentNullException.ThrowIfNull(voxels);
        ArgumentNullException.ThrowIfNull(contacts);
        if (!Enum.IsDefined(sizeClass))
            throw new ArgumentOutOfRangeException(nameof(sizeClass), sizeClass, "Unknown vehicle size class.");

        Id = id;
        Name = VoxelDefinition.NormalizeDisplayText(name, VoxelDefinition.NameMinLength, VoxelDefinition.NameMaxLength, nameof(name));
        Description = VoxelDefinition.NormalizeDisplayText(
            description,
            VoxelDefinition.DescriptionMinLength,
            VoxelDefinition.DescriptionMaxLength,
            nameof(description));
        ArchetypeId = archetypeId;
        SizeClass = sizeClass;
        Voxels = new ReadOnlyCollection<CompositeVoxelSpec>(
            voxels.OrderBy(item => item.Cell).ToArray());
        Contacts = new ReadOnlyCollection<CompositeContactAnchor>(contacts.ToArray());
    }
}
