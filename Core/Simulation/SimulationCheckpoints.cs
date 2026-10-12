
using System.Collections.Immutable;
using System.Text.Json;

namespace Train.Core.Simulation;

public sealed record CheckpointObjectId(string Kind, ulong Id);

public sealed record CheckpointObject
{
    public CheckpointObject(CheckpointObjectId identity, JsonElement fields)
    {
        Identity = identity;
        Fields = fields.Clone();
    }

    public CheckpointObjectId Identity { get; }

    public JsonElement Fields { get; }
}

public sealed record AuthoritativeCheckpoint(SimulationPosition Position, string CanonicalText, string Sha256);

public sealed record DeterminismEnvironment(string OperatingSystem, string Architecture, string DotNetRuntime);

public sealed record DeterminismIdentity(
    string SourceSha256, string BuildSha256, string SemanticContentSha256, DeterminismEnvironment Environment);

public sealed record CheckpointTrace(DeterminismIdentity Identity, ImmutableArray<AuthoritativeCheckpoint> Points);

public sealed record CheckpointDifference(
    SimulationPosition Position, CheckpointObjectId? Object, string FieldPath, string? LeftValue, string? RightValue);

public abstract record CheckpointComparison
{
    public sealed record Equal : CheckpointComparison;

    public sealed record Different(CheckpointDifference Difference) : CheckpointComparison;

    public sealed record NotComparable(DeterminismError Error) : CheckpointComparison;
}

public static class SimulationCheckpoints
{
    public const uint CheckpointFormatVersion = 1;

    public static DeterminismResult<AuthoritativeCheckpoint> Decode(string json) => ReadObjects(json) switch
    {
        DeterminismResult<ImmutableArray<CheckpointObject>>.Success success =>
            new DeterminismResult<AuthoritativeCheckpoint>.Success(Encode(success.Value)),
        DeterminismResult<ImmutableArray<CheckpointObject>>.Failure failure =>
            new DeterminismResult<AuthoritativeCheckpoint>.Failure(failure.Error),
        _ => throw new InvalidOperationException("未知检查点读取结果。")
    };

    public static CheckpointComparison Compare(CheckpointTrace left, CheckpointTrace right)
    {
        var error = CheckIdentity(left.Identity, right.Identity);
        return error is null
            ? ComparePoints(left.Points, right.Points)
            : new CheckpointComparison.NotComparable(error);
    }

    internal static AuthoritativeCheckpoint Encode(ImmutableArray<CheckpointObject> objects)
    {
        try
        {
            var text = EncodeObjects(objects);
            return new(ReadPosition(objects), text, ComputeDocumentSha256(text));
        }
        catch (SimulationJsonException exception)
        {
            throw new ArgumentException($"非法检查点对象：{exception.Error.Code} {exception.Error.Path}", nameof(objects), exception);
        }
    }

    private static DeterminismResult<ImmutableArray<CheckpointObject>> ReadObjects(string json)
    {
        try
        {
            var root = SimulationJson.ParseObject(json);
            SimulationJson.CheckFields(root, "", "checkpoint_format_version", "objects");
            var version = SimulationJson.UInt32(root.GetProperty("checkpoint_format_version"), "/checkpoint_format_version");
            if (version != CheckpointFormatVersion)
                return new DeterminismResult<ImmutableArray<CheckpointObject>>.Failure(
                    new(DeterminismErrorKind.UnknownFormat, "unsupported_checkpoint_format", "/checkpoint_format_version"));

            var array = root.GetProperty("objects");
            if (array.ValueKind != JsonValueKind.Array)
                throw SimulationJson.Invalid("invalid_type", "/objects");

            var result = ImmutableArray.CreateBuilder<CheckpointObject>();
            var identities = new HashSet<CheckpointObjectId>();
            var index = 0;
            foreach (var value in array.EnumerateArray())
            {
                var path = $"/objects/{index}";
                SimulationJson.CheckFields(value, path, "kind", "id", "fields");
                var kind = SimulationJson.String(value.GetProperty("kind"), path + "/kind", nonEmpty: true);
                var id = SimulationJson.UInt64(value.GetProperty("id"), path + "/id");
                var fields = value.GetProperty("fields");
                SimulationJson.RequireObject(fields, path + "/fields");
                SimulationJson.CheckTree(fields, path + "/fields");
                // 规范写入同时验证全部数值均能由跨进程格式精确表示。
                _ = EncodeFields(fields, path + "/fields");

                var identity = new CheckpointObjectId(kind, id);
                if (!identities.Add(identity))
                    throw SimulationJson.Invalid("duplicate_identity", path);
                if (kind == "simulation" && id != 0)
                    throw SimulationJson.Invalid("reserved_identity", path);
                result.Add(new(identity, fields));
                index++;
            }

            if (!identities.Contains(new("simulation", 0)))
                throw SimulationJson.Invalid("missing_simulation_object", "/objects");
            var basicIndex = result.Select((item, itemIndex) => (item, itemIndex))
                .Single(pair => pair.item.Identity == new CheckpointObjectId("simulation", 0));
            _ = SimulationStateDocument.Read(basicIndex.item.Fields, $"/objects/{basicIndex.itemIndex}/fields");
            return new DeterminismResult<ImmutableArray<CheckpointObject>>.Success(result.ToImmutable());
        }
        catch (SimulationJsonException exception)
        {
            return new DeterminismResult<ImmutableArray<CheckpointObject>>.Failure(exception.Error);
        }
        catch (JsonException)
        {
            return new DeterminismResult<ImmutableArray<CheckpointObject>>.Failure(
                new(DeterminismErrorKind.InvalidData, "invalid_json", ""));
        }
        catch (ArgumentException)
        {
            return new DeterminismResult<ImmutableArray<CheckpointObject>>.Failure(
                new(DeterminismErrorKind.InvalidData, "invalid_json", ""));
        }
    }

    private static SimulationPosition ReadPosition(ImmutableArray<CheckpointObject> objects)
    {
        var basic = objects.SingleOrDefault(item => item.Identity == new CheckpointObjectId("simulation", 0))
            ?? throw SimulationJson.Invalid("missing_simulation_object", "/objects");
        var state = SimulationStateDocument.Read(basic.Fields, "/objects/" + objects.IndexOf(basic) + "/fields");
        return new(state.CompletedSteps, state.BoundarySequence);
    }

    private static string EncodeObjects(ImmutableArray<CheckpointObject> objects)
    {
        var identities = new HashSet<CheckpointObjectId>();
        for (var index = 0; index < objects.Length; index++)
        {
            var item = objects[index] ?? throw new ArgumentException("检查点对象不能为空。", nameof(objects));
            var path = $"/objects/{index}";
            if (string.IsNullOrEmpty(item.Identity.Kind))
                throw SimulationJson.Invalid("empty_string", path + "/kind");
            if (!identities.Add(item.Identity))
                throw SimulationJson.Invalid("duplicate_identity", path);
            if (item.Identity.Kind == "simulation" && item.Identity.Id != 0)
                throw SimulationJson.Invalid("reserved_identity", path);
            SimulationJson.RequireObject(item.Fields, path + "/fields");
            SimulationJson.CheckTree(item.Fields, path + "/fields");
            _ = EncodeFields(item.Fields, path + "/fields");
        }
        if (!identities.Contains(new("simulation", 0)))
            throw SimulationJson.Invalid("missing_simulation_object", "/objects");

        return SimulationJson.Format(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("checkpoint_format_version", CheckpointFormatVersion);
            writer.WritePropertyName("objects");
            writer.WriteStartArray();
            foreach (var item in objects.OrderBy(item => item.Identity.Kind, StringComparer.Ordinal)
                         .ThenBy(item => item.Identity.Id))
            {
                writer.WriteStartObject();
                writer.WriteString("kind", item.Identity.Kind);
                writer.WriteNumber("id", item.Identity.Id);
                writer.WritePropertyName("fields");
                SimulationJson.WriteCanonical(writer, item.Fields);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        });
    }

    private static string EncodeFields(JsonElement fields, string path = "") =>
        SimulationJson.Format(writer => SimulationJson.WriteCanonical(writer, fields, path));

    private static string ComputeDocumentSha256(string text) => SimulationJson.Sha256(text);

    private static DeterminismError? CheckIdentity(DeterminismIdentity left, DeterminismIdentity right)
    {
        if (left is null || right is null)
            return Incompatible("missing_identity", "/identity");
        foreach (var (leftValue, rightValue, path) in new[]
                 {
                     (left.SourceSha256, right.SourceSha256, "/identity/source_sha256"),
                     (left.BuildSha256, right.BuildSha256, "/identity/build_sha256"),
                     (left.SemanticContentSha256, right.SemanticContentSha256, "/identity/semantic_content_sha256")
                 })
        {
            if (string.IsNullOrEmpty(leftValue) || string.IsNullOrEmpty(rightValue))
                return Incompatible("missing_identity", path);
            if (!IsDigest(leftValue) || !IsDigest(rightValue))
                return Incompatible("invalid_identity", path);
        }
        if (left.Environment is null || right.Environment is null)
            return Incompatible("missing_identity", "/identity/environment");
        if (string.IsNullOrEmpty(left.Environment.DotNetRuntime) || string.IsNullOrEmpty(right.Environment.DotNetRuntime))
            return Incompatible("missing_identity", "/identity/environment/dotnet_runtime");
        if (!Version.TryParse(left.Environment.DotNetRuntime, out var leftRuntime) || leftRuntime.Build < 0 ||
            !Version.TryParse(right.Environment.DotNetRuntime, out var rightRuntime) || rightRuntime.Build < 0)
            return Incompatible("invalid_identity", "/identity/environment/dotnet_runtime");
        if (left.SourceSha256 != right.SourceSha256)
            return Incompatible("source_mismatch", "/identity/source_sha256");
        if (left.BuildSha256 != right.BuildSha256)
            return Incompatible("build_mismatch", "/identity/build_sha256");
        if (left.SemanticContentSha256 != right.SemanticContentSha256)
            return Incompatible("semantic_content_mismatch", "/identity/semantic_content_sha256");
        if (left.Environment != right.Environment)
            return Incompatible("environment_mismatch", "/identity/environment");
        if (!IsSupported(left.Environment))
            return Incompatible("unsupported_environment", "/identity/environment");
        return null;
    }

    private static CheckpointComparison ComparePoints(
        ImmutableArray<AuthoritativeCheckpoint> left, ImmutableArray<AuthoritativeCheckpoint> right)
    {
        var leftRead = ReadTrace(left);
        if (leftRead.Error is not null)
            return new CheckpointComparison.NotComparable(leftRead.Error);
        var rightRead = ReadTrace(right);
        if (rightRead.Error is not null)
            return new CheckpointComparison.NotComparable(rightRead.Error);

        var leftIndex = 0;
        var rightIndex = 0;
        while (leftIndex < leftRead.Points.Length || rightIndex < rightRead.Points.Length)
        {
            if (leftIndex == leftRead.Points.Length)
                return MissingPoint(rightRead.Points[rightIndex], leftMissing: true);
            if (rightIndex == rightRead.Points.Length)
                return MissingPoint(leftRead.Points[leftIndex], leftMissing: false);
            var leftPoint = leftRead.Points[leftIndex];
            var rightPoint = rightRead.Points[rightIndex];
            var order = leftPoint.Point.Position.CompareTo(rightPoint.Point.Position);
            if (order < 0)
                return MissingPoint(leftPoint, leftMissing: false);
            if (order > 0)
                return MissingPoint(rightPoint, leftMissing: true);

            var difference = CompareObjects(leftPoint.Objects, rightPoint.Objects, leftPoint.Point.Position);
            if (difference is not null)
                return new CheckpointComparison.Different(difference);
            leftIndex++;
            rightIndex++;
        }
        return new CheckpointComparison.Equal();
    }

    private static (ImmutableArray<ReadPoint> Points, DeterminismError? Error) ReadTrace(
        ImmutableArray<AuthoritativeCheckpoint> points)
    {
        var result = ImmutableArray.CreateBuilder<ReadPoint>();
        SimulationPosition? previous = null;
        foreach (var supplied in points)
        {
            var objects = ReadObjects(supplied.CanonicalText);
            if (objects is DeterminismResult<ImmutableArray<CheckpointObject>>.Failure failure)
                return ([], failure.Error);
            var values = ((DeterminismResult<ImmutableArray<CheckpointObject>>.Success)objects).Value;
            AuthoritativeCheckpoint actual;
            try { actual = Encode(values); }
            catch (ArgumentException exception) when (exception.InnerException is SimulationJsonException jsonException)
            { return ([], jsonException.Error); }
            if (previous is { } position && actual.Position.CompareTo(position) <= 0)
                return ([], new(DeterminismErrorKind.InvalidData, "invalid_checkpoint_order", "/points"));
            previous = actual.Position;
            result.Add(new(actual, values.OrderBy(item => item.Identity.Kind, StringComparer.Ordinal)
                .ThenBy(item => item.Identity.Id).ToImmutableArray()));
        }
        return (result.ToImmutable(), null);
    }

    private static CheckpointDifference? CompareObjects(
        ImmutableArray<CheckpointObject> left, ImmutableArray<CheckpointObject> right, SimulationPosition position)
    {
        var li = 0;
        var ri = 0;
        while (li < left.Length || ri < right.Length)
        {
            if (li == left.Length)
                return new(position, right[ri].Identity, "", null, EncodeFields(right[ri].Fields));
            if (ri == right.Length)
                return new(position, left[li].Identity, "", EncodeFields(left[li].Fields), null);
            var order = CompareIdentity(left[li].Identity, right[ri].Identity);
            if (order < 0)
                return new(position, left[li].Identity, "", EncodeFields(left[li].Fields), null);
            if (order > 0)
                return new(position, right[ri].Identity, "", null, EncodeFields(right[ri].Fields));
            var fieldDifference = CompareValue(left[li].Fields, right[ri].Fields, "");
            if (fieldDifference is { } values)
                return new(position, left[li].Identity, values.Path, values.Left, values.Right);
            li++;
            ri++;
        }
        return null;
    }

    private static (string Path, string? Left, string? Right)? CompareValue(
        JsonElement left, JsonElement right, string path)
    {
        if (left.ValueKind != right.ValueKind)
            return (path, EncodeFields(left), EncodeFields(right));
        if (left.ValueKind == JsonValueKind.Object)
        {
            var leftFields = left.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal).ToArray();
            var rightFields = right.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal).ToArray();
            var li = 0;
            var ri = 0;
            while (li < leftFields.Length || ri < rightFields.Length)
            {
                if (li == leftFields.Length)
                    return (path + "/" + SimulationJson.Escape(rightFields[ri].Name), null, EncodeFields(rightFields[ri].Value));
                if (ri == rightFields.Length)
                    return (path + "/" + SimulationJson.Escape(leftFields[li].Name), EncodeFields(leftFields[li].Value), null);
                var order = StringComparer.Ordinal.Compare(leftFields[li].Name, rightFields[ri].Name);
                if (order < 0)
                    return (path + "/" + SimulationJson.Escape(leftFields[li].Name), EncodeFields(leftFields[li].Value), null);
                if (order > 0)
                    return (path + "/" + SimulationJson.Escape(rightFields[ri].Name), null, EncodeFields(rightFields[ri].Value));
                var nestedPath = path + "/" + SimulationJson.Escape(leftFields[li].Name);
                var difference = CompareValue(leftFields[li].Value, rightFields[ri].Value, nestedPath);
                if (difference is not null)
                    return difference;
                li++;
                ri++;
            }
            return null;
        }
        if (left.ValueKind == JsonValueKind.Array)
        {
            var leftItems = left.EnumerateArray().ToArray();
            var rightItems = right.EnumerateArray().ToArray();
            var common = Math.Min(leftItems.Length, rightItems.Length);
            for (var index = 0; index < common; index++)
            {
                var difference = CompareValue(leftItems[index], rightItems[index], path + "/" + index);
                if (difference is not null)
                    return difference;
            }
            if (leftItems.Length != rightItems.Length)
            {
                var index = common;
                return leftItems.Length > common
                    ? (path + "/" + index, EncodeFields(leftItems[index]), null)
                    : (path + "/" + index, null, EncodeFields(rightItems[index]));
            }
            return null;
        }
        var leftText = EncodeFields(left);
        var rightText = EncodeFields(right);
        return leftText == rightText ? null : (path, leftText, rightText);
    }

    private static CheckpointComparison MissingPoint(ReadPoint point, bool leftMissing) =>
        new CheckpointComparison.Different(new(point.Point.Position, null, "",
            leftMissing ? null : point.Point.CanonicalText, leftMissing ? point.Point.CanonicalText : null));

    private static int CompareIdentity(CheckpointObjectId left, CheckpointObjectId right)
    {
        var kindOrder = StringComparer.Ordinal.Compare(left.Kind, right.Kind);
        return kindOrder != 0 ? kindOrder : left.Id.CompareTo(right.Id);
    }

    private static bool IsDigest(string value) =>
        value.Length == 64 && value.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F');

    private static bool IsSupported(DeterminismEnvironment value) =>
        value.OperatingSystem == "windows" && value.Architecture == "x64";

    private static DeterminismError Incompatible(string code, string path) =>
        new(DeterminismErrorKind.IncompatibleContext, code, path);

    private sealed record ReadPoint(
        AuthoritativeCheckpoint Point, ImmutableArray<CheckpointObject> Objects);
}
