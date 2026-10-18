using System.Collections.Immutable;
using System.Text.Json;
using Train.Content.Designs;
using Train.Content.Persistence;
using static Train.Content.Loading.DefinitionJson;
using C = Train.Content.Contracts;

namespace Train.Content.Loading;

internal static class PlayerBlueprint
{
    internal static bool IsId(string? text) => Guid.TryParseExact(text, "D", out var id) && id != Guid.Empty && id.ToString("D") == text;

    internal static C.BlueprintRecord Read(JsonElement root, C.BlueprintLibrary library, ContentView view)
    {
        var record = ReadDefinition(root, library);
        return new(record.Address, record.Definition, record.UsedPackages, Evaluate(view, record.Definition));
    }

    internal static CapturedBlueprint ReadDefinition(JsonElement root, C.BlueprintLibrary library)
    {
        if (root.ValueKind != JsonValueKind.Object) Fail("", "玩家蓝图必须为对象。");
        if (Int32(Required(root, "schema_version", ""), "/schema_version") != 3)
            Fail("/schema_version", "玩家蓝图只支持版本 3。", "unsupported_format");
        Object(root, "", "schema_version", "id", "used_packages", "category", "simulation", "layout", "appearance", "display");
        var id = Text(Required(root, "id", ""), "/id");
        if (!IsId(id)) Fail("/id", "玩家蓝图身份必须为规范的非空 UUID。");
        var packages = Items(Required(root, "used_packages", ""), "/used_packages").Select(item =>
        {
            Object(item.Value, item.Field, "id", "name", "version");
            var package = Text(Required(item.Value, "id", item.Field), item.Field + "/id");
            if (package != "core" && !IsId(package)) Fail(item.Field + "/id", "包身份无效。");
            var version = Text(Required(item.Value, "version", item.Field), item.Field + "/version");
            if (!SemVer.IsValid(version)) Fail(item.Field + "/version", "包版本无效。");
            return new C.PackageMetadata(new(package), Text(Required(item.Value, "name", item.Field), item.Field + "/name"), version, [], []);
        }).ToImmutableArray();
        Unique(packages.Select(p => p.Id), "/used_packages");
        var body = ContentCapture.Encode(writer =>
        {
            writer.WriteStartObject();
            foreach (var property in root.EnumerateObject())
                if (property.Name is not ("schema_version" or "id" or "used_packages")) property.WriteTo(writer);
            writer.WriteEndObject();
        });
        using var document = ContentCapture.Parse(body);
        var draft = BlueprintParser.ReadDesign(document.RootElement);
        return new(new(library, new(id)), draft, packages);
    }

    internal static C.DesignEvaluation Evaluate(ContentView view, C.DesignDraft draft) =>
        draft.Specification is C.DesignSpecification.FunctionalComposite
            ? new C.DesignEvaluation.Failed([Diagnostic("design_invalid", "/simulation/behaviors", "玩家蓝图不能带功能性行为。")])
            : draft.Specification is C.DesignSpecification.Creature
                ? new C.DesignEvaluation.Failed([Diagnostic("unsupported_format", "/category", "玩家蓝图不支持生物类别。")])
                : DesignEvaluator.Evaluate(view, draft);

    internal static C.ContentDiagnostic Diagnostic(string code, string field, string message) =>
        new(code, C.ContentDiagnosticSeverity.Error, "edit", null, null, field, null, message);

    internal static ImmutableArray<C.ContentDiagnostic> Diagnostics(C.DesignEvaluation evaluation) => evaluation switch
    {
        C.DesignEvaluation.Complete complete => complete.Diagnostics,
        C.DesignEvaluation.Partial partial => partial.Diagnostics,
        C.DesignEvaluation.Failed failed => failed.Diagnostics,
        _ => throw new ArgumentOutOfRangeException(nameof(evaluation))
    };

    internal static C.BlueprintSummary Summary(C.BlueprintRecord record) => new(record.Address, record.Definition.Display,
        (record.Evaluation as C.DesignEvaluation.Complete)?.Design,
        record.Evaluation is C.DesignEvaluation.Complete { IsValid: true }, Diagnostics(record.Evaluation));

    internal static ImmutableArray<C.ContentDiagnostic> InvalidDiagnostics(C.DesignEvaluation evaluation)
    {
        var diagnostics = Diagnostics(evaluation);
        if (diagnostics.Any(d => d.Code == "reference_missing" || d.Code == "design_invalid")) return diagnostics;
        return diagnostics.Add(Diagnostic("design_invalid", "/target", "需要完整合法的玩家设计及核心投影。"));
    }

    internal static string Encode(C.BlueprintRecord record) => ContentCapture.Encode(writer => Write(writer, record));

    internal static void Write(Utf8JsonWriter writer, C.BlueprintRecord record) =>
        Write(writer, new CapturedBlueprint(record.Address, record.Definition, record.UsedPackages));

    internal static void Write(Utf8JsonWriter writer, CapturedBlueprint record)
    {
        writer.WriteStartObject();
        writer.WriteNumber("schema_version", 3);
        writer.WriteString("id", record.Address.Id.Value);
        writer.WriteStartArray("used_packages");
        foreach (var package in record.UsedPackages.OrderBy(p => p.Id.Value, StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            writer.WriteString("id", package.Id.Value);
            writer.WriteString("name", package.Name);
            writer.WriteString("version", package.Version);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        BlueprintParser.WriteDesign(writer, record.Definition);
        writer.WriteEndObject();
    }
}
