using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Train.Content.Persistence;
using C = Train.Content.Contracts;

namespace Train.Content.Loading;

internal sealed record UserLibraryLoad(ImmutableArray<C.BlueprintRecord> Records,
    ImmutableArray<C.BlueprintSummary> Summaries, ImmutableArray<C.ContentDiagnostic> Diagnostics,
    string Fingerprint);

internal static class UserLibraryLoader
{
    internal static UserLibraryLoad Load(ContentView basis, C.ContentRoot root, C.IContentFiles files)
    {
        var records = ImmutableArray.CreateBuilder<C.BlueprintRecord>();
        var summaries = ImmutableArray.CreateBuilder<C.BlueprintSummary>();
        var diagnostics = ImmutableArray.CreateBuilder<C.ContentDiagnostic>();
        var fingerprints = new List<object>();
        var listing = files.Enumerate(new(root, ""));
        if (listing is C.ContentIoResult<ImmutableArray<C.ContentFileEntry>>.Failure enumeration)
        {
            if (enumeration.Kind != C.ContentIoFailure.Missing)
                diagnostics.Add(Diagnostic(ContentDocument.IoCode(enumeration.Kind), "", "", enumeration.Diagnostic));
            fingerprints.Add(new { failure = enumeration.Kind, enumeration.Diagnostic });
            return Result();
        }
        foreach (var entry in ((C.ContentIoResult<ImmutableArray<C.ContentFileEntry>>.Success)listing).Value
            .OrderBy(e => e.Name, StringComparer.Ordinal).ThenBy(e => e.Kind))
        {
            var read = entry.Kind == C.ContentEntryKind.File && ContentDocument.SafeEntry(entry.Name)
                ? files.Read(new(root, entry.Name), ContentLimits.JsonFileMaxBytes) : null;
            fingerprints.Add(EntryFingerprint(entry, read));
            const string suffix = ".blueprint.json";
            var id = entry.Name.EndsWith(suffix, StringComparison.Ordinal) ? entry.Name[..^suffix.Length] : null;
            if (!PlayerBlueprint.IsId(id))
            {
                diagnostics.Add(Diagnostic("invalid_field", entry.Name, "/id", "用户蓝图文件名必须为规范 UUID.blueprint.json。"));
                continue;
            }
            var address = new C.BlueprintAddress(C.BlueprintLibrary.User, new(id!));
            try
            {
                if (read is not C.ContentIoResult<ImmutableArray<byte>>.Success data)
                {
                    var failure = read as C.ContentIoResult<ImmutableArray<byte>>.Failure;
                    Reject(failure is null ? "invalid_field" : ContentDocument.IoCode(failure.Kind), "", failure?.Diagnostic ?? "蓝图条目必须为普通文件。");
                    continue;
                }
                using var document = ContentCapture.Parse(new UTF8Encoding(false, true).GetString(data.Value.AsSpan()));
                var record = PlayerBlueprint.Read(document.RootElement, C.BlueprintLibrary.User, basis);
                if (record.Address != address) { Reject("invalid_field", "/id", "蓝图身份与文件名不一致。"); continue; }
                records.Add(record);
                diagnostics.AddRange(PlayerBlueprint.Diagnostics(record.Evaluation).Select(d => Locate(d, entry.Name, address)));
            }
            catch (DefinitionFormatException exception) { Reject(exception.Code, exception.Field, exception.Message); }
            catch (JsonException exception) { Reject("invalid_json", "", exception.Message); }
            catch (DecoderFallbackException exception) { Reject("invalid_json", "", exception.Message); }

            void Reject(string code, string field, string message)
            {
                var diagnostic = Locate(Diagnostic(code, entry.Name, field, message), entry.Name, address);
                diagnostics.Add(diagnostic);
                summaries.Add(new(address, new(id!, ""), null, false, [diagnostic]));
            }
        }
        return Result();

        UserLibraryLoad Result() => new(records.ToImmutable(), summaries.ToImmutable(), diagnostics.ToImmutable(),
            ContentHash.FromCanonicalJson(writer => JsonSerializer.Serialize(writer, fingerprints)));
    }

    internal static string Fingerprint(C.ContentRoot root, C.IContentFiles source)
    {
        var listing = source.Enumerate(new(root, ""));
        var fingerprints = new List<object>();
        if (listing is C.ContentIoResult<ImmutableArray<C.ContentFileEntry>>.Failure failure)
            fingerprints.Add(new { failure = failure.Kind, failure.Diagnostic });
        else
            foreach (var entry in ((C.ContentIoResult<ImmutableArray<C.ContentFileEntry>>.Success)listing).Value
                .OrderBy(e => e.Name, StringComparer.Ordinal).ThenBy(e => e.Kind))
                fingerprints.Add(EntryFingerprint(entry, entry.Kind == C.ContentEntryKind.File && ContentDocument.SafeEntry(entry.Name)
                    ? source.Read(new(root, entry.Name), ContentLimits.JsonFileMaxBytes) : null));
        return ContentHash.FromCanonicalJson(writer => JsonSerializer.Serialize(writer, fingerprints));
    }

    private static object EntryFingerprint(C.ContentFileEntry entry, C.ContentIoResult<ImmutableArray<byte>>? read) =>
        new { entry.Name, entry.Kind, bytes = read is C.ContentIoResult<ImmutableArray<byte>>.Success bytes
            ? Convert.ToHexString(SHA256.HashData(bytes.Value.AsSpan())) : null,
            failure = read is C.ContentIoResult<ImmutableArray<byte>>.Failure io ? $"{io.Kind}:{io.Diagnostic}" : null };

    private static C.ContentDiagnostic Diagnostic(string code, string file, string field, string message) =>
        new(code, C.ContentDiagnosticSeverity.Error, "user_library", null, null, file + field, null, message);

    private static C.ContentDiagnostic Locate(C.ContentDiagnostic diagnostic, string file, C.BlueprintAddress address) =>
        diagnostic with { Stage = "user_library", Subject = new C.ContentReference.Blueprint(address),
            Field = diagnostic.Field.StartsWith(file, StringComparison.Ordinal) ? diagnostic.Field : file + diagnostic.Field };
}
