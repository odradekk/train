using System.Text.Json;
using Train.Content.Voxels;

namespace Train.Content.Composites;

internal sealed class ParsedCompositeDocument
{
    public string? DisplayName { get; }
    public IReadOnlyList<CompositeDefinition> Composites { get; }

    public ParsedCompositeDocument(string? displayName, IReadOnlyList<CompositeDefinition> composites)
    {
        DisplayName = displayName;
        Composites = composites;
    }
}

internal static class CompositesJsonParser
{
    private const int SchemaVersion = 2;

    public static ParsedCompositeDocument? Parse(
        byte[] utf8,
        string packageNamespace,
        string sourceFile,
        DiagnosticBag diagnostics)
    {
        return ContentJson.ParseDocument(utf8, sourceFile, SchemaVersion,
            new[] { "schema_version", "display_name", "composites" }, diagnostics,
            root => ParseRoot(root, packageNamespace, diagnostics));
    }

    private static ParsedCompositeDocument? ParseRoot(
        JsonElement root,
        string packageNamespace,
        DiagnosticBag diagnostics)
    {
        string? displayName = null;
        if (root.TryGetProperty("display_name", out var displayElement) &&
            (!StrictJson.TryGetString(displayElement, diagnostics, "/display_name", ContentDiagnosticCodes.NameInvalid, out var rawName) ||
             !StrictJson.TryNormalizeText(rawName, VoxelDefinition.NameMinLength, VoxelDefinition.NameMaxLength,
                 diagnostics, "/display_name", ContentDiagnosticCodes.NameInvalid, out displayName)))
            return null;

        var compositesElement = ContentJson.ReadArray(root, "composites", "", diagnostics);
        if (compositesElement is null)
            return null;

        var composites = new List<CompositeDefinition>();
        var index = 0;
        foreach (var item in compositesElement.Value.EnumerateArray())
        {
            if (!TryParseComposite(item, packageNamespace, $"/composites/{index}", diagnostics, out var composite))
                return null;
            composites.Add(composite);
            index++;
        }

        if (composites.Count == 0)
        {
            diagnostics.Reject(ContentDiagnosticCodes.FieldInvalid, "A composite file must declare at least one composite.", "/composites");
            return null;
        }

        return new ParsedCompositeDocument(displayName, composites);
    }

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
            diagnostics.Reject(ContentDiagnosticCodes.FieldInvalid, "Each composite must be an object.", jsonPath);
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
                    diagnostics.Reject(ContentDiagnosticCodes.UnknownField, "Unknown field.", fieldPath);
                    return false;
            }
        }

        if (localId is null || !ContentIdentity.IsValidPart(localId))
        {
            diagnostics.Reject(ContentDiagnosticCodes.LocalIdInvalid, "Local id is invalid.", jsonPath + "/id");
            return false;
        }
        if (!StrictJson.TryNormalizeText(name, VoxelDefinition.NameMinLength, VoxelDefinition.NameMaxLength,
                diagnostics, jsonPath + "/name", ContentDiagnosticCodes.NameInvalid, out var normalizedName) ||
            !StrictJson.TryNormalizeText(description, VoxelDefinition.DescriptionMinLength, VoxelDefinition.DescriptionMaxLength,
                diagnostics, jsonPath + "/description", ContentDiagnosticCodes.DescriptionInvalid, out var normalizedDescription))
            return false;
        if (sizeText is null || !VehicleSize.TryParse(sizeText, out var sizeClass))
        {
            diagnostics.Reject(ContentDiagnosticCodes.SizeClassInvalid, "Unknown vehicle size class.", jsonPath + "/size_class");
            return false;
        }
        if (voxels is null)
        {
            diagnostics.Reject(ContentDiagnosticCodes.LayoutInvalid, "voxels is required.", jsonPath + "/voxels");
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
            diagnostics.Reject(ContentDiagnosticCodes.LayoutInvalid, "voxels must be an array.", jsonPath);
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
                diagnostics.Reject(ContentDiagnosticCodes.LayoutInvalid, "Composite voxel coordinates must be unique.", $"{jsonPath}/{index}");
                return false;
            }
            parsed.Add(spec);
            index++;
        }

        if (parsed.Count == 0)
        {
            diagnostics.Reject(ContentDiagnosticCodes.LayoutInvalid, "A composite layout must contain at least one voxel.", jsonPath);
            return false;
        }

        voxels = parsed;
        return true;
    }

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
            diagnostics.Reject(ContentDiagnosticCodes.LayoutInvalid, "Each composite voxel must be an object.", jsonPath);
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
                    if (!StrictJson.TryGetString(property.Value, diagnostics, fieldPath, ContentDiagnosticCodes.LocalIdInvalid, out definition))
                        return false;
                    break;
                case "orientation":
                    if (!StrictJson.TryGetString(property.Value, diagnostics, fieldPath, ContentDiagnosticCodes.OrientationInvalid, out orientationText))
                        return false;
                    break;
                default:
                    diagnostics.Reject(ContentDiagnosticCodes.UnknownField, "Unknown field.", fieldPath);
                    return false;
            }
        }

        if (x is null || y is null || z is null)
        {
            diagnostics.Reject(ContentDiagnosticCodes.LayoutInvalid, "Composite voxels require integer x, y, and z.", jsonPath);
            return false;
        }
        if (definition is null || !VoxelDefinitionId.TryResolve(definition, packageNamespace, out var definitionId))
        {
            diagnostics.Reject(ContentDiagnosticCodes.LocalIdInvalid, "definition must be a local id or namespace:local-id.", jsonPath + "/definition");
            return false;
        }
        if (orientationText is null || !VoxelOrientation.TryParse(orientationText, out var orientation))
        {
            diagnostics.Reject(ContentDiagnosticCodes.OrientationInvalid, "orientation is not a valid orthogonal encoding.", jsonPath + "/orientation");
            return false;
        }

        spec = new CompositeVoxelSpec(new LocalVoxelCoord(x.Value, y.Value, z.Value), definitionId, orientation);
        return true;
    }

    private static bool TryGetInt(JsonElement element, DiagnosticBag diagnostics, string jsonPath, out int value)
    {
        value = 0;
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out value))
        {
            diagnostics.Reject(ContentDiagnosticCodes.LayoutInvalid, "Expected an integer.", jsonPath);
            return false;
        }
        return true;
    }
}
