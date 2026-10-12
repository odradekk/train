// 属于内容层的体素定义模块。
// 表示体素的核心规则、表现绑定、碰撞形状与内容校验。
using System.Collections.Frozen;
using System.Collections.ObjectModel;
using System.Globalization;
using Train.Content.Behaviors;

namespace Train.Content.Voxels;

/// <summary>提供核心规则识别的内置体素标签。</summary>
public static class VoxelTags
{
    /// <summary>标记可库存和交易的初级货物。</summary>
    public const string RawGood = "raw_good";
    /// <summary>标记蓝图身份保留规则要求的核心部件。</summary>
    public const string IdentityCore = "identity_core";
    /// <summary>标记仅用于场景、不可库存或交易的体素。</summary>
    public const string SceneOnly = "scene_only";
}

/// <summary>表示一个体素定义允许使用的离散朝向集合。</summary>
public enum VoxelOrientationConstraint
{
    /// <summary>仅允许默认朝向。</summary>
    None = 0,
    /// <summary>允许保持竖直轴的四个水平朝向。</summary>
    FourWay = 1,
    /// <summary>允许全部二十四个正交朝向。</summary>
    All24 = 2
}

/// <summary>表示体素当前支持的表现形式。</summary>
public enum VoxelPresentationKind
{
    /// <summary>使用六面立方体表现。</summary>
    Cube = 0
}

/// <summary>表示体素在单位格中的碰撞形状。</summary>
public enum VoxelCollisionKind
{
    /// <summary>不产生碰撞体积。</summary>
    None = 0,
    /// <summary>整个单位格为碰撞体积。</summary>
    FullBox = 1,
    /// <summary>单位格内的轴对齐局部盒为碰撞体积。</summary>
    LocalBox = 2
}

/// <summary>表示一个体素的立方体表现及四类必需图片绑定。</summary>
/// <example>资源路径加载与拒绝用例见 <c>ContentReloaderTests</c>。</example>
public sealed class VoxelPresentation
{
    /// <summary>获取表现形式；当前只能为 <see cref="VoxelPresentationKind.Cube"/>。</summary>
    public VoxelPresentationKind Kind { get; }
    /// <summary>获取物品图标的包内相对路径。</summary>
    public string IconPath { get; }
    /// <summary>获取立方体顶面的包内相对图片路径。</summary>
    public string TopPath { get; }
    /// <summary>获取立方体侧面的包内相对图片路径。</summary>
    public string SidePath { get; }
    /// <summary>获取立方体底面的包内相对图片路径。</summary>
    public string BottomPath { get; }

    /// <summary>创建一组立方体表现绑定。</summary>
    /// <param name="kind">表现形式；当前只能为 Cube。</param>
    /// <param name="iconPath">非空图标相对路径。</param>
    /// <param name="topPath">非空顶面图片相对路径。</param>
    /// <param name="sidePath">非空侧面图片相对路径。</param>
    /// <param name="bottomPath">非空底面图片相对路径。</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> 不是 Cube。</exception>
    /// <exception cref="ArgumentException">任一路径为 <see langword="null"/> 或空字符串。</exception>
    public VoxelPresentation(
        VoxelPresentationKind kind,
        string iconPath,
        string topPath,
        string sidePath,
        string bottomPath)
    {
        if (kind != VoxelPresentationKind.Cube)
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Only cube presentation is supported.");
        ArgumentException.ThrowIfNullOrEmpty(iconPath);
        ArgumentException.ThrowIfNullOrEmpty(topPath);
        ArgumentException.ThrowIfNullOrEmpty(sidePath);
        ArgumentException.ThrowIfNullOrEmpty(bottomPath);
        Kind = kind;
        IconPath = iconPath;
        TopPath = topPath;
        SidePath = sidePath;
        BottomPath = bottomPath;
    }
}

/// <summary>表示单位体素格内的不可变碰撞形状。</summary>
/// <example>局部盒边界和浮点可表现性用例见 <c>VoxelCollisionDefinitionTests.Illegal_local_box_shapes_reject_built_in_publish_and_exclude_the_mod_package</c> 与 <c>VoxelCollisionDefinitionTests.Adapter_underflow_sizes_reject_publish_and_exclude_the_mod_package</c>。</example>
public sealed class VoxelCollision
{
    /// <summary>获取碰撞形状种类。</summary>
    public VoxelCollisionKind Kind { get; }
    /// <summary>获取局部盒在单位格内的 X 起点，范围为 0–1。</summary>
    public double OffsetX { get; }
    /// <summary>获取局部盒在单位格内的 Y 起点，范围为 0–1。</summary>
    public double OffsetY { get; }
    /// <summary>获取局部盒在单位格内的 Z 起点，范围为 0–1。</summary>
    public double OffsetZ { get; }
    /// <summary>获取局部盒沿 X 轴的格内尺寸，范围为 0–1；局部盒时必须大于 0。</summary>
    public double SizeX { get; }
    /// <summary>获取局部盒沿 Y 轴的格内尺寸，范围为 0–1；局部盒时必须大于 0。</summary>
    public double SizeY { get; }
    /// <summary>获取局部盒沿 Z 轴的格内尺寸，范围为 0–1；局部盒时必须大于 0。</summary>
    public double SizeZ { get; }

    /// <summary>创建无碰撞或整格碰撞。</summary>
    /// <param name="kind">只能为 None 或 FullBox。</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> 为 LocalBox 或未知值。</exception>
    public VoxelCollision(VoxelCollisionKind kind)
    {
        if (kind is not VoxelCollisionKind.None and not VoxelCollisionKind.FullBox)
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported collision kind.");
        Kind = kind;
    }

    private VoxelCollision(
        double offsetX,
        double offsetY,
        double offsetZ,
        double sizeX,
        double sizeY,
        double sizeZ)
    {
        Kind = VoxelCollisionKind.LocalBox;
        OffsetX = offsetX;
        OffsetY = offsetY;
        OffsetZ = offsetZ;
        SizeX = sizeX;
        SizeY = sizeY;
        SizeZ = sizeZ;
    }

    /// <summary>尝试创建完全位于单位格内、各边严格为正的局部盒。</summary>
    /// <param name="offsetX">X 起点，范围 0–1。</param><param name="offsetY">Y 起点，范围 0–1。</param><param name="offsetZ">Z 起点，范围 0–1。</param>
    /// <param name="sizeX">X 尺寸，必须为有限正数。</param><param name="sizeY">Y 尺寸，必须为有限正数。</param><param name="sizeZ">Z 尺寸，必须为有限正数。</param>
    /// <param name="collision">成功时为局部盒；失败时为 <see langword="null"/>。</param>
    /// <returns>三个闭区间均可区分且不超出单位格时返回 <see langword="true"/>。</returns>
    public static bool TryCreateLocalBox(
        double offsetX,
        double offsetY,
        double offsetZ,
        double sizeX,
        double sizeY,
        double sizeZ,
        out VoxelCollision collision)
    {
        collision = null!;
        if (!IsValidLocalBox(offsetX, offsetY, offsetZ, sizeX, sizeY, sizeZ))
            return false;
        collision = new VoxelCollision(offsetX, offsetY, offsetZ, sizeX, sizeY, sizeZ);
        return true;
    }

    /// <summary>创建完全位于单位格内、各边严格为正的局部盒。</summary>
    /// <param name="offsetX">X 起点，范围 0–1。</param><param name="offsetY">Y 起点，范围 0–1。</param><param name="offsetZ">Z 起点，范围 0–1。</param>
    /// <param name="sizeX">X 尺寸，必须为有限正数。</param><param name="sizeY">Y 尺寸，必须为有限正数。</param><param name="sizeZ">Z 尺寸，必须为有限正数。</param>
    /// <returns>校验后的局部盒。</returns>
    /// <exception cref="ArgumentException">任一值非有限、尺寸非正、区间不可区分或超出单位格。</exception>
    public static VoxelCollision CreateLocalBox(
        double offsetX,
        double offsetY,
        double offsetZ,
        double sizeX,
        double sizeY,
        double sizeZ)
    {
        if (!TryCreateLocalBox(offsetX, offsetY, offsetZ, sizeX, sizeY, sizeZ, out var collision))
            throw new ArgumentException("Local box must be finite, strictly positive, and a distinguishable interval inside the unit cell.");
        return collision;
    }

    private static bool IsValidLocalBox(
        double offsetX,
        double offsetY,
        double offsetZ,
        double sizeX,
        double sizeY,
        double sizeZ)
    {
        if (!IsFinite(offsetX) || !IsFinite(offsetY) || !IsFinite(offsetZ) ||
            !IsFinite(sizeX) || !IsFinite(sizeY) || !IsFinite(sizeZ) ||
            sizeX <= 0 || sizeY <= 0 || sizeZ <= 0 ||
            offsetX < 0 || offsetY < 0 || offsetZ < 0)
            return false;
        // 起点和终点在 double 精度下仍须可区分，且整个区间位于单位格内。
        // 正交变换保持单位格范围；世界平移和表现层精度不属于内容规则。
        return offsetX + sizeX > offsetX && offsetY + sizeY > offsetY && offsetZ + sizeZ > offsetZ &&
               offsetX + sizeX <= 1 && offsetY + sizeY <= 1 && offsetZ + sizeZ <= 1;
    }

    private static bool IsFinite(double value) =>
        !double.IsNaN(value) && !double.IsInfinity(value);
}

/// <summary>表示某种体素及货物的不可变内容定义。</summary>
/// <example>完整定义的加载、规范化和拒绝用例见 <c>ContentReloaderTests</c> 与 <c>ModLoadingTests</c>。</example>
public sealed class VoxelDefinition : ICoreVoxelDefinition
{
    /// <summary>显示名称最少包含 1 个 Unicode 标量值。</summary>
    public const int NameMinLength = 1;
    /// <summary>显示名称最多包含 100 个 Unicode 标量值。</summary>
    public const int NameMaxLength = 100;
    /// <summary>说明最少包含 1 个 Unicode 标量值。</summary>
    public const int DescriptionMinLength = 1;
    /// <summary>说明最多包含 2000 个 Unicode 标量值。</summary>
    public const int DescriptionMaxLength = 2000;
    /// <summary>基本货币价值下限，1 个货币单位。</summary>
    public const long ValueMin = 1;
    /// <summary>基本货币价值上限，十亿个货币单位。</summary>
    public const long ValueMax = 1_000_000_000;
    /// <summary>质量必须严格大于 0；单位为 t。</summary>
    public const double MassMinExclusive = 0;
    /// <summary>单个体素质量上限，十亿 t。</summary>
    public const double MassMax = 1_000_000_000;
    /// <summary>耐久上限字段的最小值 1。</summary>
    public const int MaxDurabilityMin = 1;
    /// <summary>耐久上限字段的最大值十亿。</summary>
    public const int MaxDurabilityMax = 1_000_000_000;
    /// <summary>标签最少包含 1 个 ASCII 字符。</summary>
    public const int TagMinLength = 1;
    /// <summary>标签最多包含 64 个 ASCII 字符。</summary>
    public const int TagMaxLength = 64;

    /// <summary>获取稳定体素定义身份。</summary>
    public VoxelDefinitionId Id { get; }
    /// <summary>获取去除首尾空白后的显示名称。</summary>
    public string Name { get; }
    /// <summary>获取去除首尾空白后的显示说明。</summary>
    public string Description { get; }
    /// <summary>获取固定基本货币价值，范围 1–1,000,000,000。</summary>
    public long Value { get; }
    /// <summary>获取一个单位的质量，范围 (0, 1,000,000,000] 个质量单位。</summary>
    public double Mass { get; }
    /// <summary>获取单个已放置体素的耐久上限，范围 1–1,000,000,000。</summary>
    public int MaxDurability { get; }
    /// <summary>获取按 Ordinal 比较的不可变标签集合。</summary>
    public IReadOnlySet<string> Tags { get; }
    /// <summary>获取定义声明的不可变功能行为；各种行为最多出现一次，按类型标记的 Ordinal 顺序排列，与声明顺序无关。</summary>
    public IReadOnlyList<VoxelBehavior> Behaviors { get; }
    /// <summary>获取放入蓝图或场景时允许的离散朝向集合。</summary>
    public VoxelOrientationConstraint AllowedOrientations { get; }
    /// <summary>获取必需的表现绑定。</summary>
    public VoxelPresentation Presentation { get; }
    /// <summary>获取单位格内的碰撞形状。</summary>
    public VoxelCollision Collision { get; }

    /// <summary>获取定义是否带有 <see cref="VoxelTags.RawGood"/> 标签。</summary>
    public bool HasRawGoodTag => Tags.Contains(VoxelTags.RawGood);

    /// <summary>规范化并创建一个不可变体素定义。</summary>
    /// <param name="id">非空稳定身份。</param><param name="name">1–100 个 Unicode 标量值的显示名称。</param>
    /// <param name="description">1–2000 个 Unicode 标量值的显示说明。</param><param name="value">1–1,000,000,000 个货币单位。</param>
    /// <param name="mass">有限正质量，最多十亿 t。</param><param name="maxDurability">1–1,000,000,000 的耐久上限。</param>
    /// <param name="tags">待规范化的标签集合。</param><param name="behaviors">参数有效且种类不重复的 Behavior 序列。</param>
    /// <param name="allowedOrientations">允许朝向集合。</param><param name="presentation">非空表现绑定。</param><param name="collision">非空碰撞形状。</param>
    /// <exception cref="ArgumentNullException">必需引用参数为 <see langword="null"/>。</exception>
    /// <exception cref="ArgumentOutOfRangeException">数值或枚举超出支持范围。</exception>
    /// <exception cref="ArgumentException">身份、文本、标签、Behavior 或 Behavior 参数组合无效。</exception>
    public VoxelDefinition(
        VoxelDefinitionId id,
        string name,
        string description,
        long value,
        double mass,
        int maxDurability,
        IEnumerable<string> tags,
        IEnumerable<VoxelBehavior> behaviors,
        VoxelOrientationConstraint allowedOrientations,
        VoxelPresentation presentation,
        VoxelCollision collision)
    {
        if (string.IsNullOrEmpty(id.Namespace) || string.IsNullOrEmpty(id.LocalId))
            throw new ArgumentException("VoxelDefinitionId cannot be blank.", nameof(id));
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(tags);
        ArgumentNullException.ThrowIfNull(behaviors);
        ArgumentNullException.ThrowIfNull(presentation);
        ArgumentNullException.ThrowIfNull(collision);
        if (value is < ValueMin or > ValueMax)
            throw new ArgumentOutOfRangeException(nameof(value), value, "Value is out of range.");
        if (double.IsNaN(mass) || double.IsInfinity(mass) || mass <= MassMinExclusive || mass > MassMax)
            throw new ArgumentOutOfRangeException(nameof(mass), mass, "Mass is out of range.");
        if (maxDurability is < MaxDurabilityMin or > MaxDurabilityMax)
            throw new ArgumentOutOfRangeException(nameof(maxDurability), maxDurability, "Max durability is out of range.");
        if (allowedOrientations is not VoxelOrientationConstraint.None
            and not VoxelOrientationConstraint.FourWay
            and not VoxelOrientationConstraint.All24)
            throw new ArgumentOutOfRangeException(nameof(allowedOrientations), allowedOrientations, "Unsupported orientation constraint.");

        var normalizedName = NormalizeDisplayText(name, NameMinLength, NameMaxLength, nameof(name));
        var normalizedDescription = NormalizeDisplayText(description, DescriptionMinLength, DescriptionMaxLength, nameof(description));
        var tagSet = NormalizeTags(tags);
        var behaviorList = behaviors.ToArray();
        if (behaviorList.Any(static behavior => behavior is null) ||
            behaviorList.GroupBy(static behavior => behavior.GetType()).Any(static group => group.Count() > 1) ||
            behaviorList.Any(static behavior => behavior switch
            {
                PropulsionBehavior propulsion => !double.IsFinite(propulsion.Traction) || propulsion.Traction <= 0 ||
                    !double.IsFinite(propulsion.RatedSpeed) || propulsion.RatedSpeed <= 0,
                CargoBehavior cargo => cargo.Capacity < 1,
                ControlBehavior => false,
                _ => true
            }))
            throw new ArgumentException("Behaviors must have unique registered types and valid parameters.", nameof(behaviors));

        Id = id;
        Name = normalizedName;
        Description = normalizedDescription;
        Value = value;
        Mass = mass;
        MaxDurability = maxDurability;
        Tags = tagSet;
        Behaviors = new ReadOnlyCollection<VoxelBehavior>(
            behaviorList.OrderBy(BehaviorRegistry.TypeOf, StringComparer.Ordinal).ToArray());
        AllowedOrientations = allowedOrientations;
        Presentation = presentation;
        Collision = collision;
    }

    /// <summary>去除显示文本首尾空白并按 Unicode 标量值数量校验长度。</summary>
    /// <param name="value">待规范化文本。</param><param name="minLength">允许的最少标量值数，包含边界。</param><param name="maxLength">允许的最多标量值数，包含边界。</param><param name="paramName">异常中报告的调用参数名。</param>
    /// <returns>去除首尾空白后的文本。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> 为 <see langword="null"/>。</exception>
    /// <exception cref="ArgumentException">规范化后的长度不在给定闭区间。</exception>
    public static string NormalizeDisplayText(string value, int minLength, int maxLength, string paramName)
    {
        ArgumentNullException.ThrowIfNull(value, paramName);
        var trimmed = value.Trim();
        var runes = 0;
        foreach (var _ in trimmed.EnumerateRunes())
            runes++;
        if (runes < minLength || runes > maxLength)
            throw new ArgumentException($"Must be {minLength}-{maxLength} Unicode characters after trimming.", paramName);
        return trimmed;
    }

    /// <summary>去除标签首尾空白并校验小写 ASCII 身份规则。</summary>
    /// <param name="value">待规范化标签。</param><param name="paramName">异常中报告的调用参数名。</param>
    /// <returns>长度 1–64 的规范化标签。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> 为 <see langword="null"/>。</exception>
    /// <exception cref="ArgumentException">标签字符或长度无效。</exception>
    public static string NormalizeTag(string value, string paramName)
    {
        ArgumentNullException.ThrowIfNull(value, paramName);
        var trimmed = value.Trim();
        if (!VoxelDefinitionId.IsValidPart(trimmed) ||
            trimmed.Length is < TagMinLength or > TagMaxLength)
            throw new ArgumentException("Tag must be 1-64 lowercase ASCII letters, digits, underscores, or hyphens, starting with a letter or digit.", paramName);
        return trimmed;
    }

    /// <summary>返回朝向约束在 JSON <c>allowed_orientations</c> 字段中的字符串值。</summary>
    /// <param name="orientations">朝向约束。</param><returns><c>none</c>、<c>four_way</c> 或 <c>all_24</c>。</returns>
    /// <exception cref="ArgumentOutOfRangeException">参数不是已定义枚举值。</exception>
    public static string ToWire(VoxelOrientationConstraint orientations) => orientations switch
    {
        VoxelOrientationConstraint.None => "none",
        VoxelOrientationConstraint.FourWay => "four_way",
        VoxelOrientationConstraint.All24 => "all_24",
        _ => throw new ArgumentOutOfRangeException(nameof(orientations), orientations, "Unknown orientation constraint.")
    };

    /// <summary>解析 JSON <c>allowed_orientations</c> 字段中的朝向约束，按区分大小写的精确值匹配。</summary>
    /// <param name="value">候选字符串；<see langword="null"/> 无效。</param><param name="orientations">成功时为解析结果。</param>
    /// <returns>字符串等于 <c>none</c>、<c>four_way</c> 或 <c>all_24</c> 时返回 <see langword="true"/>。</returns>
    public static bool TryParseOrientations(string? value, out VoxelOrientationConstraint orientations)
    {
        orientations = value switch
        {
            "none" => VoxelOrientationConstraint.None,
            "four_way" => VoxelOrientationConstraint.FourWay,
            "all_24" => VoxelOrientationConstraint.All24,
            _ => default
        };
        return value is "none" or "four_way" or "all_24";
    }

    /// <summary>返回表现形式在 JSON <c>presentation.kind</c> 字段中的字符串值。</summary>
    /// <param name="kind">表现形式。</param><returns>当前实现返回 <c>cube</c>。</returns>
    /// <exception cref="ArgumentOutOfRangeException">参数不是已定义枚举值。</exception>
    public static string ToWire(VoxelPresentationKind kind) => kind switch
    {
        VoxelPresentationKind.Cube => "cube",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown presentation kind.")
    };

    /// <summary>解析 JSON <c>presentation.kind</c> 字段中的表现形式，按区分大小写的精确值匹配。</summary>
    /// <param name="value">候选字符串。</param><param name="kind">成功时为 Cube。</param>
    /// <returns>字符串等于 <c>cube</c> 时返回 <see langword="true"/>。</returns>
    public static bool TryParsePresentationKind(string? value, out VoxelPresentationKind kind)
    {
        if (value == "cube")
        {
            kind = VoxelPresentationKind.Cube;
            return true;
        }

        kind = default;
        return false;
    }

    /// <summary>返回碰撞形状在 JSON <c>collision.kind</c> 字段中的字符串值。</summary>
    /// <param name="kind">碰撞形状。</param><returns><c>none</c>、<c>full_box</c> 或 <c>local_box</c>。</returns>
    /// <exception cref="ArgumentOutOfRangeException">参数不是已定义枚举值。</exception>
    public static string ToWire(VoxelCollisionKind kind) => kind switch
    {
        VoxelCollisionKind.None => "none",
        VoxelCollisionKind.FullBox => "full_box",
        VoxelCollisionKind.LocalBox => "local_box",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown collision kind.")
    };

    /// <summary>解析 JSON <c>collision.kind</c> 字段中的碰撞形状，按区分大小写的精确值匹配。</summary>
    /// <param name="value">候选字符串。</param><param name="kind">成功时为解析结果。</param>
    /// <returns>字符串等于 <c>none</c>、<c>full_box</c> 或 <c>local_box</c> 时返回 <see langword="true"/>。</returns>
    public static bool TryParseCollisionKind(string? value, out VoxelCollisionKind kind)
    {
        kind = value switch
        {
            "none" => VoxelCollisionKind.None,
            "full_box" => VoxelCollisionKind.FullBox,
            "local_box" => VoxelCollisionKind.LocalBox,
            _ => default
        };
        return value is "none" or "full_box" or "local_box";
    }

    /// <summary>以固定文化和 G17 精度格式化双精度值，供规范化 JSON 编码使用。</summary>
    /// <param name="mass">待格式化双精度值；调用方负责确保其有效。</param><returns>可无损往返的十进制字符串。</returns>
    public static string FormatMass(double mass) =>
        mass.ToString("G17", CultureInfo.InvariantCulture);

    private static FrozenSet<string> NormalizeTags(IEnumerable<string> tags)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tag in tags)
            set.Add(NormalizeTag(tag, nameof(tags)));
        return set.ToFrozenSet(StringComparer.Ordinal);
    }
}
