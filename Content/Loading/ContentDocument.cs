using System.Text.Json;
using C = Train.Content.Contracts;

namespace Train.Content.Loading;

internal static class ContentDocument
{
    internal static JsonDocument? Parse(byte[] bytes, LoadPackage package, string path)
    {
        var diagnostics = new DiagnosticBag(ContentDiagnosticSeverity.Error) { Source = path };
        if (StrictJson.HasDuplicateProperties(bytes, diagnostics, path))
        {
            foreach (var diagnostic in diagnostics.Items)
                package.Reject("invalid_json", path, diagnostic.JsonPath ?? "", diagnostic.Message);
            return null;
        }
        try
        {
            return JsonDocument.Parse(bytes, StrictJson.DocumentOptions);
        }
        catch (JsonException exception)
        {
            package.Reject("invalid_json", path, "", exception.Message);
            return null;
        }
    }

    internal static string IoCode(C.ContentIoFailure failure) => failure switch
    {
        C.ContentIoFailure.Missing => "file_missing",
        C.ContentIoFailure.LimitExceeded => "file_too_large",
        C.ContentIoFailure.LinkedPath => "linked_path",
        C.ContentIoFailure.Inaccessible => "file_unreadable",
        _ => throw new ArgumentOutOfRangeException(nameof(failure))
    };

    internal static bool SafeEntry(string name) => name.Length > 0 && name is not "." and not ".." &&
        !name.Any(character => character is '/' or '\\' or ':' or '\0');
}
