// 属于内容模块。
// 各领域保留强类型身份，共用命名空间、局部 ID 和文本分隔规则。
namespace Train.Core.Content;

/// <summary>提供已有内容身份规则；不把不同领域的身份合并成一个公共类型。</summary>
internal static class ContentIdentity
{
    public const int PartMinLength = 1;
    public const int PartMaxLength = 64;

    /// <summary>检查未修剪的身份部分：1–64 个小写 ASCII 字母、数字、下划线或连字符，首字符须为字母或数字。</summary>
    public static bool IsValidPart(string? value)
    {
        if (value is null || value.Length is < PartMinLength or > PartMaxLength)
            return false;
        if (!IsStartChar(value[0]))
            return false;
        for (var i = 1; i < value.Length; i++)
        {
            if (!IsStartChar(value[i]) && value[i] is not '_' and not '-')
                return false;
        }
        return true;
    }

    /// <summary>检查未修剪的 namespace:local-id；两部分均须符合身份字符和长度规则。</summary>
    public static bool IsValidFullId(string? value)
    {
        if (value is null)
            return false;
        var separator = value.IndexOf(':');
        return separator > 0 && IsValidPart(value[..separator]) && IsValidPart(value[(separator + 1)..]);
    }

    /// <summary>去除身份部分的首尾空白；无效部分抛出 ArgumentException，null 抛出 ArgumentNullException。</summary>
    public static string NormalizePart(string value, string paramName)
    {
        ArgumentNullException.ThrowIfNull(value, paramName);
        var trimmed = value.Trim();
        if (!IsValidPart(trimmed))
            throw new ArgumentException("Must be 1-64 lowercase ASCII letters, digits, underscores, or hyphens, starting with a letter or digit.", paramName);
        return trimmed;
    }

    /// <summary>分割 namespace:local-id；只检查分隔形式，两部分由各强类型构造器共用 NormalizePart 校验。</summary>
    public static (string Namespace, string LocalId) Split(string value, string typeName)
    {
        ArgumentNullException.ThrowIfNull(value);
        var trimmed = value.Trim();
        var separator = trimmed.IndexOf(':');
        if (separator <= 0 || separator != trimmed.LastIndexOf(':') || separator == trimmed.Length - 1)
            throw new ArgumentException($"{typeName} must have the form namespace:local-id.", nameof(value));
        return (trimmed[..separator], trimmed[(separator + 1)..]);
    }

    private static bool IsStartChar(char c) => c is (>= 'a' and <= 'z') or (>= '0' and <= '9');
}
