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
internal sealed record ParsedFoundingBuilding(int PlotNumber, C.BlueprintAddress Building, string SourceField);
internal sealed record ParsedTemplatePlan(int PlotNumber, K.CapabilityKind Capability, string Process,
    bool Enabled, int Priority, string SourceField);
internal sealed record ParsedSettlementTemplate(K.SettlementTemplateId Id, K.SettlementTypeId Type, int Weight,
    ImmutableArray<ParsedTemplatePlot> Plots, C.BlueprintAddress SceneModel,
    ImmutableArray<C.SettlementPlotLayout> Layout, ImmutableArray<ParsedFoundingBuilding> Founding,
    ImmutableArray<ParsedInventoryQuantity> Inventory, ImmutableArray<ParsedTemplatePlan> ProductionPlans,
    C.DisplayText Display, string Path);
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
            var founding = root.TryGetProperty("founding", out var buildings)
                ? Items(buildings, "/founding").Select(item =>
                {
                    Object(item.Value, item.Field, "plot", "building");
                    return new ParsedFoundingBuilding(Int32(Required(item.Value, "plot", item.Field), item.Field + "/plot", 1),
                        new(C.BlueprintLibrary.Packages, new(json.Reference(Required(item.Value, "building", item.Field), item.Field + "/building"))),
                        item.Field);
                }).OrderBy(b => b.PlotNumber).ToImmutableArray() : [];
            Unique(founding.Select(b => b.PlotNumber), "/founding");
            var inventory = root.TryGetProperty("inventory", out var quantities)
                ? GenerationProfileParser.Inventory(quantities, json, "/inventory") : [];
            var plans = root.TryGetProperty("production_plans", out var productionPlans)
                ? Items(productionPlans, "/production_plans").Select(item =>
                {
                    Object(item.Value, item.Field, "plot", "capability", "process", "enabled", "priority");
                    var capability = EnumValue<K.CapabilityKind>(Required(item.Value, "capability", item.Field), item.Field + "/capability");
                    if (capability is not (K.CapabilityKind.Production or K.CapabilityKind.Extraction))
                        Fail(item.Field + "/capability", "初始计划只支持生产或开采能力。");
                    return new ParsedTemplatePlan(Int32(Required(item.Value, "plot", item.Field), item.Field + "/plot", 1), capability,
                        json.Reference(Required(item.Value, "process", item.Field), item.Field + "/process"),
                        Boolean(Required(item.Value, "enabled", item.Field), item.Field + "/enabled"),
                        Int32(Required(item.Value, "priority", item.Field), item.Field + "/priority"), item.Field);
                }).OrderBy(p => p.PlotNumber).ThenBy(p => p.Capability).ToImmutableArray() : [];
            Unique(plans.Select(p => (p.PlotNumber, p.Capability)), "/production_plans");
            return new(id, new(json.Reference(Required(root, "settlement_type", ""), "/settlement_type")),
                root.TryGetProperty("weight", out var weight) ? Int32(weight, "/weight", 1) : 1,
                plots.OrderBy(p => p.Number).ToImmutableArray(),
                new(C.BlueprintLibrary.Packages, new(json.Reference(Required(root, "scene_model", ""), "/scene_model"))),
                layout.OrderBy(p => p.Number).ToImmutableArray(), founding, inventory, plans,
                Display(Required(root, "display", "")), path);
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
