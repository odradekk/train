// 属于内容层的聚居地类型解析模块。
// 严格读取生成参数与供需规格，本地校验后交给共同流程检查货物引用。
using System.Text.Json;
using Train.Core.Content.Voxels;
namespace Train.Core.Content.Settlements;

/// <summary>解析 schema 1 聚居地类型的本地字段；合并后的货物引用由领域格式校验。</summary>
internal static class SettlementTypesJsonParser
{
    /// <summary>将 UTF-8 文件解析成不可变类型序列；结构或字段无效时返回 null。</summary>
    /// <param name="utf8">原始 JSON；遵守共用大小及严格 JSON 约束。</param>
    /// <param name="packageNamespace">已校验的所属包命名空间，补全声明的局部 ID。</param>
    /// <param name="sourceFile">包内源文件路径，仅作来源和诊断。</param>
    /// <param name="diagnostics">共享包诊断容器；失败写入原因及 JSON 路径并使整包无效。</param>
    /// <returns>类型序列，允许为空；文件无效为 null，不返回部分成功结果。</returns>
    /// <remarks>效果：更新 diagnostics；不抽样世界状态，不恢复供需，不发布快照。</remarks>
    public static IReadOnlyList<SettlementTypeDefinition>? Parse(
        byte[] utf8, string packageNamespace, string sourceFile, DiagnosticBag diagnostics)
    {
        return ContentJson.ParseDefinitions(utf8, sourceFile, 1, "settlement_types", diagnostics,
            (element, path) => ParseDefinition(element, packageNamespace, path, diagnostics));
    }

    /// <summary>读取一个类型的身份、显示名、两种数量映射与生成参数。</summary>
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

    /// <summary>读取一边供需的有限货物映射，允许为空，拒绝同一边的重复货物。</summary>
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

    /// <summary>读取完整规范地貌身份集合；空集合表示不限，不检查目标存在性。</summary>
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

    /// <summary>读取正人口闭区间，拒绝上限小于下限。</summary>
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
