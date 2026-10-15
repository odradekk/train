using System.Collections.Immutable;
using System.Text.Json;
using Train.Content.Designs;
using static Train.Content.Loading.DefinitionJson;
using C = Train.Content.Contracts;
using K = Train.Core.Contracts;

namespace Train.Content.Loading;

internal sealed record ParsedBlueprint(C.BlueprintAddress Address, C.DesignDraft Definition, string Path);

internal sealed class BlueprintParser
{
    internal ParsedBlueprint? Parse(byte[] bytes, LoadPackage package, string path)
    {
        using var document = ContentDocument.Parse(bytes, package, path);
        if (document is null) return null;
        C.ContentReference? subject = null;
        try
        {
            var root = document.RootElement;
            Object(root, "", "schema_version", "id", "category", "simulation", "layout", "appearance", "display");
            if (Integer(Required(root, "schema_version", ""), "/schema_version") != 3)
                Fail("/schema_version", "只支持 schema_version 3。", "unsupported_format");
            var id = Text(Required(root, "id", ""), "/id");
            if (!ContentIdentity.IsValidPart(id) || id == "shared" || System.IO.Path.GetFileName(path) != id + ".blueprint.json")
                Fail("/id", "local id 必须有效且与文件名一致，不能是 shared。");
            var address = new C.BlueprintAddress(C.BlueprintLibrary.Packages, new(package.Metadata!.Id.Value + ":" + id));
            subject = new C.ContentReference.Blueprint(address);
            return new(address, ReadDesign(root, package), path);
        }
        catch (DefinitionFormatException exception)
        {
            package.Reject(exception.Code, path, exception.Field, exception.Message, subject);
            return null;
        }
    }

    internal static C.DesignDraft ReadDesign(JsonElement root)
    {
        Object(root, "", "category", "simulation", "layout", "appearance", "display");
        return ReadDesign(root, null);
    }

    private static C.DesignDraft ReadDesign(JsonElement root, LoadPackage? package)
    {
        var category = Choice(Required(root, "category", ""), "/category", ["vehicle", "building", "large_equipment", "scene_model", "creature"]);
        if (category is "large_equipment" or "creature")
            Fail("/category", "当前不支持大型设备或生物蓝图。", "unsupported_format");
        var layout = Required(root, "layout", "");
        Object(layout, "/layout", "palette", "cells", "composites");
        if (layout.TryGetProperty("composites", out var composites))
        {
            Array(composites, "/layout/composites");
            if (composites.GetArrayLength() > 0)
                Fail("/layout/composites", "当前不支持嵌套蓝图。", "unsupported_format");
        }
        var palette = ImmutableArray.CreateBuilder<C.VoxelPaletteEntry>();
        if (layout.TryGetProperty("palette", out var entries))
        {
            Array(entries, "/layout/palette");
            foreach (var entry in entries.EnumerateArray())
            {
                var field = "/layout/palette/" + palette.Count;
                Object(entry, field, "voxel", "orientation", "shape", "paint");
                var voxel = DefinitionJson.Reference(Required(entry, "voxel", field), field + "/voxel", package);
                var orientation = entry.TryGetProperty("orientation", out var o)
                    ? EnumValue<C.VoxelOrientation>(o, field + "/orientation") : C.VoxelOrientation.PxPy;
                C.VoxelShape? shape = entry.TryGetProperty("shape", out var s)
                    ? new C.VoxelShape(Text(s, field + "/shape")) : null;
                C.PaintColor? paint = entry.TryGetProperty("paint", out var p) ? new(Text(p, field + "/paint")) : null;
                palette.Add(new(new(voxel), orientation, shape, paint));
            }
        }
        var cells = ImmutableArray.CreateBuilder<C.LayoutCell>();
        if (layout.TryGetProperty("cells", out var cellValues))
        {
            Array(cellValues, "/layout/cells");
            foreach (var cell in cellValues.EnumerateArray())
            {
                var field = "/layout/cells/" + cells.Count;
                Array(cell, field, 4);
                var index = Integer(cell[3], field + "/3");
                if (index < 0 || index >= palette.Count) Fail(field + "/3", "调色板序号必须在调色板范围内。");
                cells.Add(new(new(Integer(cell[0], field + "/0"), Integer(cell[1], field + "/1"), Integer(cell[2], field + "/2")), index));
            }
        }
        var specification = Specification(root, category);
        var display = Required(root, "display", "");
        Object(display, "/display", "name", "description");
        return new(new(Text(Required(display, "name", "/display"), "/display/name"),
            Text(Required(display, "description", "/display"), "/display/description")),
            specification, new(palette.ToImmutable(), cells.ToImmutable(), []));
    }

    internal static void WriteDesign(Utf8JsonWriter writer, C.DesignDraft draft)
    {
        if (!draft.Layout.NestedItems.IsEmpty)
            Fail("/layout/composites", "当前不支持嵌套蓝图。", "unsupported_format");
        switch (draft.Specification)
        {
            case C.DesignSpecification.Vehicle { Size: { } size }:
                writer.WriteString("category", "vehicle");
                writer.WriteStartObject("simulation");
                writer.WriteString("size_class", DesignEvaluator.Wire(size));
                writer.WriteEndObject();
                break;
            case C.DesignSpecification.Building { PlotSize: { } plot, Kind: { } kind } building:
                writer.WriteString("category", "building");
                writer.WriteStartObject("simulation");
                writer.WriteString("plot_size", DesignEvaluator.Wire(plot));
                writer.WriteString("building_type", DesignEvaluator.Wire(kind));
                writer.WriteStartArray("facility_tags");
                foreach (var tag in building.FacilityTags) writer.WriteStringValue(DesignEvaluator.Wire(tag));
                writer.WriteEndArray();
                writer.WriteEndObject();
                break;
            case C.DesignSpecification.SceneModel:
                writer.WriteString("category", "scene_model");
                break;
            default:
                Fail("/category", "当前只支持完整的车辆、建筑或场景模型设计。", "unsupported_format");
                break;
        }
        writer.WriteStartObject("layout");
        writer.WriteStartArray("palette");
        foreach (var entry in draft.Layout.Palette)
        {
            writer.WriteStartObject();
            writer.WriteString("voxel", entry.Voxel.Value);
            writer.WriteString("orientation", DesignEvaluator.Wire(entry.Orientation));
            if (entry.Shape is { } shape) writer.WriteString("shape", shape.Name);
            if (entry.Paint is { } paint) writer.WriteString("paint", paint.Value);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteStartArray("cells");
        foreach (var cell in draft.Layout.Cells)
        {
            writer.WriteStartArray();
            writer.WriteNumberValue(cell.At.X);
            writer.WriteNumberValue(cell.At.Y);
            writer.WriteNumberValue(cell.At.Z);
            writer.WriteNumberValue(cell.PaletteIndex);
            writer.WriteEndArray();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
        if (draft.Specification is C.DesignSpecification.SceneModel { Flatten: { } flatten })
        {
            writer.WriteStartObject("appearance");
            writer.WriteStartObject("flatten");
            writer.WriteStartArray("from");
            writer.WriteNumberValue(flatten.From.X);
            writer.WriteNumberValue(flatten.From.Z);
            writer.WriteEndArray();
            writer.WriteStartArray("to");
            writer.WriteNumberValue(flatten.To.X);
            writer.WriteNumberValue(flatten.To.Z);
            writer.WriteEndArray();
            writer.WriteNumber("height", flatten.Height);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        writer.WriteStartObject("display");
        writer.WriteString("name", draft.Display.Name);
        writer.WriteString("description", draft.Display.Description);
        writer.WriteEndObject();
    }

    private static C.DesignSpecification Specification(JsonElement root, string category)
    {
        if (category == "scene_model")
        {
            if (root.TryGetProperty("simulation", out _)) Fail("/simulation", "场景模型不能有 simulation。");
            C.FlattenRegion? flatten = null;
            if (root.TryGetProperty("appearance", out var appearance))
            {
                Object(appearance, "/appearance", "flatten");
                if (appearance.TryGetProperty("flatten", out var f))
                {
                    Object(f, "/appearance/flatten", "from", "to", "height");
                    var from = Column(Required(f, "from", "/appearance/flatten"), "/appearance/flatten/from");
                    var to = Column(Required(f, "to", "/appearance/flatten"), "/appearance/flatten/to");
                    flatten = new(from, to, Integer(Required(f, "height", "/appearance/flatten"), "/appearance/flatten/height"));
                }
            }
            return new C.DesignSpecification.SceneModel(flatten);
        }
        if (root.TryGetProperty("appearance", out var empty)) Object(empty, "/appearance");
        var sim = Required(root, "simulation", "");
        if (category == "vehicle")
        {
            Object(sim, "/simulation", "size_class");
            return new C.DesignSpecification.Vehicle(EnumValue<K.SizeClass>(Required(sim, "size_class", "/simulation"), "/simulation/size_class"));
        }
        Object(sim, "/simulation", "plot_size", "building_type", "facility_tags");
        var plot = EnumValue<K.PlotSize>(Required(sim, "plot_size", "/simulation"), "/simulation/plot_size");
        var kind = EnumValue<K.BuildingKind>(Required(sim, "building_type", "/simulation"), "/simulation/building_type");
        var tags = ImmutableArray.CreateBuilder<K.FacilityTag>();
        if (sim.TryGetProperty("facility_tags", out var values))
        {
            Array(values, "/simulation/facility_tags");
            foreach (var value in values.EnumerateArray())
            {
                var field = "/simulation/facility_tags/" + tags.Count;
                tags.Add(EnumValue<K.FacilityTag>(value, field));
            }
        }
        return new C.DesignSpecification.Building(plot, kind, tags.ToImmutable());
    }

    private static C.GridColumn Column(JsonElement value, string field)
    {
        Array(value, field, 2);
        return new(Integer(value[0], field + "/0"), Integer(value[1], field + "/1"));
    }
    private static string Choice(JsonElement value, string field, string[] choices)
    {
        var text = Text(value, field);
        if (!choices.Contains(text, StringComparer.Ordinal)) Fail(field, "未知取值。");
        return text;
    }
    private static string Text(JsonElement value, string field)
    {
        if (value.ValueKind != JsonValueKind.String) Fail(field, "必须为字符串。");
        return value.GetString()!;
    }
    private static int Integer(JsonElement value, string field)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out _)) Fail(field, "必须为 Int32 整数。");
        return value.GetInt32();
    }
    private static void Array(JsonElement value, string field, int? length = null)
    {
        if (value.ValueKind != JsonValueKind.Array || (length is not null && value.GetArrayLength() != length)) Fail(field, "必须为长度正确的数组。");
    }
    private static JsonElement Required(JsonElement value, string key, string field)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(key, out _)) Fail(Child(field, key), "缺少必填字段或父字段不是对象。");
        return value.GetProperty(key);
    }
    private static void Object(JsonElement value, string field, params string[] keys)
    {
        if (value.ValueKind != JsonValueKind.Object) Fail(field, "必须为对象。");
        foreach (var property in value.EnumerateObject())
            if (!keys.Contains(property.Name, StringComparer.Ordinal)) Fail(Child(field, property.Name), "未知字段。");
    }
    private static string Child(string field, string key) => field + "/" + key.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
    private static void Fail(string field, string message, string code = "invalid_field") => throw new DefinitionFormatException(field, message, code);
}
