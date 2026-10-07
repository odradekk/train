// Train.Core.Simulation 定义确定性文档读取的结果。
// 错误只含稳定分类、代码和字段路径，不包含异常消息或运行环境噪声。

namespace Train.Core.Simulation;

/// <summary>区分未知格式、非法数据和不能进行同输入比较的上下文。</summary>
/// <example>命令版本 1 是 UnknownFormat；零实体 ID 是 InvalidData；不同构建是 IncompatibleContext。</example>
public enum DeterminismErrorKind
{
    /// <summary>文档版本、领域格式或命令种类缺失或不受支持。</summary>
    UnknownFormat,
    /// <summary>已知格式的数据不满足类型、范围或完整边界约束。</summary>
    InvalidData,
    /// <summary>来源、构建、语义内容或兼容环境不满足同输入对照条件。</summary>
    IncompatibleContext
}

/// <summary>表示确定性文档中可稳定定位的错误。</summary>
/// <param name="Kind">错误所属分类。</param>
/// <param name="Code">稳定错误代码，不使用异常消息。</param>
/// <param name="Path">JSON Pointer 字段路径；空字符串表示整个文档。</param>
/// <example>不支持命令版本时，代码为 unsupported_input_format，路径为 /input_format_version。</example>
public sealed record DeterminismError(DeterminismErrorKind Kind, string Code, string Path);

/// <summary>表示读取、校验或恢复得到完整值，或者得到一个明确错误。</summary>
/// <typeparam name="T">成功时的完整结果；不能用部分结果代替。</typeparam>
/// <example>恢复失败只有 Failure；不会同时返回一个已经部分恢复的会话。</example>
public abstract record DeterminismResult<T> where T : notnull
{
    /// <summary>表示全部读取与校验成功。</summary>
    /// <param name="Value">可以交给下一阶段使用的完整值。</param>
    public sealed record Success(T Value) : DeterminismResult<T>;

    /// <summary>表示读取或校验失败，没有发布任何候选。</summary>
    /// <param name="Error">首个按协议检查顺序发现的错误。</param>
    public sealed record Failure(DeterminismError Error) : DeterminismResult<T>;
}
