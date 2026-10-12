using System.Collections.Immutable;
using System.Text.Json;
using System.Text.RegularExpressions;
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
                    var voxel = Reference(Required(entry, "voxel", field), field + "/voxel", package);
                    var orientation = entry.TryGetProperty("orientation", out var o)
                        ? (C.VoxelOrientation)System.Array.IndexOf(Orientations, Choice(o, field + "/orientation", Orientations)) : C.VoxelOrientation.PxPy;
                    C.VoxelShape? shape = entry.TryGetProperty("shape", out var s)
                        ? new C.VoxelShape(Choice(s, field + "/shape", ["cube", "half", "plate", "wedge", "corner-wedge", "beam", "cylinder"])) : null;
                    C.PaintColor? paint = null;
                    if (entry.TryGetProperty("paint", out var p))
                    {
                        var color = Text(p, field + "/paint");
                        if (!Regex.IsMatch(color, "\\A#[0-9a-f]{6}\\z", RegexOptions.CultureInvariant)) Fail(field + "/paint", "颜色必须为小写 #rrggbb。");
                        paint = new(color);
                    }
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
            if (cells.Count == 0) Fail("/layout/cells", "布局必须至少有一格。");
            var specification = Specification(root, category);
            var display = Required(root, "display", "");
            Object(display, "/display", "name", "description");
            return new(address, new(new(Display(Required(display, "name", "/display"), "/display/name", 100),
                Display(Required(display, "description", "/display"), "/display/description", 2000)),
                specification, new(palette.ToImmutable(), cells.ToImmutable(), [])), path);
        }
        catch (InvalidBlueprint exception)
        {
            package.Reject(exception.Code, path, exception.Field, exception.Message, subject);
            return null;
        }
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
            var size = Choice(Required(sim, "size_class", "/simulation"), "/simulation/size_class", ["small", "medium", "large", "extra_large", "titan"]);
            return new C.DesignSpecification.Vehicle(size switch { "small" => K.SizeClass.Small, "medium" => K.SizeClass.Medium, "large" => K.SizeClass.Large, "extra_large" => K.SizeClass.ExtraLarge, _ => K.SizeClass.Titan });
        }
        Object(sim, "/simulation", "plot_size", "building_type", "facility_tags");
        var plot = Choice(Required(sim, "plot_size", "/simulation"), "/simulation/plot_size", ["small", "medium", "large", "station"]);
        var kind = Choice(Required(sim, "building_type", "/simulation"), "/simulation/building_type", ["production", "warehouse", "power", "construction", "station", "defense"]);
        var tags = ImmutableArray.CreateBuilder<K.FacilityTag>();
        if (sim.TryGetProperty("facility_tags", out var values))
        {
            Array(values, "/simulation/facility_tags");
            foreach (var value in values.EnumerateArray())
            {
                var field = "/simulation/facility_tags/" + tags.Count;
                var tag = (K.FacilityTag)System.Array.IndexOf(FacilityTags, Choice(value, field, FacilityTags));
                if (tags.Contains(tag)) Fail(field, "设施标签不能重复。");
                tags.Add(tag);
            }
        }
        return new C.DesignSpecification.Building((K.PlotSize)System.Array.IndexOf(new[] { "small", "medium", "large", "station" }, plot),
            (K.BuildingKind)System.Array.IndexOf(new[] { "production", "warehouse", "power", "construction", "station", "defense" }, kind), tags.ToImmutable());
    }

    private static string Reference(JsonElement value, string field, LoadPackage package)
    {
        var text = Text(value, field);
        var parts = text.Split(':');
        var ns = parts.Length == 1 ? package.Metadata!.Id.Value : parts[0];
        if (parts.Length > 2 || !ContentIdentity.IsValidPart(parts[^1]) ||
            (ns != "core" && (!ModManifest.TryCanonicalUuid(ns, out var canonical) || canonical != ns)))
            Fail(field, "引用必须为 local id 或规范的 namespace:local-id。");
        if (ns != package.Metadata!.Id.Value && !package.Metadata.Dependencies.Any(d => d.Package.Value == ns))
            Fail(field, "引用的包必须在 dependencies 中声明。", "reference_missing");
        return ns + ":" + parts[^1];
    }
    private static C.GridColumn Column(JsonElement value, string field)
    {
        Array(value, field, 2);
        return new(Integer(value[0], field + "/0"), Integer(value[1], field + "/1"));
    }
    private static readonly string[] Orientations = ["px_py", "px_ny", "px_pz", "px_nz", "nx_py", "nx_ny", "nx_pz", "nx_nz", "py_px", "py_nx", "py_pz", "py_nz", "ny_px", "ny_nx", "ny_pz", "ny_nz", "pz_px", "pz_nx", "pz_py", "pz_ny", "nz_px", "nz_nx", "nz_py", "nz_ny"];
    private static readonly string[] FacilityTags = ["agriculture", "extraction", "metallurgy", "building_materials", "light_industry", "chemical", "food_processing", "assembly", "electronics", "recovery"];
    private static string Display(JsonElement value, string field, int max)
    {
        var text = Text(value, field).Trim();
        if (text.Length < 1 || text.Length > max) Fail(field, $"去掉首尾空白后长度必须为 1–{max}。");
        return text;
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
    private static void Fail(string field, string message, string code = "invalid_field") => throw new InvalidBlueprint(field, message, code);
    private sealed class InvalidBlueprint(string field, string message, string code) : Exception(message)
    {
        internal string Field { get; } = field;
        internal string Code { get; } = code;
    }
}
