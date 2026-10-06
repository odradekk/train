// 属于内容层的蓝图定义解析模块。
// 校验当前 JSON 文件的字段结构与身份格式，生成供候选目录构建使用的中间数据。

using System.Text.Json;
using Train.Core.Content.Composites;
using Train.Core.Content.Voxels;

namespace Train.Core.Content;

/// <summary>表示一个蓝图文件解析后的显示名称、Archetype 规则和蓝图候选。</summary>
/// <remarks>解析结果按文件声明顺序保留条目；列表通过只读接口提供，构造函数直接保存引用，不复制或冻结集合。</remarks>
/// <example>一个文件可同时提供 Archetype 和引用它的蓝图；见 <c>CompositeContentTests.Builtin_style_composites_commit_through_the_existing_reloader</c>。</example>
internal sealed class ParsedCompositeDocument
{
    /// <summary>获取去除首尾空白后的文件显示名；文件未声明 display_name 时为 null。</summary>
    public string? DisplayName { get; }
    /// <summary>获取按声明顺序保存的 Archetype 候选；文件可不声明此集合。</summary>
    public IReadOnlyList<ParsedCompositeArchetype> Archetypes { get; }
    /// <summary>获取按声明顺序保存的蓝图候选；文件可不声明此集合。</summary>
    public IReadOnlyList<ParsedCompositeDefinition> Composites { get; }

    /// <summary>保存文件级显示信息和两组候选条目。</summary>
    /// <param name="displayName">已规范化的文件显示名；未声明时为 null。</param>
    /// <param name="archetypes">非空引用的 Archetype 候选列表，允许空列表。</param>
    /// <param name="composites">非空引用的蓝图候选列表，允许空列表。</param>
    /// <remarks>字段与集合有效性由解析器负责；构造函数不校验参数或复制列表。</remarks>
    public ParsedCompositeDocument(
        string? displayName,
        IReadOnlyList<ParsedCompositeArchetype> archetypes,
        IReadOnlyList<ParsedCompositeDefinition> composites)
    {
        DisplayName = displayName;
        Archetypes = archetypes;
        Composites = composites;
    }
}

/// <summary>表示已通过字段校验的包内 Archetype 规则及其来源，等待候选目录补全稳定身份。</summary>
/// <example>规则可声明需要两个接触点及 identity_core 核心标签；见 <c>CompositeContentTests.Builtin_style_composites_commit_through_the_existing_reloader</c>。</example>
internal sealed class ParsedCompositeArchetype
{
    /// <summary>获取未附加包命名空间的局部身份，符合小写 ASCII 身份规则。</summary>
    public string LocalId { get; }
    /// <summary>获取建造规则声明；解析阶段只校验规则值的类型和范围。</summary>
    public ConstructionRules Construction { get; }
    /// <summary>获取身份保留所要求的核心部件标签。</summary>
    public IdentityRetentionRules IdentityRetention { get; }
    /// <summary>获取定义的来源文件路径，供后续诊断和目录来源记录使用。</summary>
    public string SourceFile { get; }

    /// <summary>保存已解析的局部身份、规则和来源路径。</summary>
    /// <param name="localId">已经校验的包内局部身份。</param>
    /// <param name="construction">非空的建造规则声明。</param>
    /// <param name="identityRetention">非空的身份保留规则。</param>
    /// <param name="sourceFile">定义所属源文件的诊断路径。</param>
    /// <remarks>直接保存传入值，不再次校验规则或读取来源文件。</remarks>
    public ParsedCompositeArchetype(
        string localId,
        ConstructionRules construction,
        IdentityRetentionRules identityRetention,
        string sourceFile)
    {
        LocalId = localId;
        Construction = construction;
        IdentityRetention = identityRetention;
        SourceFile = sourceFile;
    }
}

/// <summary>表示已通过字段格式校验的蓝图布局、接触点、Archetype 引用和来源。</summary>
/// <remarks>体素引用已补全命名空间，Archetype 引用保留原文；引用存在性及建造规则由后续阶段校验。两个列表直接保存引用，不复制或冻结。</remarks>
/// <example>接触点 (1, -0.75, 1.5) 可以绑定到整数格 (1, 0, 0)，两者含义独立；见 <c>CompositeContentTests.Minimal_blueprint_parses_archetype_voxels_and_separate_contact_bindings</c>。</example>
internal sealed class ParsedCompositeDefinition
{
    /// <summary>获取未附加包命名空间的蓝图局部身份。</summary>
    public string LocalId { get; }
    /// <summary>获取去除首尾空白后的蓝图名称，长度为 1 至 100 个 Unicode 码点。</summary>
    public string Name { get; }
    /// <summary>获取去除首尾空白后的蓝图说明，长度为 1 至 2000 个 Unicode 码点。</summary>
    public string Description { get; }
    /// <summary>获取 Archetype 引用原文；格式已校验，局部引用尚未补上包命名空间。</summary>
    public string ArchetypeRaw { get; }
    /// <summary>获取声明的尺寸档位；布局是否落在该档位包络内由建造阶段检查。</summary>
    public VehicleSizeClass SizeClass { get; }
    /// <summary>获取按文件顺序保存的体素候选，坐标为相对蓝图原点的整数体素格。</summary>
    public IReadOnlyList<CompositeVoxelSpec> Voxels { get; }
    /// <summary>获取按文件顺序保存的接触点，连续坐标以一格边长为单位，另带整数绑定格坐标。</summary>
    public IReadOnlyList<CompositeContactAnchor> Contacts { get; }
    /// <summary>获取定义所属源文件的诊断路径。</summary>
    public string SourceFile { get; }

    /// <summary>保存字段校验后的蓝图候选，供目录构建阶段补全 Archetype 身份。</summary>
    /// <param name="localId">已校验的包内蓝图身份。</param>
    /// <param name="name">已规范化的非空名称。</param>
    /// <param name="description">已规范化的非空说明。</param>
    /// <param name="archetypeRaw">已通过身份格式校验的 Archetype 引用原文。</param>
    /// <param name="sizeClass">已识别的尺寸档位。</param>
    /// <param name="voxels">非空引用的体素候选列表，允许空列表。</param>
    /// <param name="contacts">非空引用的接触点候选列表，允许空列表。</param>
    /// <param name="sourceFile">定义所属源文件的诊断路径。</param>
    /// <remarks>直接保存参数，不再次验证字段、引用或布局，也不复制列表。</remarks>
    public ParsedCompositeDefinition(
        string localId,
        string name,
        string description,
        string archetypeRaw,
        VehicleSizeClass sizeClass,
        IReadOnlyList<CompositeVoxelSpec> voxels,
        IReadOnlyList<CompositeContactAnchor> contacts,
        string sourceFile)
    {
        LocalId = localId;
        Name = name;
        Description = description;
        ArchetypeRaw = archetypeRaw;
        SizeClass = sizeClass;
        Voxels = voxels;
        Contacts = contacts;
        SourceFile = sourceFile;
    }
}

/// <summary>严格解析当前蓝图 JSON schema，将文件内容转换为候选目录构建所需的中间数据。</summary>
/// <remarks>本层校验结构、必填字段、数值和身份格式。候选目录检查引用是否存在；建造流程检查非空、六向连通、重复格、尺寸包络以及接触点数量、互异和绑定要求。</remarks>
/// <example>接触点的有限连续坐标与整数绑定格分别解析；见 <c>CompositeContentTests.Minimal_blueprint_parses_archetype_voxels_and_separate_contact_bindings</c>。</example>
internal static class CompositesJsonParser
{
    /// <summary>当前唯一支持的文件 schema 版本，固定为 1；格式约定见模块说明。</summary>
    private const int SchemaVersion = 1;

    /// <summary>解析 UTF-8 蓝图文件；结构或字段无效时记录诊断并返回 null。</summary>
    /// <param name="utf8">非空引用的文件字节；最多允许 <see cref="ContentLimits.JsonFileMaxBytes"/> 字节，空数组按无效 JSON 处理。</param>
    /// <param name="packageNamespace">已校验的当前内容包命名空间，用于补全未声明命名空间的定义引用。</param>
    /// <param name="sourceFile">文件的来源路径，写入诊断和候选条目；本方法不读取此路径。</param>
    /// <param name="diagnostics">非空的调用方诊断容器；更新来源并追加诊断，保留已有诊断及无效状态。</param>
    /// <returns>当前文件通过结构和字段校验后返回中间数据，至少含一个 Archetype 或蓝图；失败时返回 null。</returns>
    /// <remarks>拒绝重复属性、未知字段、JSON 注释和尾随逗号。返回中间数据不代表引用目标存在或建造规则已满足。</remarks>
    /// <exception cref="NullReferenceException">utf8 或 diagnostics 为 null；当前实现没有单独的空引用参数检查。</exception>
    /// <exception cref="ArgumentException">局部 Archetype 引用补全身份时，packageNamespace 不符合定义身份规则。</exception>
    /// <example>未知顶层字段或不支持的 schema_version 使文件被拒绝；见 <c>CompositeContentTests.Unknown_fields_and_schema_violations_reject_built_in_composites</c>。</example>
    public static ParsedCompositeDocument? Parse(
        byte[] utf8,
        string packageNamespace,
        string sourceFile,
        DiagnosticBag diagnostics)
    {
        diagnostics.Source = sourceFile;
        if (utf8.Length > ContentLimits.JsonFileMaxBytes)
        {
            diagnostics.Warn(ContentDiagnosticCodes.FileTooLarge, "Composite JSON exceeds the 1 MiB limit.");
            return null;
        }

        if (StrictJson.HasDuplicateProperties(utf8, diagnostics, sourceFile))
            return null;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(utf8, StrictJson.DocumentOptions);
        }
        catch (JsonException exception)
        {
            diagnostics.Warn(ContentDiagnosticCodes.InvalidJson, exception.Message);
            return null;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                diagnostics.Warn(ContentDiagnosticCodes.InvalidJson, "Composite file must be a JSON object.", "$");
                return null;
            }

            return ParseDocument(document.RootElement, packageNamespace, sourceFile, diagnostics);
        }
    }

    /// <summary>检查对象根的字段和版本，并解析 Archetype、蓝图及可选文件显示名称。</summary>
    /// <param name="root">已经确认类型为对象的 JSON 根。</param>
    /// <param name="packageNamespace">用于局部引用的有效内容包命名空间。</param>
    /// <param name="sourceFile">写入候选条目的来源路径。</param>
    /// <param name="diagnostics">追加当前文件校验诊断的容器。</param>
    /// <returns>文件级中间数据；任何子项失败或两组候选都为空时为 null。</returns>
    /// <remarks>archetypes 和 composites 可分别省略；声明时必须为数组，至少一组非空。display_name 可省略，声明时必须是去除首尾空白后长度为 1 至 100 个 Unicode 码点的字符串。</remarks>
    private static ParsedCompositeDocument? ParseDocument(
        JsonElement root,
        string packageNamespace,
        string sourceFile,
        DiagnosticBag diagnostics)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (property.Name is not "schema_version" and not "display_name" and not "archetypes" and not "composites")
            {
                diagnostics.Warn(ContentDiagnosticCodes.UnknownField, "Unknown field.", "/" + property.Name);
                return null;
            }
        }

        if (!root.TryGetProperty("schema_version", out var versionElement) ||
            versionElement.ValueKind != JsonValueKind.Number ||
            !versionElement.TryGetInt32(out var version) ||
            version != SchemaVersion)
        {
            diagnostics.Warn(
                ContentDiagnosticCodes.SchemaVersionUnsupported,
                "Only schema_version 1 is supported.",
                "/schema_version");
            return null;
        }

        string? displayName = null;
        if (root.TryGetProperty("display_name", out var displayElement))
        {
            if (!StrictJson.TryGetString(displayElement, diagnostics, "/display_name", ContentDiagnosticCodes.NameInvalid, out var rawName) ||
                !StrictJson.TryNormalizeText(
                    rawName,
                    VoxelDefinition.NameMinLength,
                    VoxelDefinition.NameMaxLength,
                    diagnostics,
                    "/display_name",
                    ContentDiagnosticCodes.NameInvalid,
                    out displayName))
                return null;
        }

        var archetypes = new List<ParsedCompositeArchetype>();
        if (root.TryGetProperty("archetypes", out var archetypesElement))
        {
            if (archetypesElement.ValueKind != JsonValueKind.Array)
            {
                diagnostics.Warn(ContentDiagnosticCodes.InvalidJson, "archetypes must be an array.", "/archetypes");
                return null;
            }

            var index = 0;
            foreach (var item in archetypesElement.EnumerateArray())
            {
                if (!TryParseArchetype(item, $"/archetypes/{index}", sourceFile, diagnostics, out var archetype))
                    return null;
                archetypes.Add(archetype);
                index++;
            }
        }

        var composites = new List<ParsedCompositeDefinition>();
        if (root.TryGetProperty("composites", out var compositesElement))
        {
            if (compositesElement.ValueKind != JsonValueKind.Array)
            {
                diagnostics.Warn(ContentDiagnosticCodes.InvalidJson, "composites must be an array.", "/composites");
                return null;
            }

            var index = 0;
            foreach (var item in compositesElement.EnumerateArray())
            {
                if (!TryParseComposite(item, packageNamespace, $"/composites/{index}", sourceFile, diagnostics, out var composite))
                    return null;
                composites.Add(composite);
                index++;
            }
        }

        if (archetypes.Count == 0 && composites.Count == 0)
        {
            diagnostics.Warn(ContentDiagnosticCodes.InvalidJson, "A composite file must declare archetypes or composites.", "$");
            return null;
        }

        return new ParsedCompositeDocument(displayName, archetypes, composites);
    }

    /// <summary>解析包内 Archetype 身份及必填的建造、身份保留规则。</summary>
    /// <param name="element">包含 id、construction 和 identity_retention 的候选对象。</param>
    /// <param name="jsonPath">此项在文件中的 JSON 路径。</param>
    /// <param name="sourceFile">写入候选条目的来源文件路径。</param>
    /// <param name="diagnostics">追加结构、身份和规则诊断的容器。</param>
    /// <param name="archetype">成功时为已解析的规则候选；失败时为 null。</param>
    /// <returns>对象字段及两个规则声明均有效时返回 true，否则返回 false。</returns>
    private static bool TryParseArchetype(
        JsonElement element,
        string jsonPath,
        string sourceFile,
        DiagnosticBag diagnostics,
        out ParsedCompositeArchetype archetype)
    {
        archetype = null!;
        if (element.ValueKind != JsonValueKind.Object)
        {
            diagnostics.Warn(ContentDiagnosticCodes.InvalidJson, "Each archetype must be an object.", jsonPath);
            return false;
        }

        string? localId = null;
        JsonElement? construction = null;
        JsonElement? identity = null;
        foreach (var property in element.EnumerateObject())
        {
            var fieldPath = jsonPath + "/" + property.Name;
            switch (property.Name)
            {
                case "id":
                    if (!StrictJson.TryGetString(property.Value, diagnostics, fieldPath, ContentDiagnosticCodes.LocalIdInvalid, out localId))
                        return false;
                    break;
                case "construction":
                    construction = property.Value;
                    break;
                case "identity_retention":
                    identity = property.Value;
                    break;
                default:
                    diagnostics.Warn(ContentDiagnosticCodes.UnknownField, "Unknown field.", fieldPath);
                    return false;
            }
        }

        if (localId is null || !VoxelDefinitionId.IsValidPart(localId))
        {
            diagnostics.Warn(ContentDiagnosticCodes.LocalIdInvalid, "Local id is invalid.", jsonPath + "/id");
            return false;
        }

        var missingRequired = false;
        ConstructionRules? rules = null;
        if (construction is null)
        {
            diagnostics.Warn(ContentDiagnosticCodes.InvalidJson, "construction is required.", jsonPath + "/construction");
            missingRequired = true;
        }
        else if (!TryParseConstruction(construction.Value, jsonPath + "/construction", diagnostics, out rules))
            return false;

        IdentityRetentionRules? retention = null;
        if (identity is null)
        {
            diagnostics.Warn(ContentDiagnosticCodes.InvalidJson, "identity_retention is required.", jsonPath + "/identity_retention");
            missingRequired = true;
        }
        else if (!TryParseIdentity(identity.Value, jsonPath + "/identity_retention", diagnostics, out retention))
            return false;

        if (missingRequired)
            return false;

        archetype = new ParsedCompositeArchetype(localId, rules!, retention!, sourceFile);
        return true;
    }

    /// <summary>解析五项必填建造规则的声明值。</summary>
    /// <param name="element">包含非空六向连通、声明尺寸、接触点数量、互异及绑定要求的规则对象。</param>
    /// <param name="jsonPath">construction 对象的 JSON 路径。</param>
    /// <param name="diagnostics">追加字段校验诊断的容器。</param>
    /// <param name="rules">成功时为规则值；失败时为 null。</param>
    /// <returns>四个布尔字段均存在，接触点数量为非负 Int32 整数，且没有未知字段时返回 true。</returns>
    /// <remarks>只读取规则声明，不将这些要求应用到蓝图布局。</remarks>
    private static bool TryParseConstruction(
        JsonElement element,
        string jsonPath,
        DiagnosticBag diagnostics,
        out ConstructionRules rules)
    {
        rules = null!;
        if (element.ValueKind != JsonValueKind.Object)
        {
            diagnostics.Warn(ContentDiagnosticCodes.InvalidJson, "construction must be an object.", jsonPath);
            return false;
        }

        bool? connected = null;
        bool? declaredSize = null;
        int? contactCount = null;
        bool? distinct = null;
        bool? bound = null;
        foreach (var property in element.EnumerateObject())
        {
            var fieldPath = jsonPath + "/" + property.Name;
            switch (property.Name)
            {
                case "require_nonempty_six_connected":
                    if (!TryGetBoolean(property.Value, diagnostics, fieldPath, out var parsedConnected))
                        return false;
                    connected = parsedConnected;
                    break;
                case "require_declared_size":
                    if (!TryGetBoolean(property.Value, diagnostics, fieldPath, out var parsedSize))
                        return false;
                    declaredSize = parsedSize;
                    break;
                case "contact_anchor_count":
                    if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out var parsedCount) || parsedCount < 0)
                    {
                        diagnostics.Warn(ContentDiagnosticCodes.ContactInvalid, "contact_anchor_count must be a non-negative integer.", fieldPath);
                        return false;
                    }

                    contactCount = parsedCount;
                    break;
                case "contacts_must_be_distinct":
                    if (!TryGetBoolean(property.Value, diagnostics, fieldPath, out var parsedDistinct))
                        return false;
                    distinct = parsedDistinct;
                    break;
                case "contacts_must_bind_existing_parts":
                    if (!TryGetBoolean(property.Value, diagnostics, fieldPath, out var parsedBound))
                        return false;
                    bound = parsedBound;
                    break;
                default:
                    diagnostics.Warn(ContentDiagnosticCodes.UnknownField, "Unknown field.", fieldPath);
                    return false;
            }
        }

        if (connected is null || declaredSize is null || contactCount is null || distinct is null || bound is null)
        {
            diagnostics.Warn(ContentDiagnosticCodes.InvalidJson, "construction is missing required fields.", jsonPath);
            return false;
        }

        rules = new ConstructionRules(connected.Value, declaredSize.Value, contactCount.Value, distinct.Value, bound.Value);
        return true;
    }

    /// <summary>校验身份保留规则中的必需核心标签。</summary>
    /// <param name="element">只允许包含必填 require_core_tag 字段的对象。</param>
    /// <param name="jsonPath">identity_retention 对象的 JSON 路径。</param>
    /// <param name="diagnostics">追加字段和标签诊断的容器。</param>
    /// <param name="rules">成功时为身份保留规则；失败时为 null。</param>
    /// <returns>标签符合小写 ASCII 身份字符及长度规则时返回 true。</returns>
    /// <remarks>标签在构造规则前先按原文检查，带首尾空白的输入会被拒绝。</remarks>
    private static bool TryParseIdentity(
        JsonElement element,
        string jsonPath,
        DiagnosticBag diagnostics,
        out IdentityRetentionRules rules)
    {
        rules = null!;
        if (element.ValueKind != JsonValueKind.Object)
        {
            diagnostics.Warn(ContentDiagnosticCodes.InvalidJson, "identity_retention must be an object.", jsonPath);
            return false;
        }

        string? tag = null;
        foreach (var property in element.EnumerateObject())
        {
            var fieldPath = jsonPath + "/" + property.Name;
            if (property.Name != "require_core_tag")
            {
                diagnostics.Warn(ContentDiagnosticCodes.UnknownField, "Unknown field.", fieldPath);
                return false;
            }

            if (!StrictJson.TryGetString(property.Value, diagnostics, fieldPath, ContentDiagnosticCodes.TagInvalid, out tag))
                return false;
        }

        if (tag is null || !VoxelDefinitionId.IsValidPart(tag))
        {
            diagnostics.Warn(ContentDiagnosticCodes.TagInvalid, "require_core_tag is invalid.", jsonPath + "/require_core_tag");
            return false;
        }

        try
        {
            rules = new IdentityRetentionRules(tag);
            return true;
        }
        catch (ArgumentException)
        {
            diagnostics.Warn(ContentDiagnosticCodes.TagInvalid, "require_core_tag is invalid.", jsonPath + "/require_core_tag");
            return false;
        }
    }

    /// <summary>解析蓝图身份、显示文本、Archetype 引用格式、尺寸档位、体素和接触点字段。</summary>
    /// <param name="element">包含当前蓝图 schema 字段的候选对象。</param>
    /// <param name="packageNamespace">用于补全局部定义引用的包命名空间。</param>
    /// <param name="jsonPath">该蓝图对象的 JSON 路径。</param>
    /// <param name="sourceFile">写入候选条目的来源文件路径。</param>
    /// <param name="diagnostics">追加字段校验诊断的容器。</param>
    /// <param name="composite">成功时为蓝图候选；失败时为 null。</param>
    /// <returns>所有必填字段和列表项均符合格式时返回 true，否则返回 false。</returns>
    /// <remarks>名称和说明都必填，去除首尾空白后分别允许 1 至 100、1 至 2000 个 Unicode 码点；Archetype 引用保留原文。voxels 和 contacts 必须声明，但允许空数组。</remarks>
    private static bool TryParseComposite(
        JsonElement element,
        string packageNamespace,
        string jsonPath,
        string sourceFile,
        DiagnosticBag diagnostics,
        out ParsedCompositeDefinition composite)
    {
        composite = null!;
        if (element.ValueKind != JsonValueKind.Object)
        {
            diagnostics.Warn(ContentDiagnosticCodes.InvalidJson, "Each composite must be an object.", jsonPath);
            return false;
        }

        string? localId = null;
        string? name = null;
        string? description = null;
        string? archetypeRaw = null;
        string? sizeText = null;
        JsonElement? voxels = null;
        JsonElement? contacts = null;
        foreach (var property in element.EnumerateObject())
        {
            var fieldPath = jsonPath + "/" + property.Name;
            switch (property.Name)
            {
                case "id":
                    if (!StrictJson.TryGetString(property.Value, diagnostics, fieldPath, ContentDiagnosticCodes.LocalIdInvalid, out localId))
                        return false;
                    break;
                case "name":
                    if (!StrictJson.TryGetString(property.Value, diagnostics, fieldPath, ContentDiagnosticCodes.NameInvalid, out name))
                        return false;
                    break;
                case "description":
                    if (!StrictJson.TryGetString(property.Value, diagnostics, fieldPath, ContentDiagnosticCodes.DescriptionInvalid, out description))
                        return false;
                    break;
                case "archetype":
                    if (!StrictJson.TryGetString(property.Value, diagnostics, fieldPath, ContentDiagnosticCodes.UnresolvedArchetypeReference, out archetypeRaw))
                        return false;
                    break;
                case "size_class":
                    if (!StrictJson.TryGetString(property.Value, diagnostics, fieldPath, ContentDiagnosticCodes.SizeClassInvalid, out sizeText))
                        return false;
                    break;
                case "voxels":
                    voxels = property.Value;
                    break;
                case "contacts":
                    contacts = property.Value;
                    break;
                default:
                    diagnostics.Warn(ContentDiagnosticCodes.UnknownField, "Unknown field.", fieldPath);
                    return false;
            }
        }

        if (localId is null || !VoxelDefinitionId.IsValidPart(localId))
        {
            diagnostics.Warn(ContentDiagnosticCodes.LocalIdInvalid, "Local id is invalid.", jsonPath + "/id");
            return false;
        }

        if (!StrictJson.TryNormalizeText(name, VoxelDefinition.NameMinLength, VoxelDefinition.NameMaxLength, diagnostics, jsonPath + "/name", ContentDiagnosticCodes.NameInvalid, out var normalizedName) ||
            !StrictJson.TryNormalizeText(description, VoxelDefinition.DescriptionMinLength, VoxelDefinition.DescriptionMaxLength, diagnostics, jsonPath + "/description", ContentDiagnosticCodes.DescriptionInvalid, out var normalizedDescription))
            return false;

        if (archetypeRaw is null || !TryResolveId(archetypeRaw, packageNamespace, out _))
        {
            diagnostics.Warn(ContentDiagnosticCodes.UnresolvedArchetypeReference, "archetype must be a local id or namespace:local-id.", jsonPath + "/archetype");
            return false;
        }

        if (sizeText is null || !VehicleSize.TryParse(sizeText, out var sizeClass))
        {
            diagnostics.Warn(ContentDiagnosticCodes.SizeClassInvalid, "Unknown vehicle size class.", jsonPath + "/size_class");
            return false;
        }

        var missingRequired = false;
        IReadOnlyList<CompositeVoxelSpec> voxelSpecs = Array.Empty<CompositeVoxelSpec>();
        if (voxels is null)
        {
            diagnostics.Warn(ContentDiagnosticCodes.LayoutInvalid, "voxels is required.", jsonPath + "/voxels");
            missingRequired = true;
        }
        else if (!TryParseVoxels(voxels.Value, packageNamespace, jsonPath + "/voxels", diagnostics, out voxelSpecs))
            return false;

        IReadOnlyList<CompositeContactAnchor> contactSpecs = Array.Empty<CompositeContactAnchor>();
        if (contacts is null)
        {
            diagnostics.Warn(ContentDiagnosticCodes.ContactInvalid, "contacts is required.", jsonPath + "/contacts");
            missingRequired = true;
        }
        else if (!TryParseContacts(contacts.Value, jsonPath + "/contacts", diagnostics, out contactSpecs))
            return false;

        if (missingRequired)
            return false;

        composite = new ParsedCompositeDefinition(
            localId,
            normalizedName,
            normalizedDescription,
            archetypeRaw,
            sizeClass,
            voxelSpecs,
            contactSpecs,
            sourceFile);
        return true;
    }

    /// <summary>按文件顺序解析体素候选列表。</summary>
    /// <param name="element">voxels 数组，允许为空。</param>
    /// <param name="packageNamespace">用于补全局部体素定义引用的包命名空间。</param>
    /// <param name="jsonPath">体素数组的 JSON 路径。</param>
    /// <param name="diagnostics">追加数组或条目诊断的容器。</param>
    /// <param name="voxels">成功时为按声明顺序保存的候选列表；失败时为空列表。</param>
    /// <returns>输入为数组且每个条目均可解析时返回 true。</returns>
    /// <remarks>列表通过只读接口返回，未冻结；重复坐标、连通性和尺寸包络由建造阶段检查。</remarks>
    private static bool TryParseVoxels(
        JsonElement element,
        string packageNamespace,
        string jsonPath,
        DiagnosticBag diagnostics,
        out IReadOnlyList<CompositeVoxelSpec> voxels)
    {
        voxels = Array.Empty<CompositeVoxelSpec>();
        if (element.ValueKind != JsonValueKind.Array)
        {
            diagnostics.Warn(ContentDiagnosticCodes.LayoutInvalid, "voxels must be an array.", jsonPath);
            return false;
        }

        var parsed = new List<CompositeVoxelSpec>();
        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            if (!TryParseVoxel(item, packageNamespace, jsonPath + "/" + index, diagnostics, out var spec))
                return false;
            parsed.Add(spec);
            index++;
        }

        voxels = parsed;
        return true;
    }

    /// <summary>解析单个体素的三轴整数格坐标、定义引用格式和正交朝向。</summary>
    /// <param name="element">包含必填 x、y、z、definition 和 orientation 的体素对象。</param>
    /// <param name="packageNamespace">未显式声明命名空间时使用的内容包命名空间。</param>
    /// <param name="jsonPath">该体素对象的 JSON 路径。</param>
    /// <param name="diagnostics">追加坐标、引用格式和朝向诊断的容器。</param>
    /// <param name="spec">成功时为体素候选，局部引用已补全命名空间；失败时为 null。</param>
    /// <returns>字段类型、Int32 坐标、定义身份和正交朝向编码均有效时返回 true。</returns>
    /// <remarks>坐标相对蓝图原点，以体素格为单位，允许负数；体素定义是否存在及是否允许此朝向由后续阶段检查。</remarks>
    private static bool TryParseVoxel(
        JsonElement element,
        string packageNamespace,
        string jsonPath,
        DiagnosticBag diagnostics,
        out CompositeVoxelSpec spec)
    {
        spec = null!;
        if (element.ValueKind != JsonValueKind.Object)
        {
            diagnostics.Warn(ContentDiagnosticCodes.LayoutInvalid, "Each composite voxel must be an object.", jsonPath);
            return false;
        }

        int? x = null;
        int? y = null;
        int? z = null;
        string? definition = null;
        string? orientationText = null;
        foreach (var property in element.EnumerateObject())
        {
            var fieldPath = jsonPath + "/" + property.Name;
            switch (property.Name)
            {
                case "x":
                    if (!TryGetInt(property.Value, diagnostics, fieldPath, out var parsedX))
                        return false;
                    x = parsedX;
                    break;
                case "y":
                    if (!TryGetInt(property.Value, diagnostics, fieldPath, out var parsedY))
                        return false;
                    y = parsedY;
                    break;
                case "z":
                    if (!TryGetInt(property.Value, diagnostics, fieldPath, out var parsedZ))
                        return false;
                    z = parsedZ;
                    break;
                case "definition":
                    if (!StrictJson.TryGetString(property.Value, diagnostics, fieldPath, ContentDiagnosticCodes.UnresolvedVoxelReference, out definition))
                        return false;
                    break;
                case "orientation":
                    if (!StrictJson.TryGetString(property.Value, diagnostics, fieldPath, ContentDiagnosticCodes.OrientationInvalid, out orientationText))
                        return false;
                    break;
                default:
                    diagnostics.Warn(ContentDiagnosticCodes.UnknownField, "Unknown field.", fieldPath);
                    return false;
            }
        }

        if (x is null || y is null || z is null)
        {
            diagnostics.Warn(ContentDiagnosticCodes.LayoutInvalid, "Composite voxels require integer x, y, and z.", jsonPath);
            return false;
        }

        if (definition is null || !TryResolveVoxelId(definition, packageNamespace, out var definitionId))
        {
            diagnostics.Warn(ContentDiagnosticCodes.UnresolvedVoxelReference, "definition must be a local id or namespace:local-id.", jsonPath + "/definition");
            return false;
        }

        if (orientationText is null || !VoxelOrientation.TryParse(orientationText, out var orientation))
        {
            diagnostics.Warn(ContentDiagnosticCodes.OrientationInvalid, "orientation is not a valid orthogonal encoding.", jsonPath + "/orientation");
            return false;
        }

        spec = new CompositeVoxelSpec(new LocalVoxelCoord(x.Value, y.Value, z.Value), definitionId, orientation);
        return true;
    }

    /// <summary>按文件顺序解析蓝图接触点列表。</summary>
    /// <param name="element">contacts 数组，允许为空。</param>
    /// <param name="jsonPath">接触点数组的 JSON 路径。</param>
    /// <param name="diagnostics">追加数组或条目诊断的容器。</param>
    /// <param name="contacts">成功时为按声明顺序保存的接触点列表；失败时为空列表。</param>
    /// <returns>输入为数组且每个接触点均可解析时返回 true。</returns>
    /// <remarks>接触点数量、位置互异和绑定格存在性由建造规则检查，本层保留全部格式有效的声明。</remarks>
    private static bool TryParseContacts(
        JsonElement element,
        string jsonPath,
        DiagnosticBag diagnostics,
        out IReadOnlyList<CompositeContactAnchor> contacts)
    {
        contacts = Array.Empty<CompositeContactAnchor>();
        if (element.ValueKind != JsonValueKind.Array)
        {
            diagnostics.Warn(ContentDiagnosticCodes.ContactInvalid, "contacts must be an array.", jsonPath);
            return false;
        }

        var parsed = new List<CompositeContactAnchor>();
        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            if (!TryParseContact(item, jsonPath + "/" + index, diagnostics, out var contact))
                return false;
            parsed.Add(contact);
            index++;
        }

        contacts = parsed;
        return true;
    }

    /// <summary>校验接触点的有限局部坐标及其绑定体素坐标。</summary>
    /// <param name="element">包含必填 x、y、z 和 cell 的接触点对象。</param>
    /// <param name="jsonPath">该接触点对象的 JSON 路径。</param>
    /// <param name="diagnostics">追加接触点及绑定格字段诊断的容器。</param>
    /// <param name="contact">成功时为接触点和绑定格；失败时为 null。</param>
    /// <returns>三轴位置为有限 double，且 cell 为有效三轴整数格时返回 true。</returns>
    /// <remarks>位置相对蓝图原点，以一格边长为单位，允许负数；连续位置与整数绑定格分别存储，不要求两者重合。</remarks>
    private static bool TryParseContact(
        JsonElement element,
        string jsonPath,
        DiagnosticBag diagnostics,
        out CompositeContactAnchor contact)
    {
        contact = null!;
        if (element.ValueKind != JsonValueKind.Object)
        {
            diagnostics.Warn(ContentDiagnosticCodes.ContactInvalid, "Each contact must be an object.", jsonPath);
            return false;
        }

        double? x = null;
        double? y = null;
        double? z = null;
        JsonElement? cell = null;
        foreach (var property in element.EnumerateObject())
        {
            var fieldPath = jsonPath + "/" + property.Name;
            switch (property.Name)
            {
                case "x":
                    if (!TryGetFiniteDouble(property.Value, diagnostics, fieldPath, out var parsedX))
                        return false;
                    x = parsedX;
                    break;
                case "y":
                    if (!TryGetFiniteDouble(property.Value, diagnostics, fieldPath, out var parsedY))
                        return false;
                    y = parsedY;
                    break;
                case "z":
                    if (!TryGetFiniteDouble(property.Value, diagnostics, fieldPath, out var parsedZ))
                        return false;
                    z = parsedZ;
                    break;
                case "cell":
                    cell = property.Value;
                    break;
                default:
                    diagnostics.Warn(ContentDiagnosticCodes.UnknownField, "Unknown field.", fieldPath);
                    return false;
            }
        }

        if (x is null || y is null || z is null || cell is null ||
            !TryParseCell(cell.Value, jsonPath + "/cell", diagnostics, out var bound))
        {
            diagnostics.Warn(ContentDiagnosticCodes.ContactInvalid, "contacts require finite x, y, z and a bound cell.", jsonPath);
            return false;
        }

        contact = new CompositeContactAnchor(x.Value, y.Value, z.Value, bound);
        return true;
    }

    /// <summary>读取绑定体素的三轴整数局部坐标。</summary>
    /// <param name="element">包含且只包含 x、y、z 的绑定格对象。</param>
    /// <param name="jsonPath">cell 对象的 JSON 路径。</param>
    /// <param name="diagnostics">追加绑定格字段诊断的容器。</param>
    /// <param name="cell">成功时为相对蓝图原点的整数体素格；失败时为默认结构值。</param>
    /// <returns>三轴坐标均存在且可表示为 Int32 时返回 true。</returns>
    /// <remarks>允许负坐标；此处只校验坐标格式，不检查该格是否包含蓝图体素。</remarks>
    private static bool TryParseCell(
        JsonElement element,
        string jsonPath,
        DiagnosticBag diagnostics,
        out LocalVoxelCoord cell)
    {
        cell = default;
        if (element.ValueKind != JsonValueKind.Object)
        {
            diagnostics.Warn(ContentDiagnosticCodes.ContactInvalid, "cell must be an object.", jsonPath);
            return false;
        }

        int? x = null;
        int? y = null;
        int? z = null;
        foreach (var property in element.EnumerateObject())
        {
            var fieldPath = jsonPath + "/" + property.Name;
            switch (property.Name)
            {
                case "x":
                    if (!TryGetInt(property.Value, diagnostics, fieldPath, out var parsedX))
                        return false;
                    x = parsedX;
                    break;
                case "y":
                    if (!TryGetInt(property.Value, diagnostics, fieldPath, out var parsedY))
                        return false;
                    y = parsedY;
                    break;
                case "z":
                    if (!TryGetInt(property.Value, diagnostics, fieldPath, out var parsedZ))
                        return false;
                    z = parsedZ;
                    break;
                default:
                    diagnostics.Warn(ContentDiagnosticCodes.UnknownField, "Unknown field.", fieldPath);
                    return false;
            }
        }

        if (x is null || y is null || z is null)
        {
            diagnostics.Warn(ContentDiagnosticCodes.ContactInvalid, "cell requires integer x, y, and z.", jsonPath);
            return false;
        }

        cell = new LocalVoxelCoord(x.Value, y.Value, z.Value);
        return true;
    }

    /// <summary>将局部标识或显式命名空间标识解析为完整定义标识。</summary>
    /// <param name="raw">非空引用的 Archetype 身份文本，形式为 local-id 或 namespace:local-id。</param>
    /// <param name="packageNamespace">局部身份需要补全时使用的有效包命名空间；显式身份不使用此值。</param>
    /// <param name="namespaced">成功时为完整身份字符串，失败时为空字符串。</param>
    /// <returns>引用格式有效时返回 true，引用格式错误时返回 false。</returns>
    /// <remarks>显式身份按定义身份规则去除首尾空白；局部身份先按原文校验，不修剪空白。这里只处理身份格式，引用存在性由候选目录检查。</remarks>
    /// <exception cref="NullReferenceException">raw 为 null。</exception>
    /// <exception cref="ArgumentNullException">局部身份有效但 packageNamespace 为 null。</exception>
    /// <exception cref="ArgumentException">局部身份有效但 packageNamespace 不符合身份规则。</exception>
    /// <example>core 包中的局部引用 dev-hollow-shell 补全为 core:dev-hollow-shell；见 <c>CompositeContentTests.Builtin_style_composites_commit_through_the_existing_reloader</c>。</example>
    public static bool TryResolveId(string raw, string packageNamespace, out string namespaced)
    {
        namespaced = "";
        if (raw.Contains(':', StringComparison.Ordinal))
        {
            try
            {
                namespaced = CompositeArchetypeId.Parse(raw).Value;
                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        if (!VoxelDefinitionId.IsValidPart(raw))
            return false;
        namespaced = new CompositeArchetypeId(packageNamespace, raw).Value;
        return true;
    }

    /// <summary>解析体素定义引用；格式错误时返回 false。</summary>
    /// <param name="raw">非空引用的局部或显式命名空间体素身份文本。</param>
    /// <param name="packageNamespace">局部引用补全时使用的包命名空间；显式引用不使用此值。</param>
    /// <param name="id">成功时为完整的体素定义身份；失败时为不代表有效身份的默认结构值。</param>
    /// <returns>引用及参与补全的命名空间符合身份规则时返回 true，否则返回 false。</returns>
    /// <remarks>通过定义身份构造规则去除各部分首尾空白；这里只解析身份，不查询候选目录。</remarks>
    /// <exception cref="NullReferenceException">raw 为 null。</exception>
    /// <example>文件中的 identity-core 在 core 包中解析为 core:identity-core；见 <c>CompositeContentTests.Minimal_blueprint_parses_archetype_voxels_and_separate_contact_bindings</c>。</example>
    public static bool TryResolveVoxelId(string raw, string packageNamespace, out VoxelDefinitionId id)
    {
        id = default;
        try
        {
            id = raw.Contains(':', StringComparison.Ordinal)
                ? VoxelDefinitionId.Parse(raw)
                : new VoxelDefinitionId(packageNamespace, raw);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>将已经过格式校验的 Archetype 引用转换为稳定定义身份。</summary>
    /// <param name="raw">非空引用的局部或显式命名空间 Archetype 身份文本。</param>
    /// <param name="packageNamespace">未声明命名空间时使用的包命名空间；显式引用不使用此值。</param>
    /// <returns>去除身份各部分首尾空白，并在需要时补全包命名空间的 Archetype 身份。</returns>
    /// <exception cref="NullReferenceException">raw 为 null。</exception>
    /// <exception cref="ArgumentNullException">局部引用需要补全时 packageNamespace 为 null。</exception>
    /// <exception cref="ArgumentException">引用或参与补全的命名空间不符合身份规则。</exception>
    /// <example>玩家蓝图可以显式引用 core 包中的 Archetype；见 <c>CompositeContentTests.Player_blueprint_uses_file_uuid_namespace_and_in_file_display_name</c>。</example>
    public static CompositeArchetypeId ResolveArchetypeId(string raw, string packageNamespace) =>
        raw.Contains(':', StringComparison.Ordinal)
            ? CompositeArchetypeId.Parse(raw)
            : new CompositeArchetypeId(packageNamespace, raw);

    /// <summary>读取 JSON true 或 false；其他类型记录 InvalidJson 诊断并输出 false。</summary>
    private static bool TryGetBoolean(JsonElement element, DiagnosticBag diagnostics, string jsonPath, out bool value)
    {
        if (element.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            value = element.GetBoolean();
            return true;
        }

        diagnostics.Warn(ContentDiagnosticCodes.InvalidJson, "Expected a boolean.", jsonPath);
        value = false;
        return false;
    }

    /// <summary>读取可表示为 Int32 的 JSON 整数；类型或范围不符时记录 LayoutInvalid 诊断。</summary>
    private static bool TryGetInt(JsonElement element, DiagnosticBag diagnostics, string jsonPath, out int value)
    {
        value = 0;
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out value))
        {
            diagnostics.Warn(ContentDiagnosticCodes.LayoutInvalid, "Expected an integer.", jsonPath);
            return false;
        }

        return true;
    }

    /// <summary>校验 JSON 数值为有限 double，并在失败时记录字段诊断。</summary>
    private static bool TryGetFiniteDouble(JsonElement element, DiagnosticBag diagnostics, string jsonPath, out double value)
    {
        value = 0;
        if (element.ValueKind != JsonValueKind.Number ||
            !element.TryGetDouble(out value) ||
            !double.IsFinite(value))
        {
            diagnostics.Warn(ContentDiagnosticCodes.ContactInvalid, "Number must be finite.", jsonPath);
            return false;
        }

        return true;
    }
}
