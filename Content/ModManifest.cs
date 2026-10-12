using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Train.Content;

public sealed class ModManifest
{
    public const int SchemaVersion = 1;
    public const int NameMinLength = 1;
    public const int NameMaxLength = 100;
    public const int DescriptionMinLength = 0;
    public const int DescriptionMaxLength = 4000;
    public const int AuthorMinLength = 1;
    public const int AuthorMaxLength = 100;
    public const int AuthorsMinCount = 1;
    public const int AuthorsMaxCount = 16;

    private static readonly Regex CanonicalUuid = new(
        "^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public string Id { get; }
    public string Name { get; }
    public string Version { get; }
    public IReadOnlyList<string> Authors { get; }
    public string Description { get; }

    public ModManifest(
        string id,
        string name,
        string version,
        IReadOnlyList<string> authors,
        string description)
    {
        ArgumentNullException.ThrowIfNull(authors);
        Id = id;
        Name = name;
        Version = version;
        Authors = new ReadOnlyCollection<string>(authors.ToArray());
        Description = description;
    }

    public static bool TryParse(
        byte[] utf8,
        string source,
        out ModManifest? manifest,
        out IReadOnlyList<ContentDiagnostic> diagnostics)
    {
        var bag = new DiagnosticBag(ContentDiagnosticSeverity.Warning) { Source = source };
        manifest = Parse(utf8, source, bag);
        diagnostics = bag.Items;
        return manifest is not null && !bag.Invalid;
    }

    internal static ModManifest? Parse(byte[] utf8, string source, DiagnosticBag diagnostics) =>
        Read(utf8, source, diagnostics).Manifest;

    internal static ModManifestRead Read(byte[] utf8, string source, DiagnosticBag diagnostics)
    {
        diagnostics.Source = source;
        if (utf8.Length > ContentLimits.JsonFileMaxBytes)
        {
            diagnostics.Reject(ContentDiagnosticCodes.FileTooLarge, "Mod manifest exceeds the 1 MiB limit.");
            return ModManifestRead.None;
        }

        if (StrictJson.HasDuplicateProperties(utf8, diagnostics, source))
            return ModManifestRead.None;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(utf8, StrictJson.DocumentOptions);
        }
        catch (JsonException exception)
        {
            diagnostics.Reject(ContentDiagnosticCodes.InvalidJson, exception.Message);
            return ModManifestRead.None;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                diagnostics.Reject(ContentDiagnosticCodes.InvalidJson, "Mod manifest must be a JSON object.", "$");
                return ModManifestRead.None;
            }

            return ParseDocument(document.RootElement, diagnostics);
        }
    }

    private static ModManifestRead ParseDocument(JsonElement root, DiagnosticBag diagnostics)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (property.Name is not "schema_version" and not "id" and not "name" and not "version"
                and not "authors" and not "description")
            {
                diagnostics.Reject(ContentDiagnosticCodes.UnknownField, "Unknown field.", "/" + property.Name);
            }
        }

        if (!root.TryGetProperty("schema_version", out var versionElement) ||
            versionElement.ValueKind != JsonValueKind.Number ||
            !versionElement.TryGetInt32(out var schemaVersion) ||
            schemaVersion != SchemaVersion)
        {
            diagnostics.Reject(
                ContentDiagnosticCodes.SchemaVersionUnsupported,
                "Only schema_version 1 is supported.",
                "/schema_version");
        }

        var canonicalId = TryReadCanonicalId(root, diagnostics, out var id);
        var hasName = TryReadName(root, diagnostics, out var name);
        var hasVersion = TryReadVersion(root, diagnostics, out var version);
        var hasAuthors = TryReadAuthors(root, diagnostics, out var authors);
        var hasDescription = TryReadDescription(root, diagnostics, out var description);
        if (!canonicalId || !hasName || !hasVersion || !hasAuthors || !hasDescription || diagnostics.Invalid)
            return new ModManifestRead(id);

        return new ModManifestRead(id, new ModManifest(id!, name, version, authors, description));
    }

    private static bool TryReadCanonicalId(JsonElement root, DiagnosticBag diagnostics, out string? id)
    {
        id = null;
        if (!root.TryGetProperty("id", out var idElement))
        {
            diagnostics.Reject(ContentDiagnosticCodes.UuidInvalid, "id is required.", "/id");
            return false;
        }

        if (!StrictJson.TryGetString(idElement, diagnostics, "/id", ContentDiagnosticCodes.UuidInvalid, out var rawId) ||
            !TryCanonicalUuid(rawId, out var canonical))
        {
            if (!diagnostics.Items.Any(item => item.Code == ContentDiagnosticCodes.UuidInvalid && item.JsonPath == "/id"))
                diagnostics.Reject(ContentDiagnosticCodes.UuidInvalid, "Mod id must be a canonical lowercase non-nil UUID.", "/id");
            return false;
        }

        id = canonical;
        return true;
    }

    private static bool TryReadName(JsonElement root, DiagnosticBag diagnostics, out string name)
    {
        name = "";
        if (!root.TryGetProperty("name", out var nameElement))
        {
            diagnostics.Reject(ContentDiagnosticCodes.NameInvalid, "name is required.", "/name");
            return false;
        }

        return StrictJson.TryGetString(nameElement, diagnostics, "/name", ContentDiagnosticCodes.NameInvalid, out var rawName) &&
               StrictJson.TryNormalizeText(
                   rawName,
                   NameMinLength,
                   NameMaxLength,
                   diagnostics,
                   "/name",
                   ContentDiagnosticCodes.NameInvalid,
                   out name);
    }

    private static bool TryReadVersion(JsonElement root, DiagnosticBag diagnostics, out string version)
    {
        version = "";
        if (!root.TryGetProperty("version", out var versionElement))
        {
            diagnostics.Reject(ContentDiagnosticCodes.VersionInvalid, "version is required.", "/version");
            return false;
        }

        if (!StrictJson.TryGetString(versionElement, diagnostics, "/version", ContentDiagnosticCodes.VersionInvalid, out var rawVersion) ||
            !SemVer.IsValid(rawVersion))
        {
            if (!diagnostics.Items.Any(item => item.Code == ContentDiagnosticCodes.VersionInvalid && item.JsonPath == "/version"))
                diagnostics.Reject(ContentDiagnosticCodes.VersionInvalid, "version must be a SemVer 2.0 string.", "/version");
            return false;
        }

        version = rawVersion;
        return true;
    }

    private static bool TryReadAuthors(JsonElement root, DiagnosticBag diagnostics, out IReadOnlyList<string> authors)
    {
        authors = Array.Empty<string>();
        if (!root.TryGetProperty("authors", out var authorsElement))
        {
            diagnostics.Reject(ContentDiagnosticCodes.AuthorsInvalid, "authors is required.", "/authors");
            return false;
        }

        return TryParseAuthors(authorsElement, diagnostics, out authors);
    }

    private static bool TryReadDescription(JsonElement root, DiagnosticBag diagnostics, out string description)
    {
        description = "";
        if (!root.TryGetProperty("description", out var descriptionElement))
            return true;

        return StrictJson.TryGetString(
                   descriptionElement,
                   diagnostics,
                   "/description",
                   ContentDiagnosticCodes.DescriptionInvalid,
                   out var rawDescription) &&
               StrictJson.TryNormalizeText(
                   rawDescription,
                   DescriptionMinLength,
                   DescriptionMaxLength,
                   diagnostics,
                   "/description",
                   ContentDiagnosticCodes.DescriptionInvalid,
                   out description);
    }

    private static bool TryParseAuthors(
        JsonElement element,
        DiagnosticBag diagnostics,
        out IReadOnlyList<string> authors)
    {
        authors = Array.Empty<string>();
        if (element.ValueKind != JsonValueKind.Array)
        {
            diagnostics.Reject(ContentDiagnosticCodes.AuthorsInvalid, "authors must be an array.", "/authors");
            return false;
        }

        var parsed = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            var path = $"/authors/{index}";
            if (!StrictJson.TryGetString(item, diagnostics, path, ContentDiagnosticCodes.AuthorsInvalid, out var raw) ||
                !StrictJson.TryNormalizeText(
                    raw,
                    AuthorMinLength,
                    AuthorMaxLength,
                    diagnostics,
                    path,
                    ContentDiagnosticCodes.AuthorsInvalid,
                    out var author))
            {
                return false;
            }

            if (!seen.Add(author))
            {
                diagnostics.Reject(ContentDiagnosticCodes.AuthorsInvalid, "authors must be unique after trimming.", path);
                return false;
            }

            parsed.Add(author);
            index++;
        }

        if (parsed.Count is < AuthorsMinCount or > AuthorsMaxCount)
        {
            diagnostics.Reject(
                ContentDiagnosticCodes.AuthorsInvalid,
                "authors must contain 1 to 16 unique names.",
                "/authors");
            return false;
        }

        authors = new ReadOnlyCollection<string>(parsed);
        return true;
    }

    public static bool TryCanonicalUuid(string? value, out string canonical)
    {
        canonical = "";
        if (value is null || !CanonicalUuid.IsMatch(value))
            return false;
        if (value == "00000000-0000-0000-0000-000000000000")
            return false;
        canonical = value;
        return true;
    }
}

internal readonly struct ModManifestRead
{
    public static ModManifestRead None => default;

    public string? CanonicalId { get; }
    public ModManifest? Manifest { get; }

    public ModManifestRead(string? canonicalId, ModManifest? manifest = null)
    {
        CanonicalId = canonicalId;
        Manifest = manifest;
    }
}

internal static class SemVer
{
    public static bool IsValid(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return false;

        var plus = value.IndexOf('+');
        var coreAndPre = plus < 0 ? value : value[..plus];
        var build = plus < 0 ? null : value[(plus + 1)..];
        if (plus >= 0 && build!.Length == 0)
            return false;

        var dash = coreAndPre.IndexOf('-');
        var core = dash < 0 ? coreAndPre : coreAndPre[..dash];
        var pre = dash < 0 ? null : coreAndPre[(dash + 1)..];
        if (dash >= 0 && pre!.Length == 0)
            return false;

        var coreParts = core.Split('.');
        if (coreParts.Length != 3)
            return false;
        if (!IsNumericIdentifier(coreParts[0]) ||
            !IsNumericIdentifier(coreParts[1]) ||
            !IsNumericIdentifier(coreParts[2]))
            return false;

        if (pre is not null && !AreIdentifiers(pre, prerelease: true))
            return false;
        if (build is not null && !AreIdentifiers(build, prerelease: false))
            return false;
        return true;
    }

    private static bool AreIdentifiers(string text, bool prerelease)
    {
        if (text.Length == 0)
            return false;
        var start = 0;
        for (var i = 0; i <= text.Length; i++)
        {
            if (i < text.Length && text[i] != '.')
                continue;
            if (i == start)
                return false;
            if (!IsIdentifier(text.AsSpan(start, i - start), prerelease))
                return false;
            start = i + 1;
        }

        return true;
    }

    private static bool IsIdentifier(ReadOnlySpan<char> value, bool prerelease)
    {
        if (value.Length == 0)
            return false;
        var digitsOnly = true;
        foreach (var c in value)
        {
            if (c is (>= '0' and <= '9') or (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or '-')
            {
                if (c is not (>= '0' and <= '9'))
                    digitsOnly = false;
                continue;
            }

            return false;
        }

        if (prerelease && digitsOnly)
            return IsNumericIdentifier(value.ToString());
        return true;
    }

    private static bool IsNumericIdentifier(string value)
    {
        if (value.Length == 0)
            return false;
        if (value == "0")
            return true;
        if (value[0] is < '1' or > '9')
            return false;
        for (var i = 1; i < value.Length; i++)
        {
            if (value[i] is < '0' or > '9')
                return false;
        }

        return true;
    }
}
