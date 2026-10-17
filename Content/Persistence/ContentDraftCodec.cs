using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Train.Content.Loading;
using static Train.Content.Loading.DefinitionJson;
using C = Train.Content.Contracts;
using K = Train.Core.Contracts;

namespace Train.Content.Persistence;

internal static class ContentDraftCodec
{
    internal static string Encode(C.SavedContentDraft draft)
    {
        if (draft is null) Fail("", "必须为草稿对象。");
        if (draft.Id.Value == Guid.Empty) Fail("/id", "草稿身份不能为空。");
        if (draft.Payload is C.DraftPayload.Definition || draft.Target is C.DraftTarget.PackageDefinition)
            throw new NotImplementedException();
        if (draft.Payload is not C.DraftPayload.Design) Fail("/payload", "必须为设计草稿。");
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
            writer.WriteStartObject();
            try { BlueprintParser.WriteDesign(writer, ((C.DraftPayload.Design)draft.Payload).Value, incomplete: true); }
            catch (ArgumentException exception) { throw new DefinitionFormatException("/payload/value", exception.Message, "invalid_field"); }
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        var result = System.Text.Encoding.UTF8.GetString(stream.ToArray());
        Decode(result);
        return result;
    }

    internal static C.SavedContentDraft Decode(string payload)
    {
        using var document = ContentCapture.Parse(payload);
        return Decode(document.RootElement);
    }

    internal static C.SavedContentDraft Decode(JsonElement root)
    {
        if (Int32(Required(root, "format_version", ""), "/format_version") != 1)
            Fail("/format_version", "只支持草稿格式 1。", "unsupported_format");
        Object(root, "", "format_version", "id", "revision", "target", "payload");
        var idText = Text(Required(root, "id", ""), "/id");
        if (!PlayerBlueprint.IsId(idText)) Fail("/id", "草稿身份必须是非空 UUID。");
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
            if (instanceId == 0) Fail("/target/instance/id", "实例身份必须非零。");
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
                if (!PlayerBlueprint.IsId(text)) Fail("/target/id", "蓝图身份必须是非空 UUID。");
                blueprint = new(text);
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
        C.DesignDraft design;
        try { design = BlueprintParser.ReadDesign(Required(content, "value", "/payload"), draft: true); }
        catch (DefinitionFormatException exception)
        { throw new DefinitionFormatException("/payload/value" + exception.Field, exception.Message, exception.Code); }
        return new(new(Guid.Parse(idText)), new(revision), destination, new C.DraftPayload.Design(design));
    }

    private static void WriteLibrary(Utf8JsonWriter writer, string kind, C.BlueprintId? id)
    {
        writer.WriteString("kind", kind);
        if (id is null) writer.WriteNull("id");
        else
        {
            if (!PlayerBlueprint.IsId(id.Value.Value)) Fail("/target/id", "蓝图身份必须是非空 UUID。");
            writer.WriteString("id", id.Value.Value);
        }
    }

    [DoesNotReturn]
    private static void Fail(string field, string message, string code = "invalid_field") =>
        throw new DefinitionFormatException(field, message, code);

}
