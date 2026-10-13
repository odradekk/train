using System.Collections.Immutable;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes;

internal sealed class RailwayCache
{
    private readonly object gate = new();
    private ulong next;
    private readonly Dictionary<EdgeId, (ulong Owner, ImmutableDictionary<int, RailwaySegment> Segments)> edges = [];
    internal ulong OpenSession(EdgeId edge)
    {
        lock (gate)
        {
            var old = edges.GetValueOrDefault(edge);
            edges[edge] = (++next, old.Segments ?? ImmutableDictionary<int, RailwaySegment>.Empty);
            return next;
        }
    }
    internal RailwaySegment? Find(SceneWorldSnapshot snapshot, RailwayPath path, int index)
    {
        RailwaySegment? result;
        lock (gate) result = edges.GetValueOrDefault(path.Edge.Id).Segments?.GetValueOrDefault(index);
        return result?.Dependencies.Matches(snapshot, path, index) == true ? result : null;
    }
    internal void Publish(EdgeId edge, ulong session, IEnumerable<RailwaySegment> current)
    {
        var replacement = current.ToImmutableDictionary(item => item.Index);
        lock (gate)
            if (edges.TryGetValue(edge, out var old) && old.Owner == session) edges[edge] = (session, replacement);
    }
    internal void Retain(EdgeId? current, EdgeId? candidate)
    { lock (gate) foreach (var id in edges.Keys.Where(id => id != current && id != candidate).ToArray()) edges.Remove(id); }
}
