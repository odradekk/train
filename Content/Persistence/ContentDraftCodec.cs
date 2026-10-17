using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Train.Content.Loading;
using static Train.Content.Loading.DefinitionJson;
using C = Train.Content.Contracts;
using K = Train.Core.Contracts;

namespace Train.Content.Persistence;

internal static class ContentDraftCodec
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    internal static string Encode(C.SavedContentDraft draft)
    {
        if (draft is null) Fail("", "必须为草稿对象。");
        if (draft.Id.Value == Guid.Empty) Fail("/id", "草稿身份不能为空。");
        if (draft.Payload is C.DraftPayload.Definition || draft.Target is C.DraftTarget.PackageDefinition)
            throw new NotImplementedException();
        if (draft.Payload is not C.DraftPayload.Design) Fail("/payload", "必须为设计草稿。");
        Validate(((C.DraftPayload.Design)draft.Payload).Value, "/payload/value", new(ReferenceEqualityComparer.Instance));
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("format_version", 1);
            writer.WriteString("id", draft.Id.Value);
            writer.WriteNumber("revision", draft.Revision.Value);
            writer.WriteStartObject("target");
            switch (draft.Target)
            {
                case C.DraftTarget.Instance instance:
                    writer.WriteString("kind", "instance");
                    writer.WriteStartObject("instance");
                    switch (instance.Id)
                    {
                        case K.InstanceId.Vehicle vehicle: writer.WriteString("kind", "vehicle"); writer.WriteNumber("id", vehicle.Id.Value); break;
                        case K.InstanceId.Building building: writer.WriteString("kind", "building"); writer.WriteNumber("id", building.Id.Value); break;
                        default: Fail("/target/instance", "未知实例类型。"); break;
                    }
                    writer.WriteEndObject();
                    break;
                case C.DraftTarget.LocalBlueprint local: WriteLibrary(writer, "local_blueprint", local.Id); break;
                case C.DraftTarget.UserBlueprint user: WriteLibrary(writer, "user_blueprint", user.Id); break;
                default: Fail("/target", "未知草稿目标。"); break;
            }
            writer.WriteEndObject();
            writer.WriteStartObject("payload");
            writer.WriteString("kind", "design");
            writer.WritePropertyName("value");
            try { JsonSerializer.Serialize(writer, ((C.DraftPayload.Design)draft.Payload).Value, Options); }
            catch (ArgumentException exception) { throw new DefinitionFormatException("/payload/value", exception.Message, "invalid_field"); }
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    internal static C.SavedContentDraft Decode(string payload)
    {
        using var document = ContentCapture.Parse(payload);
        var root = document.RootElement;
        if (Int32(Required(root, "format_version", ""), "/format_version") != 1)
            Fail("/format_version", "只支持草稿格式 1。", "unsupported_format");
        Object(root, "", "format_version", "id", "revision", "target", "payload");
        CheckNumbers(root, "");
        var idText = Text(Required(root, "id", ""), "/id");
        if (!Guid.TryParseExact(idText, "D", out var id) || id == Guid.Empty) Fail("/id", "草稿身份必须是非空 UUID。");
        var revision = UInt64(Required(root, "revision", ""), "/revision");
        var target = Required(root, "target", "");
        var kind = Text(Required(target, "kind", "/target"), "/target/kind");
        C.DraftTarget destination;
        if (kind == "package_definition") throw new NotImplementedException();
        if (kind == "instance")
        {
            Object(target, "/target", "kind", "instance");
            var instance = Required(target, "instance", "/target");
            Object(instance, "/target/instance", "kind", "id");
            var instanceId = UInt64(Required(instance, "id", "/target/instance"), "/target/instance/id");
            destination = Text(Required(instance, "kind", "/target/instance"), "/target/instance/kind") switch
            {
                "vehicle" => new C.DraftTarget.Instance(new K.InstanceId.Vehicle(new(instanceId))),
                "building" => new C.DraftTarget.Instance(new K.InstanceId.Building(new(instanceId))),
                _ => throw new DefinitionFormatException("/target/instance/kind", "未知实例类型。", "invalid_field")
            };
        }
        else
        {
            Object(target, "/target", "kind", "id");
            var value = Required(target, "id", "/target");
            C.BlueprintId? blueprint = null;
            if (value.ValueKind != JsonValueKind.Null)
            {
                var text = Text(value, "/target/id");
                if (!Guid.TryParseExact(text, "D", out var uuid) || uuid == Guid.Empty) Fail("/target/id", "蓝图身份必须是非空 UUID。");
                blueprint = new(uuid.ToString("D"));
            }
            destination = kind switch
            {
                "local_blueprint" => new C.DraftTarget.LocalBlueprint(blueprint),
                "user_blueprint" => new C.DraftTarget.UserBlueprint(blueprint),
                _ => throw new DefinitionFormatException("/target/kind", "未知草稿目标。", "invalid_field")
            };
        }
        var content = Required(root, "payload", "");
        Object(content, "/payload", "kind", "value");
        var payloadKind = Text(Required(content, "kind", "/payload"), "/payload/kind");
        if (payloadKind == "definition") throw new NotImplementedException();
        if (payloadKind != "design") Fail("/payload/kind", "未知草稿类型。");
        var design = Required(content, "value", "/payload").Deserialize<C.DesignDraft>(Options)
            ?? throw new DefinitionFormatException("/payload/value", "必须为设计对象。", "invalid_field");
        Validate(design, "/payload/value", new(ReferenceEqualityComparer.Instance));
        return new(new(id), new(revision), destination, new C.DraftPayload.Design(design));
    }

    private static void Validate(C.DesignDraft draft, string field, HashSet<C.DesignDraft> active)
    {
        if (draft is null) Fail(field, "必须为设计对象。");
        if (active.Count >= 64 || !active.Add(draft)) Fail(field, "设计嵌套过深或包含循环。");
        if (draft.Display is null || draft.Display.Name is null || draft.Display.Description is null)
            Fail(field + "/display", "显示文本必须为字符串。");
        if (draft.Specification is null) Fail(field + "/specification", "必须指定设计类别。");
        if (draft.Specification is C.DesignSpecification.Building building)
            ValidateArray(building.FacilityTags, field + "/specification/facility_tags");
        if (draft.Specification is C.DesignSpecification.FunctionalComposite composite)
            ValidateArray(composite.Behaviors, field + "/specification/behaviors");
        if (draft.Layout is null) Fail(field + "/layout", "必须为布局对象。");
        ValidateArray(draft.Layout.Palette, field + "/layout/palette");
        ValidateArray(draft.Layout.Cells, field + "/layout/cells");
        ValidateArray(draft.Layout.NestedItems, field + "/layout/nested_items");
        foreach (var entry in draft.Layout.Palette)
            if (entry.Voxel.Value is null || entry.Shape is { Name: null } || entry.Paint is { Value: null })
                Fail(field + "/layout/palette", "引用、形状及涂装必须为字符串。");
        for (var index = 0; index < draft.Layout.NestedItems.Length; index++)
        {
            var item = draft.Layout.NestedItems[index];
            var nested = field + "/layout/nested_items/" + index;
            switch (item)
            {
                case C.NestedDesignItem.AssemblyCopy copy: Validate(copy.Design, nested + "/design", active); break;
                case C.NestedDesignItem.SceneModelCopy copy: Validate(copy.Design, nested + "/design", active); break;
                case C.NestedDesignItem.FunctionalCompositeReference reference:
                    if (reference.Definition.Value is null || reference.Paint is { Value: null })
                        Fail(nested, "引用及涂装必须为字符串。");
                    break;
            }
        }
        active.Remove(draft);
    }

    private static void ValidateArray<T>(ImmutableArray<T> values, string field)
    {
        if (values.IsDefault || values.Any(value => value is null)) Fail(field, "必须为初始化且无空元素的数组。");
    }

    private static void WriteLibrary(Utf8JsonWriter writer, string kind, C.BlueprintId? id)
    {
        writer.WriteString("kind", kind);
        if (id is null) writer.WriteNull("id");
        else
        {
            if (!Guid.TryParseExact(id.Value.Value, "D", out var uuid) || uuid == Guid.Empty) Fail("/target/id", "蓝图身份必须是非空 UUID。");
            writer.WriteString("id", uuid);
        }
    }

    [DoesNotReturn]
    private static void Fail(string field, string message, string code = "invalid_field") =>
        throw new DefinitionFormatException(field, message, code);

    private static void CheckNumbers(JsonElement value, string field)
    {
        if (value.ValueKind == JsonValueKind.Number &&
            (!value.TryGetDouble(out var number) || !double.IsFinite(number)))
            Fail(field, "必须为有限数值。");
        if (value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject()) CheckNumbers(property.Value, Child(field, property.Name));
        if (value.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in value.EnumerateArray()) CheckNumbers(item, field + "/" + index++);
        }
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info =>
        {
            if (info.Type == typeof(C.DesignSpecification.Building))
                info.Properties.Single(property => property.Name == "kind").Name = "building_kind";
            if (info.Type == typeof(C.DesignSpecification)) Variants(info,
                (typeof(C.DesignSpecification.Vehicle), "vehicle"), (typeof(C.DesignSpecification.Building), "building"),
                (typeof(C.DesignSpecification.Assembly), "assembly"), (typeof(C.DesignSpecification.FunctionalComposite), "functional_composite"),
                (typeof(C.DesignSpecification.SceneModel), "scene_model"), (typeof(C.DesignSpecification.Creature), "creature"));
            if (info.Type == typeof(C.NestedDesignItem)) Variants(info,
                (typeof(C.NestedDesignItem.AssemblyCopy), "assembly_copy"), (typeof(C.NestedDesignItem.SceneModelCopy), "scene_model_copy"),
                (typeof(C.NestedDesignItem.FunctionalCompositeReference), "functional_composite_reference"));
            if (info.Type == typeof(K.PartCapability)) Variants(info,
                (typeof(K.PartCapability.Propulsion), "propulsion"), (typeof(K.PartCapability.Control), "control"),
                (typeof(K.PartCapability.Storage), "storage"), (typeof(K.PartCapability.Production), "production"),
                (typeof(K.PartCapability.Extraction), "extraction"), (typeof(K.PartCapability.Construction), "construction"),
                (typeof(K.PartCapability.Power), "power"), (typeof(K.PartCapability.Weapon), "weapon"), (typeof(K.PartCapability.Platform), "platform"));
        });
        return new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            TypeInfoResolver = resolver,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false) }
        };
    }

    private static void Variants(JsonTypeInfo info, params (Type Type, string Kind)[] variants)
    {
        info.PolymorphismOptions = new() { TypeDiscriminatorPropertyName = "kind" };
        foreach (var (type, kind) in variants) info.PolymorphismOptions.DerivedTypes.Add(new(type, kind));
    }
}
