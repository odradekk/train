using System.Collections.Immutable;
using System.Text.Json;
using Train.Content.Designs;
using static Train.Content.Loading.DefinitionJson;
using C = Train.Content.Contracts;
using K = Train.Core.Contracts;

namespace Train.Content.Loading;

internal sealed record ParsedProcess(string Id, K.ProcessProjection Projection, C.DisplayText Display, string Path,
    ImmutableArray<(K.VoxelDefinitionId Id, string Field)> Goods,
    ImmutableArray<(K.TerrainTypeId Id, string Field)> Terrains)
{
    internal K.ProcessRef Reference => Projection.Details switch
    {
        K.ProcessDetails.Recovery => new K.ProcessRef.Recovery(new(Id)),
        _ => new K.ProcessRef.Production(new(Id))
    };
}

internal static class ProcessParser
{
    internal static ParsedProcess? Parse(byte[] bytes, LoadPackage package, string path)
    {
        using var document = ContentDocument.Parse(bytes, package, path);
        if (document is null) return null;
        C.ContentReference? subject = null;
        try
        {
            var root = document.RootElement;
            var json = new DefinitionJson(root, package, path, "process", 1);
            Object(root, "", "schema_version", "id", "simulation", "display");
            var sim = Required(root, "simulation", "");
            const string field = "/simulation";
            var kind = Text(Required(sim, "kind", field), field + "/kind");
            if (kind is not ("production" or "extraction" or "recovery")) Fail(field + "/kind", "未知流程种类。");
            var reference = kind == "recovery"
                ? (K.ProcessRef)new K.ProcessRef.Recovery(new(json.Id)) : new K.ProcessRef.Production(new(json.Id));
            subject = new C.ContentReference.Core(new K.CoreDefinitionReference.Process(reference));
            Object(sim, field, ["kind", "work_required", "power_per_work", "labor_per_work", "facility_tag", "environment",
                .. kind == "extraction" ? new[] { "extracts" } : new[] { "inputs", "outputs" }]);
            var goods = ImmutableArray.CreateBuilder<(K.VoxelDefinitionId Id, string Field)>();
            var inputs = kind == "extraction" ? [] : ReadGoods("inputs", kind == "recovery");
            var outputs = kind == "extraction" ? [] : ReadGoods("outputs", true);
            var extracts = kind == "extraction" ? ReadGood(Required(sim, "extracts", field), field + "/extracts") : null;
            K.ProcessDetails details = kind switch
            {
                "production" => new K.ProcessDetails.Production(new(json.Id), inputs, outputs),
                "extraction" => new K.ProcessDetails.Extraction(new(json.Id), extracts!),
                _ => new K.ProcessDetails.Recovery(new(json.Id), inputs, outputs)
            };
            var terrains = ImmutableArray.CreateBuilder<(K.TerrainTypeId Id, string Field)>();
            if (sim.TryGetProperty("environment", out var environment))
            {
                Object(environment, field + "/environment", "terrain_types");
                foreach (var (value, terrainField) in Items(Required(environment, "terrain_types", field + "/environment"), field + "/environment/terrain_types"))
                    terrains.Add((new(json.Reference(value, terrainField)), terrainField));
                if (terrains.Count == 0) Fail(field + "/environment/terrain_types", "地貌要求不能为空。");
                Unique(terrains.Select(t => t.Id), field + "/environment/terrain_types");
            }
            var projection = new K.ProcessProjection(details, default,
                Int64(Required(sim, "work_required", field), field + "/work_required", 1),
                Number(Required(sim, "power_per_work", field), field + "/power_per_work", 0),
                Number(Required(sim, "labor_per_work", field), field + "/labor_per_work", 0),
                BlueprintParser.EnumValue<K.FacilityTag>(Required(sim, "facility_tag", field), field + "/facility_tag"),
                terrains.Select(t => t.Id).OrderBy(t => t.Value, StringComparer.Ordinal).ToImmutableArray());
            var revision = Semantic("process", json.Id, new
            {
                kind, inputs = Quantities(inputs), outputs = Quantities(outputs),
                extracts = extracts is null ? null : new { voxel = ((K.GoodId.Voxel)extracts.Good).Definition.Value, quantity = extracts.Quantity },
                work_required = projection.WorkRequired, power_per_work = projection.PowerPerWork, labor_per_work = projection.LaborPerWork,
                facility_tag = DesignEvaluator.Wire(projection.RequiredFacilityTag),
                terrain_types = projection.AllowedTerrainTypes.Select(t => t.Value)
            });
            return new(json.Id, projection with { Revision = revision }, Display(Required(root, "display", "")), path,
                goods.ToImmutable(), terrains.ToImmutable());

            K.GoodsQuantity ReadGood(JsonElement value, string goodField)
            {
                Object(value, goodField, "voxel", "quantity");
                var id = new K.VoxelDefinitionId(json.Reference(Required(value, "voxel", goodField), goodField + "/voxel"));
                goods.Add((id, goodField + "/voxel"));
                return new(new K.GoodId.Voxel(id), Int32(Required(value, "quantity", goodField), goodField + "/quantity", 1));
            }
            ImmutableArray<K.GoodsQuantity> ReadGoods(string key, bool required)
            {
                if (!required && !sim.TryGetProperty(key, out _)) return [];
                var values = Items(Required(sim, key, field), field + "/" + key).Select(item => ReadGood(item.Value, item.Field)).ToImmutableArray();
                if (required && values.IsEmpty) Fail(field + "/" + key, "货物数组不能为空。");
                Unique(values.Select(v => v.Good), field + "/" + key);
                return values.OrderBy(v => ((K.GoodId.Voxel)v.Good).Definition.Value, StringComparer.Ordinal).ToImmutableArray();
            }
        }
        catch (DefinitionFormatException error)
        {
            package.Reject(error.Code, path, error.Field, error.Message, subject);
            return null;
        }
    }

    private static object Quantities(ImmutableArray<K.GoodsQuantity> goods) => goods.Select(good => new
    {
        voxel = ((K.GoodId.Voxel)good.Good).Definition.Value, quantity = good.Quantity
    });
}
