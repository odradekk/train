// Train.Core.Simulation 定义权威检查点的对照数据和首差异结果。
// 上下文身份位于权威文档之外；比较先核对上下文，再定位最早对象与字段。

using System.Collections.Immutable;
using System.Text.Json;

namespace Train.Core.Simulation;

/// <summary>以稳定种类名和数值身份定位一个权威对象。</summary>
/// <param name="Kind">Ordinal 比较的稳定种类名；simulation 保留给基础边界。</param>
/// <param name="Id">同种类内稳定的 ulong 身份；零用于全局对象，实体对象必须使用非零实体 ID。</param>
/// <example>simulation/0 是基础边界；fixture.entity/7 是资源夹具的实体 7。</example>
public sealed record CheckpointObjectId(string Kind, ulong Id);

/// <summary>表示一个权威对象及其可比较的字段树。</summary>
/// <remarks>Fields 必须是无重复字段的 JSON 对象。对象字段按 Ordinal 排序，数组保留序列顺序；数值限明确整数及有限 double。</remarks>
/// <example>实体 7 的 fields 为 {"holding":3}；库存属于另一个全局对象。</example>
public sealed record CheckpointObject
{
    /// <summary>保存稳定对象身份和拥有独立生命周期的只读字段。</summary>
    /// <param name="identity">对象的稳定种类和身份。</param>
    /// <param name="fields">对象字段树；构造时克隆，原 JsonDocument 可随后释放。</param>
    public CheckpointObject(CheckpointObjectId identity, JsonElement fields)
    {
        Identity = identity;
        Fields = fields.Clone();
    }

    /// <summary>对象的稳定身份。</summary>
    public CheckpointObjectId Identity { get; }

    /// <summary>只读字段树，拥有独立于调用方 JsonDocument 的生命周期。</summary>
    public JsonElement Fields { get; }
}

/// <summary>表示一个完整边界的规范权威文档和摘要。</summary>
/// <param name="Position">由文档中的基础边界字段得到的模拟位置。</param>
/// <param name="CanonicalText">带检查点格式版本的规范 UTF-8 JSON 文档；不含外层对照身份。</param>
/// <param name="Sha256">文档 UTF-8 字节的 64 个大写十六进制字符。</param>
/// <remarks>由检查点编码或读取入口产生；比较必须核对实际字段，不能把调用方提供的摘要当成全部证据。</remarks>
/// <example>实体 7 的持有量从 3 改为 4 时，首差异为该对象的 /holding。</example>
public sealed record AuthoritativeCheckpoint(SimulationPosition Position, string CanonicalText, string Sha256);

/// <summary>表示确定性证据所在的操作系统、架构及 .NET 运行时版本。</summary>
/// <param name="OperatingSystem">规范操作系统名；本阶段支持 windows。</param>
/// <param name="Architecture">规范进程架构名；本阶段支持 x64。</param>
    /// <param name="DotNetRuntime">实际 System.Environment.Version 的数值版本文本，不是 SDK 版本。</param>
/// <example>windows、x64、10.0.x；跨运行时版本不能直接报告已验证一致。</example>
public sealed record DeterminismEnvironment(string OperatingSystem, string Architecture, string DotNetRuntime);

/// <summary>表示权威文档外的同输入对照身份，由驱动程序提供。</summary>
/// <param name="SourceSha256">原始起点的规范核心载荷摘要；中途恢复对照仍携带原始起点身份。</param>
/// <param name="BuildSha256">参与模拟的实际构建身份摘要，不以缺失值或单独版本名称替代。</param>
/// <param name="SemanticContentSha256">有效语义内容的摘要；不同值归为内容适配而非确定性失败。</param>
/// <param name="Environment">实际运行环境，本阶段只承诺兼容 Windows x64 与相同 .NET 运行时。</param>
/// <remarks>三个摘要均须是 64 个大写十六进制字符。这些字段不写入权威文档，不改变模拟。</remarks>
/// <example>中途恢复的对照沿用最初起点摘要；新的恢复载荷不替代这个原始来源身份。</example>
public sealed record DeterminismIdentity(
    string SourceSha256, string BuildSha256, string SemanticContentSha256, DeterminismEnvironment Environment);

/// <summary>表示同一对照身份下，按模拟位置严格递增采样的一组检查点。</summary>
/// <param name="Identity">在比较权威数据前核对的外层身份。</param>
/// <param name="Points">允许为空的不可变序列；位置不重复，不按墙钟时间排列。</param>
/// <example>依次采样 (0,0)、(0,1)、(1,0)，能够区分暂停命令和下一完整步。</example>
public sealed record CheckpointTrace(DeterminismIdentity Identity, ImmutableArray<AuthoritativeCheckpoint> Points);

/// <summary>表示兼容对照中最早缺失或不同的数据。</summary>
/// <param name="Position">最早不一致或单侧缺失的采样位置。</param>
/// <param name="Object">对象身份；整个检查点缺失时为 null。</param>
/// <param name="FieldPath">对象 fields 内的 JSON Pointer；整个对象或检查点缺失时为空。</param>
/// <param name="LeftValue">左侧值的规范 JSON；null 表示缺失，字符串 "null" 表示存在的 JSON null。</param>
/// <param name="RightValue">右侧值的规范 JSON；缺失与 JSON null 分别解释。</param>
/// <example>fixture.entity/7 的 /holding，左值 "3"，右值 "4"。</example>
public sealed record CheckpointDifference(
    SimulationPosition Position, CheckpointObjectId? Object, string FieldPath, string? LeftValue, string? RightValue);

/// <summary>表示已验证一致、找到首差异，或者不具备同输入比较条件。</summary>
/// <example>不同语义内容返回 NotComparable；同内容库存不同返回 Different。</example>
public abstract record CheckpointComparison
{
    /// <summary>表示上下文兼容且全部采样位置的实际权威字段一致。</summary>
    public sealed record Equal : CheckpointComparison;

    /// <summary>表示兼容对照中找到最早权威差异。</summary>
    /// <param name="Difference">最早检查点、对象及字段或单侧缺失位置。</param>
    public sealed record Different(CheckpointDifference Difference) : CheckpointComparison;

    /// <summary>表示身份缺失、不兼容或输入文档非法，不能宣称确定性一致或失败。</summary>
    /// <param name="Error">阻止有效对照的稳定分类与路径。</param>
    public sealed record NotComparable(DeterminismError Error) : CheckpointComparison;
}

/// <summary>读取、规范化检查点文档，并在兼容的轨迹中定位首差异。</summary>
/// <remarks>摘要和位置均由实际文档重算；比较不会信任调用方附带的摘要或位置。</remarks>
public static class SimulationCheckpoints
{
    /// <summary>检查点规范对象文档的第一版，与核心恢复载荷分别演进。</summary>
    public const uint CheckpointFormatVersion = 1;

    /// <summary>校验检查点文本，计算规范文档、位置及摘要。</summary>
    /// <param name="json">来自编码器或另一进程的检查点文档。</param>
    /// <returns>完整检查点，或未知版本、非法对象、重复身份及字段等错误。</returns>
    /// <remarks>不修改任何会话；读取的对象与状态投影使用同一规范编码。</remarks>
    public static DeterminismResult<AuthoritativeCheckpoint> Decode(string json) => ReadObjects(json) switch
    {
        DeterminismResult<ImmutableArray<CheckpointObject>>.Success success =>
            new DeterminismResult<AuthoritativeCheckpoint>.Success(Encode(success.Value)),
        DeterminismResult<ImmutableArray<CheckpointObject>>.Failure failure =>
            new DeterminismResult<AuthoritativeCheckpoint>.Failure(failure.Error),
        _ => throw new InvalidOperationException("未知检查点读取结果。")
    };

    /// <summary>在相同输入上下文下，计算两条轨迹的最早采样、对象和字段差异。</summary>
    /// <param name="left">第一条轨迹及完整对照身份。</param>
    /// <param name="right">第二条轨迹及完整对照身份。</param>
    /// <returns>已验证一致、最早差异，或不能开展同输入对照的明确原因。</returns>
    /// <remarks>先比较身份，再按模拟位置、对象种类及数值身份、字段 Ordinal 和数组下标定位。</remarks>
    public static CheckpointComparison Compare(CheckpointTrace left, CheckpointTrace right)
    {
        var error = CheckIdentity(left.Identity, right.Identity);
        return error is null
            ? ComparePoints(left.Points, right.Points)
            : new CheckpointComparison.NotComparable(error);
    }

    /// <summary>计算对象序列的规范文档、基础对象中的位置及文档摘要。</summary>
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

    /// <summary>校验完整文档、对象身份和字段树，读取可独立存活的对象。</summary>
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

    /// <summary>从必需的 simulation/0 对象读取步号和边界序号。</summary>
    private static SimulationPosition ReadPosition(ImmutableArray<CheckpointObject> objects)
    {
        var basic = objects.SingleOrDefault(item => item.Identity == new CheckpointObjectId("simulation", 0))
            ?? throw SimulationJson.Invalid("missing_simulation_object", "/objects");
        var state = SimulationStateDocument.Read(basic.Fields, "/objects/" + objects.IndexOf(basic) + "/fields");
        return new(state.CompletedSteps, state.BoundarySequence);
    }

    /// <summary>按 Kind、数值 ID 和递归字段顺序计算整个对象文档。</summary>
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

    /// <summary>计算字段树的规范文本；对象按字段名排序，数组保持顺序。</summary>
    private static string EncodeFields(JsonElement fields, string path = "") =>
        SimulationJson.Format(writer => SimulationJson.WriteCanonical(writer, fields, path));

    /// <summary>计算规范检查点文档 UTF-8 字节的大写 SHA-256 文本。</summary>
    private static string ComputeDocumentSha256(string text) => SimulationJson.Sha256(text);

    /// <summary>检查同输入对照所需的来源、实际构建、语义内容和运行环境。</summary>
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

    /// <summary>按采样位置比较完整文档，递归寻找最早对象及字段差异。</summary>
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

    /// <summary>读取一条轨迹的实际文档，并检查实际位置严格递增。</summary>
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

    /// <summary>按身份全序比较同一位置的对象。</summary>
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

    /// <summary>按字段名和数组下标递归返回第一处字段差异。</summary>
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
