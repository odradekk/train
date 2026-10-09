// 属于内容层的体素定义模块。
// 定义体素内容在包与存档中使用的稳定强类型身份。
namespace Train.Core.Content.Voxels;

/// <summary>表示体素定义的稳定强类型身份，由命名空间和包内局部 ID 组成。</summary>
/// <example><c>core:iron</c>；解析与拒绝用例见 <c>VoxelIdentityTests.Identities_trim_reject_blank_and_preserve_case</c>。</example>
public readonly record struct VoxelDefinitionId
{
    /// <summary>命名空间和局部 ID 的最少字符数。</summary>
    public const int PartMinLength = ContentIdentity.PartMinLength;
    /// <summary>命名空间和局部 ID 的最多字符数。</summary>
    public const int PartMaxLength = ContentIdentity.PartMaxLength;
    /// <summary>内置内容固定使用的命名空间。</summary>
    public const string BuiltInNamespace = "core";

    /// <summary>获取定义所属命名空间。</summary>
    public string Namespace { get; }
    /// <summary>获取定义在包内的局部 ID。</summary>
    public string LocalId { get; }

    /// <summary>创建体素定义身份，并去除两部分首尾空白。</summary>
    /// <param name="namespace">1–64 个字符的命名空间。</param>
    /// <param name="localId">1–64 个字符的包内局部 ID。</param>
    /// <exception cref="ArgumentNullException">任一参数为 <see langword="null"/>。</exception>
    /// <exception cref="ArgumentException">任一参数不符合小写 ASCII 身份规则。</exception>
    public VoxelDefinitionId(string @namespace, string localId)
    {
        Namespace = ContentIdentity.NormalizePart(@namespace, nameof(@namespace));
        LocalId = ContentIdentity.NormalizePart(localId, nameof(localId));
    }

    /// <summary>获取 <c>namespace:local-id</c> 形式的完整身份。</summary>
    public string Value => $"{Namespace}:{LocalId}";

    /// <summary>返回 <see cref="Value"/>。</summary>
    public override string ToString() => Value;

    /// <summary>解析 <c>namespace:local-id</c> 形式的体素定义身份。</summary>
    /// <param name="value">待解析文本；首尾空白会被去除。</param>
    /// <returns>规范化后的强类型身份。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> 为 <see langword="null"/>。</exception>
    /// <exception cref="ArgumentException">文本格式或任一身份部分无效。</exception>
    public static VoxelDefinitionId Parse(string value)
    {
        var (ns, localId) = ContentIdentity.Split(value, nameof(VoxelDefinitionId));
        return new VoxelDefinitionId(ns, localId);
    }

    /// <summary>检查一个未修剪字符串是否可用作命名空间或局部 ID。</summary>
    /// <param name="value">候选字符串；<see langword="null"/> 无效。</param>
    /// <returns>长度为 1–64，首字符为小写 ASCII 字母或数字，其余字符另可包含下划线和连字符时返回 <see langword="true"/>。</returns>
    public static bool IsValidPart(string? value) => ContentIdentity.IsValidPart(value);

    /// <summary>将内容文件中的局部或完整引用解析为体素身份；与声明 ID 一样不修剪空白，格式无效时返回 false。</summary>
    internal static bool TryResolve(string raw, string packageNamespace, out VoxelDefinitionId id)
    {
        if (ContentIdentity.IsValidFullId(raw))
            id = Parse(raw);
        else if (ContentIdentity.IsValidPart(raw))
            id = new VoxelDefinitionId(packageNamespace, raw);
        else
        {
            id = default;
            return false;
        }
        return true;
    }
}
