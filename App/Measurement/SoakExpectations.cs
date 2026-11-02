using System.Collections.Immutable;
using System.Text.Json;
using Train.Content.Contracts;
using Train.Core.Contracts;

namespace Train.App.Measurement;

internal sealed record SoakExpectation(string Key, NodeId? Node, double? Minimum, double? Maximum)
{
    internal string Counter => Node is { } node ? $"node:{node.Value}/{Key}" : Key;
}

internal sealed record SoakExpectations(ulong SavedAt, ulong ExpectedAt, ImmutableArray<SoakExpectation> Items)
{
    internal static SoakExpectations Read(JsonElement recipe, ulong savedAt)
    {
        var batches = recipe.GetProperty("expectations").EnumerateArray()
            .Where(item => item.GetProperty("at").GetUInt64() <= savedAt).ToArray();
        if (batches.Length == 0) throw new InvalidDataException("配方在保存点或之前没有负载期望。");
        var at = batches.Max(item => item.GetProperty("at").GetUInt64());
        return new(savedAt, at, batches.Where(item => item.GetProperty("at").GetUInt64() == at)
            .Select(item => new SoakExpectation(item.GetProperty("key").GetString()!,
                item.TryGetProperty("node", out var node) ? new NodeId(node.GetUInt64()) : null,
                item.TryGetProperty("minimum", out var minimum) ? minimum.GetDouble() : null,
                item.TryGetProperty("maximum", out var maximum) ? maximum.GetDouble() : null)).ToImmutableArray());
    }

    internal ImmutableSortedDictionary<string, double> Count(ICoreReadView view, IContentView content)
    {
        var world = WorldCounts.Read(view, content);
        if (Items.All(item => item.Node is null)) return world;
        var counts = world.ToBuilder();
        foreach (var group in Items.Where(item => item.Node is not null).GroupBy(item => item.Node))
        {
            var local = WorldCounts.Read(view, content, group.Key);
            foreach (var item in group) counts[item.Counter] = local[item.Key];
        }
        return counts.ToImmutable();
    }

    internal SoakExpectation[] Failed(IReadOnlyDictionary<string, double> counts) => Items
        .Where(item => !counts.TryGetValue(item.Counter, out var value) ||
            item.Minimum is { } minimum && value < minimum || item.Maximum is { } maximum && value > maximum).ToArray();
}

internal sealed record SoakCount(ImmutableSortedDictionary<string, double> Counts, HostBinding Binding, double At, double Seconds);

internal sealed class SoakLoadWindow
{
    internal const string FullLoad = "full";
    internal const string BelowRecipe = "below-recipe";
    internal const string Unknown = "unknown";
    private HostBinding _binding;
    private double _started;
    internal SoakCount? Reading { get; private set; }
    internal SoakExpectation[] Failed { get; private set; } = [];
    internal string LoadClass => Reading is null ? Unknown : Failed.Length == 0 ? FullLoad : BelowRecipe;

    internal void Begin(HostBinding binding, double started)
    {
        _binding = binding;
        _started = started;
        Reading = null;
        Failed = [];
    }

    internal bool Observe(SoakCount reading, SoakExpectations expectations)
    {
        if (reading.Binding != _binding || reading.At <= _started) return false;
        Reading = reading;
        Failed = expectations.Failed(reading.Counts);
        return true;
    }
}
