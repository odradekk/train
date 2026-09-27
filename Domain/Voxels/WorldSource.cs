using System.Text.Json;

namespace Train.Domain.Voxels;

public readonly record struct WorldSourceParameter(string Name, string Value);

public readonly record struct WorldSourceDependency(string Kind, string Id, string SemanticRevision);

public sealed class WorldSource : IEquatable<WorldSource>
{
    public const string FixedConfigurationKind = "fixed_configuration";

    public string Kind { get; }
    public string ConfigurationId { get; }
    public int ConfigurationVersion { get; }
    public IReadOnlyList<WorldSourceParameter> EffectiveParameters { get; }
    public IReadOnlyList<WorldSourceDependency> ContentDependencies { get; }

    public WorldSource(
        string kind,
        string configurationId,
        int configurationVersion,
        IReadOnlyList<WorldSourceParameter> effectiveParameters,
        IReadOnlyList<WorldSourceDependency> contentDependencies)
    {
        ArgumentException.ThrowIfNullOrEmpty(kind);
        ArgumentException.ThrowIfNullOrEmpty(configurationId);
        ArgumentNullException.ThrowIfNull(effectiveParameters);
        ArgumentNullException.ThrowIfNull(contentDependencies);
        Kind = kind;
        ConfigurationId = configurationId;
        ConfigurationVersion = configurationVersion;
        EffectiveParameters = effectiveParameters.ToArray();
        ContentDependencies = contentDependencies.ToArray();
    }

    public string? Parameter(string name) =>
        EffectiveParameters.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.Ordinal)).Value;

    public bool Equals(WorldSource? other) =>
        other is not null &&
        string.Equals(Kind, other.Kind, StringComparison.Ordinal) &&
        string.Equals(ConfigurationId, other.ConfigurationId, StringComparison.Ordinal) &&
        ConfigurationVersion == other.ConfigurationVersion &&
        EffectiveParameters.SequenceEqual(other.EffectiveParameters) &&
        ContentDependencies.SequenceEqual(other.ContentDependencies);

    public override bool Equals(object? obj) => Equals(obj as WorldSource);

    public override int GetHashCode() => HashCode.Combine(Kind, ConfigurationId, ConfigurationVersion);
}

internal static class WorldSourceCodec
{
    public static void Write(Utf8JsonWriter writer, WorldSource source)
    {
        writer.WriteStartObject("world_source");
        writer.WriteString("kind", source.Kind);
        writer.WriteString("configuration_id", source.ConfigurationId);
        writer.WriteNumber("configuration_version", source.ConfigurationVersion);
        writer.WriteStartArray("effective_parameters");
        foreach (var parameter in source.EffectiveParameters)
        {
            writer.WriteStartObject();
            writer.WriteString("name", parameter.Name);
            writer.WriteString("value", parameter.Value);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteStartArray("content_dependencies");
        foreach (var dependency in source.ContentDependencies)
        {
            writer.WriteStartObject();
            writer.WriteString("kind", dependency.Kind);
            writer.WriteString("id", dependency.Id);
            writer.WriteString("semantic_revision", dependency.SemanticRevision);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    public static bool TryRead(JsonElement element, out WorldSource source, out WorldSaveStatus status)
    {
        source = null!;
        status = WorldSaveStatus.UnknownFormat;
        if (element.ValueKind != JsonValueKind.Object)
            return false;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? kind = null;
        string? id = null;
        int? version = null;
        IReadOnlyList<WorldSourceParameter>? parameters = null;
        IReadOnlyList<WorldSourceDependency>? dependencies = null;
        foreach (var property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name))
                return false;
            switch (property.Name)
            {
                case "kind":
                    kind = ReadText(property.Value);
                    if (kind is null)
                        return false;
                    break;
                case "configuration_id":
                    id = ReadText(property.Value);
                    if (id is null)
                        return false;
                    break;
                case "configuration_version":
                    if (property.Value.ValueKind != JsonValueKind.Number ||
                        !property.Value.TryGetInt32(out var parsed) ||
                        parsed <= 0)
                        return false;
                    version = parsed;
                    break;
                case "effective_parameters":
                    if (!TryReadPairs(property.Value, "name", "value", out var pairs))
                        return false;
                    if (pairs.Select(pair => pair.First).Distinct(StringComparer.Ordinal).Count() != pairs.Count)
                        return false;
                    parameters = pairs.Select(pair => new WorldSourceParameter(pair.First, pair.Second)).ToArray();
                    break;
                case "content_dependencies":
                    if (!TryReadDependencies(property.Value, out dependencies))
                        return false;
                    break;
                default:
                    return false;
            }
        }

        if (kind is null || id is null || version is null || parameters is null || dependencies is null)
        {
            status = WorldSaveStatus.Truncated;
            return false;
        }

        if (!string.Equals(kind, WorldSource.FixedConfigurationKind, StringComparison.Ordinal))
        {
            status = WorldSaveStatus.UnknownSource;
            return false;
        }

        source = new WorldSource(kind, id, version.Value, parameters, dependencies);
        status = WorldSaveStatus.Loaded;
        return true;
    }

    private static string? ReadText(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.String)
            return null;
        var text = element.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static bool TryReadPairs(
        JsonElement element,
        string firstName,
        string secondName,
        out List<(string First, string Second)> pairs)
    {
        pairs = new List<(string, string)>();
        if (element.ValueKind != JsonValueKind.Array)
            return false;
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                return false;
            string? first = null;
            string? second = null;
            var count = 0;
            foreach (var property in item.EnumerateObject())
            {
                count++;
                if (property.Name == firstName && first is null)
                    first = ReadText(property.Value);
                else if (property.Name == secondName && second is null)
                    second = ReadText(property.Value);
                else
                    return false;
            }

            if (count != 2 || first is null || second is null)
                return false;
            pairs.Add((first, second));
        }

        return true;
    }

    private static bool TryReadDependencies(JsonElement element, out IReadOnlyList<WorldSourceDependency> dependencies)
    {
        dependencies = Array.Empty<WorldSourceDependency>();
        if (element.ValueKind != JsonValueKind.Array)
            return false;
        var list = new List<WorldSourceDependency>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                return false;
            string? kind = null;
            string? id = null;
            string? revision = null;
            var count = 0;
            foreach (var property in item.EnumerateObject())
            {
                count++;
                switch (property.Name)
                {
                    case "kind" when kind is null:
                        kind = ReadText(property.Value);
                        break;
                    case "id" when id is null:
                        id = ReadText(property.Value);
                        break;
                    case "semantic_revision" when revision is null:
                        revision = ReadText(property.Value);
                        break;
                    default:
                        return false;
                }
            }

            if (count != 3 || kind is null || id is null || revision is null)
                return false;
            list.Add(new WorldSourceDependency(kind, id, revision));
        }

        if (list.Select(item => (item.Kind, item.Id)).Distinct().Count() != list.Count)
            return false;
        dependencies = list;
        return true;
    }
}
