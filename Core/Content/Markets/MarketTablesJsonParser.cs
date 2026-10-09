// 属于内容层的市场表解析模块。
// 严格读取价格结构，本地校验后交给共同流程合并与检查引用。
using System.Text.Json;
using Train.Core.Content.Voxels;
namespace Train.Core.Content.Markets;

/// <summary>解析 schema 1 市场表的本地字段；合并后的货物引用由领域格式校验。</summary>
internal static class MarketTablesJsonParser
{
    /// <summary>市场表文件唯一支持的 schema 版本；格式约定见内容模块 README。</summary>
    private const int SchemaVersion = 1;

    /// <summary>将 UTF-8 文件解析成不可变定义序列；结构或字段无效时返回 null。</summary>
    /// <param name="utf8">原始 JSON；遵守共用大小及严格 JSON 约束。</param>
    /// <param name="packageNamespace">已校验的所属包命名空间，补全声明的局部 ID。</param>
    /// <param name="sourceFile">包内源文件路径，仅作来源和诊断。</param>
    /// <param name="diagnostics">共享包诊断容器；失败写入原因及 JSON 路径并使整包无效。</param>
    /// <returns>定义序列，允许为空；文件无效为 null，不返回部分成功结果。</returns>
    /// <remarks>效果：更新 diagnostics；不读文件，不解析全局引用，不发布快照。</remarks>
    public static IReadOnlyList<MarketTableDefinition>? Parse(
        byte[] utf8, string packageNamespace, string sourceFile, DiagnosticBag diagnostics)
    {
        return ContentJson.ParseDefinitions(utf8, sourceFile, SchemaVersion, "market_tables", diagnostics,
            (element, path) => ParseDefinition(element, packageNamespace, path, diagnostics));
    }

    /// <summary>从一个市场表对象取得身份和非空价格映射。</summary>
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

    /// <summary>逐条读取货物引用和价格参数，拒绝重复货物及倒置价格区间。</summary>
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

    /// <summary>读取正 Int64 货币值。</summary>
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

    /// <summary>读取有限非负敏感度，正零和负零规范化为同一数值。</summary>
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
