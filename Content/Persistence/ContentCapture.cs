using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Train.Content.Designs;
using Train.Content.Loading;
using static Train.Content.Loading.DefinitionJson;
using C = Train.Content.Contracts;
using K = Train.Core.Contracts;

namespace Train.Content.Persistence;

internal sealed record CapturedDesign(K.DesignHash Hash, C.DesignDraft Definition);
internal sealed record CapturedTemplate(K.SettlementId Owner, K.SettlementTemplateId Source,
    C.DisplayText Display, ImmutableArray<C.SettlementPlotLayout> Plots, K.DesignHash SceneModel);

internal sealed class ContentCapture(C.ContentRevision revision, ImmutableArray<CapturedDesign> designs,
    ImmutableArray<CapturedTemplate> templates, ImmutableArray<C.PackageMetadata> packages) : C.IContentCapture
{
    public C.ContentRevision Revision { get; } = revision;
    internal ImmutableArray<CapturedDesign> Records { get; } = designs;
    internal ImmutableArray<CapturedTemplate> Copies { get; } = templates;
    public ImmutableArray<K.DesignHash> Designs { get; } = designs.Select(d => d.Hash).ToImmutableArray();
    public ImmutableArray<K.OwnedSettlementTemplateReference> Templates { get; } = templates.Select(t =>
        new K.OwnedSettlementTemplateReference(t.Owner, t.Source,
            t.Plots.Select(p => new K.TemplatePlotReference(p.Number, p.Size)).ToImmutableArray())).ToImmutableArray();
    public ImmutableArray<C.PackageMetadata> ReferencedPackages { get; } = packages;

    internal static ContentCapture Create(ContentView view, K.CoreReferenceManifest references)
    {
        if (references.Designs.IsDefault || references.Templates.IsDefault || references.Definitions.IsDefault || references.Inputs.IsDefault)
            Inconsistent("/references", "引用清单必须初始化。");
        var layouts = references.Templates.Select(reference =>
        {
            if (view.FindSettlementLayout(reference.Owner) is not C.ContentLookup<C.SettlementLayoutProjection>.Found found)
            {
                Inconsistent("/templates", "缺少所有者的模板副本。");
                return null!;
            }
            var layout = found.Value;
            if (layout.SourceTemplate != reference.SourceTemplate || reference.Plots.IsDefault ||
                !layout.Plots.Select(p => new K.TemplatePlotReference(p.Number, p.Size)).SequenceEqual(reference.Plots.OrderBy(p => p.Number)))
                Inconsistent("/templates", "模板副本与核心地块清单不配对。");
            return new CapturedTemplate(layout.Owner, layout.SourceTemplate, layout.Display, layout.Plots, layout.SceneModel.Design);
        }).OrderBy(t => t.Owner.Value).ToImmutableArray();
        Unique(layouts.Select(t => t.Owner), "/templates");
        var records = references.Designs.Concat(layouts.Select(t => t.SceneModel)).Distinct()
            .OrderBy(hash => hash.Value, StringComparer.Ordinal).Select(hash =>
            {
                if (view.FindDesign(hash) is not C.ContentLookup<C.DesignEvaluation>.Found found)
                {
                    Inconsistent("/designs", "缺少被引用的实例设计。");
                    return null!;
                }
                var draft = found.Value switch
                {
                    C.DesignEvaluation.Complete complete => complete.Definition,
                    C.DesignEvaluation.Partial partial => partial.Definition,
                    _ => throw new InvalidOperationException("固定视图不能保存无法计算的设计。")
                };
                if (DesignEvaluator.Identity(draft) != hash) Inconsistent("/designs", "设计哈希与正文不一致。");
                return new CapturedDesign(hash, draft);
            }).ToImmutableArray();
        var usedPackages = records.SelectMany(d => d.Definition.Layout.Palette.Select(p => p.Voxel.Value.Split(':')[0]))
            .Concat(layouts.Select(t => t.Source.Value.Split(':')[0]))
            .Concat(references.Definitions.Select(d => ContentView.DefinitionId(d.Definition).Split(':')[0])).ToHashSet(StringComparer.Ordinal);
        var metadata = view.Packages.Select(p => p.Metadata).OfType<C.PackageMetadata>().Concat(view.SavedPackages)
            .DistinctBy(p => p.Id).Where(p => usedPackages.Contains(p.Id.Value)).OrderBy(p => p.Id.Value, StringComparer.Ordinal).ToImmutableArray();
        return new(view.PersistentRevision, records, layouts, metadata);
    }

    public string Encode() => Encode(Write);

    internal void Write(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteNumber("format_version", 1);
        writer.WriteNumber("revision", Revision.Value);
        writer.WriteStartArray("packages");
        foreach (var package in ReferencedPackages.OrderBy(p => p.Id.Value, StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            writer.WriteString("id", package.Id.Value);
            writer.WriteString("name", package.Name);
            writer.WriteString("version", package.Version);
            writer.WriteStartArray("authors");
            foreach (var author in package.Authors) writer.WriteStringValue(author);
            writer.WriteEndArray();
            writer.WriteStartArray("dependencies");
            foreach (var dependency in package.Dependencies.OrderBy(d => d.Package.Value, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("package", dependency.Package.Value);
                writer.WriteString("minimum_version", dependency.MinimumVersion);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteStartArray("designs");
        foreach (var design in Records.OrderBy(d => d.Hash.Value, StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            writer.WriteString("hash", design.Hash.Value);
            writer.WriteStartObject("definition");
            BlueprintParser.WriteDesign(writer, design.Definition);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteStartArray("templates");
        foreach (var template in Copies.OrderBy(t => t.Owner.Value))
        {
            writer.WriteStartObject();
            writer.WriteNumber("owner", template.Owner.Value);
            writer.WriteString("source", template.Source.Value);
            writer.WriteString("scene_model", template.SceneModel.Value);
            writer.WriteStartObject("display");
            writer.WriteString("name", template.Display.Name);
            writer.WriteString("description", template.Display.Description);
            writer.WriteEndObject();
            writer.WriteStartArray("plots");
            foreach (var plot in template.Plots.OrderBy(p => p.Number)) SettlementParser.WritePlotLayout(writer, plot);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    internal static ContentCapture Read(JsonElement root)
    {
        Object(root, "", "format_version", "revision", "packages", "designs", "templates");
        if (Int32(Required(root, "format_version", ""), "/format_version") != 1)
            Fail("/format_version", "内容载荷只支持版本 1。", "unsupported_format");
        var revision = new C.ContentRevision(UInt64(Required(root, "revision", ""), "/revision"));
        var packages = Items(Required(root, "packages", ""), "/packages").Select(item => ReadPackage(item.Value, item.Field)).ToImmutableArray();
        Unique(packages.Select(p => p.Id), "/packages");
        var designs = Items(Required(root, "designs", ""), "/designs").Select(item =>
        {
            Object(item.Value, item.Field, "hash", "definition");
            var hash = new K.DesignHash(ReadHash(Required(item.Value, "hash", item.Field), item.Field + "/hash"));
            var draft = BlueprintParser.ReadDesign(Required(item.Value, "definition", item.Field));
            if (DesignEvaluator.Identity(draft) != hash) Inconsistent(item.Field + "/hash", "设计哈希与正文不一致。");
            return new CapturedDesign(hash, draft);
        }).OrderBy(d => d.Hash.Value, StringComparer.Ordinal).ToImmutableArray();
        Unique(designs.Select(d => d.Hash), "/designs");
        var templates = Items(Required(root, "templates", ""), "/templates").Select(item =>
        {
            Object(item.Value, item.Field, "owner", "source", "scene_model", "display", "plots");
            var owner = new K.SettlementId(UInt64(Required(item.Value, "owner", item.Field), item.Field + "/owner", 1));
            var source = new K.SettlementTemplateId(ReadDefinitionId(Required(item.Value, "source", item.Field), item.Field + "/source"));
            var scene = new K.DesignHash(ReadHash(Required(item.Value, "scene_model", item.Field), item.Field + "/scene_model"));
            var display = Display(Required(item.Value, "display", item.Field), item.Field + "/display");
            var plots = Items(Required(item.Value, "plots", item.Field), item.Field + "/plots")
                .Select(plot => SettlementParser.ReadPlotLayout(plot.Value, plot.Field)).OrderBy(p => p.Number).ToImmutableArray();
            Unique(plots.Select(p => p.Number), item.Field + "/plots");
            if (plots.IsEmpty || !designs.Any(d => d.Hash == scene && d.Definition.Specification is C.DesignSpecification.SceneModel))
                Inconsistent(item.Field, "模板副本必须包含地块与场景模型设计。");
            return new CapturedTemplate(owner, source, display, plots, scene);
        }).OrderBy(t => t.Owner.Value).ToImmutableArray();
        Unique(templates.Select(t => t.Owner), "/templates");
        return new(revision, designs, templates, packages);
    }

    private static C.PackageMetadata ReadPackage(JsonElement value, string field)
    {
        Object(value, field, "id", "name", "version", "authors", "dependencies");
        var id = Text(Required(value, "id", field), field + "/id");
        if (id != "core" && (!ModManifest.TryCanonicalUuid(id, out var canonical) || canonical != id)) Inconsistent(field + "/id", "包身份无效。");
        var version = Text(Required(value, "version", field), field + "/version");
        if (!SemVer.IsValid(version)) Inconsistent(field + "/version", "包版本无效。");
        var authors = Items(Required(value, "authors", field), field + "/authors").Select(item => Text(item.Value, item.Field)).ToImmutableArray();
        var dependencies = Items(Required(value, "dependencies", field), field + "/dependencies").Select(item =>
        {
            Object(item.Value, item.Field, "package", "minimum_version");
            var package = Text(Required(item.Value, "package", item.Field), item.Field + "/package");
            var minimum = Text(Required(item.Value, "minimum_version", item.Field), item.Field + "/minimum_version");
            if ((package != "core" && (!ModManifest.TryCanonicalUuid(package, out var canonical) || canonical != package)) || !SemVer.IsValid(minimum))
                Inconsistent(item.Field, "包依赖无效。");
            return new C.PackageDependency(new(package), minimum);
        }).ToImmutableArray();
        Unique(dependencies.Select(d => d.Package), field + "/dependencies");
        return new(new(id), Text(Required(value, "name", field), field + "/name"), version, authors, dependencies);
    }

    internal static string ReadHash(JsonElement value, string field)
    {
        var text = Text(value, field);
        if (text.Length != 64 || text.Any(c => c is not (>= '0' and <= '9') and not (>= 'A' and <= 'F')))
            Inconsistent(field, "必须为规范 SHA-256。");
        return text;
    }

    internal static string ReadDefinitionId(JsonElement value, string field)
    {
        var text = Text(value, field);
        var parts = text.Split(':');
        if (parts.Length != 2 || !ContentIdentity.IsValidPart(parts[1]) || parts[1] == "shared" ||
            (parts[0] != "core" && (!ModManifest.TryCanonicalUuid(parts[0], out var canonical) || canonical != parts[0])))
            Inconsistent(field, "必须为规范的全限定定义身份。");
        return text;
    }

    internal static JsonDocument Parse(string payload)
    {
        var document = JsonDocument.Parse(payload, StrictJson.DocumentOptions);
        if (ContentDocument.DuplicateField(document.RootElement, "") is { } duplicate)
        {
            document.Dispose();
            Fail(duplicate, "JSON 字段重复。", "invalid_json");
        }
        return document;
    }

    internal static string Encode(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) write(writer);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    internal static void Inconsistent(string field, string message) => Fail(field, message, "inconsistent_capture");
}
