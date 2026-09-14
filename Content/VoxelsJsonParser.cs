using System.Text.Json;
using Train.Domain.Behaviors;
using Train.Domain.Voxels;

namespace Train.Content;

internal sealed class ParsedVoxel
{
    public string LocalId { get; }
    public string Name { get; }
    public string Description { get; }
    public long Value { get; }
    public double Mass { get; }
    public int MaxDurability { get; }
    public IReadOnlyList<string> Tags { get; }
    public IReadOnlyList<string> Behaviors { get; }
    public PropulsionParameters? Propulsion { get; }
    public VoxelOrientationConstraint AllowedOrientations { get; }
    public VoxelPresentation Presentation { get; }
    public VoxelCollision Collision { get; }
    public string SourceFile { get; }

    public ParsedVoxel(
        string localId,
        string name,
        string description,
        long value,
        double mass,
        int maxDurability,
        IReadOnlyList<string> tags,
        IReadOnlyList<string> behaviors,
        PropulsionParameters? propulsion,
        VoxelOrientationConstraint allowedOrientations,
        VoxelPresentation presentation,
        VoxelCollision collision,
        string sourceFile)
    {
        LocalId = localId;
        Name = name;
        Description = description;
        Value = value;
        Mass = mass;
        MaxDurability = maxDurability;
        Tags = tags;
        Behaviors = behaviors;
        Propulsion = propulsion;
        AllowedOrientations = allowedOrientations;
        Presentation = presentation;
        Collision = collision;
        SourceFile = sourceFile;
    }
}

internal static class VoxelsJsonParser
{
    public static IReadOnlyList<ParsedVoxel> Parse(
        byte[] utf8,
        string sourceFile,
        DiagnosticBag diagnostics)
    {
        diagnostics.Source = sourceFile;
        if (utf8.Length > ContentLimits.JsonFileMaxBytes)
        {
            diagnostics.Warn(ContentDiagnosticCodes.FileTooLarge, "Voxel JSON exceeds the 1 MiB limit.");
            return Array.Empty<ParsedVoxel>();
        }

        if (StrictJson.HasDuplicateProperties(utf8, diagnostics, sourceFile))
            return Array.Empty<ParsedVoxel>();

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(utf8, StrictJson.DocumentOptions);
        }
        catch (JsonException exception)
        {
            diagnostics.Warn(ContentDiagnosticCodes.InvalidJson, exception.Message);
            return Array.Empty<ParsedVoxel>();
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                diagnostics.Warn(ContentDiagnosticCodes.InvalidJson, "Voxel file must be a JSON object.", "$");
                return Array.Empty<ParsedVoxel>();
            }

            return ParseDocument(document.RootElement, sourceFile, diagnostics);
        }
    }

    private static IReadOnlyList<ParsedVoxel> ParseDocument(
        JsonElement root,
        string sourceFile,
        DiagnosticBag diagnostics)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (property.Name is not "schema_version" and not "voxels")
            {
                diagnostics.Warn(ContentDiagnosticCodes.UnknownField, "Unknown field.", "/" + property.Name);
                return Array.Empty<ParsedVoxel>();
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
            return Array.Empty<ParsedVoxel>();
        }

        if (!root.TryGetProperty("voxels", out var voxelsElement) || voxelsElement.ValueKind != JsonValueKind.Array)
        {
            diagnostics.Warn(ContentDiagnosticCodes.InvalidJson, "voxels must be an array.", "/voxels");
            return Array.Empty<ParsedVoxel>();
        }

        var voxels = new List<ParsedVoxel>();
        var index = 0;
        foreach (var item in voxelsElement.EnumerateArray())
        {
            var path = $"/voxels/{index}";
            if (TryParseVoxel(item, sourceFile, path, diagnostics, out var voxel))
                voxels.Add(voxel);
            index++;
        }

        return voxels;
    }

    private static bool TryParseVoxel(
        JsonElement element,
        string sourceFile,
        string jsonPath,
        DiagnosticBag diagnostics,
        out ParsedVoxel voxel)
    {
        voxel = null!;
        if (element.ValueKind != JsonValueKind.Object)
        {
            diagnostics.Warn(ContentDiagnosticCodes.InvalidJson, "Each voxel must be an object.", jsonPath);
            return false;
        }

        string? localId = null;
        string? name = null;
        string? description = null;
        long? value = null;
        double? mass = null;
        int? maxDurability = null;
        IReadOnlyList<string>? tags = null;
        IReadOnlyList<string>? behaviors = null;
        PropulsionParameters? propulsion = null;
        string? orientationsText = null;
        JsonElement? presentation = null;
        JsonElement? collision = null;

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
                case "value":
                    if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt64(out var parsedValue))
                    {
                        diagnostics.Warn(ContentDiagnosticCodes.ValueInvalid, "Value must be an integer.", fieldPath);
                        return false;
                    }

                    value = parsedValue;
                    break;
                case "mass":
                    if (!TryGetFiniteDouble(property.Value, diagnostics, fieldPath, ContentDiagnosticCodes.MassInvalid, out var parsedMass))
                        return false;
                    mass = parsedMass;
                    break;
                case "max_durability":
                    if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out var parsedDurability))
                    {
                        diagnostics.Warn(ContentDiagnosticCodes.MaxDurabilityInvalid, "Max durability must be an integer.", fieldPath);
                        return false;
                    }

                    maxDurability = parsedDurability;
                    break;
                case "tags":
                    if (!TryParseTags(property.Value, fieldPath, diagnostics, out tags))
                        return false;
                    break;
                case "behaviors":
                    if (!TryParseBehaviors(property.Value, fieldPath, diagnostics, out behaviors, out propulsion))
                        return false;
                    break;
                case "allowed_orientations":
                    if (!StrictJson.TryGetString(property.Value, diagnostics, fieldPath, ContentDiagnosticCodes.OrientationInvalid, out orientationsText))
                        return false;
                    break;
                case "presentation":
                    presentation = property.Value;
                    break;
                case "collision":
                    collision = property.Value;
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

        if (!StrictJson.TryNormalizeText(name, VoxelDefinition.NameMinLength, VoxelDefinition.NameMaxLength, diagnostics, jsonPath + "/name", ContentDiagnosticCodes.NameInvalid, out var normalizedName))
            return false;
        if (!StrictJson.TryNormalizeText(description, VoxelDefinition.DescriptionMinLength, VoxelDefinition.DescriptionMaxLength, diagnostics, jsonPath + "/description", ContentDiagnosticCodes.DescriptionInvalid, out var normalizedDescription))
            return false;

        if (value is null or < VoxelDefinition.ValueMin or > VoxelDefinition.ValueMax)
        {
            diagnostics.Warn(ContentDiagnosticCodes.ValueInvalid, "Value is out of range.", jsonPath + "/value");
            return false;
        }

        if (mass is null ||
            double.IsNaN(mass.Value) ||
            double.IsInfinity(mass.Value) ||
            mass.Value <= VoxelDefinition.MassMinExclusive ||
            mass.Value > VoxelDefinition.MassMax)
        {
            diagnostics.Warn(ContentDiagnosticCodes.MassInvalid, "Mass is out of range.", jsonPath + "/mass");
            return false;
        }

        if (maxDurability is null or < VoxelDefinition.MaxDurabilityMin or > VoxelDefinition.MaxDurabilityMax)
        {
            diagnostics.Warn(ContentDiagnosticCodes.MaxDurabilityInvalid, "Max durability is out of range.", jsonPath + "/max_durability");
            return false;
        }

        if (tags is null)
        {
            diagnostics.Warn(ContentDiagnosticCodes.TagInvalid, "tags must be an array.", jsonPath + "/tags");
            return false;
        }

        if (behaviors is null)
        {
            diagnostics.Warn(ContentDiagnosticCodes.BehaviorUnregistered, "behaviors must be an array.", jsonPath + "/behaviors");
            return false;
        }

        if (behaviors.Any(behavior => !BehaviorRegistry.IsRegistered(behavior)))
        {
            diagnostics.Warn(ContentDiagnosticCodes.BehaviorUnregistered, "Unknown or unregistered behavior.", jsonPath + "/behaviors");
            return false;
        }

        if (!VoxelDefinition.TryParseOrientations(orientationsText, out var orientations))
        {
            diagnostics.Warn(ContentDiagnosticCodes.OrientationInvalid, "Unsupported orientation constraint.", jsonPath + "/allowed_orientations");
            return false;
        }

        if (presentation is null || !TryParsePresentation(presentation.Value, jsonPath + "/presentation", diagnostics, out var parsedPresentation))
        {
            if (presentation is null)
                diagnostics.Warn(ContentDiagnosticCodes.PresentationRequired, "presentation is required.", jsonPath + "/presentation");
            return false;
        }

        if (collision is null || !TryParseCollision(collision.Value, jsonPath + "/collision", diagnostics, out var parsedCollision))
        {
            if (collision is null)
                diagnostics.Warn(ContentDiagnosticCodes.CollisionInvalid, "collision is required.", jsonPath + "/collision");
            return false;
        }

        voxel = new ParsedVoxel(
            localId,
            normalizedName,
            normalizedDescription,
            value.Value,
            mass.Value,
            maxDurability.Value,
            tags,
            behaviors,
            propulsion,
            orientations,
            parsedPresentation,
            parsedCollision,
            sourceFile);
        return true;
    }

    private static bool TryParseTags(
        JsonElement element,
        string jsonPath,
        DiagnosticBag diagnostics,
        out IReadOnlyList<string> tags)
    {
        tags = Array.Empty<string>();
        if (element.ValueKind != JsonValueKind.Array)
        {
            diagnostics.Warn(ContentDiagnosticCodes.TagInvalid, "tags must be an array.", jsonPath);
            return false;
        }

        var parsed = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            var itemPath = $"{jsonPath}/{index}";
            if (!StrictJson.TryGetString(item, diagnostics, itemPath, ContentDiagnosticCodes.TagInvalid, out var raw))
                return false;
            try
            {
                var tag = VoxelDefinition.NormalizeTag(raw, "tag");
                if (!seen.Add(tag))
                {
                    diagnostics.Warn(ContentDiagnosticCodes.TagInvalid, "Duplicate tag.", itemPath);
                    return false;
                }

                parsed.Add(tag);
            }
            catch (ArgumentException)
            {
                diagnostics.Warn(ContentDiagnosticCodes.TagInvalid, "Tag is invalid.", itemPath);
                return false;
            }

            index++;
        }

        tags = parsed;
        return true;
    }

    private static bool TryParseBehaviors(
        JsonElement element,
        string jsonPath,
        DiagnosticBag diagnostics,
        out IReadOnlyList<string> behaviors,
        out PropulsionParameters? propulsion)
    {
        behaviors = Array.Empty<string>();
        propulsion = null;
        if (element.ValueKind != JsonValueKind.Array)
        {
            diagnostics.Warn(ContentDiagnosticCodes.BehaviorUnregistered, "behaviors must be an array.", jsonPath);
            return false;
        }

        var parsed = new List<string>();
        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            var itemPath = $"{jsonPath}/{index}";
            if (item.ValueKind != JsonValueKind.Object)
            {
                diagnostics.Warn(ContentDiagnosticCodes.BehaviorUnregistered, "Each behavior must be an object.", itemPath);
                return false;
            }

            string? type = null;
            double? targetSpeed = null;
            double? acceleration = null;
            if (item.TryGetProperty("type", out var typeElement) &&
                !StrictJson.TryGetString(typeElement, diagnostics, itemPath + "/type", ContentDiagnosticCodes.BehaviorUnregistered, out type))
                return false;

            foreach (var property in item.EnumerateObject())
            {
                if (property.Name == "type")
                    continue;

                if (type == BehaviorTypes.Propulsion && property.Name == "target_speed")
                {
                    if (!TryGetFiniteDouble(property.Value, diagnostics, itemPath + "/target_speed", ContentDiagnosticCodes.BehaviorUnregistered, out var parsedTarget))
                        return false;
                    targetSpeed = parsedTarget;
                    continue;
                }

                if (type == BehaviorTypes.Propulsion && property.Name == "acceleration")
                {
                    if (!TryGetFiniteDouble(property.Value, diagnostics, itemPath + "/acceleration", ContentDiagnosticCodes.BehaviorUnregistered, out var parsedAcceleration))
                        return false;
                    acceleration = parsedAcceleration;
                    continue;
                }

                diagnostics.Warn(ContentDiagnosticCodes.UnknownField, "Unknown field.", itemPath + "/" + property.Name);
                return false;
            }

            if (string.IsNullOrEmpty(type))
            {
                diagnostics.Warn(ContentDiagnosticCodes.BehaviorUnregistered, "Behavior type is required.", itemPath + "/type");
                return false;
            }

            if (type == BehaviorTypes.Propulsion)
            {
                if (propulsion is not null)
                {
                    diagnostics.Warn(ContentDiagnosticCodes.BehaviorUnregistered, "Only one propulsion behavior is allowed.", itemPath);
                    return false;
                }

                if (!BehaviorRegistry.TryNormalizePropulsion(targetSpeed, acceleration, out var parameters))
                {
                    diagnostics.Warn(
                        ContentDiagnosticCodes.BehaviorUnregistered,
                        "Propulsion parameters must be finite and strictly positive.",
                        itemPath);
                    return false;
                }

                propulsion = parameters;
            }
            else if (targetSpeed is not null || acceleration is not null)
            {
                diagnostics.Warn(ContentDiagnosticCodes.UnknownField, "Unknown field.", itemPath);
                return false;
            }

            parsed.Add(type);
            index++;
        }

        behaviors = parsed;
        return true;
    }

    private static bool TryParsePresentation(
        JsonElement element,
        string jsonPath,
        DiagnosticBag diagnostics,
        out VoxelPresentation presentation)
    {
        presentation = null!;
        if (element.ValueKind != JsonValueKind.Object)
        {
            diagnostics.Warn(ContentDiagnosticCodes.PresentationRequired, "presentation must be an object.", jsonPath);
            return false;
        }

        string? kindText = null, icon = null, top = null, side = null, bottom = null;
        foreach (var property in element.EnumerateObject())
        {
            var fieldPath = jsonPath + "/" + property.Name;
            switch (property.Name)
            {
                case "kind":
                    if (!StrictJson.TryGetString(property.Value, diagnostics, fieldPath, ContentDiagnosticCodes.PresentationInvalid, out kindText))
                        return false;
                    break;
                case "icon":
                    if (!TryGetResourcePath(property.Value, diagnostics, fieldPath, out icon))
                        return false;
                    break;
                case "voxel_top":
                    if (!TryGetResourcePath(property.Value, diagnostics, fieldPath, out top))
                        return false;
                    break;
                case "voxel_side":
                    if (!TryGetResourcePath(property.Value, diagnostics, fieldPath, out side))
                        return false;
                    break;
                case "voxel_bottom":
                    if (!TryGetResourcePath(property.Value, diagnostics, fieldPath, out bottom))
                        return false;
                    break;
                default:
                    diagnostics.Warn(ContentDiagnosticCodes.UnknownField, "Unknown field.", fieldPath);
                    return false;
            }
        }

        if (!VoxelDefinition.TryParsePresentationKind(kindText, out var kind))
        {
            diagnostics.Warn(ContentDiagnosticCodes.PresentationInvalid, "Unsupported presentation kind.", jsonPath + "/kind");
            return false;
        }

        if (icon is null || top is null || side is null || bottom is null)
        {
            diagnostics.Warn(ContentDiagnosticCodes.PresentationRequired, "cube presentation requires icon, voxel_top, voxel_side, and voxel_bottom.", jsonPath);
            return false;
        }

        presentation = new VoxelPresentation(kind, icon, top, side, bottom);
        return true;
    }

    private static bool TryParseCollision(
        JsonElement element,
        string jsonPath,
        DiagnosticBag diagnostics,
        out VoxelCollision collision)
    {
        collision = null!;
        if (element.ValueKind != JsonValueKind.Object)
        {
            diagnostics.Warn(ContentDiagnosticCodes.CollisionInvalid, "collision must be an object.", jsonPath);
            return false;
        }

        string? kindText = null;
        JsonElement? offset = null;
        JsonElement? size = null;
        foreach (var property in element.EnumerateObject())
        {
            var fieldPath = jsonPath + "/" + property.Name;
            switch (property.Name)
            {
                case "kind":
                    if (!StrictJson.TryGetString(property.Value, diagnostics, fieldPath, ContentDiagnosticCodes.CollisionInvalid, out kindText))
                        return false;
                    break;
                case "offset":
                    offset = property.Value;
                    break;
                case "size":
                    size = property.Value;
                    break;
                default:
                    diagnostics.Warn(ContentDiagnosticCodes.UnknownField, "Unknown field.", fieldPath);
                    return false;
            }
        }

        if (!VoxelDefinition.TryParseCollisionKind(kindText, out var kind))
        {
            diagnostics.Warn(ContentDiagnosticCodes.CollisionInvalid, "Unsupported collision kind.", jsonPath + "/kind");
            return false;
        }

        if (kind is VoxelCollisionKind.None or VoxelCollisionKind.FullBox)
        {
            if (offset is not null || size is not null)
            {
                diagnostics.Warn(ContentDiagnosticCodes.CollisionInvalid, "none and full_box cannot include a local box.", jsonPath);
                return false;
            }

            collision = new VoxelCollision(kind);
            return true;
        }

        if (offset is null || size is null)
        {
            diagnostics.Warn(ContentDiagnosticCodes.CollisionInvalid, "local_box requires offset and size.", jsonPath);
            return false;
        }

        if (!TryParseLocalVector(offset.Value, jsonPath + "/offset", diagnostics, out var offsetX, out var offsetY, out var offsetZ) ||
            !TryParseLocalVector(size.Value, jsonPath + "/size", diagnostics, out var sizeX, out var sizeY, out var sizeZ))
            return false;

        if (!VoxelCollision.TryCreateLocalBox(offsetX, offsetY, offsetZ, sizeX, sizeY, sizeZ, out collision))
        {
            diagnostics.Warn(
                ContentDiagnosticCodes.CollisionInvalid,
                "local_box must be finite, strictly positive, and a distinguishable interval inside the unit cell.",
                jsonPath);
            return false;
        }

        if (!VoxelCollisionAdapter.CanDerivePositiveVolume(collision))
        {
            diagnostics.Warn(
                ContentDiagnosticCodes.CollisionInvalid,
                "local_box must keep a positive volume after the collision adapter conversion.",
                jsonPath);
            return false;
        }

        return true;
    }

    private static bool TryParseLocalVector(
        JsonElement element,
        string jsonPath,
        DiagnosticBag diagnostics,
        out double x,
        out double y,
        out double z)
    {
        x = 0;
        y = 0;
        z = 0;
        if (element.ValueKind != JsonValueKind.Object)
        {
            diagnostics.Warn(ContentDiagnosticCodes.CollisionInvalid, "Local box vector must be an object.", jsonPath);
            return false;
        }

        double? parsedX = null, parsedY = null, parsedZ = null;
        foreach (var property in element.EnumerateObject())
        {
            var fieldPath = jsonPath + "/" + property.Name;
            switch (property.Name)
            {
                case "x":
                    if (!TryGetFiniteDouble(property.Value, diagnostics, fieldPath, ContentDiagnosticCodes.CollisionInvalid, out var valueX))
                        return false;
                    parsedX = valueX;
                    break;
                case "y":
                    if (!TryGetFiniteDouble(property.Value, diagnostics, fieldPath, ContentDiagnosticCodes.CollisionInvalid, out var valueY))
                        return false;
                    parsedY = valueY;
                    break;
                case "z":
                    if (!TryGetFiniteDouble(property.Value, diagnostics, fieldPath, ContentDiagnosticCodes.CollisionInvalid, out var valueZ))
                        return false;
                    parsedZ = valueZ;
                    break;
                default:
                    diagnostics.Warn(ContentDiagnosticCodes.UnknownField, "Unknown field.", fieldPath);
                    return false;
            }
        }

        if (parsedX is null || parsedY is null || parsedZ is null)
        {
            diagnostics.Warn(ContentDiagnosticCodes.CollisionInvalid, "Local box vector requires x, y, and z.", jsonPath);
            return false;
        }

        x = parsedX.Value;
        y = parsedY.Value;
        z = parsedZ.Value;
        return true;
    }

    private static bool TryGetResourcePath(JsonElement element, DiagnosticBag diagnostics, string jsonPath, out string path)
    {
        path = "";
        if (!StrictJson.TryGetString(element, diagnostics, jsonPath, ContentDiagnosticCodes.PathInvalid, out var raw))
            return false;
        if (!ContentPath.TryNormalize(raw, out var normalized) || !ContentPath.IsPngPath(normalized))
        {
            diagnostics.Warn(ContentDiagnosticCodes.PathInvalid, "Visual path is not a safe relative PNG path.", jsonPath);
            return false;
        }

        path = normalized;
        return true;
    }

    private static bool TryGetFiniteDouble(
        JsonElement element,
        DiagnosticBag diagnostics,
        string jsonPath,
        string code,
        out double value)
    {
        value = 0;
        var message = code == ContentDiagnosticCodes.MassInvalid
            ? "Mass must be a finite number."
            : "Number must be finite.";
        if (element.ValueKind != JsonValueKind.Number)
        {
            diagnostics.Warn(code, message, jsonPath);
            return false;
        }

        if (!element.TryGetDouble(out value) || double.IsNaN(value) || double.IsInfinity(value))
        {
            diagnostics.Warn(code, message, jsonPath);
            return false;
        }

        return true;
    }
}
