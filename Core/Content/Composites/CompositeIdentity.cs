// 属于内容层的蓝图定义模块。
// 定义蓝图内容使用的局部坐标与稳定强类型身份。

using Train.Core.Content.Voxels;

namespace Train.Core.Content.Composites;

/// <summary>表示蓝图布局中相对蓝图原点的整数体素格坐标。</summary>
/// <param name="X">X 轴偏移，单位为体素；可为 <c>0</c> 或负数。</param>
/// <param name="Y">Y 轴偏移，单位为体素；可为 <c>0</c> 或负数。</param>
/// <param name="Z">Z 轴偏移，单位为体素；可为 <c>0</c> 或负数。</param>
/// <example><c>(1,2,3)</c> 表示相对蓝图原点偏移一、二、三个体素。</example>
public readonly record struct LocalVoxelCoord(int X, int Y, int Z) : IComparable<LocalVoxelCoord>
{
    /// <summary>按 X、Y、Z 依次比较坐标，以提供确定性排序。</summary>
    /// <param name="other">要比较的另一个局部体素格坐标。</param>
    /// <returns>当前坐标排在 <paramref name="other"/> 前时为负数，相同时为 <c>0</c>，排在其后时为正数。</returns>
    public int CompareTo(LocalVoxelCoord other)
    {
        var x = X.CompareTo(other.X);
        if (x != 0)
            return x;
        var y = Y.CompareTo(other.Y);
        return y != 0 ? y : Z.CompareTo(other.Z);
    }

    /// <summary>返回 <c>(X,Y,Z)</c> 形式的局部体素格坐标。</summary>
    /// <returns>使用十进制整数且不含空格的坐标文本。</returns>
    public override string ToString() => $"({X},{Y},{Z})";
}

/// <summary>表示由内容命名空间和包内局部 ID 组成的蓝图定义稳定身份。</summary>
/// <example><c>core:starter-engine</c>。</example>
public readonly record struct CompositeDefinitionId
{
    /// <summary>获取蓝图所属内容包的命名空间。</summary>
    public string Namespace { get; }
    /// <summary>获取蓝图在内容包内的局部 ID。</summary>
    public string LocalId { get; }
    /// <summary>创建蓝图定义身份，并去除两部分首尾空白。</summary>
    /// <param name="namespace">1–64 个字符的内容命名空间。</param>
    /// <param name="localId">1–64 个字符的包内局部 ID。</param>
    /// <exception cref="ArgumentNullException">任一参数为 <see langword="null"/>。</exception>
    /// <exception cref="ArgumentException">任一参数不符合小写 ASCII 身份规则。</exception>
    public CompositeDefinitionId(string @namespace, string localId)
    {
        Namespace = VoxelDefinitionId.NormalizePart(@namespace, nameof(@namespace));
        LocalId = VoxelDefinitionId.NormalizePart(localId, nameof(localId));
    }
    /// <summary>获取 <c>namespace:local-id</c> 形式的完整身份。</summary>
    public string Value => $"{Namespace}:{LocalId}";
    /// <summary>返回 <see cref="Value"/>。</summary>
    /// <returns><c>namespace:local-id</c> 形式的完整身份。</returns>
    public override string ToString() => Value;
    /// <summary>解析 <c>namespace:local-id</c> 形式的蓝图定义身份。</summary>
    /// <param name="value">待解析文本；整体及两部分的首尾空白会被去除。</param>
    /// <returns>规范化后的蓝图定义身份。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> 为 <see langword="null"/>。</exception>
    /// <exception cref="ArgumentException">文本不含且仅含一个分隔冒号，或任一身份部分不符合小写 ASCII 身份规则。</exception>
    public static CompositeDefinitionId Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var trimmed = value.Trim();
        var separator = trimmed.IndexOf(':');
        if (separator <= 0 || separator != trimmed.LastIndexOf(':') || separator == trimmed.Length - 1)
            throw new ArgumentException("CompositeDefinitionId must have the form namespace:local-id.", nameof(value));
        return new CompositeDefinitionId(trimmed[..separator], trimmed[(separator + 1)..]);
    }
}

/// <summary>表示由内容命名空间和包内局部 ID 组成的蓝图 Archetype 稳定身份。</summary>
/// <example><c>core:rail-vehicle</c>。</example>
public readonly record struct CompositeArchetypeId
{
    /// <summary>获取 Archetype 所属内容包的命名空间。</summary>
    public string Namespace { get; }
    /// <summary>获取 Archetype 在内容包内的局部 ID。</summary>
    public string LocalId { get; }
    /// <summary>创建蓝图 Archetype 身份，并去除两部分首尾空白。</summary>
    /// <param name="namespace">1–64 个字符的内容命名空间。</param>
    /// <param name="localId">1–64 个字符的包内局部 ID。</param>
    /// <exception cref="ArgumentNullException">任一参数为 <see langword="null"/>。</exception>
    /// <exception cref="ArgumentException">任一参数不符合小写 ASCII 身份规则。</exception>
    public CompositeArchetypeId(string @namespace, string localId)
    {
        Namespace = VoxelDefinitionId.NormalizePart(@namespace, nameof(@namespace));
        LocalId = VoxelDefinitionId.NormalizePart(localId, nameof(localId));
    }
    /// <summary>获取 <c>namespace:local-id</c> 形式的完整身份。</summary>
    public string Value => $"{Namespace}:{LocalId}";
    /// <summary>返回 <see cref="Value"/>。</summary>
    /// <returns><c>namespace:local-id</c> 形式的完整身份。</returns>
    public override string ToString() => Value;
    /// <summary>解析 <c>namespace:local-id</c> 形式的蓝图 Archetype 身份。</summary>
    /// <param name="value">待解析文本；整体及两部分的首尾空白会被去除。</param>
    /// <returns>规范化后的蓝图 Archetype 身份。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> 为 <see langword="null"/>。</exception>
    /// <exception cref="ArgumentException">文本不含且仅含一个分隔冒号，或任一身份部分不符合小写 ASCII 身份规则。</exception>
    public static CompositeArchetypeId Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var trimmed = value.Trim();
        var separator = trimmed.IndexOf(':');
        if (separator <= 0 || separator != trimmed.LastIndexOf(':') || separator == trimmed.Length - 1)
            throw new ArgumentException("CompositeArchetypeId must have the form namespace:local-id.", nameof(value));
        return new CompositeArchetypeId(trimmed[..separator], trimmed[(separator + 1)..]);
    }
}
