using System.Text.Json;
using Train.Domain.Composites;
using Train.Domain.Voxels;

namespace Train.Content;

internal sealed class ParsedCompositeDocument
{
    public string? DisplayName { get; }
    public IReadOnlyList<ParsedCompositeArchetype> Archetypes { get; }
    public IReadOnlyList<ParsedCompositeDefinition> Composites { get; }

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

internal sealed class ParsedCompositeArchetype
{
    public string LocalId { get; }
    public ConstructionRules Construction { get; }
    public IdentityRetentionRules IdentityRetention { get; }
    public string SourceFile { get; }

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

internal sealed class ParsedCompositeDefinition
{
    public string LocalId { get; }
    public string Name { get; }
    public string Description { get; }
    public string ArchetypeRaw { get; }
    public VehicleSizeClass SizeClass { get; }
    public IReadOnlyList<CompositeVoxelSpec> Voxels { get; }
    public IReadOnlyList<CompositeContactAnchor> Contacts { get; }
    public string SourceFile { get; }

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

internal static class CompositesJsonParser
{
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
            version != 1)
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

    public static CompositeArchetypeId ResolveArchetypeId(string raw, string packageNamespace) =>
        raw.Contains(':', StringComparison.Ordinal)
            ? CompositeArchetypeId.Parse(raw)
            : new CompositeArchetypeId(packageNamespace, raw);

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
