using System.Globalization;
using System.Text.Json;
using Train.Content;
using Train.Domain.Voxels;

namespace Train.Domain.Playground;

public static class AuthorityComparison
{
    private const int ValueLimit = 160;

    public static AuthorityDifference Compare(AuthorityObservation expected, AuthorityObservation actual)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);
        var context = CompareContext(expected.Context, actual.Context);
        if (!context.Equal)
            return context;

        var count = Math.Min(expected.Checkpoints.Count, actual.Checkpoints.Count);
        for (var index = 0; index < count; index++)
        {
            var difference = CompareCheckpoint(expected.Checkpoints[index], actual.Checkpoints[index]);
            if (!difference.Equal)
                return difference;
        }

        if (expected.Checkpoints.Count == actual.Checkpoints.Count)
            return AuthorityDifference.Match;

        var missing = count;
        var longer = expected.Checkpoints.Count > actual.Checkpoints.Count ? expected.Checkpoints : actual.Checkpoints;
        return new AuthorityDifference(
            AuthorityDifferenceKind.MissingCheckpoint,
            missing,
            longer[missing].Input,
            longer[missing].Boundary.ToString(),
            null,
            null,
            expected.Checkpoints.Count > missing ? "present" : "absent",
            actual.Checkpoints.Count > missing ? "present" : "absent",
            "检查点数量不一致。");
    }

    public static AuthorityDifference CompareContext(AuthorityContext expected, AuthorityContext actual)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);
        if (expected.FormatVersion != actual.FormatVersion)
            return ContextDifference(
                AuthorityDifferenceKind.VersionMismatch,
                "authority_format_version",
                expected.FormatVersion.ToString(CultureInfo.InvariantCulture),
                actual.FormatVersion.ToString(CultureInfo.InvariantCulture),
                "编码版本不一致。");

        if (Specified(expected.BuildId) != Specified(actual.BuildId) ||
            (Specified(expected.BuildId) && !string.Equals(expected.BuildId, actual.BuildId, StringComparison.Ordinal)))
            return ContextDifference(
                AuthorityDifferenceKind.EnvironmentMismatch,
                "build",
                expected.BuildId ?? "unspecified",
                actual.BuildId ?? "unspecified",
                "构建或运行环境不一致。");
        if (!IsWindows(expected.OperatingSystem) || !IsWindows(actual.OperatingSystem))
            return ContextDifference(
                AuthorityDifferenceKind.EnvironmentMismatch,
                "operating_system",
                expected.OperatingSystem,
                actual.OperatingSystem,
                "构建或运行环境不一致。");
        if (!string.Equals(expected.Architecture, actual.Architecture, StringComparison.Ordinal))
            return ContextDifference(
                AuthorityDifferenceKind.EnvironmentMismatch,
                "architecture",
                expected.Architecture,
                actual.Architecture,
                "构建或运行环境不一致。");
        if (!string.Equals(expected.Framework, actual.Framework, StringComparison.Ordinal))
            return ContextDifference(
                AuthorityDifferenceKind.EnvironmentMismatch,
                "framework",
                expected.Framework,
                actual.Framework,
                "构建或运行环境不一致。");

        if (!string.Equals(expected.SourceKind, actual.SourceKind, StringComparison.Ordinal) ||
            !string.Equals(expected.ConfigurationId, actual.ConfigurationId, StringComparison.Ordinal) ||
            expected.ConfigurationVersion != actual.ConfigurationVersion ||
            expected.Seed != actual.Seed ||
            !string.Equals(expected.GeneratorVersion, actual.GeneratorVersion, StringComparison.Ordinal))
            return ContextDifference(
                AuthorityDifferenceKind.SourceMismatch,
                "source",
                DescribeSource(expected),
                DescribeSource(actual),
                "来源、种子或生成器不一致。");

        if (!string.Equals(expected.ContentDigest, actual.ContentDigest, StringComparison.Ordinal))
            return ContextDifference(
                AuthorityDifferenceKind.ContentAdaptation,
                "content",
                expected.ContentDigest,
                actual.ContentDigest,
                "这是合法的内容适配，不是同一输入的确定性失败。");

        return AuthorityDifference.Match;
    }

    private static AuthorityDifference CompareCheckpoint(AuthorityCheckpoint expected, AuthorityCheckpoint actual)
    {
        if (string.Equals(expected.Canonical, actual.Canonical, StringComparison.Ordinal))
        {
            if (string.Equals(expected.Digest, actual.Digest, StringComparison.Ordinal))
                return AuthorityDifference.Match;
            return At(
                expected,
                AuthorityDifferenceKind.FieldMismatch,
                null,
                "digest",
                expected.Digest,
                actual.Digest,
                "摘要与规范文本不一致。");
        }

        try
        {
            using var left = JsonDocument.Parse(expected.Canonical);
            using var right = JsonDocument.Parse(actual.Canonical);
            return Walk(expected, left.RootElement, right.RootElement, "", null, null, null) ??
                At(
                    expected,
                    AuthorityDifferenceKind.FieldMismatch,
                    null,
                    "canonical",
                    Show(expected.Canonical),
                    Show(actual.Canonical),
                    "规范文本不同，但逐字段值没有更早的差异。");
        }
        catch (JsonException exception)
        {
            return At(
                expected,
                AuthorityDifferenceKind.FieldMismatch,
                null,
                "canonical",
                Show(expected.Canonical),
                Show(actual.Canonical),
                "权威文档不是合法 JSON。" + exception.Message);
        }
    }

    private static AuthorityDifference? Walk(
        AuthorityCheckpoint checkpoint,
        JsonElement expected,
        JsonElement actual,
        string path,
        string? propertyName,
        string? parentName,
        string? objectLabel)
    {
        if (expected.ValueKind != actual.ValueKind)
            return Leaf(checkpoint, path, objectLabel, expected, actual);

        switch (expected.ValueKind)
        {
            case JsonValueKind.Object:
                return WalkObject(checkpoint, expected, actual, path, propertyName, objectLabel);
            case JsonValueKind.Array:
                return IsKeyed(propertyName, parentName)
                    ? WalkKeyed(checkpoint, expected, actual, path, propertyName!, objectLabel)
                    : WalkOrdered(checkpoint, expected, actual, path, propertyName, objectLabel);
            default:
                return expected.GetRawText() == actual.GetRawText()
                    ? null
                    : Leaf(checkpoint, path, objectLabel, expected, actual);
        }
    }

    private static AuthorityDifference? WalkObject(
        AuthorityCheckpoint checkpoint,
        JsonElement expected,
        JsonElement actual,
        string path,
        string? container,
        string? objectLabel)
    {
        var actualFields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in actual.EnumerateObject())
            actualFields[property.Name] = property.Value;

        foreach (var property in expected.EnumerateObject())
        {
            var next = Append(path, property.Name);
            if (!actualFields.Remove(property.Name, out var actualValue))
                return At(checkpoint, AuthorityDifferenceKind.FieldMismatch, objectLabel, next, property.Value.GetRawText(), "absent", Detail(next, AuthorityDifferenceKind.FieldMismatch));
            if (Walk(checkpoint, property.Value, actualValue, next, property.Name, container, objectLabel) is { } difference)
                return difference;
        }

        foreach (var extra in actualFields)
        {
            var next = Append(path, extra.Key);
            return At(checkpoint, AuthorityDifferenceKind.FieldMismatch, objectLabel, next, "absent", Show(extra.Value.GetRawText()), Detail(next, AuthorityDifferenceKind.FieldMismatch));
        }

        return null;
    }

    private static AuthorityDifference? WalkOrdered(
        AuthorityCheckpoint checkpoint,
        JsonElement expected,
        JsonElement actual,
        string path,
        string? container,
        string? objectLabel)
    {
        var left = expected.EnumerateArray().ToArray();
        var right = actual.EnumerateArray().ToArray();
        var count = Math.Min(left.Length, right.Length);
        for (var index = 0; index < count; index++)
        {
            var next = $"{path}[{index.ToString(CultureInfo.InvariantCulture)}]";
            if (Walk(checkpoint, left[index], right[index], next, container, null, objectLabel) is { } difference)
                return difference;
        }

        if (left.Length == right.Length)
            return null;
        var missing = count;
        var nextPath = $"{path}[{missing.ToString(CultureInfo.InvariantCulture)}]";
        return At(
            checkpoint,
            AuthorityDifferenceKind.MissingObject,
            objectLabel,
            nextPath,
            missing < left.Length ? Show(left[missing].GetRawText()) : "absent",
            missing < right.Length ? Show(right[missing].GetRawText()) : "absent",
            "一侧缺少该对象。");
    }

    private static AuthorityDifference? WalkKeyed(
        AuthorityCheckpoint checkpoint,
        JsonElement expected,
        JsonElement actual,
        string path,
        string container,
        string? objectLabel)
    {
        var actualItems = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var item in actual.EnumerateArray())
        {
            var key = Identity(container, item);
            if (key is null || !actualItems.TryAdd(key, item))
                return At(checkpoint, AuthorityDifferenceKind.FieldMismatch, objectLabel, path, Show(item.GetRawText()), "duplicate", "对象身份重复，无法对照。");
        }

        foreach (var item in expected.EnumerateArray())
        {
            var key = Identity(container, item);
            if (key is null)
                return At(checkpoint, AuthorityDifferenceKind.FieldMismatch, objectLabel, path, Show(item.GetRawText()), "unkeyed", "对象缺少稳定身份。");
            var label = Label(container, item, objectLabel);
            var next = $"{path}[{key}]";
            if (!actualItems.Remove(key, out var actualItem))
                return At(checkpoint, AuthorityDifferenceKind.MissingObject, label, next, Show(item.GetRawText()), "absent", "一侧缺少该对象。");
            if (Walk(checkpoint, item, actualItem, next, container, null, label) is { } difference)
                return difference;
        }

        foreach (var extra in actualItems)
        {
            var label = Label(container, extra.Value, objectLabel);
            return At(checkpoint, AuthorityDifferenceKind.MissingObject, label, $"{path}[{extra.Key}]", "absent", Show(extra.Value.GetRawText()), "一侧缺少该对象。");
        }

        return null;
    }

    private static bool IsKeyed(string? name, string? parent) =>
        name is "world" or "save_local_definitions" or "vehicles" or "parts" or "segments" or
            "occupancy" or "destroyed" or "supports" or "assemblies" or "pending" ||
        (name == "connections" && parent == "railway");

    private static string? Identity(string name, JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
            return null;
        return name switch
        {
            "world" or "occupancy" or "destroyed" => Coord(item),
            "vehicles" or "parts" or "segments" or "assemblies" => Number(item, "id"),
            "save_local_definitions" => Text(item, "revision"),
            "pending" => Number(item, "edit_id"),
            "supports" => Join(Number(item, "segment_id"), Raw(item, "world_x_start"), Raw(item, "along_start")),
            "connections" when item.TryGetProperty("from_segment_id", out _) => Join(
                Number(item, "from_segment_id"),
                Text(item, "from_end"),
                Number(item, "to_segment_id"),
                Text(item, "to_end")),
            _ => null
        };
    }

    private static string Label(string name, JsonElement item, string? parent)
    {
        var identity = Identity(name, item) ?? "?";
        var local = name switch
        {
            "vehicles" => $"vehicle {Number(item, "id")}",
            "parts" => $"part {Number(item, "id")}",
            "assemblies" => $"assembly {Number(item, "id")}",
            "segments" => $"segment {Number(item, "id")}",
            "world" or "occupancy" or "destroyed" => $"cell {Coord(item)}",
            "save_local_definitions" => $"definition {Text(item, "revision")}",
            "pending" => $"pending {Number(item, "edit_id")}",
            "supports" => $"support {Number(item, "segment_id")}",
            "connections" => $"link {Number(item, "from_segment_id")}",
            _ => identity
        };
        return string.IsNullOrEmpty(parent) ? local : $"{parent} {local}";
    }

    private static AuthorityDifference Leaf(AuthorityCheckpoint checkpoint, string path, string? objectLabel, JsonElement expected, JsonElement actual) =>
        At(checkpoint, AuthorityDifferenceKind.FieldMismatch, objectLabel, path, Show(expected.GetRawText()), Show(actual.GetRawText()), Detail(path, AuthorityDifferenceKind.FieldMismatch));

    private static AuthorityDifference At(
        AuthorityCheckpoint checkpoint,
        AuthorityDifferenceKind kind,
        string? objectLabel,
        string? field,
        string? expected,
        string? actual,
        string detail) =>
        new(
            kind,
            checkpoint.Index,
            checkpoint.Input,
            checkpoint.Boundary.ToString(),
            objectLabel,
            field,
            expected,
            actual,
            Detail(field, kind, detail));

    private static AuthorityDifference ContextDifference(
        AuthorityDifferenceKind kind,
        string field,
        string? expected,
        string? actual,
        string detail) =>
        new(kind, null, null, null, null, field, expected, actual, detail);

    private static string Detail(string? field, AuthorityDifferenceKind kind, string fallback)
    {
        if (field is not null && field.Contains("simulation_fault", StringComparison.Ordinal))
            return "内部不变量故障与合法故障停车不是同一字段。这里是内部不变量故障。";
        if (field is not null && field.Contains("halt_reason", StringComparison.Ordinal))
            return "内部不变量故障与合法故障停车不是同一字段。这里是合法故障停车。";
        return kind == AuthorityDifferenceKind.FieldMismatch && fallback.Length == 0 ? "字段值不一致。" : fallback;
    }

    private static string Detail(string path, AuthorityDifferenceKind kind) =>
        Detail(path, kind, kind == AuthorityDifferenceKind.MissingObject ? "一侧缺少该对象。" : "字段值不一致。");

    private static string DescribeSource(AuthorityContext context)
    {
        var configuration = context.ConfigurationId is null
            ? context.SourceKind
            : $"{context.SourceKind} {context.ConfigurationId} v{context.ConfigurationVersion?.ToString(CultureInfo.InvariantCulture)}";
        return $"{configuration} seed={context.Seed.ToString(CultureInfo.InvariantCulture)} generator={context.GeneratorVersion}";
    }

    private static bool Specified(string? value) => !string.IsNullOrWhiteSpace(value);

    private static bool IsWindows(string operatingSystem) =>
        operatingSystem.Contains("Windows", StringComparison.OrdinalIgnoreCase);

    private static string Append(string path, string name) => path.Length == 0 ? name : path + "." + name;

    private static string Show(string raw) => raw.Length <= ValueLimit ? raw : raw[..ValueLimit] + "…";

    private static string? Coord(JsonElement item) => Join(Number(item, "x"), Number(item, "y"), Number(item, "z"));

    private static string? Join(params string?[] parts) => parts.Any(part => part is null) ? null : string.Join(',', parts);

    private static string? Number(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetRawText() : null;

    private static string? Text(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? Raw(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) ? value.GetRawText() : null;
}

public sealed record AuthoritySequenceReport(PlaygroundSequenceReport Sequence, AuthorityObservation? Observation)
{
    public bool Passed => Sequence.Passed && Observation is not null;

    public IEnumerable<string> Lines()
    {
        foreach (var line in Sequence.Lines())
            yield return line;
        if (Observation is null)
            yield break;
        foreach (var line in Observation.Lines())
            yield return line;
    }
}

public static class AuthorityValidation
{
    public static AuthoritySequenceReport Run(
        PlaygroundSequence sequence,
        VoxelCatalog catalog,
        CompositeCatalog composites,
        IWorldSaveStore? store = null,
        string? buildId = null)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        var status = PlaygroundSession.TryEstablish(sequence.ConfigurationId, catalog, composites, store, out var session);
        if (status != PlaygroundEstablishStatus.Established)
            return new AuthoritySequenceReport(
                new PlaygroundSequenceReport(sequence.Id, sequence.ConfigurationId, status, Array.Empty<PlaygroundStepResult>(), null),
                null);

        try
        {
            return Execute(sequence, session!, buildId);
        }
        finally
        {
            session!.Detach();
        }
    }

    public static AuthoritySequenceReport Execute(PlaygroundSequence sequence, VoxelDevelopmentSession session, string? buildId = null)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(session);
        if (!string.Equals(session.WorldSource?.ConfigurationId, sequence.ConfigurationId, StringComparison.Ordinal))
            return new AuthoritySequenceReport(
                new PlaygroundSequenceReport(
                    sequence.Id,
                    sequence.ConfigurationId,
                    PlaygroundEstablishStatus.ConfigurationMismatch,
                    Array.Empty<PlaygroundStepResult>(),
                    null),
                null);

        var checkpoints = new List<AuthorityCheckpoint> { AuthorityCodec.Checkpoint(session, 0, "establish") };
        var results = new List<PlaygroundStepResult>();
        for (var index = 0; index < sequence.Steps.Count; index++)
        {
            var step = sequence.Steps[index];
            var actual = step.Operation.Apply(session);
            var checks = step.Checkpoint
                .Select(check => new PlaygroundCheckResult(check.Observation, check.Expected, PlaygroundCheck.Observe(session, check.Observation)))
                .ToArray();
            results.Add(new PlaygroundStepResult(index, step.Operation.Describe(), step.ExpectedStatus, actual, checks));
            checkpoints.Add(AuthorityCodec.Checkpoint(session, index + 1, step.Operation.Describe()));
        }

        return new AuthoritySequenceReport(
            new PlaygroundSequenceReport(
                sequence.Id,
                sequence.ConfigurationId,
                PlaygroundEstablishStatus.Established,
                results,
                WorldSaveAuthority.Fingerprint(session)),
            new AuthorityObservation(AuthorityCodec.Context(session, buildId), checkpoints));
    }
}
