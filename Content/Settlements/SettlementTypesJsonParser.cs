using System.Text.Json;
using Train.Content.Voxels;
namespace Train.Content.Settlements;

internal static class SettlementTypesJsonParser
{
    private const int SchemaVersion = 1;

    public static IReadOnlyList<SettlementTypeDefinition>? Parse(
        byte[] utf8, string packageNamespace, string sourceFile, DiagnosticBag diagnostics)
    {
        return ContentJson.ParseDefinitions(utf8, sourceFile, SchemaVersion, "settlement_types", diagnostics,
            (element, path) => ParseDefinition(element, packageNamespace, path, diagnostics));
    }

    private static SettlementTypeDefinition? ParseDefinition(JsonElement element, string packageNamespace, string path, DiagnosticBag diagnostics)
    {
        if (!ContentJson.CheckFields(element, path, diagnostics, "id", "name", "supply", "demand", "preferred_terrain",
                "weight", "population_min", "population_max", "initial_platform_count"))
            return null;
        var localId = ContentJson.ReadLocalId(element, "id", path, diagnostics);
        var rawName = ContentJson.ReadString(element, "name", path, diagnostics, ContentDiagnosticCodes.NameInvalid);
        if (localId is null || rawName is null ||
            !StrictJson.TryNormalizeText(rawName, VoxelDefinition.NameMinLength, VoxelDefinition.NameMaxLength,
                diagnostics, path + "/name", ContentDiagnosticCodes.NameInvalid, out var name))
            return null;
        var supply = ParseQuantities(element, "supply", packageNamespace, path, diagnostics);
        var demand = ParseQuantities(element, "demand", packageNamespace, path, diagnostics);
        var terrain = ParseTerrain(element, path, diagnostics);
        var weight = ContentJson.ReadInt32(element, "weight", 1, path, diagnostics);
        var population = ParsePopulation(element, path, diagnostics);
        var platforms = ContentJson.ReadInt32(element, "initial_platform_count", 0, path, diagnostics);
        if (supply is null || demand is null || terrain is null || weight is null || population is null || platforms is null)
            return null;
        return new SettlementTypeDefinition(new SettlementTypeDefinitionId(packageNamespace, localId), name,
            supply, demand, terrain, weight.Value, population, platforms.Value);
    }

    private static IReadOnlyDictionary<VoxelDefinitionId, SettlementMarketQuantity>? ParseQuantities(JsonElement element,
        string name, string packageNamespace, string path, DiagnosticBag diagnostics)
    {
        var items = ContentJson.ReadArray(element, name, path, diagnostics);
        if (items is null)
            return null;
        var quantities = new Dictionary<VoxelDefinitionId, SettlementMarketQuantity>();
        var index = 0;
        foreach (var item in items.Value.EnumerateArray())
        {
            var itemPath = $"{path}/{name}/{index++}";
            if (!ContentJson.CheckFields(item, itemPath, diagnostics, "definition", "capacity", "restore_per_tick"))
                return null;
            if (ContentJson.ReadVoxelReference(item, packageNamespace, itemPath, diagnostics) is not { } id ||
                ContentJson.ReadInt32(item, "capacity", 1, itemPath, diagnostics) is not { } capacity ||
                ContentJson.ReadInt32(item, "restore_per_tick", 0, itemPath, diagnostics) is not { } restore)
                return null;
            if (!quantities.TryAdd(id, new SettlementMarketQuantity(capacity, restore)))
            {
                diagnostics.Reject(ContentDiagnosticCodes.FieldInvalid, $"Duplicate good '{id}'.", itemPath + "/definition");
                return null;
            }
        }
        return quantities;
    }

    private static IReadOnlySet<string>? ParseTerrain(JsonElement element, string path, DiagnosticBag diagnostics)
    {
        var items = ContentJson.ReadArray(element, "preferred_terrain", path, diagnostics);
        if (items is null)
            return null;
        var terrain = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var item in items.Value.EnumerateArray())
        {
            var itemPath = $"{path}/preferred_terrain/{index++}";
            var identity = ContentJson.ReadFullIdentity(item, itemPath, diagnostics);
            if (identity is null)
                return null;
            if (!terrain.Add(identity))
            {
                diagnostics.Reject(ContentDiagnosticCodes.FieldInvalid, $"Duplicate terrain identity '{identity}'.", itemPath);
                return null;
            }
        }
        return terrain;
    }

    private static SettlementPopulationRange? ParsePopulation(JsonElement element, string path, DiagnosticBag diagnostics)
    {
        if (ContentJson.ReadInt32(element, "population_min", 1, path, diagnostics) is not { } min ||
            ContentJson.ReadInt32(element, "population_max", 1, path, diagnostics) is not { } max)
            return null;
        if (max >= min)
            return new SettlementPopulationRange(min, max);
        diagnostics.Reject(ContentDiagnosticCodes.FieldInvalid, "population_max must be at least population_min.", path + "/population_max");
        return null;
    }
}
