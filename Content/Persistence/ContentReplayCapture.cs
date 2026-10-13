using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Text.Json;
using Train.Content.Loading;
using static Train.Content.Loading.DefinitionJson;
using C = Train.Content.Contracts;
using K = Train.Core.Contracts;

namespace Train.Content.Persistence;

internal sealed class ContentReplayCapture(ImmutableArray<C.IContentView> history) : C.IContentReplayCapture
{
    public ImmutableArray<C.IContentView> History { get; } = history;

    internal static ContentReplayCapture Create(ImmutableArray<C.IContentView> history, K.CoreReferenceManifest references)
    {
        if (history.IsDefaultOrEmpty || references.Inputs.IsDefault || references.Designs.IsDefault ||
            references.Templates.IsDefault || references.Definitions.IsDefault)
            ContentCapture.Inconsistent("/history", "历史与引用清单必须初始化且历史不能为空。");
        if (history.Any(view => view is not ContentView { Source: not null }))
            ContentCapture.Inconsistent("/history", "历史视图缺少固定来源。");
        foreach (var identity in references.Inputs)
            if (!history.Any(view => view.Core.Identity == identity)) ContentCapture.Inconsistent("/history/inputs", "缺少所需的输入历史。");
        foreach (var design in references.Designs)
            if (!history.Any(view => view.FindDesign(design) is C.ContentLookup<C.DesignEvaluation>.Found))
                ContentCapture.Inconsistent("/history/designs", "缺少所需的历史设计。");
        foreach (var template in references.Templates)
            if (!history.Any(view => view.FindSettlementLayout(template.Owner) is C.ContentLookup<C.SettlementLayoutProjection>.Found found &&
                found.Value.SourceTemplate == template.SourceTemplate && !template.Plots.IsDefault &&
                found.Value.Plots.Select(p => new K.TemplatePlotReference(p.Number, p.Size)).SequenceEqual(template.Plots.OrderBy(p => p.Number))))
                ContentCapture.Inconsistent("/history/templates", "缺少所需的历史模板副本。");
        return new(history);
    }

    public string Encode() => ContentCapture.Encode(writer =>
    {
        var views = History.Cast<ContentView>().ToArray();
        var blobs = views.SelectMany(view => view.Source!.FileBytes)
            .GroupBy(bytes => ContentHash.FromBytes(bytes.AsSpan())).OrderBy(group => group.Key, StringComparer.Ordinal);
        writer.WriteStartObject();
        writer.WriteNumber("format_version", 1);
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
        foreach (var view in views)
        {
            writer.WriteStartObject();
            writer.WriteString("input", view.Core.Identity.Value);
            writer.WriteNumber("generation", view.Generation.Value);
            writer.WritePropertyName("source");
            view.Source!.Write(writer);
            writer.WritePropertyName("content");
            ContentCapture.Create(view, view.PersistentReferences).Write(writer);
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
        Object(root, "", "format_version", "blobs", "history");
        if (Int32(Required(root, "format_version", ""), "/format_version") != 1)
            Fail("/format_version", "复现内容载荷只支持版本 1。", "unsupported_format");
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
        foreach (var (value, field) in Items(Required(root, "history", ""), "/history"))
        {
            Object(value, field, "input", "generation", "source", "content", "missing");
            var identity = new K.CoreInputId(ContentCapture.ReadHash(Required(value, "input", field), field + "/input"));
            var generation = new C.ContentGeneration(UInt64(Required(value, "generation", field), field + "/generation", 1));
            var source = ContentSourceSnapshot.Read(Required(value, "source", field), immutableBlobs);
            var capture = ContentCapture.Read(Required(value, "content", field));
            var missing = Items(Required(value, "missing", field), field + "/missing").Select(item =>
            {
                Object(item.Value, item.Field, "kind", "id", "field");
                var definition = ReadDefinition(Text(Required(item.Value, "kind", item.Field), item.Field + "/kind"),
                    ContentCapture.ReadDefinitionId(Required(item.Value, "id", item.Field), item.Field + "/id"));
                return new K.RequiredDefinition(definition, null, Text(Required(item.Value, "field", item.Field), item.Field + "/field"), true);
            }).ToImmutableArray();
            var loaded = new InitialLoader(source.Installation, source, source).Load();
            if (loaded is not C.ContentResult<C.IContentCandidate>.Success success)
            {
                ContentCapture.Inconsistent(field + "/source", "固定来源无法重建有效内容。");
                continue;
            }
            using var candidate = success.Value;
            var view = ((ContentView)candidate.View).WithCapture(capture,
                new(capture.Designs, capture.Templates, missing, [identity]), generation);
            if (view.Core.Identity != identity) ContentCapture.Inconsistent(field + "/input", "重算的输入身份与记录不一致。");
            history.Add(view);
        }
        if (history.Count == 0) ContentCapture.Inconsistent("/history", "历史不能为空。");
        return new(history.ToImmutable());
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

    private static K.CoreDefinitionReference ReadDefinition(string kind, string id) => kind switch
    {
        "voxel" => new K.CoreDefinitionReference.Voxel(new(id)), "composite" => new K.CoreDefinitionReference.Composite(new(id)),
        "rule" => new K.CoreDefinitionReference.Rule(new(id)), "settlement_type" => new K.CoreDefinitionReference.SettlementType(new(id)),
        "template" => new K.CoreDefinitionReference.Template(new(id)), "terrain" => new K.CoreDefinitionReference.Terrain(new(id)),
        "creature" => new K.CoreDefinitionReference.Creature(new(id)), "lair" => new K.CoreDefinitionReference.Lair(new(id)),
        "production" => new K.CoreDefinitionReference.Process(new K.ProcessRef.Production(new(id))),
        "recovery" => new K.CoreDefinitionReference.Process(new K.ProcessRef.Recovery(new(id))),
        _ => throw new DefinitionFormatException("/missing/kind", "未知的引用种类。", "inconsistent_capture")
    };
}
