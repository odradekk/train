
namespace Train.Core.Simulation;

public enum DeterminismErrorKind
{
    UnknownFormat,
    InvalidData,
    IncompatibleContext
}

public sealed record DeterminismError(DeterminismErrorKind Kind, string Code, string Path);

public abstract record DeterminismResult<T> where T : notnull
{
    public sealed record Success(T Value) : DeterminismResult<T>;

    public sealed record Failure(DeterminismError Error) : DeterminismResult<T>;
}
