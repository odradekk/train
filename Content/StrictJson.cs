// Train.Content 内容基础设施模块。
// 提供禁止注释、尾随逗号与重复属性的严格 JSON 读取辅助逻辑。
using System.Text.Json;

namespace Train.Content;

/// <summary>为内容解析器提供 UTF-8 JSON 预扫描、字符串读取和显示文本规范化。</summary>
/// <remarks>辅助方法向调用方的诊断容器报告问题；定义字段、文件大小和引用是否合法仍由各解析器校验。</remarks>
/// <example>清单中的注释、尾随逗号和重复字段被拒绝；见 <c>ModManifestTests.Strict_schema_rejects_unknown_duplicate_and_malformed_manifests</c>。</example>
internal static class StrictJson
{
    /// <summary>正式文档解析使用的严格选项：禁止注释和尾随逗号，嵌套深度上限为 16 层。</summary>
    /// <remarks>16 沿用当前格式的解析约束，未找到独立设计或测量依据。此选项用于后续 JsonDocument.Parse；重复属性预扫描没有采用此深度值，而使用 JsonReaderOptions 的默认上限 64 层。</remarks>
    public static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
        MaxDepth = 16
    };

    /// <summary>预扫描 JSON，发现首个重复属性或读取语法错误时报告诊断。</summary>
    /// <param name="utf8">待扫描的 UTF-8 原始字节，不复制输入或构造定义。</param>
    /// <param name="diagnostics">接收问题并标记当前校验范围无效的共享诊断容器。</param>
    /// <param name="sourceFile">JSON 读取异常时写入诊断容器的来源；重复属性诊断沿用容器已有来源。</param>
    /// <returns>遇到重复属性或 JsonException 时为 true；完成扫描且未遇到这些问题时为 false，不代表通过定义格式校验。</returns>
    /// <remarks>
    /// 各对象中的属性名按 Ordinal 比较，遇到首个问题即停止；JSON 注释与尾随逗号也会触发读取错误。
    /// 读取深度使用 JsonReaderOptions 的默认上限 64 层，正式文档解析仍须另行采用 DocumentOptions 的 16 层限制。
    /// 问题通过诊断容器报告，实际保留的条目受其额度限制；重复属性的位置省略数组索引，且不转义属性名，不保证为完整 JSON 指针。
    /// 仅捕获 JsonException，其他异常及诊断写入异常向调用方传播。
    /// </remarks>
    /// <exception cref="NullReferenceException"><paramref name="diagnostics"/> 为 null，且扫描遇到需要报告的问题。</exception>
    /// <example>重复 schema_version、JSON 注释和尾随逗号会使清单解析失败；见 <c>ModManifestTests.Strict_schema_rejects_unknown_duplicate_and_malformed_manifests</c>。</example>
    public static bool HasDuplicateProperties(byte[] utf8, DiagnosticBag diagnostics, string sourceFile)
    {
        var reader = new Utf8JsonReader(utf8, new JsonReaderOptions
        {
            CommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false
        });
        var stack = new Stack<HashSet<string>>();
        var path = new Stack<string>();
        try
        {
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject:
                        stack.Push(new HashSet<string>(StringComparer.Ordinal));
                        break;
                    case JsonTokenType.EndObject:
                        if (stack.Count > 0)
                            stack.Pop();
                        if (path.Count > 0)
                            path.Pop();
                        break;
                    case JsonTokenType.StartArray:
                        path.Push("[]");
                        break;
                    case JsonTokenType.EndArray:
                        if (path.Count > 0)
                            path.Pop();
                        break;
                    case JsonTokenType.PropertyName:
                        var name = reader.GetString()!;
                        if (stack.Count > 0 && !stack.Peek().Add(name))
                        {
                            diagnostics.Reject(ContentDiagnosticCodes.DuplicateProperty, "Duplicate JSON property.", JsonPointer(path, name));
                            return true;
                        }

                        path.Push(name);
                        break;
                }

                if (reader.TokenType is JsonTokenType.String or JsonTokenType.Number or JsonTokenType.True
                    or JsonTokenType.False or JsonTokenType.Null)
                {
                    if (path.Count > 0)
                        path.Pop();
                }
            }
        }
        catch (JsonException exception)
        {
            diagnostics.Source = sourceFile;
            diagnostics.Reject(ContentDiagnosticCodes.InvalidJson, exception.Message);
            return true;
        }

        return false;
    }

    /// <summary>读取 JSON 字符串原文，类型不符时报告诊断并返回失败。</summary>
    /// <param name="element">当前文档中的待检查元素。</param>
    /// <param name="diagnostics">类型错误时接收警告并标记范围无效的共享诊断容器。</param>
    /// <param name="jsonPath">类型错误诊断的位置，原样传入。</param>
    /// <param name="code">类型错误使用的非空诊断代码。</param>
    /// <param name="value">成功时为字符串原文，不修剪空白；失败时为空串。</param>
    /// <returns>元素为 JSON 字符串时返回 true，否则报告诊断并返回 false。</returns>
    /// <remarks>只检查 JSON 值的类型，不检查文本长度；元素访问和诊断写入的异常不在此处捕获。</remarks>
    /// <exception cref="NullReferenceException"><paramref name="diagnostics"/> 为 null，且元素不是 JSON 字符串。</exception>
    /// <example>清单文本先按字符串读取，再规范化首尾空白；见 <c>ModManifestTests.Valid_manifest_trims_text_and_accepts_optional_description</c>。</example>
    public static bool TryGetString(
        JsonElement element,
        DiagnosticBag diagnostics,
        string jsonPath,
        string code,
        out string value)
    {
        if (element.ValueKind != JsonValueKind.String)
        {
            diagnostics.Reject(code, "Expected a string.", jsonPath);
            value = "";
            return false;
        }

        value = element.GetString()!;
        return true;
    }

    /// <summary>修剪显示文本首尾空白，并按 Unicode 标量值数量校验给定长度范围。</summary>
    /// <param name="value">待规范化的文本；null 按空串处理，是否可接受由长度范围决定。</param>
    /// <param name="min">允许的最少 Unicode 标量值数量，包含边界。</param>
    /// <param name="max">允许的最多 Unicode 标量值数量，包含边界。</param>
    /// <param name="diagnostics">文本校验失败时接收警告并标记范围无效的共享诊断容器。</param>
    /// <param name="jsonPath">文本错误诊断的位置，原样传入。</param>
    /// <param name="code">文本错误使用的非空诊断代码。</param>
    /// <param name="normalized">成功时为去除首尾空白的文本；失败时为空串。</param>
    /// <returns>规范化后的标量值数量落在 min 到 max 的闭区间时为 true；否则报告诊断并返回 false。</returns>
    /// <remarks>不校验上下限自身的范围或顺序；仅捕获文本规范化抛出的 ArgumentException，诊断写入异常不在此处捕获。</remarks>
    /// <exception cref="NullReferenceException"><paramref name="diagnostics"/> 为 null，且文本未通过长度校验。</exception>
    /// <example>“  铜矿扩展  ”规范化为“铜矿扩展”，全空白的可选说明可以成为空串；见 <c>ModManifestTests.Valid_manifest_trims_text_and_accepts_optional_description</c>。</example>
    public static bool TryNormalizeText(
        string? value,
        int min,
        int max,
        DiagnosticBag diagnostics,
        string jsonPath,
        string code,
        out string normalized)
    {
        try
        {
            normalized = Voxels.VoxelDefinition.NormalizeDisplayText(value ?? "", min, max, "value");
            return true;
        }
        catch (ArgumentException)
        {
            diagnostics.Reject(code, "Text is empty or outside the allowed length.", jsonPath);
            normalized = "";
            return false;
        }
    }

    /// <summary>从当前路径栈和属性名拼接诊断位置，省略数组占位符。</summary>
    /// <remarks>仅构造以斜杠分隔的提示路径；不包含数组索引，也不转义属性名中的斜杠或波浪号。</remarks>
    private static string JsonPointer(Stack<string> path, string name)
    {
        if (path.Count == 0)
            return "/" + name;
        var parts = path.Reverse().Where(part => part != "[]");
        return "/" + string.Join('/', parts.Append(name));
    }
}
