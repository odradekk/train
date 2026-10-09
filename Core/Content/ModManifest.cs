// 属于内容层的 Mod 清单模块。
// 解析并校验数据 Mod 的清单与 SemVer 版本。
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Train.Core.Content;

/// <summary>表示数据 Mod 根目录中 mod.json 声明的身份、显示信息、版本和作者。</summary>
/// <remarks>
/// <para>通过 <see cref="TryParse"/> 得到的清单已通过当前 schema 校验；直接调用构造函数时，字段校验和文本规范化由调用方负责。文本长度按 Unicode 码点计数，名称、作者名和说明去除首尾空白。</para>
/// <para>文本长度和作者数量上限为当前清单规则；边界用例见 <c>ModManifestTests.Unicode_name_author_and_description_boundaries_are_enforced</c> 和 <c>ModManifestTests.Authors_must_be_one_to_sixteen_unique_trimmed_names</c>。</para>
/// </remarks>
/// <example>名称“  铜矿扩展  ”解析为“铜矿扩展”，版本 1.2.3-alpha.1+build.7 保持原样；见 <c>ModManifestTests.Valid_manifest_trims_text_and_accepts_optional_description</c>。</example>
public sealed class ModManifest
{
    /// <summary>当前 Mod 清单 schema_version 字段唯一支持的版本值 1；与 Mod 自身的 version 字段分开管理。</summary>
    public const int SchemaVersion = 1;
    /// <summary>清单名称去除首尾空白后的最少 Unicode 码点数；当前 schema 为 1。</summary>
    public const int NameMinLength = 1;
    /// <summary>清单名称去除首尾空白后的最多 Unicode 码点数；当前 schema 为 100。</summary>
    public const int NameMaxLength = 100;
    /// <summary>清单说明去除首尾空白后的最少 Unicode 码点数；当前 schema 为 0，表示允许空说明。</summary>
    public const int DescriptionMinLength = 0;
    /// <summary>清单说明去除首尾空白后的最多 Unicode 码点数；当前 schema 为 4000。</summary>
    public const int DescriptionMaxLength = 4000;
    /// <summary>单个作者名去除首尾空白后的最少 Unicode 码点数；当前 schema 为 1。</summary>
    public const int AuthorMinLength = 1;
    /// <summary>单个作者名去除首尾空白后的最多 Unicode 码点数；当前 schema 为 100。</summary>
    public const int AuthorMaxLength = 100;
    /// <summary>作者数组的最少元素数；当前 schema 为 1。</summary>
    public const int AuthorsMinCount = 1;
    /// <summary>作者数组的最多元素数；当前 schema 为 16。</summary>
    public const int AuthorsMaxCount = 16;

    /// <summary>匹配以连字符分隔的 8、4、4、4、12 位小写十六进制 UUID 文本。</summary>
    private static readonly Regex CanonicalUuid = new(
        "^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>取得用于 Mod 命名空间的身份文本；解析结果保留 UUID 校验接受的原始文本。</summary>
    public string Id { get; }
    /// <summary>取得去除首尾空白后的清单名称。</summary>
    public string Name { get; }
    /// <summary>取得通过 SemVer 2.0 校验的版本文本。</summary>
    public string Version { get; }
    /// <summary>取得保留声明顺序的只读作者列表；解析结果中的名称已去除首尾空白，并按区分大小写的精确值确认互不重复。</summary>
    public IReadOnlyList<string> Authors { get; }
    /// <summary>取得去除首尾空白后的说明；未声明时为空字符串。</summary>
    public string Description { get; }

    /// <summary>保存调用方提供的清单字段，并复制作者列表为只读集合。</summary>
    /// <param name="id">调用方已校验的 Mod UUID。</param>
    /// <param name="name">调用方已校验长度并去除首尾空白的名称。</param>
    /// <param name="version">调用方已校验的 SemVer 2.0 版本文本。</param>
    /// <param name="authors">调用方已校验数量、名称长度和唯一性的作者列表；可以通过只读接口传入可变集合。</param>
    /// <param name="description">调用方已校验长度并去除首尾空白的说明；允许空字符串。</param>
    /// <remarks>仅检查作者列表不是 null；其他字段直接保存。后续修改传入的作者集合不会改变清单中的列表。</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="authors"/> 为 <see langword="null"/>。</exception>
    public ModManifest(
        string id,
        string name,
        string version,
        IReadOnlyList<string> authors,
        string description)
    {
        ArgumentNullException.ThrowIfNull(authors);
        Id = id;
        Name = name;
        Version = version;
        Authors = new ReadOnlyCollection<string>(authors.ToArray());
        Description = description;
    }

    /// <summary>按当前 schema 严格解析 UTF-8 Mod 清单，并返回字段校验诊断。</summary>
    /// <param name="utf8">非空引用的清单字节数组；超过 <see cref="ContentLimits.JsonFileMaxBytes"/> 时拒绝解析，空数组按无效 JSON 处理。</param>
    /// <param name="source">写入诊断的来源路径；本方法只解析字节，不读取该路径。</param>
    /// <param name="manifest">成功时为已校验清单；失败时为 <see langword="null"/>。</param>
    /// <param name="diagnostics">本次解析的诊断列表，最多保留 <see cref="ContentLimits.MaxDiagnosticsPerPackage"/> 项。</param>
    /// <returns>JSON 和全部清单字段均有效时返回 true，否则返回 false。</returns>
    /// <remarks>拒绝未知字段、重复属性、不支持的 schema 版本及不合法的字段值；JSON 注释和尾随逗号也不接受。格式或字段校验失败以诊断报告；即使诊断级别为警告，该清单也无效。</remarks>
    /// <exception cref="NullReferenceException"><paramref name="utf8"/> 为 <see langword="null"/>；当前实现没有单独的空引用参数检查。</exception>
    /// <example>省略 description 时结果说明为空字符串；未知字段会拒绝清单；见 <c>ModManifestTests.Valid_manifest_trims_text_and_accepts_optional_description</c> 和 <c>ModManifestTests.Strict_schema_rejects_unknown_duplicate_and_malformed_manifests</c>。</example>
    public static bool TryParse(
        byte[] utf8,
        string source,
        out ModManifest? manifest,
        out IReadOnlyList<ContentDiagnostic> diagnostics)
    {
        var bag = new DiagnosticBag(ContentDiagnosticSeverity.Warning) { Source = source };
        manifest = Parse(utf8, source, bag);
        diagnostics = bag.Items;
        return manifest is not null && !bag.Invalid;
    }

    /// <summary>把解析诊断追加到调用方的诊断容器，并只返回完整有效的清单；失败时返回 null。</summary>
    internal static ModManifest? Parse(byte[] utf8, string source, DiagnosticBag diagnostics) =>
        Read(utf8, source, diagnostics).Manifest;

    /// <summary>检查字节上限及严格 JSON 格式，再读取清单；字段校验失败时仍可保留已识别的 UUID。</summary>
    /// <param name="utf8">非空引用的 UTF-8 清单字节。</param>
    /// <param name="source">本次读取的诊断来源路径，同时更新 diagnostics 的来源。</param>
    /// <param name="diagnostics">非空的调用方诊断容器；追加本次诊断，不清除已有诊断或无效状态。</param>
    /// <returns>读取到的 UUID 及有效清单；无法读取对象根或识别 UUID 时，相应字段为 null。</returns>
    /// <remarks>保留无效清单中的合法 UUID，供 Mod 发现阶段检测重复声明；同 UUID 的无效包也参与冲突判定。</remarks>
    internal static ModManifestRead Read(byte[] utf8, string source, DiagnosticBag diagnostics)
    {
        diagnostics.Source = source;
        if (utf8.Length > ContentLimits.JsonFileMaxBytes)
        {
            diagnostics.Reject(ContentDiagnosticCodes.FileTooLarge, "Mod manifest exceeds the 1 MiB limit.");
            return ModManifestRead.None;
        }

        if (StrictJson.HasDuplicateProperties(utf8, diagnostics, source))
            return ModManifestRead.None;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(utf8, StrictJson.DocumentOptions);
        }
        catch (JsonException exception)
        {
            diagnostics.Reject(ContentDiagnosticCodes.InvalidJson, exception.Message);
            return ModManifestRead.None;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                diagnostics.Reject(ContentDiagnosticCodes.InvalidJson, "Mod manifest must be a JSON object.", "$");
                return ModManifestRead.None;
            }

            return ParseDocument(document.RootElement, diagnostics);
        }
    }

    /// <summary>校验对象根的字段集合、schema 版本和全部清单字段；其他字段失败时仍返回已识别的 UUID。</summary>
    private static ModManifestRead ParseDocument(JsonElement root, DiagnosticBag diagnostics)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (property.Name is not "schema_version" and not "id" and not "name" and not "version"
                and not "authors" and not "description")
            {
                diagnostics.Reject(ContentDiagnosticCodes.UnknownField, "Unknown field.", "/" + property.Name);
            }
        }

        if (!root.TryGetProperty("schema_version", out var versionElement) ||
            versionElement.ValueKind != JsonValueKind.Number ||
            !versionElement.TryGetInt32(out var schemaVersion) ||
            schemaVersion != SchemaVersion)
        {
            diagnostics.Reject(
                ContentDiagnosticCodes.SchemaVersionUnsupported,
                "Only schema_version 1 is supported.",
                "/schema_version");
        }

        var canonicalId = TryReadCanonicalId(root, diagnostics, out var id);
        var hasName = TryReadName(root, diagnostics, out var name);
        var hasVersion = TryReadVersion(root, diagnostics, out var version);
        var hasAuthors = TryReadAuthors(root, diagnostics, out var authors);
        var hasDescription = TryReadDescription(root, diagnostics, out var description);
        if (!canonicalId || !hasName || !hasVersion || !hasAuthors || !hasDescription || diagnostics.Invalid)
            return new ModManifestRead(id);

        return new ModManifestRead(id, new ModManifest(id!, name, version, authors, description));
    }

    /// <summary>读取必填 UUID 字段；失败时输出 null，并确保 id 路径已有对应诊断。</summary>
    private static bool TryReadCanonicalId(JsonElement root, DiagnosticBag diagnostics, out string? id)
    {
        id = null;
        if (!root.TryGetProperty("id", out var idElement))
        {
            diagnostics.Reject(ContentDiagnosticCodes.UuidInvalid, "id is required.", "/id");
            return false;
        }

        if (!StrictJson.TryGetString(idElement, diagnostics, "/id", ContentDiagnosticCodes.UuidInvalid, out var rawId) ||
            !TryCanonicalUuid(rawId, out var canonical))
        {
            if (!diagnostics.Items.Any(item => item.Code == ContentDiagnosticCodes.UuidInvalid && item.JsonPath == "/id"))
                diagnostics.Reject(ContentDiagnosticCodes.UuidInvalid, "Mod id must be a canonical lowercase non-nil UUID.", "/id");
            return false;
        }

        id = canonical;
        return true;
    }

    /// <summary>读取必填名称，去除首尾空白并按 Unicode 码点校验长度；失败时输出空字符串。</summary>
    private static bool TryReadName(JsonElement root, DiagnosticBag diagnostics, out string name)
    {
        name = "";
        if (!root.TryGetProperty("name", out var nameElement))
        {
            diagnostics.Reject(ContentDiagnosticCodes.NameInvalid, "name is required.", "/name");
            return false;
        }

        return StrictJson.TryGetString(nameElement, diagnostics, "/name", ContentDiagnosticCodes.NameInvalid, out var rawName) &&
               StrictJson.TryNormalizeText(
                   rawName,
                   NameMinLength,
                   NameMaxLength,
                   diagnostics,
                   "/name",
                   ContentDiagnosticCodes.NameInvalid,
                   out name);
    }

    /// <summary>读取必填 SemVer 版本并原样保存；不去除首尾空白，失败时输出空字符串并记录诊断。</summary>
    private static bool TryReadVersion(JsonElement root, DiagnosticBag diagnostics, out string version)
    {
        version = "";
        if (!root.TryGetProperty("version", out var versionElement))
        {
            diagnostics.Reject(ContentDiagnosticCodes.VersionInvalid, "version is required.", "/version");
            return false;
        }

        if (!StrictJson.TryGetString(versionElement, diagnostics, "/version", ContentDiagnosticCodes.VersionInvalid, out var rawVersion) ||
            !SemVer.IsValid(rawVersion))
        {
            if (!diagnostics.Items.Any(item => item.Code == ContentDiagnosticCodes.VersionInvalid && item.JsonPath == "/version"))
                diagnostics.Reject(ContentDiagnosticCodes.VersionInvalid, "version must be a SemVer 2.0 string.", "/version");
            return false;
        }

        version = rawVersion;
        return true;
    }

    /// <summary>读取必填作者数组并委托名称、数量及唯一性校验；失败时输出空列表。</summary>
    private static bool TryReadAuthors(JsonElement root, DiagnosticBag diagnostics, out IReadOnlyList<string> authors)
    {
        authors = Array.Empty<string>();
        if (!root.TryGetProperty("authors", out var authorsElement))
        {
            diagnostics.Reject(ContentDiagnosticCodes.AuthorsInvalid, "authors is required.", "/authors");
            return false;
        }

        return TryParseAuthors(authorsElement, diagnostics, out authors);
    }

    /// <summary>读取可选说明，去除首尾空白并校验长度；未声明时输出空字符串并返回成功。</summary>
    private static bool TryReadDescription(JsonElement root, DiagnosticBag diagnostics, out string description)
    {
        description = "";
        if (!root.TryGetProperty("description", out var descriptionElement))
            return true;

        return StrictJson.TryGetString(
                   descriptionElement,
                   diagnostics,
                   "/description",
                   ContentDiagnosticCodes.DescriptionInvalid,
                   out var rawDescription) &&
               StrictJson.TryNormalizeText(
                   rawDescription,
                   DescriptionMinLength,
                   DescriptionMaxLength,
                   diagnostics,
                   "/description",
                   ContentDiagnosticCodes.DescriptionInvalid,
                   out description);
    }

    /// <summary>按声明顺序读取 1 至 16 个作者名；去除首尾空白后以区分大小写的精确值检查重复，重复名称使校验失败。</summary>
    private static bool TryParseAuthors(
        JsonElement element,
        DiagnosticBag diagnostics,
        out IReadOnlyList<string> authors)
    {
        authors = Array.Empty<string>();
        if (element.ValueKind != JsonValueKind.Array)
        {
            diagnostics.Reject(ContentDiagnosticCodes.AuthorsInvalid, "authors must be an array.", "/authors");
            return false;
        }

        var parsed = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            var path = $"/authors/{index}";
            if (!StrictJson.TryGetString(item, diagnostics, path, ContentDiagnosticCodes.AuthorsInvalid, out var raw) ||
                !StrictJson.TryNormalizeText(
                    raw,
                    AuthorMinLength,
                    AuthorMaxLength,
                    diagnostics,
                    path,
                    ContentDiagnosticCodes.AuthorsInvalid,
                    out var author))
            {
                return false;
            }

            if (!seen.Add(author))
            {
                diagnostics.Reject(ContentDiagnosticCodes.AuthorsInvalid, "authors must be unique after trimming.", path);
                return false;
            }

            parsed.Add(author);
            index++;
        }

        if (parsed.Count is < AuthorsMinCount or > AuthorsMaxCount)
        {
            diagnostics.Reject(
                ContentDiagnosticCodes.AuthorsInvalid,
                "authors must contain 1 to 16 unique names.",
                "/authors");
            return false;
        }

        authors = new ReadOnlyCollection<string>(parsed);
        return true;
    }

    /// <summary>按小写 UUID 模式校验 Mod 身份文本，成功时原样返回输入。</summary>
    /// <param name="value">候选 UUID 文本；null 无效，不去除首尾空白或改变大小写。</param>
    /// <param name="canonical">成功时为输入文本本身，失败时为空字符串。</param>
    /// <returns>文本匹配小写、带连字符的 UUID 模式，且不等于全零 UUID 字符串时返回 true。</returns>
    /// <example>550e8400-e29b-41d4-a716-446655440000 可通过校验，全零 UUID 文本被拒绝；见 <c>ModManifestTests.Canonical_non_nil_uuid_is_required</c>。</example>
    public static bool TryCanonicalUuid(string? value, out string canonical)
    {
        canonical = "";
        if (value is null || !CanonicalUuid.IsMatch(value))
            return false;
        if (value == "00000000-0000-0000-0000-000000000000")
            return false;
        canonical = value;
        return true;
    }
}

/// <summary>分别保存读取到的 Mod UUID 和完整有效的清单，使清单其他字段失败时仍能识别 UUID 冲突。</summary>
/// <example>有效包和同 UUID 的无效版本包会一起被禁用；见 <c>ModDiscoveryTests.Valid_and_same_uuid_invalid_version_disable_every_declaration</c>。</example>
internal readonly struct ModManifestRead
{
    /// <summary>获取 UUID 和清单均为 null 的读取结果，表示尚未识别到清单身份。</summary>
    public static ModManifestRead None => default;

    /// <summary>获取已识别的 UUID；UUID 无效或无法读取时为 null，其他字段失败时仍保留该值。</summary>
    public string? CanonicalId { get; }
    /// <summary>获取完整有效的清单；任一校验失败时为 null。</summary>
    public ModManifest? Manifest { get; }

    /// <summary>保存读取结果的身份和清单；本构造函数不再次校验字段或检查两者是否一致。</summary>
    /// <param name="canonicalId">读取到的 UUID；未识别到时为 null。</param>
    /// <param name="manifest">完整有效的清单；未提供或校验失败时为 null。</param>
    public ModManifestRead(string? canonicalId, ModManifest? manifest = null)
    {
        CanonicalId = canonicalId;
        Manifest = manifest;
    }
}

/// <summary>校验当前 Mod 清单接受的 SemVer 2.0 版本字符串。</summary>
/// <remarks>只校验主版本、次版本、修订号和可选预发布、构建标识的语法，不比较版本先后，也不去除首尾空白。</remarks>
/// <example>1.0.0-alpha+001 合法，1.0.0-01 的纯数字预发布标识有前导零而被拒绝；见 <c>ModManifestTests.SemVer_2_0_accepts_prerelease_and_build_and_rejects_invalid_forms</c>。</example>
internal static class SemVer
{
    /// <summary>判断版本文本是否符合三段数字及可选预发布、构建标识的格式。</summary>
    /// <param name="value">候选版本文本；null 或空字符串无效。</param>
    /// <returns>三个版本号均为无多余前导零的非负整数，且可选标识字符与前导零规则均合法时返回 true。</returns>
    public static bool IsValid(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return false;

        var plus = value.IndexOf('+');
        var coreAndPre = plus < 0 ? value : value[..plus];
        var build = plus < 0 ? null : value[(plus + 1)..];
        if (plus >= 0 && build!.Length == 0)
            return false;

        var dash = coreAndPre.IndexOf('-');
        var core = dash < 0 ? coreAndPre : coreAndPre[..dash];
        var pre = dash < 0 ? null : coreAndPre[(dash + 1)..];
        if (dash >= 0 && pre!.Length == 0)
            return false;

        var coreParts = core.Split('.');
        if (coreParts.Length != 3)
            return false;
        if (!IsNumericIdentifier(coreParts[0]) ||
            !IsNumericIdentifier(coreParts[1]) ||
            !IsNumericIdentifier(coreParts[2]))
            return false;

        if (pre is not null && !AreIdentifiers(pre, prerelease: true))
            return false;
        if (build is not null && !AreIdentifiers(build, prerelease: false))
            return false;
        return true;
    }

    /// <summary>逐段校验点号分隔的标识列表；空段无效，prerelease 决定纯数字标识是否禁止前导零。</summary>
    private static bool AreIdentifiers(string text, bool prerelease)
    {
        if (text.Length == 0)
            return false;
        var start = 0;
        for (var i = 0; i <= text.Length; i++)
        {
            if (i < text.Length && text[i] != '.')
                continue;
            if (i == start)
                return false;
            if (!IsIdentifier(text.AsSpan(start, i - start), prerelease))
                return false;
            start = i + 1;
        }

        return true;
    }

    /// <summary>检查非空标识只含 ASCII 字母、数字或连字符，并对纯数字预发布标识额外校验前导零。</summary>
    private static bool IsIdentifier(ReadOnlySpan<char> value, bool prerelease)
    {
        if (value.Length == 0)
            return false;
        var digitsOnly = true;
        foreach (var c in value)
        {
            if (c is (>= '0' and <= '9') or (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or '-')
            {
                if (c is not (>= '0' and <= '9'))
                    digitsOnly = false;
                continue;
            }

            return false;
        }

        if (prerelease && digitsOnly)
            return IsNumericIdentifier(value.ToString());
        return true;
    }

    /// <summary>检查文本为 0，或首位为 1 至 9、后续全为数字；直接检查字符，使合法整数不受机器整数范围限制。</summary>
    private static bool IsNumericIdentifier(string value)
    {
        if (value.Length == 0)
            return false;
        if (value == "0")
            return true;
        if (value[0] is < '1' or > '9')
            return false;
        for (var i = 1; i < value.Length; i++)
        {
            if (value[i] is < '0' or > '9')
                return false;
        }

        return true;
    }
}
