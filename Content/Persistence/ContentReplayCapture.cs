using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Text.Json;
using Train.Content.Loading;
using static Train.Content.Loading.DefinitionJson;
using C = Train.Content.Contracts;
using K = Train.Core.Contracts;

namespace Train.Content.Persistence;

internal sealed class ContentReplayCapture(ImmutableArray<C.IContentView> history,
    ImmutableArray<ContentCapture> captures) : C.IContentReplayCapture
{
    public ImmutableArray<C.IContentView> History { get; } = history;

    internal static ContentReplayCapture Create(ImmutableArray<C.IContentView> history, K.CoreReferenceManifest references)
    {
        if (history.IsEmpty) ContentCapture.Inconsistent("/history", "历史不能为空。");
        var captures = history.Cast<ContentView>().Select(view => ContentCapture.Create(view, view.PersistentReferences)).ToImmutableArray();
        foreach (var identity in references.Inputs)
            if (!history.Any(view => view.Core.Identity == identity)) ContentCapture.Inconsistent("/history/inputs", "缺少所需的输入历史。");
        foreach (var design in references.Designs)
            if (!history.Any(view => view.FindDesign(design) is C.ContentLookup<C.DesignEvaluation>.Found))
                ContentCapture.Inconsistent("/history/designs", "缺少所需的历史设计。");
        foreach (var template in references.Templates)
            if (!history.Any(view => view.FindSettlementLayout(template.Owner) is C.ContentLookup<C.SettlementLayoutProjection>.Found found &&
                ContentCapture.MatchesTemplate(template, found.Value.Owner, found.Value.SourceTemplate, found.Value.Plots)))
                ContentCapture.Inconsistent("/history/templates", "缺少所需的历史模板副本。");
        return new(history, captures);
    }

    public string Encode() => ContentCapture.Encode(writer =>
    {
        var views = History.Cast<ContentView>().ToArray();
        var blobs = views.SelectMany(view => view.Source!.FileBytes)
            .GroupBy(bytes => ContentHash.FromBytes(bytes.AsSpan())).OrderBy(group => group.Key, StringComparer.Ordinal);
        writer.WriteStartObject();
        writer.WriteNumber("format_version", 5);
        writer.WriteStartArray("blobs");
        foreach (var blob in blobs)
        {
            writer.WriteStartObject();
            writer.WriteString("hash", blob.Key);
            writer.WriteBase64String("bytes", blob.First().AsSpan());
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteStartArray("history");
        for (var index = 0; index < views.Length; index++)
        {
            var view = views[index];
            writer.WriteStartObject();
            writer.WriteString("input", view.Core.Identity.Value);
            writer.WriteNumber("generation", view.Generation.Value);
            writer.WritePropertyName("source");
            view.Source!.Write(writer);
            writer.WritePropertyName("content");
            captures[index].Write(writer);
            writer.WriteStartArray("missing");
            foreach (var missing in view.Core.MissingReferences)
            {
                writer.WriteStartObject();
                writer.WriteString("kind", DefinitionKind(missing.Reference));
                writer.WriteString("id", ContentView.DefinitionId(missing.Reference));
                writer.WriteString("field", missing.FieldPath);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    });

    internal static ContentReplayCapture Read(JsonElement root)
    {
        if (Int32(Required(root, "format_version", ""), "/format_version") != 5)
            Fail("/format_version", "复现内容载荷只支持版本 5。", "unsupported_format");
        Object(root, "", "format_version", "blobs", "history");
        var blobs = new Dictionary<string, ImmutableArray<byte>>(StringComparer.Ordinal);
        foreach (var (value, field) in Items(Required(root, "blobs", ""), "/blobs"))
        {
            Object(value, field, "hash", "bytes");
            var hash = ContentCapture.ReadHash(Required(value, "hash", field), field + "/hash");
            var bytesValue = Required(value, "bytes", field);
            if (bytesValue.ValueKind != JsonValueKind.String || !bytesValue.TryGetBytesFromBase64(out var bytes))
            {
                ContentCapture.Inconsistent(field + "/bytes", "字节必须为 Base64。");
                continue;
            }
            if (ContentHash.FromBytes(bytes) != hash || !blobs.TryAdd(hash, bytes.ToImmutableArray()))
                ContentCapture.Inconsistent(field, "字节哈希不一致或重复。");
        }
        var immutableBlobs = blobs.ToFrozenDictionary(StringComparer.Ordinal);
        var history = ImmutableArray.CreateBuilder<C.IContentView>();
        var captures = ImmutableArray.CreateBuilder<ContentCapture>();
        foreach (var (value, field) in Items(Required(root, "history", ""), "/history"))
        {
            Object(value, field, "input", "generation", "source", "content", "missing");
            var identity = new K.CoreInputId(ContentCapture.ReadHash(Required(value, "input", field), field + "/input"));
            var generation = new C.ContentGeneration(UInt64(Required(value, "generation", field), field + "/generation", 1));
            var sourceValue = Required(value, "source", field);
            ContentSourceSnapshot source;
            try { source = ContentSourceSnapshot.Read(sourceValue, immutableBlobs); }
            catch (DefinitionFormatException exception)
            {
                throw new DefinitionFormatException(field + exception.Field, exception.Message, exception.Code);
            }
            var capture = ContentCapture.Read(Required(value, "content", field), field + "/content");
            var missing = Items(Required(value, "missing", field), field + "/missing").Select(item =>
            {
                Object(item.Value, item.Field, "kind", "id", "field");
                var definition = ReadDefinition(Text(Required(item.Value, "kind", item.Field), item.Field + "/kind"),
                    Reference(Required(item.Value, "id", item.Field), item.Field + "/id", null), item.Field + "/kind");
                return new K.RequiredDefinition(definition, null, Text(Required(item.Value, "field", item.Field), item.Field + "/field"), true);
            }).ToImmutableArray();
            C.ContentResult<C.IContentCandidate> loaded;
            try { loaded = new InitialLoader(source.Installation, source, source).Load(); }
            catch (DefinitionFormatException exception)
            {
                throw new DefinitionFormatException(field + exception.Field, exception.Message, exception.Code);
            }
            if (loaded is not C.ContentResult<C.IContentCandidate>.Success success)
            {
                ContentCapture.Inconsistent(field + "/source", "固定来源无法重建有效内容。");
                continue;
            }
            using var candidate = success.Value;
            ContentView view;
            try
            {
                view = ((ContentView)candidate.View).WithCapture(capture,
                    new(capture.Designs, capture.Templates, missing, [identity]), generation);
            }
            catch (DefinitionFormatException exception)
            {
                throw new DefinitionFormatException(field + "/content" + exception.Field, exception.Message, exception.Code);
            }
            if (view.Core.Identity != identity) ContentCapture.Inconsistent(field + "/input", "重算的输入身份与记录不一致。");
            if (!view.Core.MissingReferences.SequenceEqual(missing.Select(m => new K.MissingCoreReference(m.Definition, m.Field))))
                ContentCapture.Inconsistent(field + "/missing", "缺失清单与重建内容不一致。");
            history.Add(view);
            captures.Add(capture);
        }
        if (history.Count == 0) ContentCapture.Inconsistent("/history", "历史不能为空。");
        return new(history.ToImmutable(), captures.ToImmutable());
    }

    private static string DefinitionKind(K.CoreDefinitionReference definition) => definition switch
    {
        K.CoreDefinitionReference.Voxel => "voxel", K.CoreDefinitionReference.Composite => "composite",
        K.CoreDefinitionReference.Rule => "rule", K.CoreDefinitionReference.SettlementType => "settlement_type",
        K.CoreDefinitionReference.Template => "template", K.CoreDefinitionReference.Terrain => "terrain",
        K.CoreDefinitionReference.Creature => "creature", K.CoreDefinitionReference.Lair => "lair",
        K.CoreDefinitionReference.Process { Id: K.ProcessRef.Production } => "production",
        K.CoreDefinitionReference.Process { Id: K.ProcessRef.Recovery } => "recovery",
        _ => throw new ArgumentOutOfRangeException(nameof(definition))
    };

    private static K.CoreDefinitionReference ReadDefinition(string kind, string id, string field) => kind switch
    {
        "voxel" => new K.CoreDefinitionReference.Voxel(new(id)), "composite" => new K.CoreDefinitionReference.Composite(new(id)),
        "rule" => new K.CoreDefinitionReference.Rule(new(id)), "settlement_type" => new K.CoreDefinitionReference.SettlementType(new(id)),
        "template" => new K.CoreDefinitionReference.Template(new(id)), "terrain" => new K.CoreDefinitionReference.Terrain(new(id)),
        "creature" => new K.CoreDefinitionReference.Creature(new(id)), "lair" => new K.CoreDefinitionReference.Lair(new(id)),
        "production" => new K.CoreDefinitionReference.Process(new K.ProcessRef.Production(new(id))),
        "recovery" => new K.CoreDefinitionReference.Process(new K.ProcessRef.Recovery(new(id))),
        _ => throw new DefinitionFormatException(field, "未知的引用种类。", "inconsistent_capture")
    };
}
