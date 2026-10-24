using Train.Core.Contracts;

namespace Train.Scenes;

internal static class NodePartKeys
{
    internal const string Preparation = "preparation";
    internal const string Diagnostics = "diagnostics";
    private const string BoundaryPrefix = "boundary:";
    internal static string Boundary(EdgeId edge) => BoundaryPrefix + edge.Value;
    internal static bool IsBoundary(string key) => key.StartsWith(BoundaryPrefix, StringComparison.Ordinal);
}
