using System.Collections.Immutable;
using Train.Scenes.Contracts;

namespace Train.Scenes;

internal sealed class RailwayCache
{
    private readonly object gate = new();
    private ulong owner;
    private ImmutableDictionary<int, RailwaySegment> segments = ImmutableDictionary<int, RailwaySegment>.Empty;
    internal ulong OpenSession()
    {
        lock (gate) return ++owner;
    }
    internal RailwaySegment? Find(SceneWorldSnapshot snapshot, RailwayPath path, int index)
    {
        RailwaySegment? result;
        lock (gate) segments.TryGetValue(index, out result);
        return result?.Dependencies.Matches(snapshot, path, index) == true ? result : null;
    }
    internal void Publish(ulong session, IEnumerable<RailwaySegment> current)
    {
        var replacement = current.ToImmutableDictionary(item => item.Index);
        lock (gate)
            if (owner == session) segments = replacement;
    }
}
