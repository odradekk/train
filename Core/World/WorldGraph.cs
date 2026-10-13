using System.Collections.Immutable;
using Train.Core.Contracts;

namespace Train.Core.World;

internal readonly record struct CellId(ulong X, ulong Y)
{
    public override string ToString() => FormattableString.Invariant($"cell:{X},{Y}");
}

internal sealed record CandidateNode(CellId Cell, MapPoint Position, TerrainView Terrain);
internal sealed record CandidateEdge(int A, int B, double LengthMeters, TerrainView Terrain, int TrackLimit);

internal static class WorldGraph
{
    internal static ImmutableArray<CandidateEdge> Build(ImmutableArray<CandidateNode> nodes,
        WorldTerrain terrain, GenerationBudgetCounter budget)
    {
        budget.Stage = "edges";
        var candidates = new List<CandidateEdge>();
        for (var a = 0; a < nodes.Length; a++)
        for (var b = a + 1; b < nodes.Length; b++)
        {
            budget.Candidate = $"{nodes[a].Cell}/{nodes[b].Cell}";
            budget.CandidateCount();
            var passesNode = false;
            for (var c = 0; c < nodes.Length; c++)
                if (c != a && c != b && OnSegment(nodes[a].Position, nodes[b].Position, nodes[c].Position, budget))
                { passesNode = true; break; }
            if (!passesNode) candidates.Add(SampleEdge(a, b, nodes, terrain, budget));
        }
        budget.Sort(candidates.Count);
        candidates.Sort(Compare);
        var kept = new List<CandidateEdge>();
        foreach (var candidate in candidates)
        {
            budget.Candidate = $"{nodes[candidate.A].Cell}/{nodes[candidate.B].Cell}";
            if (!kept.Any(edge => Conflicts(candidate, edge, nodes, budget))) kept.Add(candidate);
        }
        var lookup = kept.ToDictionary(e => (e.A, e.B));
        for (var index = kept.Count - 1; index >= 0; index--)
        {
            var edge = kept[index];
            budget.Candidate = $"{nodes[edge.A].Cell}/{nodes[edge.B].Cell}";
            for (var c = 0; c < nodes.Length; c++)
            {
                budget.Work();
                if (c == edge.A || c == edge.B) continue;
                if (lookup.TryGetValue((Math.Min(edge.A, c), Math.Max(edge.A, c)), out var first)
                    && lookup.TryGetValue((Math.Min(edge.B, c), Math.Max(edge.B, c)), out var second)
                    && Compare(first, edge) < 0 && Compare(second, edge) < 0)
                { kept.RemoveAt(index); lookup.Remove((edge.A, edge.B)); break; }
            }
        }
        if (nodes.IsEmpty || WorldTraversal.Build(nodes.Length, kept.Select(e => (e.A, e.B)), budget).Reachable(0).Nodes.Count != nodes.Length)
        {
            budget.Candidate = "";
            throw new GenerationFailure("constraint_unsatisfied", "World.Connected");
        }
        budget.Sort(kept.Count);
        return kept.OrderBy(e => e.A).ThenBy(e => e.B).ToImmutableArray();
    }

    internal static int Compare(CandidateEdge left, CandidateEdge right)
    {
        var result = left.LengthMeters.CompareTo(right.LengthMeters);
        if (result == 0) result = left.A.CompareTo(right.A);
        return result == 0 ? left.B.CompareTo(right.B) : result;
    }

    private static CandidateEdge SampleEdge(int a, int b, ImmutableArray<CandidateNode> nodes,
        WorldTerrain terrain, GenerationBudgetCounter budget)
    {
        var start = nodes[a].Position;
        var end = nodes[b].Position;
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (!double.IsFinite(length) || length == 0)
            throw new GenerationFailure("config_invalid", "Configuration.WidthMeters");
        var interval = terrain.SampleSpacing;
        var steps = Math.Ceiling(length / interval);
        if (!double.IsFinite(steps) || steps >= (double)long.MaxValue)
            throw new GenerationFailure("budget_exhausted", "Configuration.Budget.MaximumSearchWork");
        var types = new Dictionary<TerrainTypeId, long>();
        var features = new HashSet<string>(StringComparer.Ordinal);
        var relief = 0.0;
        var multiplier = 1.0;
        var tracks = terrain.MaximumTracks;
        for (long sample = 0; sample <= (long)steps; sample++)
        {
            var t = Math.Min(sample * interval, length) / length;
            var current = terrain.Sample(new(start.X + dx * t, start.Y + dy * t));
            types[current.View.TypeId] = types.GetValueOrDefault(current.View.TypeId) + 1;
            relief = Math.Max(relief, current.View.Relief);
            multiplier = Math.Max(multiplier, current.Band.DetourMultiplier);
            tracks = Math.Min(tracks, current.Band.MaximumTracks);
            features.UnionWith(current.View.FeatureIds);
        }
        var effectiveLength = length * multiplier;
        if (!double.IsFinite(effectiveLength))
            throw new GenerationFailure("config_invalid", "Configuration.TerrainBands");
        var main = types.OrderByDescending(p => p.Value).ThenBy(p => p.Key.Value, StringComparer.Ordinal).First().Key;
        return new(a, b, effectiveLength, new(main, relief, features.Order(StringComparer.Ordinal).ToImmutableArray()), tracks);
    }

    private static double Direction(MapPoint a, MapPoint b, MapPoint c, GenerationBudgetCounter budget)
    {
        budget.Work();
        var value = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
        if (!double.IsFinite(value))
            throw new GenerationFailure("config_invalid", "Configuration.WidthMeters");
        return value;
    }

    private static bool OnSegment(MapPoint a, MapPoint b, MapPoint point, GenerationBudgetCounter budget)
        => Direction(a, b, point, budget) == 0 && point.X >= Math.Min(a.X, b.X) && point.X <= Math.Max(a.X, b.X)
            && point.Y >= Math.Min(a.Y, b.Y) && point.Y <= Math.Max(a.Y, b.Y);

    private static bool Conflicts(CandidateEdge left, CandidateEdge right,
        ImmutableArray<CandidateNode> nodes, GenerationBudgetCounter budget)
    {
        var a = nodes[left.A].Position;
        var b = nodes[left.B].Position;
        var c = nodes[right.A].Position;
        var d = nodes[right.B].Position;
        var first = Direction(a, b, c, budget);
        var second = Direction(a, b, d, budget);
        var third = Direction(c, d, a, budget);
        var fourth = Direction(c, d, b, budget);
        if (Math.Sign(first) * Math.Sign(second) < 0 && Math.Sign(third) * Math.Sign(fourth) < 0) return true;
        return (right.A != left.A && right.A != left.B && first == 0 && OnSegment(a, b, c, budget))
            || (right.B != left.A && right.B != left.B && second == 0 && OnSegment(a, b, d, budget))
            || (left.A != right.A && left.A != right.B && third == 0 && OnSegment(c, d, a, budget))
            || (left.B != right.A && left.B != right.B && fourth == 0 && OnSegment(c, d, b, budget));
    }
}
