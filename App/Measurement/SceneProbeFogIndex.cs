using System.Collections.Immutable;
using System.Numerics;
using Train.Scenes.Contracts;

namespace Train.App.Measurement;

internal sealed class SceneProbeFogIndex
{
    private readonly Node? _root;

    internal SceneProbeFogIndex(ImmutableArray<SceneBounds> regions)
    {
        var items = regions.ToArray();
        _root = Build(items, 0, items.Length, 0);
    }

    internal IEnumerable<SceneBounds> Query(SceneBounds query) => Query(_root, query);

    private static Node? Build(SceneBounds[] items, int start, int count, int depth)
    {
        if (count == 0) return null;
        var useX = depth % 2 == 0;
        Array.Sort(items, start, count, Comparer<SceneBounds>.Create((a, b) =>
            (useX ? a.Minimum.X + a.Maximum.X : a.Minimum.Z + a.Maximum.Z)
            .CompareTo(useX ? b.Minimum.X + b.Maximum.X : b.Minimum.Z + b.Maximum.Z)));
        var middle = start + count / 2;
        var left = Build(items, start, middle - start, depth + 1);
        var right = Build(items, middle + 1, start + count - middle - 1, depth + 1);
        var region = items[middle];
        var minimum = region.Minimum;
        var maximum = region.Maximum;
        foreach (var child in new[] { left, right })
        {
            if (child is null) continue;
            minimum = Vector3.Min(minimum, child.Bounds.Minimum);
            maximum = Vector3.Max(maximum, child.Bounds.Maximum);
        }
        return new(new(minimum, maximum), region, left, right);
    }

    private static IEnumerable<SceneBounds> Query(Node? node, SceneBounds query)
    {
        if (node is null || !Intersects(node.Bounds, query)) yield break;
        if (Intersects(node.Region, query)) yield return node.Region;
        foreach (var region in Query(node.Left, query)) yield return region;
        foreach (var region in Query(node.Right, query)) yield return region;
    }

    private static bool Intersects(SceneBounds a, SceneBounds b) =>
        a.Minimum.X <= b.Maximum.X && a.Maximum.X >= b.Minimum.X &&
        a.Minimum.Z <= b.Maximum.Z && a.Maximum.Z >= b.Minimum.Z;

    private sealed record Node(SceneBounds Bounds, SceneBounds Region, Node? Left, Node? Right);
}
