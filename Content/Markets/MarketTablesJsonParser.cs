using System.Text.Json;
using Train.Content.Voxels;
namespace Train.Content.Markets;

internal static class MarketTablesJsonParser
{
    private const int SchemaVersion = 1;

    public static IReadOnlyList<MarketTableDefinition>? Parse(
        byte[] utf8, string packageNamespace, string sourceFile, DiagnosticBag diagnostics)
    {
        return ContentJson.ParseDefinitions(utf8, sourceFile, SchemaVersion, "market_tables", diagnostics,
            (element, path) => ParseDefinition(element, packageNamespace, path, diagnostics));
    }

    private static MarketTableDefinition? ParseDefinition(JsonElement element, string packageNamespace, string path, DiagnosticBag diagnostics)
    {
        if (!ContentJson.CheckFields(element, path, diagnostics, "id", "goods"))
            return null;
        var localId = ContentJson.ReadLocalId(element, "id", path, diagnostics);
        if (localId is null)
            return null;
        var goods = ParseGoods(element, packageNamespace, path, diagnostics);
        return goods is null ? null : new MarketTableDefinition(new MarketTableDefinitionId(packageNamespace, localId), goods);
    }

    private static IReadOnlyDictionary<VoxelDefinitionId, MarketPriceRule>? ParseGoods(JsonElement element,
        string packageNamespace, string path, DiagnosticBag diagnostics)
    {
        var items = ContentJson.ReadArray(element, "goods", path, diagnostics);
        if (items is null)
            return null;
        if (items.Value.GetArrayLength() == 0)
        {
            diagnostics.Reject(ContentDiagnosticCodes.FieldInvalid, "A market table must contain at least one good.", path + "/goods");
            return null;
        }
        var goods = new Dictionary<VoxelDefinitionId, MarketPriceRule>();
        var index = 0;
        foreach (var item in items.Value.EnumerateArray())
        {
            var itemPath = $"{path}/goods/{index++}";
            if (!ContentJson.CheckFields(item, itemPath, diagnostics, "definition", "sensitivity", "min_price", "max_price"))
                return null;
            if (ContentJson.ReadVoxelReference(item, packageNamespace, itemPath, diagnostics) is not { } id ||
                ReadSensitivity(item, itemPath, diagnostics) is not { } sensitivity ||
                ReadPrice(item, "min_price", itemPath, diagnostics) is not { } min ||
                ReadPrice(item, "max_price", itemPath, diagnostics) is not { } max)
                return null;
            if (max < min)
            {
                diagnostics.Reject(ContentDiagnosticCodes.FieldInvalid, "max_price must be at least min_price.", itemPath + "/max_price");
                return null;
            }
            if (!goods.TryAdd(id, new MarketPriceRule(sensitivity, min, max)))
            {
                diagnostics.Reject(ContentDiagnosticCodes.FieldInvalid, $"Duplicate good '{id}'.", itemPath + "/definition");
                return null;
            }
        }
        return goods;
    }

    private static long? ReadPrice(JsonElement element, string name, string path, DiagnosticBag diagnostics)
    {
        var field = ContentJson.ReadField(element, name, path, diagnostics);
        if (field is null)
            return null;
        if (field.Value.ValueKind == JsonValueKind.Number && field.Value.TryGetInt64(out var value) && value > 0)
            return value;
        diagnostics.Reject(ContentDiagnosticCodes.FieldInvalid, "Expected a positive Int64 currency amount.", path + "/" + name);
        return null;
    }

    private static double? ReadSensitivity(JsonElement element, string path, DiagnosticBag diagnostics)
    {
        var field = ContentJson.ReadField(element, "sensitivity", path, diagnostics);
        if (field is null)
            return null;
        if (field.Value.ValueKind == JsonValueKind.Number && field.Value.TryGetDouble(out var value) &&
            double.IsFinite(value) && value >= 0)
            return value == 0 ? 0d : value;
        diagnostics.Reject(ContentDiagnosticCodes.FieldInvalid, "Expected a finite nonnegative sensitivity.", path + "/sensitivity");
        return null;
    }
}
