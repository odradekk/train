using System.Collections.Immutable;
using System.Text.Json;
using Train.Content.Designs;
using static Train.Content.Loading.DefinitionJson;
using C = Train.Content.Contracts;
using K = Train.Core.Contracts;

namespace Train.Content.Loading;

internal sealed record ParsedSettlementType(K.SettlementTypeProjection Core, C.DisplayText Display,
    string MapSymbol, bool Major, string Path);
internal sealed record LoadedSettlementType(ParsedSettlementType Source, C.AssetRevision Symbol);
internal sealed record ParsedTemplatePlot(int Number, K.PlotSize Size, C.BlueprintAddress? Building, string SourceField);
internal sealed record ParsedSettlementTemplate(K.SettlementTemplateId Id, K.SettlementTypeId Type, int Weight,
    ImmutableArray<ParsedTemplatePlot> Plots, C.BlueprintAddress SceneModel,
    ImmutableArray<C.SettlementPlotLayout> Layout, C.DisplayText Display, string Path);
internal sealed record LoadedSettlementTemplate(ParsedSettlementTemplate Source, K.SettlementTemplateProjection Core,
    C.SceneModelProjection SceneModel);

internal static class SettlementParser
{
    internal static ParsedSettlementType? Type(byte[] bytes, LoadPackage package, string path)
    {
        using var document = ContentDocument.Parse(bytes, package, path);
        if (document is null) return null;
        C.ContentReference? subject = null;
        try
        {
            var root = document.RootElement;
            Object(root, "", "schema_version", "id", "simulation", "display");
            var json = new DefinitionJson(root, package, path, "settlement-type", 3);
            var id = new K.SettlementTypeId(json.Id);
            subject = new C.ContentReference.Core(new K.CoreDefinitionReference.SettlementType(id));
            var sim = Required(root, "simulation", "");
            Object(sim, "/simulation", "preferred_terrain", "weight", "population_min", "population_max");
            var terrains = sim.TryGetProperty("preferred_terrain", out var preferred)
                ? Items(preferred, "/simulation/preferred_terrain").Select(t => new K.TerrainTypeId(json.Reference(t.Value, t.Field)))
                    .OrderBy(t => t.Value, StringComparer.Ordinal).ToImmutableArray() : [];
            Unique(terrains, "/simulation/preferred_terrain");
            var weight = Int32(Required(sim, "weight", "/simulation"), "/simulation/weight", 1);
            var minimum = Int64(Required(sim, "population_min", "/simulation"), "/simulation/population_min", 1);
            var maximum = Int64(Required(sim, "population_max", "/simulation"), "/simulation/population_max", minimum);
            var projection = new K.SettlementTypeProjection(id, Semantic("settlement-type", json.Id,
                new { preferred_terrain = terrains.Select(t => t.Value), weight,
                    population_min = minimum, population_max = maximum }), weight, terrains, minimum, maximum);
            var display = Required(root, "display", "");
            var text = Display(display, "/display", "map_symbol", "major");
            var symbol = Asset(Required(display, "map_symbol", "/display"), "/display/map_symbol", "png");
            var major = display.TryGetProperty("major", out var value) && Boolean(value, "/display/major");
            return new(projection, text, symbol, major, path);
        }
        catch (DefinitionFormatException error)
        {
            package.Reject(error.Code, path, error.Field, error.Message, subject);
            return null;
        }
    }

    internal static ParsedSettlementTemplate? Template(byte[] bytes, LoadPackage package, string path)
    {
        using var document = ContentDocument.Parse(bytes, package, path);
        if (document is null) return null;
        C.ContentReference? subject = null;
        try
        {
            var root = document.RootElement;
            Object(root, "", "schema_version", "id", "settlement_type", "weight", "scene_model", "plots", "display",
                "founding", "inventory", "production_plans");
            var json = new DefinitionJson(root, package, path, "settlement-template", 2);
            var id = new K.SettlementTemplateId(json.Id);
            subject = new C.ContentReference.Core(new K.CoreDefinitionReference.Template(id));
            foreach (var key in new[] { "founding", "inventory", "production_plans" })
                if (root.TryGetProperty(key, out var future))
                {
                    Array(future, "/" + key);
                    if (future.GetArrayLength() > 0) Fail("/" + key, "当前尚未实现非空的 " + key + "。", "unsupported_format");
                }
            var plots = ImmutableArray.CreateBuilder<ParsedTemplatePlot>();
            var layout = ImmutableArray.CreateBuilder<C.SettlementPlotLayout>();
            foreach (var (value, field) in Items(Required(root, "plots", ""), "/plots"))
            {
                Object(value, field, "id", "size", "at", "orientation", "building");
                var plotLayout = ReadPlotLayoutFields(value, field);
                C.BlueprintAddress? building = value.TryGetProperty("building", out var reference)
                    ? new(C.BlueprintLibrary.Packages, new(json.Reference(reference, field + "/building"))) : null;
                plots.Add(new(plotLayout.Number, plotLayout.Size, building, field));
                layout.Add(plotLayout);
            }
            if (plots.Count == 0) Fail("/plots", "地块表不能为空。");
            Unique(plots.Select(p => p.Number), "/plots");
            return new(id, new(json.Reference(Required(root, "settlement_type", ""), "/settlement_type")),
                root.TryGetProperty("weight", out var weight) ? Int32(weight, "/weight", 1) : 1,
                plots.OrderBy(p => p.Number).ToImmutableArray(),
                new(C.BlueprintLibrary.Packages, new(json.Reference(Required(root, "scene_model", ""), "/scene_model"))),
                layout.OrderBy(p => p.Number).ToImmutableArray(), Display(Required(root, "display", "")), path);
        }
        catch (DefinitionFormatException error)
        {
            package.Reject(error.Code, path, error.Field, error.Message, subject);
            return null;
        }
    }

    internal static C.SettlementPlotLayout ReadPlotLayout(JsonElement value, string field)
    {
        Object(value, field, "id", "size", "at", "orientation");
        return ReadPlotLayoutFields(value, field);
    }

    private static C.SettlementPlotLayout ReadPlotLayoutFields(JsonElement value, string field)
    {
        var number = Int32(Required(value, "id", field), field + "/id", 1);
        var size = EnumValue<K.PlotSize>(Required(value, "size", field), field + "/size");
        var at = Required(value, "at", field);
        Array(at, field + "/at", 3);
        var position = new C.GridPosition(Int32(at[0], field + "/at/0"), Int32(at[1], field + "/at/1"), Int32(at[2], field + "/at/2"));
        var orientation = value.TryGetProperty("orientation", out var rotation)
            ? EnumValue<C.VoxelOrientation>(rotation, field + "/orientation") : C.VoxelOrientation.PxPy;
        if (orientation is not (C.VoxelOrientation.PxPy or C.VoxelOrientation.PzPy or C.VoxelOrientation.NxPy or C.VoxelOrientation.NzPy))
            Fail(field + "/orientation", "地块朝向必须绕竖直轴旋转。");
        return new(number, size, position, orientation);
    }

    internal static void WritePlotLayout(Utf8JsonWriter writer, C.SettlementPlotLayout layout)
    {
        writer.WriteStartObject();
        writer.WriteNumber("id", layout.Number);
        writer.WriteString("size", DesignEvaluator.Wire(layout.Size));
        writer.WriteStartArray("at");
        writer.WriteNumberValue(layout.At.X);
        writer.WriteNumberValue(layout.At.Y);
        writer.WriteNumberValue(layout.At.Z);
        writer.WriteEndArray();
        writer.WriteString("orientation", DesignEvaluator.Wire(layout.Orientation));
        writer.WriteEndObject();
    }
}
