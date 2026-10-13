using System.Collections.Immutable;
using System.Text.Json;
using System.Text.RegularExpressions;
using C = Train.Content.Contracts;
using K = Train.Core.Contracts;

namespace Train.Content.Loading;

internal sealed record ParsedVoxel(K.VoxelDefinitionId Definition, K.GoodDefinitionProjection? Good,
    K.SemanticRevision Revision, long MaxDurability, ImmutableArray<K.PartCapability> Capabilities,
    C.DisplayText Display, string Icon,
    C.AllowedVoxelOrientations Orientations, C.VoxelCollision Collision,
    ParsedPresentation Presentation, C.VoxelAppearanceBehaviors Behaviors);
internal abstract record ParsedPresentation
{
    internal sealed record Cube(ImmutableArray<C.VoxelShape> Shapes, C.MaterialCategory Material,
        int TileSpan, string Top, string Side, string Bottom, string Front) : ParsedPresentation;
    internal sealed record Mesh(string Model, ImmutableArray<C.MaterialCategory> SurfaceMaterials) : ParsedPresentation;
}

internal sealed class VoxelParser
{
    internal ParsedVoxel? Parse(byte[] bytes, LoadPackage package, string path)
    {
        using var document = ContentDocument.Parse(bytes, package, path);
        if (document is null) return null;
        C.ContentReference? subject = null;
        try
        {
            var root = document.RootElement;
            Object(root, "", "schema_version", "id", "simulation", "appearance", "display");
            if (Integer(Required(root, "schema_version", ""), "/schema_version", 3) != 3) Fail("/schema_version", "必须为 3。");
            var id = Text(Required(root, "id", ""), "/id");
            if (!ContentIdentity.IsValidPart(id) || id == "shared" || Path.GetFileName(path) != id + ".voxel.json")
                Fail("/id", "local id 必须有效且与文件名一致，不能是 shared。");
            var definition = new K.VoxelDefinitionId(package.Metadata!.Id.Value + ":" + id);
            subject = new C.ContentReference.Core(new K.CoreDefinitionReference.Voxel(definition));
            var semantic = Map(("encoding_version", 1), ("id", definition.Value), ("kind", "voxel"));
            (int Price, double Mass, ImmutableArray<string> Tags, double? FuelEnergy)? simulation = null;
            var maxDurability = 0L;
            var capabilities = ImmutableArray.CreateBuilder<K.PartCapability>();
            if (root.TryGetProperty("simulation", out var sim))
            {
                Object(sim, "/simulation", "value", "mass", "max_durability", "tags", "fuel_energy", "behaviors");
                var value = Integer(Required(sim, "value", "/simulation"), "/simulation/value", 1000000000);
                var mass = Positive(Required(sim, "mass", "/simulation"), "/simulation/mass", 1000000000);
                var durability = Integer(Required(sim, "max_durability", "/simulation"), "/simulation/max_durability", 1000000000);
                maxDurability = durability;
                var tags = sim.TryGetProperty("tags", out var tagValue) ? Strings(tagValue, "/simulation/tags", ["raw_good"], false) : ImmutableArray<string>.Empty;
                double? fuel = sim.TryGetProperty("fuel_energy", out var fuelValue) ? Positive(fuelValue, "/simulation/fuel_energy", 1000000000) : null;
                var behaviorData = Map();
                if (sim.TryGetProperty("behaviors", out var behaviors))
                {
                    Object(behaviors, "/simulation/behaviors", "propulsion", "control", "storage", "production", "extraction", "construction", "power", "weapon", "platform");
                    foreach (var property in behaviors.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                    {
                        var field = Child("/simulation/behaviors", property.Name);
                        var b = property.Value;
                        var data = Map();
                        double Number(string key) { var v = Positive(Required(b, key, field), field + "/" + key); data[key] = v; return v; }
                        int Int(string key) { var v = Integer(Required(b, key, field), field + "/" + key); data[key] = v; return v; }
                        switch (property.Name)
                        {
                            case "propulsion":
                                Object(b, field, "traction", "rated_speed");
                                capabilities.Add(new K.PartCapability.Propulsion(Number("traction"), Number("rated_speed")));
                                break;
                            case "control": Object(b, field); capabilities.Add(new K.PartCapability.Control()); break;
                            case "storage": Object(b, field, "capacity"); capabilities.Add(new K.PartCapability.Storage(Int("capacity"))); break;
                            case "production": Object(b, field, "work_rate"); capabilities.Add(new K.PartCapability.Production(Number("work_rate"))); break;
                            case "extraction": Object(b, field, "work_rate"); capabilities.Add(new K.PartCapability.Extraction(Number("work_rate"))); break;
                            case "construction":
                                Object(b, field, "work_rate", "max_size_class");
                                var workRate = Number("work_rate");
                                var size = Choice(Required(b, "max_size_class", field), field + "/max_size_class", ["small", "medium", "large", "extra_large", "titan"]);
                                data["max_size_class"] = size;
                                capabilities.Add(new K.PartCapability.Construction(workRate, size switch
                                {
                                    "small" => K.SizeClass.Small, "medium" => K.SizeClass.Medium,
                                    "large" => K.SizeClass.Large, "extra_large" => K.SizeClass.ExtraLarge,
                                    _ => K.SizeClass.Titan
                                }));
                                break;
                            case "power": Object(b, field, "output"); capabilities.Add(new K.PartCapability.Power(Number("output"))); break;
                            case "weapon": Object(b, field, "damage", "interval"); capabilities.Add(new K.PartCapability.Weapon(Int("damage"), Number("interval"))); break;
                            case "platform": Object(b, field); capabilities.Add(new K.PartCapability.Platform()); break;
                        }
                        behaviorData[property.Name] = data;
                    }
                }
                simulation = (value, mass, tags, fuel);
                semantic["simulation"] = Map(("behaviors", behaviorData), ("fuel_energy", fuel), ("mass", mass), ("max_durability", durability), ("tags", tags.Order(StringComparer.Ordinal).ToArray()), ("value", value));
            }
            else semantic["simulation"] = null;
            var appearance = Required(root, "appearance", "");
            Object(appearance, "/appearance", "allowed_orientations", "collision", "presentation", "behaviors");
            var orientation = appearance.TryGetProperty("allowed_orientations", out var o) ? Choice(o, "/appearance/allowed_orientations", ["none", "four_way", "all_24"]) : "none";
            semantic["allowed_orientations"] = orientation;
            var orientations = orientation switch { "four_way" => C.AllowedVoxelOrientations.FourWay, "all_24" => C.AllowedVoxelOrientations.All24, _ => C.AllowedVoxelOrientations.None };
            C.VoxelCollision collision = new C.VoxelCollision.FullBox();
            if (appearance.TryGetProperty("collision", out var c))
            {
                var kind = Choice(Required(c, "kind", "/appearance/collision"), "/appearance/collision/kind", ["none", "full_box", "local_box"]);
                if (kind == "local_box")
                {
                    Object(c, "/appearance/collision", "kind", "offset", "size");
                    var offset = Point(Required(c, "offset", "/appearance/collision"), "/appearance/collision/offset", false);
                    var size = Point(Required(c, "size", "/appearance/collision"), "/appearance/collision/size", true);
                    // 较大值不小于 0.5 时差值精确，避免加法舍入吞掉越界量。
                    if (Math.Min(offset.X, size.X) > 1 - Math.Max(offset.X, size.X) ||
                        Math.Min(offset.Y, size.Y) > 1 - Math.Max(offset.Y, size.Y) ||
                        Math.Min(offset.Z, size.Z) > 1 - Math.Max(offset.Z, size.Z)) Fail("/appearance/collision", "碰撞盒必须在格内。");
                    collision = new C.VoxelCollision.LocalBox(offset, size);
                }
                else { Object(c, "/appearance/collision", "kind"); collision = kind == "none" ? new C.VoxelCollision.None() : new C.VoxelCollision.FullBox(); }
            }
            var presentation = Presentation(Required(appearance, "presentation", "/appearance"));
            var wheelset = false;
            C.LightAppearance? light = null;
            if (appearance.TryGetProperty("behaviors", out var visual))
            {
                Object(visual, "/appearance/behaviors", "wheelset", "light");
                if (visual.TryGetProperty("wheelset", out var w)) { Object(w, "/appearance/behaviors/wheelset"); wheelset = true; }
                if (visual.TryGetProperty("light", out var l))
                {
                    Object(l, "/appearance/behaviors/light", "color", "range");
                    var color = l.TryGetProperty("color", out var colorValue) ? Text(colorValue, "/appearance/behaviors/light/color") : "#ffd9a0";
                    if (!Regex.IsMatch(color, "\\A#[0-9a-f]{6}\\z", RegexOptions.CultureInvariant)) Fail("/appearance/behaviors/light/color", "颜色必须为小写 #rrggbb。");
                    light = new(new C.PaintColor(color), l.TryGetProperty("range", out var range) ? Positive(range, "/appearance/behaviors/light/range") : 4);
                }
            }
            var display = Required(root, "display", "");
            Object(display, "/display", "name", "description", "icon");
            var name = Display(Required(display, "name", "/display"), "/display/name", 100);
            var description = Display(Required(display, "description", "/display"), "/display/description", 2000);
            var icon = Asset(Required(display, "icon", "/display"), "/display/icon", "png");
            var revision = new K.SemanticRevision(ContentHash.FromCanonicalJson(writer => JsonSerializer.Serialize(writer, semantic)));
            K.GoodDefinitionProjection? good = simulation is null ? null : new(new K.GoodId.Voxel(definition), revision, new K.Money(simulation.Value.Price), simulation.Value.Mass, simulation.Value.Tags.Contains("raw_good"), simulation.Value.FuelEnergy);
            return new(definition, good, revision, maxDurability, capabilities.ToImmutable(), new C.DisplayText(name, description), icon, orientations, collision, presentation, new(wheelset, light));
        }
        catch (InvalidVoxel exception)
        {
            package.Reject(exception.Field == "/schema_version" ? "unsupported_format" : "invalid_field",
                path, exception.Field, exception.Message, subject);
            return null;
        }
    }

    private static ParsedPresentation Presentation(JsonElement p)
    {
        const string field = "/appearance/presentation";
        var kind = Choice(Required(p, "kind", field), field + "/kind", ["cube", "mesh"]);
        if (kind == "mesh")
        {
            Object(p, field, "kind", "model", "surface_materials");
            var materials = Required(p, "surface_materials", field);
            if (materials.ValueKind != JsonValueKind.Array || materials.GetArrayLength() == 0) Fail(field + "/surface_materials", "必须为非空材质数组。");
            return new ParsedPresentation.Mesh(Asset(Required(p, "model", field), field + "/model", "glb"), materials.EnumerateArray().Select((v, index) => Material(v, field + "/surface_materials/" + index)).ToImmutableArray());
        }
        Object(p, field, "kind", "shapes", "material", "tile_span", "faces");
        ImmutableArray<string> shapes = p.TryGetProperty("shapes", out var s) ? Strings(s, field + "/shapes", ["cube", "half", "plate", "wedge", "corner-wedge", "beam", "cylinder"], true) : ["cube"];
        var material = p.TryGetProperty("material", out var m) ? Material(m, field + "/material") : C.MaterialCategory.PaintedMetal;
        var tile = p.TryGetProperty("tile_span", out var t) ? Integer(t, field + "/tile_span", 8) : 1;
        if (tile is not (1 or 2 or 4 or 8)) Fail(field + "/tile_span", "跨度必须为 1、2、4 或 8。");
        var faces = Required(p, "faces", field);
        Object(faces, field + "/faces", "top", "side", "bottom", "front");
        string Face(string key) => Asset(Required(faces, key, field + "/faces"), field + "/faces/" + key, "png");
        var side = Face("side");
        return new ParsedPresentation.Cube(shapes.Select(v => new C.VoxelShape(v)).ToImmutableArray(), material, tile, Face("top"), side, Face("bottom"), faces.TryGetProperty("front", out var front) ? Asset(front, field + "/faces/front", "png") : side);
    }
    private static C.MaterialCategory Material(JsonElement value, string field) => (C.MaterialCategory)Array.IndexOf(Materials, Choice(value, field, Materials));
    private static readonly string[] Materials = ["painted_metal", "bare_metal", "concrete", "stone_soil", "glass", "polymer_rubber", "organic"];
    private static C.LocalPoint Point(JsonElement value, string field, bool size)
    {
        Object(value, field, "x", "y", "z");
        double Axis(string key)
        {
            var v = Required(value, key, field);
            if (v.ValueKind != JsonValueKind.Number || !v.TryGetDouble(out var n) || !double.IsFinite(n) || n < 0 || n > 1 || (size && (n == 0 || !VoxelCollisionValidation.TryDeriveSize(n, out _)))) Fail(field + "/" + key, "必须为格内有限数值，边长须能表示为正 float。");
            return v.GetDouble();
        }
        return new(Axis("x"), Axis("y"), Axis("z"));
    }
    private static string Asset(JsonElement value, string field, string extension)
    {
        var text = Text(value, field);
        if (!Regex.IsMatch(text, "\\A(shared/)?[a-z0-9][a-z0-9_-]*\\." + extension + "\\z", RegexOptions.CultureInvariant)) Fail(field, "资产路径必须是同名目录文件或 shared/ 文件，且扩展名正确。");
        return text;
    }
    private static string Display(JsonElement value, string field, int max)
    {
        var text = Text(value, field).Trim();
        if (text.Length < 1 || text.Length > max) Fail(field, $"去掉首尾空白后长度必须为 1–{max}。");
        return text;
    }
    private static ImmutableArray<string> Strings(JsonElement value, string field, string[] choices, bool nonempty)
    {
        if (value.ValueKind != JsonValueKind.Array || (nonempty && value.GetArrayLength() == 0)) Fail(field, "必须为有效数组。");
        var result = ImmutableArray.CreateBuilder<string>();
        foreach (var item in value.EnumerateArray())
        {
            var elementField = field + "/" + result.Count;
            var text = Choice(item, elementField, choices);
            if (result.Contains(text)) Fail(elementField, "数组不能重复。");
            result.Add(text);
        }
        return result.ToImmutable();
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
    private static int Integer(JsonElement value, string field, int max = int.MaxValue)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number) || number < 1 || number > max) Fail(field, $"必须为 1–{max} 的整数。");
        return value.GetInt32();
    }
    private static double Positive(JsonElement value, string field, double max = double.MaxValue)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number) || number <= 0 || number > max) Fail(field, "必须为范围内的有限正数。");
        return value.GetDouble();
    }
    private static JsonElement Required(JsonElement value, string key, string field)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(key, out var result)) Fail(Child(field, key), "缺少必填字段或父字段不是对象。");
        return value.GetProperty(key);
    }
    private static void Object(JsonElement value, string field, params string[] keys)
    {
        if (value.ValueKind != JsonValueKind.Object) Fail(field, "必须为对象。");
        foreach (var property in value.EnumerateObject())
            if (!keys.Contains(property.Name, StringComparer.Ordinal)) Fail(Child(field, property.Name), "未知字段。");
    }
    private static string Child(string field, string key) => field + "/" + key.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
    private static SortedDictionary<string, object?> Map(params (string Key, object? Value)[] entries) => new(entries.ToDictionary(e => e.Key, e => e.Value), StringComparer.Ordinal);
    private static void Fail(string field, string message) => throw new InvalidVoxel(field, message);
    private sealed class InvalidVoxel(string field, string message) : Exception(message) { internal string Field { get; } = field; }
}
