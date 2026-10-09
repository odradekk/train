// 属于内容层的蓝图定义模块。
// 定义蓝图内容使用的局部坐标与稳定强类型身份。
namespace Train.Core.Content.Composites;

/// <summary>表示蓝图布局中相对蓝图原点的整数体素格坐标。</summary>
/// <param name="X">X 轴偏移，单位为体素；可为 <c>0</c> 或负数。</param>
/// <param name="Y">Y 轴偏移，单位为体素；可为 <c>0</c> 或负数。</param>
/// <param name="Z">Z 轴偏移，单位为体素；可为 <c>0</c> 或负数。</param>
/// <example><c>(1,2,3)</c> 表示相对蓝图原点偏移一、二、三个体素。</example>
public readonly record struct LocalVoxelCoord(int X, int Y, int Z) : IComparable<LocalVoxelCoord>
{
    /// <summary>按 X、Y、Z 依次比较坐标，以提供确定性排序。</summary>
    public int CompareTo(LocalVoxelCoord other)
    {
        var x = X.CompareTo(other.X);
        if (x != 0)
            return x;
        var y = Y.CompareTo(other.Y);
        return y != 0 ? y : Z.CompareTo(other.Z);
    }

    /// <summary>返回 <c>(X,Y,Z)</c> 形式的局部体素格坐标。</summary>
    public override string ToString() => $"({X},{Y},{Z})";
}

/// <summary>表示由内容命名空间和包内局部 ID 组成的蓝图定义稳定身份。</summary>
/// <example><c>core:starter-engine</c>。</example>
public readonly record struct CompositeDefinitionId
{
    /// <summary>获取蓝图所属内容包的命名空间。</summary>
    public string Namespace { get; }
    /// <summary>获取蓝图在包内的局部 ID。</summary>
    public string LocalId { get; }

    /// <summary>创建蓝图定义身份，并去除两部分首尾空白。</summary>
    public CompositeDefinitionId(string @namespace, string localId)
    {
        Namespace = ContentIdentity.NormalizePart(@namespace, nameof(@namespace));
        LocalId = ContentIdentity.NormalizePart(localId, nameof(localId));
    }

    /// <summary>获取 <c>namespace:local-id</c> 形式的完整身份。</summary>
    public string Value => $"{Namespace}:{LocalId}";
    /// <summary>返回 <see cref="Value"/>。</summary>
    public override string ToString() => Value;

    /// <summary>解析 <c>namespace:local-id</c> 形式的蓝图定义身份。</summary>
    public static CompositeDefinitionId Parse(string value)
    {
        var (ns, localId) = ContentIdentity.Split(value, nameof(CompositeDefinitionId));
        return new CompositeDefinitionId(ns, localId);
    }
}
