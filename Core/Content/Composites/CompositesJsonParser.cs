// 属于内容层的蓝图定义解析模块。
// 严格校验 schema 2 文件并创建不可变蓝图定义。
using System.Text.Json;
using Train.Core.Content.Voxels;

namespace Train.Core.Content.Composites;

/// <summary>表示一个蓝图文件解析后的可选显示名称和蓝图定义。</summary>
/// <remarks>定义保持文件声明顺序；每个定义自身的体素布局按坐标排序。</remarks>
internal sealed class ParsedCompositeDocument
{
    /// <summary>获取去除首尾空白后的文件显示名；未声明时为 null。</summary>
    public string? DisplayName { get; }
    /// <summary>获取按文件声明顺序保存的蓝图定义。</summary>
    public IReadOnlyList<CompositeDefinition> Composites { get; }

    /// <summary>保存已经校验的文件级显示信息和蓝图定义。</summary>
    public ParsedCompositeDocument(string? displayName, IReadOnlyList<CompositeDefinition> composites)
    {
        DisplayName = displayName;
        Composites = composites;
    }
}

/// <summary>严格解析 schema 2 蓝图 JSON，并补全包内局部引用的命名空间。</summary>
/// <remarks>本层校验字段、身份、非空布局、坐标唯一性和朝向编码；目录候选校验引用存在性和定义允许的朝向。</remarks>
internal static class CompositesJsonParser
{
    private const int SchemaVersion = 2;

    /// <summary>解析 UTF-8 蓝图文件；结构或字段无效时记录确定路径的诊断并返回 null。</summary>
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

            return ParseDocument(document.RootElement, packageNamespace, diagnostics);
        }
    }

    /// <summary>解析文件级版本、显示名和必填蓝图数组。</summary>
    private static ParsedCompositeDocument? ParseDocument(
        JsonElement root,
        string packageNamespace,
        DiagnosticBag diagnostics)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (property.Name is not "schema_version" and not "display_name" and not "composites")
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
            diagnostics.Warn(ContentDiagnosticCodes.SchemaVersionUnsupported, "Only schema_version 2 is supported.", "/schema_version");
            return null;
        }

        string? displayName = null;
        if (root.TryGetProperty("display_name", out var displayElement) &&
            (!StrictJson.TryGetString(displayElement, diagnostics, "/display_name", ContentDiagnosticCodes.NameInvalid, out var rawName) ||
             !StrictJson.TryNormalizeText(rawName, VoxelDefinition.NameMinLength, VoxelDefinition.NameMaxLength,
                 diagnostics, "/display_name", ContentDiagnosticCodes.NameInvalid, out displayName)))
            return null;

        if (!root.TryGetProperty("composites", out var compositesElement) || compositesElement.ValueKind != JsonValueKind.Array)
        {
            diagnostics.Warn(ContentDiagnosticCodes.InvalidJson, "composites is required and must be an array.", "/composites");
            return null;
        }

        var composites = new List<CompositeDefinition>();
        var index = 0;
        foreach (var item in compositesElement.EnumerateArray())
        {
            if (!TryParseComposite(item, packageNamespace, $"/composites/{index}", diagnostics, out var composite))
                return null;
            composites.Add(composite);
            index++;
        }

        if (composites.Count == 0)
        {
            diagnostics.Warn(ContentDiagnosticCodes.InvalidJson, "A composite file must declare at least one composite.", "/composites");
            return null;
        }

        return new ParsedCompositeDocument(displayName, composites);
    }

    /// <summary>解析蓝图的五个必填字段，并创建已校验定义。</summary>
    private static bool TryParseComposite(
        JsonElement element,
        string packageNamespace,
        string jsonPath,
        DiagnosticBag diagnostics,
        out CompositeDefinition composite)
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
        string? sizeText = null;
        JsonElement? voxels = null;
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
                case "size_class":
                    if (!StrictJson.TryGetString(property.Value, diagnostics, fieldPath, ContentDiagnosticCodes.SizeClassInvalid, out sizeText))
                        return false;
                    break;
                case "voxels":
                    voxels = property.Value;
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
        if (!StrictJson.TryNormalizeText(name, VoxelDefinition.NameMinLength, VoxelDefinition.NameMaxLength,
                diagnostics, jsonPath + "/name", ContentDiagnosticCodes.NameInvalid, out var normalizedName) ||
            !StrictJson.TryNormalizeText(description, VoxelDefinition.DescriptionMinLength, VoxelDefinition.DescriptionMaxLength,
                diagnostics, jsonPath + "/description", ContentDiagnosticCodes.DescriptionInvalid, out var normalizedDescription))
            return false;
        if (sizeText is null || !VehicleSize.TryParse(sizeText, out var sizeClass))
        {
            diagnostics.Warn(ContentDiagnosticCodes.SizeClassInvalid, "Unknown vehicle size class.", jsonPath + "/size_class");
            return false;
        }
        if (voxels is null)
        {
            diagnostics.Warn(ContentDiagnosticCodes.LayoutInvalid, "voxels is required.", jsonPath + "/voxels");
            return false;
        }
        if (!TryParseVoxels(voxels.Value, packageNamespace, jsonPath + "/voxels", diagnostics, out var voxelSpecs))
            return false;

        composite = new CompositeDefinition(
            new CompositeDefinitionId(packageNamespace, localId),
            normalizedName,
            normalizedDescription,
            sizeClass,
            voxelSpecs);
        return true;
    }

    /// <summary>解析非空体素数组，并拒绝重复局部坐标。</summary>
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
        var occupied = new HashSet<LocalVoxelCoord>();
        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            if (!TryParseVoxel(item, packageNamespace, $"{jsonPath}/{index}", diagnostics, out var spec))
                return false;
            if (!occupied.Add(spec.Cell))
            {
                diagnostics.Warn(ContentDiagnosticCodes.LayoutInvalid, "Composite voxel coordinates must be unique.", $"{jsonPath}/{index}");
                return false;
            }
            parsed.Add(spec);
            index++;
        }

        if (parsed.Count == 0)
        {
            diagnostics.Warn(ContentDiagnosticCodes.LayoutInvalid, "A composite layout must contain at least one voxel.", jsonPath);
            return false;
        }

        voxels = parsed;
        return true;
    }

    /// <summary>解析单个体素的整数坐标、定义引用和正交朝向。</summary>
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

        int? x = null, y = null, z = null;
        string? definition = null;
        string? orientationText = null;
        foreach (var property in element.EnumerateObject())
        {
            var fieldPath = jsonPath + "/" + property.Name;
            switch (property.Name)
            {
                case "x":
                    if (!TryGetInt(property.Value, diagnostics, fieldPath, out var parsedX)) return false;
                    x = parsedX;
                    break;
                case "y":
                    if (!TryGetInt(property.Value, diagnostics, fieldPath, out var parsedY)) return false;
                    y = parsedY;
                    break;
                case "z":
                    if (!TryGetInt(property.Value, diagnostics, fieldPath, out var parsedZ)) return false;
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

    /// <summary>解析局部或显式命名空间的体素定义引用。</summary>
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

    /// <summary>读取可表示为 Int32 的 JSON 整数。</summary>
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
}
