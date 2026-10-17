using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
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

    internal static C.DesignDraft ReadDesign(JsonElement root, bool draft = false)
    {
        Object(root, "", "category", "simulation", "layout", "appearance", "display");
        return ReadDesign(root, null, draft);
    }

    private static C.DesignDraft ReadDesign(JsonElement root, LoadPackage? package, bool draft = false)
    {
        var category = Choice(Required(root, "category", ""), "/category", ["vehicle", "building", "large_equipment", "scene_model", "creature"]);
        if (category == "creature")
            Fail("/category", "当前不支持生物蓝图。", "unsupported_format");
        var layout = Required(root, "layout", "");
        Object(layout, "/layout", "palette", "cells", "composites");
        var nested = ImmutableArray.CreateBuilder<C.NestedDesignItem>();
        if (layout.TryGetProperty("composites", out var composites))
        {
            Array(composites, "/layout/composites");
            foreach (var item in composites.EnumerateArray())
            {
                var field = "/layout/composites/" + nested.Count;
                Object(item, field, "at", "orientation", "design", "blueprint", "paint");
                var at = Position(Required(item, "at", field), field + "/at");
                var orientation = item.TryGetProperty("orientation", out var o)
                    ? EnumValue<C.VoxelOrientation>(o, field + "/orientation") : C.VoxelOrientation.PxPy;
                if (item.TryGetProperty("design", out var design))
                {
                    if (item.TryGetProperty("blueprint", out _) || item.TryGetProperty("paint", out _))
                        Fail(field, "复制设计不能带 blueprint 或 paint。");
                    Object(design, field + "/design", "category", "simulation", "layout", "appearance", "display");
                    C.DesignDraft child;
                    try { child = ReadDesign(design, package, draft); }
                    catch (DefinitionFormatException exception)
                    { throw new DefinitionFormatException(field + "/design" + exception.Field, exception.Message, exception.Code); }
                    if (category == "scene_model")
                        nested.Add(new C.NestedDesignItem.SceneModelCopy(at, orientation, child));
                    else
                        nested.Add(new C.NestedDesignItem.AssemblyCopy(at, orientation, child));
                }
                else
                {
                    var reference = DefinitionJson.Reference(Required(item, "blueprint", field), field + "/blueprint", package);
                    C.PaintColor? paint = item.TryGetProperty("paint", out var p) ? new(Text(p, field + "/paint")) : null;
                    nested.Add(new C.NestedDesignItem.FunctionalCompositeReference(at, orientation, new(reference), paint));
                }
            }
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
                if (!draft && (index < 0 || index >= palette.Count)) Fail(field + "/3", "调色板序号必须在调色板范围内。");
                cells.Add(new(new(Integer(cell[0], field + "/0"), Integer(cell[1], field + "/1"), Integer(cell[2], field + "/2")), index));
            }
        }
        var specification = Specification(root, category, draft);
        var display = Required(root, "display", "");
        Object(display, "/display", "name", "description");
        return new(new(Text(Required(display, "name", "/display"), "/display/name"),
            Text(Required(display, "description", "/display"), "/display/description")),
            specification, new(palette.ToImmutable(), cells.ToImmutable(), nested.ToImmutable()));
    }

    internal static void WriteDesign(Utf8JsonWriter writer, C.DesignDraft draft, bool incomplete = false, int depth = 0)
    {
        if (depth >= 64) Fail("/layout/composites", "设计嵌套过深或包含循环。");
        if (draft is null || draft.Display is null || draft.Layout is null || draft.Specification is null)
            Fail("", "设计、显示、布局和规格必须为对象。");
        if (draft.Layout.Palette.IsDefault || draft.Layout.Cells.IsDefault || draft.Layout.NestedItems.IsDefault)
            Fail("/layout", "布局数组必须初始化。");
        switch (draft.Specification)
        {
            case C.DesignSpecification.Vehicle vehicle when incomplete || vehicle.Size is not null:
                writer.WriteString("category", "vehicle");
                writer.WriteStartObject("simulation");
                if (vehicle.Size is { } size) writer.WriteString("size_class", DesignEvaluator.Wire(size));
                writer.WriteEndObject();
                break;
            case C.DesignSpecification.Building building when incomplete || building.PlotSize is not null && building.Kind is not null:
                writer.WriteString("category", "building");
                writer.WriteStartObject("simulation");
                if (building.PlotSize is { } plot) writer.WriteString("plot_size", DesignEvaluator.Wire(plot));
                if (building.Kind is { } kind) writer.WriteString("building_type", DesignEvaluator.Wire(kind));
                writer.WriteStartArray("facility_tags");
                if (building.FacilityTags.IsDefault) Fail("/simulation/facility_tags", "数组必须初始化。");
                foreach (var tag in building.FacilityTags) writer.WriteStringValue(DesignEvaluator.Wire(tag));
                writer.WriteEndArray();
                writer.WriteEndObject();
                break;
            case C.DesignSpecification.Assembly:
                writer.WriteString("category", "large_equipment");
                break;
            case C.DesignSpecification.FunctionalComposite functional:
                writer.WriteString("category", "large_equipment");
                writer.WriteStartObject("simulation");
                writer.WriteStartObject("behaviors");
                if (functional.Behaviors.IsDefault) Fail("/simulation/behaviors", "数组必须初始化。");
                foreach (var capability in functional.Behaviors) WriteBehavior(writer, capability);
                writer.WriteEndObject();
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
            if (entry is null) Fail("/layout/palette", "调色板条目不能为空。");
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
            if (cell is null) Fail("/layout/cells", "布局单元不能为空。");
            writer.WriteStartArray();
            writer.WriteNumberValue(cell.At.X);
            writer.WriteNumberValue(cell.At.Y);
            writer.WriteNumberValue(cell.At.Z);
            writer.WriteNumberValue(cell.PaletteIndex);
            writer.WriteEndArray();
        }
        writer.WriteEndArray();
        writer.WriteStartArray("composites");
        foreach (var item in draft.Layout.NestedItems)
        {
            if (item is null) Fail("/layout/composites", "嵌套项不能为空。");
            writer.WriteStartObject();
            WritePosition(writer, "at", DesignTraversal.At(item));
            writer.WriteString("orientation", DesignEvaluator.Wire(DesignTraversal.Orientation(item)));
            if (item is C.NestedDesignItem.FunctionalCompositeReference reference)
            {
                writer.WriteString("blueprint", reference.Definition.Value);
                if (reference.Paint is { } paint) writer.WriteString("paint", paint.Value);
            }
            else
            {
                writer.WriteStartObject("design");
                WriteDesign(writer, DesignTraversal.Copy(item)!, incomplete, depth + 1);
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
        var rotation = draft.Specification switch
        {
            C.DesignSpecification.Assembly a => a.Rotation,
            C.DesignSpecification.FunctionalComposite f => f.Rotation,
            _ => null
        };
        if (rotation is not null)
        {
            writer.WriteStartObject("appearance");
            writer.WriteStartObject("behaviors");
            writer.WriteStartObject("rotation");
            writer.WriteString("axis", DesignEvaluator.Wire(rotation.Axis));
            WritePosition(writer, "pivot", rotation.Pivot);
            writer.WriteStartArray("range");
            writer.WriteNumberValue(rotation.MinimumDegrees);
            writer.WriteNumberValue(rotation.MaximumDegrees);
            writer.WriteEndArray();
            writer.WriteNumber("speed", rotation.SpeedDegreesPerSecond);
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
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

    private static C.DesignSpecification Specification(JsonElement root, string category, bool draft)
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
        if (category == "large_equipment")
        {
            C.RotationAppearance? rotation = null;
            if (root.TryGetProperty("appearance", out var appearance))
            {
                Object(appearance, "/appearance", "behaviors");
                if (appearance.TryGetProperty("behaviors", out var visual))
                {
                    Object(visual, "/appearance/behaviors", "rotation");
                    if (visual.TryGetProperty("rotation", out var r))
                    {
                        const string field = "/appearance/behaviors/rotation";
                        Object(r, field, "axis", "pivot", "range", "speed");
                        var axis = EnumValue<C.RotationAxis>(Required(r, "axis", field), field + "/axis");
                        var pivot = Position(Required(r, "pivot", field), field + "/pivot");
                        var range = Required(r, "range", field);
                        Array(range, field + "/range", 2);
                        var minimum = Finite(range[0], field + "/range/0");
                        var maximum = Finite(range[1], field + "/range/1");
                        if (minimum < -180 || maximum > 180 || minimum >= maximum) Fail(field + "/range", "角度必须在 −180 到 180 内，且最小角小于最大角。");
                        var speed = r.TryGetProperty("speed", out var speedValue) ? Finite(speedValue, field + "/speed") : 90;
                        if (speed <= 0) Fail(field + "/speed", "转速必须大于 0。");
                        rotation = new(axis, pivot, minimum, maximum, speed);
                    }
                }
            }
            if (root.TryGetProperty("simulation", out var simulation))
            {
                Object(simulation, "/simulation", "behaviors");
                if (simulation.TryGetProperty("behaviors", out var behaviors))
                {
                    var capabilities = VoxelParser.ReadBehaviors(behaviors).Capabilities;
                    if (capabilities.IsEmpty) Fail("/simulation/behaviors", "功能性行为不能为空。");
                    return new C.DesignSpecification.FunctionalComposite(capabilities, rotation);
                }
            }
            return new C.DesignSpecification.Assembly(rotation);
        }
        if (root.TryGetProperty("appearance", out var empty)) Object(empty, "/appearance");
        var sim = Required(root, "simulation", "");
        if (category == "vehicle")
        {
            Object(sim, "/simulation", "size_class");
            return new C.DesignSpecification.Vehicle(draft && !sim.TryGetProperty("size_class", out _) ? null : EnumValue<K.SizeClass>(Required(sim, "size_class", "/simulation"), "/simulation/size_class"));
        }
        Object(sim, "/simulation", "plot_size", "building_type", "facility_tags");
        K.PlotSize? plot = draft && !sim.TryGetProperty("plot_size", out _) ? null : EnumValue<K.PlotSize>(Required(sim, "plot_size", "/simulation"), "/simulation/plot_size");
        K.BuildingKind? kind = draft && !sim.TryGetProperty("building_type", out _) ? null : EnumValue<K.BuildingKind>(Required(sim, "building_type", "/simulation"), "/simulation/building_type");
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

    private static double Finite(JsonElement value, string field)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number)) Fail(field, "必须为有限数值。");
        return value.GetDouble();
    }
    private static C.GridPosition Position(JsonElement value, string field)
    {
        Array(value, field, 3);
        return new(Integer(value[0], field + "/0"), Integer(value[1], field + "/1"), Integer(value[2], field + "/2"));
    }
    private static void WritePosition(Utf8JsonWriter writer, string name, C.GridPosition at)
    {
        writer.WriteStartArray(name);
        writer.WriteNumberValue(at.X);
        writer.WriteNumberValue(at.Y);
        writer.WriteNumberValue(at.Z);
        writer.WriteEndArray();
    }
    internal static void WriteBehavior(Utf8JsonWriter writer, K.PartCapability capability)
    {
        if (capability is null) Fail("/simulation/behaviors", "行为不能为空。");
        var name = capability switch
        {
            K.PartCapability.Propulsion => "propulsion", K.PartCapability.Control => "control",
            K.PartCapability.Storage => "storage", K.PartCapability.Production => "production",
            K.PartCapability.Extraction => "extraction", K.PartCapability.Construction => "construction",
            K.PartCapability.Power => "power", K.PartCapability.Weapon => "weapon",
            K.PartCapability.Platform => "platform", _ => throw new InvalidOperationException()
        };
        writer.WriteStartObject(name);
        switch (capability)
        {
            case K.PartCapability.Propulsion p: writer.WriteNumber("traction", p.TractionKilonewtons); writer.WriteNumber("rated_speed", p.RatedSpeedMetersPerSecond); break;
            case K.PartCapability.Storage s: writer.WriteNumber("capacity", s.Capacity); break;
            case K.PartCapability.Production p: writer.WriteNumber("work_rate", p.WorkPerSecond); break;
            case K.PartCapability.Extraction e: writer.WriteNumber("work_rate", e.WorkPerSecond); break;
            case K.PartCapability.Construction c:
                writer.WriteNumber("work_rate", c.WorkPerSecond);
                writer.WriteString("max_plot_size", DesignEvaluator.Wire(c.MaximumBuildingPlotSize));
                if (c.MaximumVehicleSize is { } maximumVehicleSize)
                    writer.WriteString("max_size_class", DesignEvaluator.Wire(maximumVehicleSize));
                break;
            case K.PartCapability.Power p: writer.WriteNumber("output", p.EnergyPerSecond); break;
            case K.PartCapability.Weapon w: writer.WriteNumber("damage", w.Damage); writer.WriteNumber("interval", w.IntervalSeconds); break;
        }
        writer.WriteEndObject();
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
    [DoesNotReturn]
    private static void Fail(string field, string message, string code = "invalid_field") => throw new DefinitionFormatException(field, message, code);
}
