using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Train.Content.Designs;
using Train.Content.Loading;
using static Train.Content.Loading.DefinitionJson;
using C = Train.Content.Contracts;
using K = Train.Core.Contracts;

namespace Train.Content.Authoring;

internal static class DefinitionDraftJson
{
    internal static C.AuthoringDefinitionAddress Address(C.DefinitionDraft draft) => draft switch
    {
        C.DefinitionDraft.Voxel v => new(C.DefinitionKind.Voxel, v.Value.LocalId),
        C.DefinitionDraft.Blueprint b => new(C.DefinitionKind.Blueprint, b.LocalId),
        C.DefinitionDraft.SettlementType t => new(C.DefinitionKind.SettlementType, t.Value.LocalId),
        C.DefinitionDraft.SettlementTemplate t => new(C.DefinitionKind.SettlementTemplate, t.Value.LocalId),
        C.DefinitionDraft.LairType lair => new(C.DefinitionKind.LairType, lair.Value.LocalId),
        _ => throw new DefinitionFormatException("/definition_kind", "此定义种类不支持创作表单。", "invalid_field")
    };

    internal static string Kind(C.DefinitionKind kind) => kind switch
    {
        C.DefinitionKind.Voxel => "voxel", C.DefinitionKind.Blueprint => "blueprint",
        C.DefinitionKind.SettlementType => "settlement-type", C.DefinitionKind.SettlementTemplate => "settlement-template",
        C.DefinitionKind.LairType => "lair-type",
        _ => throw new DefinitionFormatException("/definition_kind", "此定义种类不支持创作表单。", "invalid_field")
    };

    internal static string Path(C.AuthoringDefinitionAddress address)
    {
        var kind = Kind(address.Kind);
        if (!ContentIdentity.IsValidPart(address.LocalId) || address.LocalId == "shared")
            Fail("/id", "本地 ID 必须有效，不能为 shared。");
        return $"resources/{kind}s/{address.LocalId}.{kind}.json";
    }

    internal static JsonObject Write(C.DefinitionDraft draft, JsonNode? templateDisplay = null, bool incomplete = false)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var address = Address(draft);
        var root = new JsonObject
        {
            ["schema_version"] = address.Kind switch
            {
                C.DefinitionKind.SettlementTemplate => 2, C.DefinitionKind.LairType => 1, _ => 3
            },
            ["id"] = address.LocalId
        };
        switch (draft)
        {
            case C.DefinitionDraft.Blueprint blueprint:
                using (var stream = new MemoryStream())
                {
                    using (var writer = new Utf8JsonWriter(stream))
                    {
                        writer.WriteStartObject();
                        BlueprintParser.WriteDesign(writer, blueprint.Value, incomplete: true);
                        writer.WriteEndObject();
                    }
                    foreach (var property in JsonNode.Parse(stream.ToArray())!.AsObject()) root[property.Key] = property.Value?.DeepClone();
                }
                break;
            case C.DefinitionDraft.Voxel voxel:
                var v = voxel.Value;
                root["display"] = DisplayNode(v.Display);
                if (v.Icon is { } icon) root["display"]!["icon"] = icon.Value;
                if (v.Simulation is { } simulation)
                {
                    var sim = new JsonObject();
                    if (simulation.BaseValue is { } value) sim["value"] = value;
                    if (simulation.MassTons is { } mass) sim["mass"] = mass;
                    if (simulation.MaxDurability is { } durability) sim["max_durability"] = durability;
                    sim["tags"] = simulation.RawGood ? new JsonArray("raw_good") : new JsonArray();
                    if (simulation.FuelEnergyPerUnit is { } fuel) sim["fuel_energy"] = fuel;
                    sim["behaviors"] = Behaviors(simulation.Behaviors);
                    root["simulation"] = sim;
                }
                var appearance = new JsonObject { ["allowed_orientations"] = v.Orientations == C.AllowedVoxelOrientations.All24 ? "all_24" : DesignEvaluator.Wire(v.Orientations), ["collision"] = Collision(v.Collision) };
                if (v.Presentation is { } presentation) appearance["presentation"] = Presentation(presentation);
                var visual = new JsonObject();
                if (v.AppearanceBehaviors.Wheelset) visual["wheelset"] = new JsonObject();
                if (v.AppearanceBehaviors.Light is { } light) visual["light"] = new JsonObject { ["color"] = light.Color.Value, ["range"] = light.RangeCells };
                appearance["behaviors"] = visual;
                root["appearance"] = appearance;
                break;
            case C.DefinitionDraft.SettlementType type:
                var t = type.Value;
                if (t.PreferredTerrain.IsDefault) Fail("/simulation/preferred_terrain", "数组必须初始化。");
                var rules = new JsonObject { ["preferred_terrain"] = new JsonArray(t.PreferredTerrain.Select(id => (JsonNode?)JsonValue.Create(id.Value)).ToArray()) };
                if (t.Weight is { } weight) rules["weight"] = weight;
                if (t.MinimumPopulation is { } minimum) rules["population_min"] = minimum;
                if (t.MaximumPopulation is { } maximum) rules["population_max"] = maximum;
                root["simulation"] = rules;
                root["display"] = DisplayNode(t.Display);
                root["display"]!["major"] = t.Major;
                if (t.MapSymbol is { } symbol) root["display"]!["map_symbol"] = symbol.Value;
                break;
            case C.DefinitionDraft.SettlementTemplate template:
                var p = template.Value;
                if (p.Plots.IsDefault || p.FoundingBuildings.IsDefault || p.Inventory.IsDefault || p.ProductionPlans.IsDefault)
                    Fail("", "模板数组必须初始化。");
                if (p.Type is { } typeId) root["settlement_type"] = typeId.Value;
                if (p.Weight is { } templateWeight) root["weight"] = templateWeight;
                if (p.SceneModel is { } scene) root["scene_model"] = scene.Value;
                root["plots"] = new JsonArray(p.Plots.Select(plot =>
                {
                    var item = new JsonObject { ["id"] = plot.Number, ["size"] = DesignEvaluator.Wire(plot.Size),
                        ["at"] = new JsonArray(plot.At.X, plot.At.Y, plot.At.Z), ["orientation"] = DesignEvaluator.Wire(plot.Orientation) };
                    if (plot.PrebuiltBuilding is { } building) item["building"] = building.Value;
                    return (JsonNode?)item;
                }).ToArray());
                root["founding"] = new JsonArray(p.FoundingBuildings.Select(b => (JsonNode?)new JsonObject { ["plot"] = b.PlotNumber, ["building"] = b.Building.Value }).ToArray());
                root["inventory"] = new JsonArray(p.Inventory.Select((quantity, index) =>
                {
                    var item = new JsonObject { ["quantity"] = quantity.Quantity };
                    switch (quantity.Good)
                    {
                        case K.GoodId.Voxel good: item["voxel"] = good.Definition.Value; break;
                        case K.GoodId.Composite good: item["blueprint"] = good.Definition.Value; break;
                        default: Fail($"/inventory/{index}", "组装体构成哈希不能还原源蓝图；请手工编辑库存文件。"); break;
                    }
                    return (JsonNode?)item;
                }).ToArray());
                root["production_plans"] = new JsonArray(p.ProductionPlans.Select(plan =>
                {
                    var item = new JsonObject { ["plot"] = plan.PlotNumber, ["capability"] = DesignEvaluator.Wire(plan.Capability),
                        ["process"] = plan.Process switch { K.ProcessRef.Production process => process.Definition.Value, K.ProcessRef.Recovery process => process.Definition.Value, _ => throw new ArgumentException("未知工序类型。") },
                        ["enabled"] = plan.Enabled, ["priority"] = plan.Priority };
                    if (incomplete) item["process_kind"] = plan.Process is K.ProcessRef.Recovery ? "recovery" : "production";
                    return (JsonNode?)item;
                }).ToArray());
                if (templateDisplay is not null) root["display"] = templateDisplay.DeepClone();
                else if (!incomplete) Fail("/display", "新建模板需要契约提供显示文本；当前只能编辑已有模板。");
                break;
            case C.DefinitionDraft.LairType lair:
                WriteLair(root, lair.Value, incomplete);
                break;
        }
        return root;
    }

    internal static C.DefinitionDraft Read(JsonElement root, C.DefinitionKind kind, LoadPackage? package = null,
        Func<string, bool>? isComposite = null, Func<string, bool>? isRecovery = null)
    {
        Kind(kind);
        var expected = kind switch { C.DefinitionKind.SettlementTemplate => 2, C.DefinitionKind.LairType => 1, _ => 3 };
        if (Int32(Required(root, "schema_version", ""), "/schema_version") != expected) Fail("/schema_version", "不支持此定义版本。", "unsupported_format");
        var id = Text(Required(root, "id", ""), "/id");
        string Reference(JsonElement value, string field) => package is null ? Text(value, field) : DefinitionJson.Reference(value, field, package);
        string? OptionalReference(JsonElement value, string key, string field) => value.TryGetProperty(key, out var entry) ? Reference(entry, Child(field, key)) : null;
        C.DisplayText RawDisplay(JsonElement value, params string[] extra)
        {
            Object(value, "/display", ["name", "description", .. extra]);
            return new(Text(Required(value, "name", "/display"), "/display/name"), Text(Required(value, "description", "/display"), "/display/description"));
        }
        switch (kind)
        {
            case C.DefinitionKind.Blueprint:
                Object(root, "", "schema_version", "id", "category", "simulation", "layout", "appearance", "display");
                return new C.DefinitionDraft.Blueprint(id, BlueprintParser.ReadDesign(root, package, draft: true));
            case C.DefinitionKind.Voxel:
                Object(root, "", "schema_version", "id", "simulation", "appearance", "display");
                var display = Required(root, "display", "");
                var text = RawDisplay(display, "icon");
                C.VoxelSimulationDraft? simulation = null;
                if (root.TryGetProperty("simulation", out var sim))
                {
                    Object(sim, "/simulation", "value", "mass", "max_durability", "tags", "fuel_energy", "behaviors");
                    var tags = sim.TryGetProperty("tags", out var values) ? Items(values, "/simulation/tags").Select(t => Text(t.Value, t.Field)).ToArray() : [];
                    if (tags.Any(tag => tag != "raw_good")) Fail("/simulation/tags", "未知标签。");
                    simulation = new(OptionalLong(sim, "value", "/simulation"), OptionalNumber(sim, "mass", "/simulation"),
                        OptionalLong(sim, "max_durability", "/simulation"), tags.Contains("raw_good"), OptionalNumber(sim, "fuel_energy", "/simulation"),
                        sim.TryGetProperty("behaviors", out var behaviors) ? VoxelParser.ReadBehaviors(behaviors).Capabilities : []);
                }
                var a = Required(root, "appearance", "");
                Object(a, "/appearance", "allowed_orientations", "collision", "presentation", "behaviors");
                var orientations = a.TryGetProperty("allowed_orientations", out var o) ? Text(o, "/appearance/allowed_orientations") switch
                {
                    "none" => C.AllowedVoxelOrientations.None, "four_way" => C.AllowedVoxelOrientations.FourWay, "all_24" => C.AllowedVoxelOrientations.All24,
                    _ => throw new DefinitionFormatException("/appearance/allowed_orientations", "未知允许朝向。", "invalid_field")
                } : C.AllowedVoxelOrientations.None;
                var collision = a.TryGetProperty("collision", out var c) ? ReadCollision(c) : new C.VoxelCollision.FullBox();
                var presentation = a.TryGetProperty("presentation", out var present) ? ReadPresentation(present) : null;
                var wheelset = false;
                C.LightAppearance? light = null;
                if (a.TryGetProperty("behaviors", out var visual))
                {
                    Object(visual, "/appearance/behaviors", "wheelset", "light");
                    if (visual.TryGetProperty("wheelset", out var wheel)) { Object(wheel, "/appearance/behaviors/wheelset"); wheelset = true; }
                    if (visual.TryGetProperty("light", out var l))
                    {
                        Object(l, "/appearance/behaviors/light", "color", "range");
                        light = new(new(l.TryGetProperty("color", out var color) ? Text(color, "/appearance/behaviors/light/color") : "#ffd9a0"), OptionalNumber(l, "range", "/appearance/behaviors/light") ?? 4);
                    }
                }
                return new C.DefinitionDraft.Voxel(new(id, text, display.TryGetProperty("icon", out var icon) ? new(Text(icon, "/display/icon")) : null,
                    simulation, orientations, collision, presentation, new(wheelset, light)));
            case C.DefinitionKind.SettlementType:
                Object(root, "", "schema_version", "id", "simulation", "display");
                var rules = Required(root, "simulation", "");
                Object(rules, "/simulation", "preferred_terrain", "weight", "population_min", "population_max");
                var d = Required(root, "display", "");
                var terrain = rules.TryGetProperty("preferred_terrain", out var preferred) ? Items(preferred, "/simulation/preferred_terrain")
                    .Select(t => new K.TerrainTypeId(Reference(t.Value, t.Field))).ToImmutableArray() : [];
                return new C.DefinitionDraft.SettlementType(new(id, RawDisplay(d, "major", "map_symbol"),
                    d.TryGetProperty("map_symbol", out var symbol) ? new(Text(symbol, "/display/map_symbol")) : null,
                    d.TryGetProperty("major", out var major) && Boolean(major, "/display/major"), OptionalInt(rules, "weight", "/simulation"), terrain,
                    OptionalLong(rules, "population_min", "/simulation"), OptionalLong(rules, "population_max", "/simulation")));
            case C.DefinitionKind.SettlementTemplate:
                Object(root, "", "schema_version", "id", "settlement_type", "weight", "scene_model", "plots", "founding", "inventory", "production_plans", "display");
                var plots = Items(Required(root, "plots", ""), "/plots").Select(item =>
                {
                    Object(item.Value, item.Field, "id", "size", "at", "orientation", "building");
                    var at = Required(item.Value, "at", item.Field);
                    Array(at, item.Field + "/at", 3);
                    var position = new C.GridPosition(Int32(at[0], item.Field + "/at/0"), Int32(at[1], item.Field + "/at/1"), Int32(at[2], item.Field + "/at/2"));
                    var orientation = item.Value.TryGetProperty("orientation", out var facing) ? EnumValue<C.VoxelOrientation>(facing, item.Field + "/orientation") : C.VoxelOrientation.PxPy;
                    var building = OptionalReference(item.Value, "building", item.Field);
                    return new C.TemplatePlotDraft(Int32(Required(item.Value, "id", item.Field), item.Field + "/id"),
                        EnumValue<K.PlotSize>(Required(item.Value, "size", item.Field), item.Field + "/size"), position, orientation, building is null ? null : new(building));
                }).ToImmutableArray();
                var founding = root.TryGetProperty("founding", out var f) ? Items(f, "/founding").Select(item =>
                {
                    Object(item.Value, item.Field, "plot", "building");
                    return new C.FoundingBuildingDraft(Int32(Required(item.Value, "plot", item.Field), item.Field + "/plot"), new(Reference(Required(item.Value, "building", item.Field), item.Field + "/building")));
                }).ToImmutableArray() : [];
                var inventory = root.TryGetProperty("inventory", out var goods) ? Items(goods, "/inventory").Select(item =>
                {
                    Object(item.Value, item.Field, "voxel", "blueprint", "quantity");
                    var voxel = item.Value.TryGetProperty("voxel", out var good);
                    if (voxel == item.Value.TryGetProperty("blueprint", out var blueprint)) Fail(item.Field, "库存必须且只能指定 voxel 或 blueprint。");
                    var reference = Reference(voxel ? good : blueprint, item.Field + (voxel ? "/voxel" : "/blueprint"));
                    if (!voxel && isComposite is not null && !isComposite(reference))
                        Fail(item.Field, "无法无损映射此库存蓝图；请手工编辑文件。");
                    K.GoodId key = voxel ? new K.GoodId.Voxel(new(reference)) : new K.GoodId.Composite(new(reference));
                    return new K.GoodsQuantity(key, Int64(Required(item.Value, "quantity", item.Field), item.Field + "/quantity"));
                }).ToImmutableArray() : [];
                var plans = root.TryGetProperty("production_plans", out var planValues) ? Items(planValues, "/production_plans").Select(item =>
                {
                    Object(item.Value, item.Field, package is null ? ["plot", "capability", "process", "enabled", "priority", "process_kind"] : ["plot", "capability", "process", "enabled", "priority"]);
                    var process = Reference(Required(item.Value, "process", item.Field), item.Field + "/process");
                    var recovery = item.Value.TryGetProperty("process_kind", out var processKind) ? Text(processKind, item.Field + "/process_kind") switch
                    {
                        "recovery" => true, "production" => false, _ => throw new DefinitionFormatException(item.Field + "/process_kind", "未知工序类型。", "invalid_field")
                    } : isRecovery?.Invoke(process) == true;
                    K.ProcessRef reference = recovery ? new K.ProcessRef.Recovery(new(process)) : new K.ProcessRef.Production(new(process));
                    return new K.TemplateProductionPlan(Int32(Required(item.Value, "plot", item.Field), item.Field + "/plot"),
                        EnumValue<K.CapabilityKind>(Required(item.Value, "capability", item.Field), item.Field + "/capability"), reference,
                        Boolean(Required(item.Value, "enabled", item.Field), item.Field + "/enabled"), Int32(Required(item.Value, "priority", item.Field), item.Field + "/priority"));
                }).ToImmutableArray() : [];
                var type = OptionalReference(root, "settlement_type", "");
                var scene = OptionalReference(root, "scene_model", "");
                return new C.DefinitionDraft.SettlementTemplate(new(id, type is null ? null : new(type), OptionalInt(root, "weight", "") ?? (package is null ? null : 1),
                    scene is null ? null : new(scene), plots, founding, inventory, plans));
            case C.DefinitionKind.LairType:
                return new C.DefinitionDraft.LairType(ReadLair(root, Reference));
            default: throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    private static void WriteLair(JsonObject root, C.LairTypeDraft value, bool incomplete)
    {
        if (value.Members.IsDefault) Fail("/members", "数组必须初始化。");
        root["display"] = DisplayNode(value.Display);
        WriteOptional(root, "generation_weight", value.GenerationWeight, "/generation_weight", incomplete, 1, false);
        var members = new JsonArray();
        var creatures = ImmutableArray.CreateBuilder<K.CreatureDefinitionId>();
        for (var index = 0; index < value.Members.Length; index++)
        {
            if (value.Members[index] is not { } member)
                Fail("/members", "成员不能为空。");
            else
            {
                if (member.Weight < 1) Fail($"/members/{index}/weight", "必须为范围内的有限数值。");
                creatures.Add(member.Creature);
                members.Add(new JsonObject { ["creature"] = member.Creature.Value, ["weight"] = member.Weight });
            }
        }
        Unique(creatures, "/members");
        if (!incomplete && members.Count == 0) Fail("/members", "成员不能为空。");
        root["members"] = members;
        WriteOptional(root, "maximum_members", value.MaximumMembers, "/maximum_members", incomplete, 1, false);
        WriteOptional(root, "spawn_interval_seconds", value.SpawnIntervalSeconds, "/spawn_interval_seconds", incomplete, null, true);
        WriteOptional(root, "raid_interval_seconds", value.RaidIntervalSeconds, "/raid_interval_seconds", incomplete, null, true);
        WriteOptional(root, "roaming_radius_hops", value.RoamingRadiusHops, "/roaming_radius_hops", incomplete, 0, false);
        WriteOptional(root, "raid_radius_hops", value.RaidRadiusHops, "/raid_radius_hops", incomplete, 0, false);
        WriteOptional(root, "rail_damage_work_seconds", value.RailDamageWorkSeconds, "/rail_damage_work_seconds", incomplete, null, true);
        WriteOptional(root, "clearance_reward", value.ClearanceReward, "/clearance_reward", incomplete, 0L, false);
        if (value.SceneModel is { } scene) root["scene_model"] = scene.Value;
        else if (!incomplete) Fail("/scene_model", "场景模型尚未指定。");
    }

    private static void WriteOptional<T>(JsonObject root, string key, T? value, string field, bool incomplete, T? minimum, bool positive)
        where T : struct, IComparable<T>
    {
        if (value is null)
        {
            if (!incomplete) Fail(field, "字段尚未指定。");
            return;
        }
        if (minimum is { } lower && value.Value.CompareTo(lower) < 0 || positive && value.Value.CompareTo(default) <= 0 ||
            value is double number && !double.IsFinite(number))
            Fail(field, "必须为范围内的有限数值。");
        root[key] = JsonValue.Create(value.Value);
    }

    private static C.LairTypeDraft ReadLair(JsonElement root, Func<JsonElement, string, string> reference)
    {
        Object(root, "", "schema_version", "id", "display", "generation_weight", "members", "maximum_members",
            "spawn_interval_seconds", "raid_interval_seconds", "roaming_radius_hops", "raid_radius_hops",
            "rail_damage_work_seconds", "clearance_reward", "scene_model");
        var members = root.TryGetProperty("members", out var values) ? Items(values, "/members").Select(item =>
        {
            Object(item.Value, item.Field, "creature", "weight");
            return new K.WeightedCreature(new(reference(Required(item.Value, "creature", item.Field), item.Field + "/creature")),
                Int32(Required(item.Value, "weight", item.Field), item.Field + "/weight", 1));
        }).ToImmutableArray() : [];
        Unique(members.Select(member => member.Creature), "/members");
        var scene = root.TryGetProperty("scene_model", out var model) ? reference(model, "/scene_model") : null;
        return new(Text(Required(root, "id", ""), "/id"), RawDisplay(Required(root, "display", "")),
            scene is null ? null : new(scene), OptionalMinimum(root, "generation_weight", "", 1), members,
            OptionalMinimum(root, "maximum_members", "", 1), OptionalPositive(root, "spawn_interval_seconds", ""),
            OptionalPositive(root, "raid_interval_seconds", ""), OptionalMinimum(root, "roaming_radius_hops", "", 0),
            OptionalMinimum(root, "raid_radius_hops", "", 0), OptionalPositive(root, "rail_damage_work_seconds", ""),
            OptionalLongMinimum(root, "clearance_reward", "", 0));
    }

    private static C.DisplayText RawDisplay(JsonElement value, params string[] extra)
    {
        Object(value, "/display", ["name", "description", .. extra]);
        return new(Text(Required(value, "name", "/display"), "/display/name"), Text(Required(value, "description", "/display"), "/display/description"));
    }

    private static int? OptionalMinimum(JsonElement root, string key, string field, int minimum) =>
        root.TryGetProperty(key, out var value) ? Int32(value, Child(field, key), minimum) : null;
    private static long? OptionalLongMinimum(JsonElement root, string key, string field, long minimum) =>
        root.TryGetProperty(key, out var value) ? Int64(value, Child(field, key), minimum) : null;
    private static double? OptionalPositive(JsonElement root, string key, string field) =>
        root.TryGetProperty(key, out var value) ? Number(value, Child(field, key), positive: true) : null;

    private static JsonObject DisplayNode(C.DisplayText display) => new() { ["name"] = display.Name, ["description"] = display.Description };
    private static long? OptionalLong(JsonElement root, string key, string field) => root.TryGetProperty(key, out var value) ? Int64(value, Child(field, key)) : null;
    private static int? OptionalInt(JsonElement root, string key, string field) => root.TryGetProperty(key, out var value) ? Int32(value, Child(field, key)) : null;
    private static double? OptionalNumber(JsonElement root, string key, string field) => root.TryGetProperty(key, out var value) ? Number(value, Child(field, key)) : null;
    private static JsonNode Behaviors(ImmutableArray<K.PartCapability> behaviors)
    {
        if (behaviors.IsDefault) Fail("/simulation/behaviors", "数组必须初始化。");
        if (behaviors.Any(b => b is null)) Fail("/simulation/behaviors", "行为不能为空。");
        Unique(behaviors.Select(b => b.GetType()), "/simulation/behaviors");
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var behavior in behaviors) BlueprintParser.WriteBehavior(writer, behavior);
            writer.WriteEndObject();
        }
        return JsonNode.Parse(stream.ToArray())!;
    }

    private static JsonObject Collision(C.VoxelCollision collision) => collision switch
    {
        C.VoxelCollision.None => new() { ["kind"] = "none" }, C.VoxelCollision.FullBox => new() { ["kind"] = "full_box" },
        C.VoxelCollision.LocalBox box => new() { ["kind"] = "local_box", ["offset"] = Point(box.Offset), ["size"] = Point(box.Size) },
        _ => throw new ArgumentException("未知碰撞类型。")
    };
    private static JsonObject Point(C.LocalPoint point) => new() { ["x"] = point.X, ["y"] = point.Y, ["z"] = point.Z };
    private static C.VoxelCollision ReadCollision(JsonElement root)
    {
        var kind = Text(Required(root, "kind", "/appearance/collision"), "/appearance/collision/kind");
        Object(root, "/appearance/collision", kind == "local_box" ? ["kind", "offset", "size"] : ["kind"]);
        return kind switch
        {
            "none" => new C.VoxelCollision.None(), "full_box" => new C.VoxelCollision.FullBox(),
            "local_box" => new C.VoxelCollision.LocalBox(ReadPoint(Required(root, "offset", "/appearance/collision"), "/appearance/collision/offset"), ReadPoint(Required(root, "size", "/appearance/collision"), "/appearance/collision/size")),
            _ => throw new DefinitionFormatException("/appearance/collision/kind", "未知碰撞类型。", "invalid_field")
        };
    }
    private static C.LocalPoint ReadPoint(JsonElement root, string field)
    {
        Object(root, field, "x", "y", "z");
        return new(Number(Required(root, "x", field), field + "/x"), Number(Required(root, "y", field), field + "/y"), Number(Required(root, "z", field), field + "/z"));
    }
    private static JsonObject Presentation(C.AuthoringVoxelPresentation presentation)
    {
        if (presentation is C.AuthoringVoxelPresentation.Cube { Shapes.IsDefault: true }) Fail("/appearance/presentation/shapes", "数组必须初始化。");
        if (presentation is C.AuthoringVoxelPresentation.Mesh { SurfaceMaterials.IsDefault: true }) Fail("/appearance/presentation/surface_materials", "数组必须初始化。");
        return presentation switch
        {
        C.AuthoringVoxelPresentation.Cube cube => new() { ["kind"] = "cube", ["shapes"] = new JsonArray(cube.Shapes.Select(s => (JsonNode?)JsonValue.Create(s.Name)).ToArray()),
            ["material"] = DesignEvaluator.Wire(cube.Material), ["tile_span"] = cube.TileSpan,
            ["faces"] = cube.Front is { } front ? new JsonObject { ["top"] = cube.Top.Value, ["side"] = cube.Side.Value, ["bottom"] = cube.Bottom.Value, ["front"] = front.Value }
                : new JsonObject { ["top"] = cube.Top.Value, ["side"] = cube.Side.Value, ["bottom"] = cube.Bottom.Value } },
        C.AuthoringVoxelPresentation.Mesh mesh => new() { ["kind"] = "mesh", ["model"] = mesh.Model.Value,
            ["surface_materials"] = new JsonArray(mesh.SurfaceMaterials.Select(m => (JsonNode?)JsonValue.Create(DesignEvaluator.Wire(m))).ToArray()) },
        _ => throw new ArgumentException("未知表现类型。")
        };
    }
    private static C.AuthoringVoxelPresentation ReadPresentation(JsonElement root)
    {
        const string field = "/appearance/presentation";
        var kind = Text(Required(root, "kind", field), field + "/kind");
        if (kind == "mesh")
        {
            Object(root, field, "kind", "model", "surface_materials");
            return new C.AuthoringVoxelPresentation.Mesh(new(Text(Required(root, "model", field), field + "/model")),
                Items(Required(root, "surface_materials", field), field + "/surface_materials").Select(m => EnumValue<C.MaterialCategory>(m.Value, m.Field)).ToImmutableArray());
        }
        if (kind != "cube") Fail(field + "/kind", "未知表现类型。");
        Object(root, field, "kind", "shapes", "material", "tile_span", "faces");
        var faces = Required(root, "faces", field);
        Object(faces, field + "/faces", "top", "side", "bottom", "front");
        C.AuthoringAssetPath Face(string key) => new(Text(Required(faces, key, field + "/faces"), field + "/faces/" + key));
        return new C.AuthoringVoxelPresentation.Cube(root.TryGetProperty("shapes", out var shapes)
            ? Items(shapes, field + "/shapes").Select(s => new C.VoxelShape(Text(s.Value, s.Field))).ToImmutableArray() : [new("cube")],
            root.TryGetProperty("material", out var material) ? EnumValue<C.MaterialCategory>(material, field + "/material") : C.MaterialCategory.PaintedMetal,
            OptionalInt(root, "tile_span", field) ?? 1, Face("top"), Face("side"), Face("bottom"), faces.TryGetProperty("front", out var front) ? new(Text(front, field + "/faces/front")) : null);
    }
}
