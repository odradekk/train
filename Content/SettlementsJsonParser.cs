using System.Text.Json;
using Train.Domain.Goods;
using Train.Domain.Settlements;

namespace Train.Content;

internal sealed class ParsedSettlement
{
    public string LocalId { get; }
    public string Type { get; }
    public string Name { get; }
    public MapPosition Position { get; }
    public int Population { get; }
    public IReadOnlyList<GoodId> NaturalResources { get; }
    public string SourceFile { get; }

    public ParsedSettlement(
        string localId,
        string type,
        string name,
        MapPosition position,
        int population,
        IReadOnlyList<GoodId> naturalResources,
        string sourceFile)
    {
        LocalId = localId;
        Type = type;
        Name = name;
        Position = position;
        Population = population;
        NaturalResources = naturalResources;
        SourceFile = sourceFile;
    }
}

internal static class SettlementsJsonParser
{
    public const string CityType = "city";

    public static IReadOnlyList<ParsedSettlement> Parse(
        byte[] utf8,
        string @namespace,
        string sourceFile,
        DiagnosticBag diagnostics)
    {
        diagnostics.Source = sourceFile;
        if (utf8.Length > ContentLimits.GoodsFileMaxBytes)
        {
            diagnostics.Warn(ContentDiagnosticCodes.FileTooLarge, "Settlements JSON exceeds the 1 MiB limit.");
            return Array.Empty<ParsedSettlement>();
        }

        if (StrictJson.HasDuplicateProperties(utf8, diagnostics, sourceFile))
            return Array.Empty<ParsedSettlement>();

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(utf8, StrictJson.DocumentOptions);
        }
        catch (JsonException exception)
        {
            diagnostics.Warn(ContentDiagnosticCodes.InvalidJson, exception.Message);
            return Array.Empty<ParsedSettlement>();
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                diagnostics.Warn(ContentDiagnosticCodes.InvalidJson, "Settlements file must be a JSON object.", "$");
                return Array.Empty<ParsedSettlement>();
            }

            return ParseDocument(document.RootElement, @namespace, sourceFile, diagnostics);
        }
    }

    private static IReadOnlyList<ParsedSettlement> ParseDocument(
        JsonElement root,
        string @namespace,
        string sourceFile,
        DiagnosticBag diagnostics)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (property.Name is not "schema_version" and not "settlements")
            {
                diagnostics.Warn(ContentDiagnosticCodes.UnknownField, "Unknown field.", "/" + property.Name);
                return Array.Empty<ParsedSettlement>();
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
            return Array.Empty<ParsedSettlement>();
        }

        if (!root.TryGetProperty("settlements", out var settlementsElement) ||
            settlementsElement.ValueKind != JsonValueKind.Array)
        {
            diagnostics.Warn(ContentDiagnosticCodes.InvalidJson, "settlements must be an array.", "/settlements");
            return Array.Empty<ParsedSettlement>();
        }

        var settlements = new List<ParsedSettlement>();
        var index = 0;
        foreach (var item in settlementsElement.EnumerateArray())
        {
            var path = $"/settlements/{index}";
            if (TryParseSettlement(item, @namespace, sourceFile, path, diagnostics, out var settlement))
                settlements.Add(settlement);
            index++;
        }

        return settlements;
    }

    private static bool TryParseSettlement(
        JsonElement element,
        string @namespace,
        string sourceFile,
        string jsonPath,
        DiagnosticBag diagnostics,
        out ParsedSettlement settlement)
    {
        settlement = null!;
        if (element.ValueKind != JsonValueKind.Object)
        {
            diagnostics.Warn(ContentDiagnosticCodes.InvalidJson, "Each settlement must be an object.", jsonPath);
            return false;
        }

        string? localId = null;
        string? type = null;
        string? name = null;
        int? population = null;
        MapPosition? position = null;
        IReadOnlyList<GoodId>? resources = null;

        foreach (var property in element.EnumerateObject())
        {
            var fieldPath = jsonPath + "/" + property.Name;
            switch (property.Name)
            {
                case "id":
                    if (!StrictJson.TryGetString(property.Value, diagnostics, fieldPath, ContentDiagnosticCodes.LocalIdInvalid, out localId))
                        return false;
                    break;
                case "type":
                    if (!StrictJson.TryGetString(property.Value, diagnostics, fieldPath, ContentDiagnosticCodes.CategoryUnsupported, out type))
                        return false;
                    break;
                case "name":
                    if (!StrictJson.TryGetString(property.Value, diagnostics, fieldPath, ContentDiagnosticCodes.NameInvalid, out name))
                        return false;
                    break;
                case "population":
                    if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out var parsedPopulation))
                    {
                        diagnostics.Warn(ContentDiagnosticCodes.PopulationInvalid, "Population must be an integer.", fieldPath);
                        return false;
                    }

                    population = parsedPopulation;
                    break;
                case "position":
                    if (!TryParsePosition(property.Value, fieldPath, diagnostics, out var parsedPosition))
                        return false;
                    position = parsedPosition;
                    break;
                case "natural_resources":
                    if (!TryParseResources(property.Value, fieldPath, diagnostics, out resources))
                        return false;
                    break;
                default:
                    diagnostics.Warn(ContentDiagnosticCodes.UnknownField, "Unknown field.", fieldPath);
                    return false;
            }
        }

        if (localId is null || !GoodId.IsValidPart(localId))
        {
            diagnostics.Warn(ContentDiagnosticCodes.LocalIdInvalid, "Local id is invalid.", jsonPath + "/id");
            return false;
        }

        if (type != CityType)
        {
            diagnostics.Warn(ContentDiagnosticCodes.CategoryUnsupported, "Unsupported settlement type.", jsonPath + "/type");
            return false;
        }

        var trimmedName = name?.Trim() ?? "";
        if (trimmedName.Length == 0)
        {
            diagnostics.Warn(ContentDiagnosticCodes.NameInvalid, "Name cannot be blank.", jsonPath + "/name");
            return false;
        }

        if (population is null or <= 0)
        {
            diagnostics.Warn(ContentDiagnosticCodes.PopulationInvalid, "Population must be a positive integer.", jsonPath + "/population");
            return false;
        }

        if (position is null)
        {
            diagnostics.Warn(ContentDiagnosticCodes.PositionInvalid, "Position is required.", jsonPath + "/position");
            return false;
        }

        if (resources is null)
        {
            diagnostics.Warn(ContentDiagnosticCodes.InvalidJson, "natural_resources must be an array.", jsonPath + "/natural_resources");
            return false;
        }

        _ = @namespace;
        settlement = new ParsedSettlement(localId, type, trimmedName, position.Value, population.Value, resources, sourceFile);
        return true;
    }

    private static bool TryParsePosition(
        JsonElement element,
        string jsonPath,
        DiagnosticBag diagnostics,
        out MapPosition position)
    {
        position = default;
        if (element.ValueKind != JsonValueKind.Object)
        {
            diagnostics.Warn(ContentDiagnosticCodes.PositionInvalid, "position must be an object.", jsonPath);
            return false;
        }

        int? x = null;
        int? y = null;
        foreach (var property in element.EnumerateObject())
        {
            var fieldPath = jsonPath + "/" + property.Name;
            switch (property.Name)
            {
                case "x":
                    if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out var parsedX))
                    {
                        diagnostics.Warn(ContentDiagnosticCodes.PositionInvalid, "Position coordinates must be integers.", fieldPath);
                        return false;
                    }

                    x = parsedX;
                    break;
                case "y":
                    if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out var parsedY))
                    {
                        diagnostics.Warn(ContentDiagnosticCodes.PositionInvalid, "Position coordinates must be integers.", fieldPath);
                        return false;
                    }

                    y = parsedY;
                    break;
                default:
                    diagnostics.Warn(ContentDiagnosticCodes.UnknownField, "Unknown field.", fieldPath);
                    return false;
            }
        }

        if (x is null || y is null)
        {
            diagnostics.Warn(ContentDiagnosticCodes.PositionInvalid, "position requires integer x and y.", jsonPath);
            return false;
        }

        position = new MapPosition(x.Value, y.Value);
        return true;
    }

    private static bool TryParseResources(
        JsonElement element,
        string jsonPath,
        DiagnosticBag diagnostics,
        out IReadOnlyList<GoodId> resources)
    {
        resources = Array.Empty<GoodId>();
        if (element.ValueKind != JsonValueKind.Array)
        {
            diagnostics.Warn(ContentDiagnosticCodes.InvalidJson, "natural_resources must be an array.", jsonPath);
            return false;
        }

        var parsed = new List<GoodId>();
        var seen = new HashSet<GoodId>();
        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            var itemPath = $"{jsonPath}/{index}";
            if (!StrictJson.TryGetString(item, diagnostics, itemPath, ContentDiagnosticCodes.UnresolvedGoodReference, out var raw))
                return false;

            GoodId id;
            try
            {
                id = GoodId.Parse(raw);
            }
            catch (ArgumentException)
            {
                diagnostics.Warn(
                    ContentDiagnosticCodes.UnresolvedGoodReference,
                    "Natural resource ids must be fully qualified namespace:local values.",
                    itemPath);
                return false;
            }

            if (!seen.Add(id))
            {
                diagnostics.Warn(
                    ContentDiagnosticCodes.DuplicateNaturalResource,
                    "Duplicate natural resource id.",
                    itemPath);
                return false;
            }

            parsed.Add(id);
            index++;
        }

        resources = parsed;
        return true;
    }
}
