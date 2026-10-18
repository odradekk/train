using System.Collections.Immutable;
using System.Numerics;
using Train.Content.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes;

internal static class ConstructionFog
{
    internal static ImmutableArray<SceneBounds> Band(Func<double, RailwaySample> sample,
        double begin, double end, double halfWidth, double minimumY, double maximumY)
    {
        if (end <= begin) return [];
        var rows = new SortedDictionary<int, List<(int Begin, int End)>>();
        var count = Math.Max(1, checked((int)Math.Ceiling((end - begin) / 2)));
        var previous = sample(begin);
        for (var i = 1; i <= count; i++)
        {
            var current = sample(begin + (end - begin) * i / count);
            var polygon = new[] { Side(previous, -halfWidth), Side(previous, halfWidth),
                Side(current, halfWidth), Side(current, -halfWidth) };
            var first = checked((int)Math.Floor(polygon.Min(p => p.Z)));
            var last = checked((int)Math.Ceiling(polygon.Max(p => p.Z)));
            for (var z = first; z < last; z++)
            {
                var xs = new List<double>();
                for (var edge = 0; edge < polygon.Length; edge++)
                {
                    var a = polygon[edge];
                    var b = polygon[(edge + 1) % polygon.Length];
                    if (a.Z >= z && a.Z <= z + 1) xs.Add(a.X);
                    foreach (var boundary in new[] { z, z + 1 })
                        if ((a.Z < boundary && b.Z > boundary) || (b.Z < boundary && a.Z > boundary))
                            xs.Add(a.X + (b.X - a.X) * (boundary - a.Z) / (b.Z - a.Z));
                }
                if (xs.Count == 0) continue;
                // Scene coordinates are voxel cells (0.5 m); each footprint is [x,x+1] × [z,z+1].
                var left = checked((int)Math.Floor(xs.Min()));
                var right = checked((int)Math.Ceiling(xs.Max()));
                if (left == right) continue;
                if (!rows.TryGetValue(z, out var intervals)) rows.Add(z, intervals = []);
                intervals.Add((left, right));
            }
            previous = current;
        }
        return Merge(rows, minimumY, maximumY);
    }

    internal static ImmutableArray<SceneBounds> Columns(IEnumerable<GridColumn> columns)
    {
        var rows = new SortedDictionary<int, List<(int Begin, int End)>>();
        foreach (var column in columns)
        {
            if (!rows.TryGetValue(column.Z, out var intervals)) rows.Add(column.Z, intervals = []);
            intervals.Add((column.X, column.X + 1));
        }
        return Merge(rows, -128, 128);
    }

    private static ImmutableArray<SceneBounds> Merge(SortedDictionary<int, List<(int Begin, int End)>> rows,
        double minimumY, double maximumY)
    {
        var result = ImmutableArray.CreateBuilder<SceneBounds>();
        var active = new Dictionary<(int Begin, int End), (int BeginZ, int EndZ)>();
        foreach (var (z, intervals) in rows)
        {
            intervals.Sort();
            var merged = new List<(int Begin, int End)>();
            foreach (var interval in intervals)
                if (merged.Count > 0 && interval.Begin <= merged[^1].End)
                    merged[^1] = (merged[^1].Begin, Math.Max(merged[^1].End, interval.End));
                else merged.Add(interval);
            foreach (var key in active.Keys.ToArray())
                if (active[key].EndZ != z || !merged.Contains(key))
                {
                    Add(key, active[key]);
                    active.Remove(key);
                }
            foreach (var interval in merged)
                active[interval] = (active.TryGetValue(interval, out var span) ? span.BeginZ : z, z + 1);
        }
        foreach (var (interval, span) in active) Add(interval, span);
        return result.ToImmutable();

        void Add((int Begin, int End) interval, (int BeginZ, int EndZ) span) => result.Add(new(
            new(interval.Begin, (float)minimumY, span.BeginZ), new(interval.End, (float)maximumY, span.EndZ)));
    }

    private static ScenePoint Side(RailwaySample sample, double width) => new(
        sample.Position.X + sample.Right.X * width, sample.Position.Y, sample.Position.Z + sample.Right.Z * width);

    internal static ImmutableArray<SceneBounds> Clip(IEnumerable<SceneBounds> missing, IEnumerable<SceneBounds> built)
    {
        var cutters = built.ToArray();
        var result = ImmutableArray.CreateBuilder<SceneBounds>();
        foreach (var group in missing.GroupBy(box => (box.Minimum.Y, box.Maximum.Y))
                     .OrderBy(group => group.Key.Item1).ThenBy(group => group.Key.Item2))
        {
            var sources = group.ToArray();
            // Keep exact source boundaries; built masks already have voxel-column precision.
            var boundaries = sources.Concat(cutters).SelectMany(box => new[] { box.Minimum.Z, box.Maximum.Z })
                .Distinct().Order().ToArray();
            var active = new Dictionary<(float Begin, float End), (float BeginZ, float EndZ)>();
            for (var row = 1; row < boundaries.Length; row++)
            {
                var z0 = boundaries[row - 1];
                var z1 = boundaries[row];
                var occupied = Intervals(cutters, z0, z1);
                var remaining = new List<(float Begin, float End)>();
                foreach (var interval in Intervals(sources, z0, z1))
                {
                    var begin = interval.Begin;
                    foreach (var cutter in occupied)
                    {
                        if (cutter.End <= begin) continue;
                        if (cutter.Begin >= interval.End) break;
                        if (cutter.Begin > begin) remaining.Add((begin, cutter.Begin));
                        begin = Math.Max(begin, cutter.End);
                        if (begin >= interval.End) break;
                    }
                    if (begin < interval.End) remaining.Add((begin, interval.End));
                }
                foreach (var interval in active.Keys.ToArray())
                    if (!remaining.Contains(interval))
                    {
                        Add(interval, active[interval]);
                        active.Remove(interval);
                    }
                foreach (var interval in remaining)
                    active[interval] = (active.TryGetValue(interval, out var span) ? span.BeginZ : z0, z1);
            }
            foreach (var (interval, span) in active) Add(interval, span);

            void Add((float Begin, float End) interval, (float BeginZ, float EndZ) span) => result.Add(new(
                new Vector3(interval.Begin, group.Key.Item1, span.BeginZ),
                new Vector3(interval.End, group.Key.Item2, span.EndZ)));
        }
        return result.ToImmutable();
    }

    private static List<(float Begin, float End)> Intervals(SceneBounds[] boxes, float minimumZ, float maximumZ)
    {
        var merged = new List<(float Begin, float End)>();
        foreach (var box in boxes.Where(box => box.Minimum.Z < maximumZ && box.Maximum.Z > minimumZ &&
                     box.Minimum.X < box.Maximum.X).OrderBy(box => box.Minimum.X).ThenBy(box => box.Maximum.X))
            if (merged.Count > 0 && box.Minimum.X <= merged[^1].End)
                merged[^1] = (merged[^1].Begin, Math.Max(merged[^1].End, box.Maximum.X));
            else merged.Add((box.Minimum.X, box.Maximum.X));
        return merged;
    }
}
