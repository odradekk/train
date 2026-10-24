using System.Collections.Immutable;
using PartOrder = (int Kind, int Z, int X, string Key);

namespace Train.Scenes;

internal sealed class NodeHeights
{
    private sealed record Entry(NodeScene.Part Part, ImmutableArray<(double X, double Z)> Buckets);
    internal static readonly IComparer<PartOrder> Order = Comparer<PartOrder>.Create((a, b) =>
        a.Kind != b.Kind ? a.Kind.CompareTo(b.Kind) : a.Z != b.Z ? a.Z.CompareTo(b.Z) :
        a.X != b.X ? a.X.CompareTo(b.X) : StringComparer.Ordinal.Compare(a.Key, b.Key));
    internal static NodeHeights Empty { get; } = new(
        ImmutableDictionary<(double X, double Z), ImmutableSortedDictionary<PartOrder, NodeScene.Part>>.Empty,
        ImmutableDictionary<PartOrder, Entry>.Empty);
    private readonly ImmutableDictionary<(double X, double Z), ImmutableSortedDictionary<PartOrder, NodeScene.Part>> buckets;
    private readonly ImmutableDictionary<PartOrder, Entry> entries;

    private NodeHeights(
        ImmutableDictionary<(double X, double Z), ImmutableSortedDictionary<PartOrder, NodeScene.Part>> buckets,
        ImmutableDictionary<PartOrder, Entry> entries)
    { this.buckets = buckets; this.entries = entries; }

    internal NodeHeights WithPart(PartOrder key, NodeScene.Part? part)
    {
        entries.TryGetValue(key, out var old);
        if (ReferenceEquals(old?.Part, part) || old is null && part?.Heights.Count == 0) return this;
        var next = buckets.ToBuilder();
        if (old is not null)
            foreach (var bucket in old.Buckets)
            {
                var remaining = next[bucket].Remove(key);
                if (remaining.Count == 0) next.Remove(bucket);
                else next[bucket] = remaining;
            }
        var nextEntries = entries.Remove(key);
        if (part is not null && part.Heights.Count != 0)
        {
            var columns = part.Heights.Keys;
            var minX = Math.Floor((columns.Min(column => column.X) + part.Origin.X) / VoxelGrid.ChunkSize);
            var maxX = Math.Ceiling((columns.Max(column => column.X) + part.Origin.X + 1) / VoxelGrid.ChunkSize) - 1;
            var minZ = Math.Floor((columns.Min(column => column.Z) + part.Origin.Z) / VoxelGrid.ChunkSize);
            var maxZ = Math.Ceiling((columns.Max(column => column.Z) + part.Origin.Z + 1) / VoxelGrid.ChunkSize) - 1;
            var added = ImmutableArray.CreateBuilder<(double X, double Z)>();
            for (var z = minZ; z <= maxZ; z++)
                for (var x = minX; x <= maxX; x++)
                {
                    var bucket = (x, z);
                    next[bucket] = (next.GetValueOrDefault(bucket) ??
                        ImmutableSortedDictionary.Create<PartOrder, NodeScene.Part>(Order)).SetItem(key, part);
                    added.Add(bucket);
                }
            nextEntries = nextEntries.Add(key, new(part, added.ToImmutable()));
        }
        return new(next.ToImmutable(), nextEntries);
    }

    internal double? At(double x, double z)
    {
        if (!buckets.TryGetValue((Math.Floor(x / VoxelGrid.ChunkSize), Math.Floor(z / VoxelGrid.ChunkSize)), out var nearby)) return null;
        foreach (var part in nearby.Values)
        {
            var localX = Math.Floor(x - part.Origin.X);
            var localZ = Math.Floor(z - part.Origin.Z);
            if (localX >= int.MinValue && localX <= int.MaxValue && localZ >= int.MinValue && localZ <= int.MaxValue &&
                part.Heights.TryGetValue(new((int)localX, (int)localZ), out var height)) return height;
        }
        return null;
    }
}
