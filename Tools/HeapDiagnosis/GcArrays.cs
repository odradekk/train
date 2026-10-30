using Graphs;
using System.Text.RegularExpressions;

namespace Train.HeapDiagnosis;

internal sealed class GcArrays
{
    public string Attribution => "Raw graph object bytes including headers. ParentTypes counts each array once per parent type; types overlap and are not exclusive retained sizes. PoolTypes uses type-name matches only and direct edges. StackPaths counts all reachable arrays and existence of any path through an ArrayPool-named object, independently per root category. Same-size matches do not identify objects.";
    public List<GcArrayBucket> Histogram { get; } = [];
    public List<GcArrayLargest> LargestByteArrays { get; } = [];
    public List<GcArrayLargest> LargestCharArrays { get; } = [];
    public List<GcArrayParent> ParentTypes { get; } = [];
    public List<GcArrayPool> PoolTypes { get; } = [];
    public List<GcArrayStack> StackPaths { get; } = [];
    private readonly Dictionary<(string Type, long Bytes), long> _sizes = [];

    internal static GcArrays Read(MemoryGraph graph, string[] rawTypeNames, int[] bfsParents,
        string?[] bfsCategories, Func<int, GcPath> pathFor)
    {
        var report = new GcArrays();
        var typeNames = rawTypeNames.Select(GcReport.SafeType).ToArray();
        var poolTypes = typeNames.Select(x => x.Contains("ArrayPool", StringComparison.Ordinal)).ToArray();
        var count = checked((int)graph.NodeIndexLimit);
        var node = graph.AllocNodeStorage();
        var targets = new Dictionary<int, Target>();
        var buckets = new Dictionary<(string Type, long Lower), GcTally>();
        var pools = new Dictionary<string, long>(StringComparer.Ordinal);
        var sources = new Dictionary<string, List<int>> { ["stack"] = [], ["thread_static"] = [] };
        for (var i = 0; i < count; i++)
        {
            graph.GetNode((NodeIndex)i, node);
            if (!node.Defined) continue;
            var raw = rawTypeNames[(int)node.TypeIndex];
            if (graph.GetAddress((NodeIndex)i) == 0)
            {
                var category = GcReport.RootCategory(raw);
                if (category != null && sources.TryGetValue(category, out var roots)) roots.Add(i);
                continue;
            }
            var type = typeNames[(int)node.TypeIndex];
            if (poolTypes[(int)node.TypeIndex]) pools[type] = pools.GetValueOrDefault(type) + 1;
            if (type is not ("System.Byte[]" or "System.Char[]")) continue;
            targets.Add(i, new(type, node.Size));
            report._sizes[(type, node.Size)] = report._sizes.GetValueOrDefault((type, node.Size)) + 1;
            long lower = node.Size == 0 ? 0 : 1;
            while (lower > 0 && lower * 2 <= node.Size) lower *= 2;
            if (!buckets.TryGetValue((type, lower), out var tally)) buckets[(type, lower)] = tally = new();
            tally.Count++;
            tally.Bytes += node.Size;
        }
        foreach (var pair in buckets.OrderBy(x => x.Key.Type, StringComparer.Ordinal).ThenBy(x => x.Key.Lower))
            report.Histogram.Add(new(pair.Key.Type, pair.Key.Lower, pair.Key.Lower == 0 ? 1 : pair.Key.Lower * 2, pair.Value.Count, pair.Value.Bytes));

        // Retain reverse edges only for the two target array types; duplicate edges from one parent collapse here.
        for (var i = 0; i < count; i++)
        {
            graph.GetNode((NodeIndex)i, node);
            if (!node.Defined) continue;
            for (var child = node.GetFirstChildIndex(); child != NodeIndex.Invalid; child = node.GetNextChildIndex())
                if (targets.TryGetValue((int)child, out var target)) target.Parents.Add(i);
        }
        var parents = new Dictionary<(string ArrayType, string ParentType), GcTally>();
        var poolArrays = new Dictionary<(string ArrayType, string ParentType), GcTally>();
        foreach (var target in targets.Values)
        {
            var types = new HashSet<string>(StringComparer.Ordinal);
            foreach (var parent in target.Parents)
            {
                graph.GetNode((NodeIndex)parent, node);
                types.Add(graph.GetAddress((NodeIndex)parent) == 0 ? "synthetic_root" : typeNames[(int)node.TypeIndex]);
            }
            foreach (var type in types)
            {
                Add(parents, (target.Type, type), target.Bytes);
                if (pools.ContainsKey(type)) Add(poolArrays, (target.Type, type), target.Bytes);
            }
        }
        foreach (var pair in parents.OrderBy(x => x.Key.ArrayType, StringComparer.Ordinal).ThenBy(x => x.Key.ParentType, StringComparer.Ordinal))
            report.ParentTypes.Add(new(pair.Key.ArrayType, pair.Key.ParentType, pair.Value.Count, pair.Value.Bytes));
        foreach (var pair in pools.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            poolArrays.TryGetValue(("System.Byte[]", pair.Key), out var bytes);
            poolArrays.TryGetValue(("System.Char[]", pair.Key), out var chars);
            report.PoolTypes.Add(new(pair.Key, pair.Value, bytes?.Count ?? 0, bytes?.Bytes ?? 0, chars?.Count ?? 0, chars?.Bytes ?? 0));
        }
        Largest("System.Byte[]", 50, report.LargestByteArrays);
        Largest("System.Char[]", 20, report.LargestCharArrays);
        foreach (var source in sources)
        {
            var visited = new byte[count];
            var visitNode = graph.AllocNodeStorage();
            var queue = new Queue<(int Index, bool Pool)>();
            foreach (var index in source.Value) Visit(index, false);
            while (queue.TryDequeue(out var state))
            {
                graph.GetNode((NodeIndex)state.Index, node);
                if (!node.Defined) continue;
                for (var child = node.GetFirstChildIndex(); child != NodeIndex.Invalid; child = node.GetNextChildIndex())
                {
                    var next = (int)child;
                    if (next >= 0 && next < count) Visit(next, state.Pool);
                }
            }
            foreach (var type in new[] { "System.Byte[]", "System.Char[]" })
            {
                long n = 0, size = 0, via = 0, viaSize = 0;
                foreach (var pair in targets)
                {
                    if (pair.Value.Type != type || visited[pair.Key] == 0) continue;
                    n++; size += pair.Value.Bytes;
                    if ((visited[pair.Key] & 2) != 0) { via++; viaSize += pair.Value.Bytes; }
                }
                report.StackPaths.Add(new(source.Key, type, n, size, via, viaSize));
            }
            void Visit(int index, bool pool)
            {
                // A second storage avoids disturbing the caller's current child iterator.
                var nextNode = graph.GetNode((NodeIndex)index, visitNode);
                if (!nextNode.Defined) return;
                pool |= graph.GetAddress((NodeIndex)index) != 0 && poolTypes[(int)nextNode.TypeIndex];
                var bit = (byte)(pool ? 2 : 1);
                if ((visited[index] & bit) != 0) return;
                visited[index] |= bit;
                queue.Enqueue((index, pool));
            }
        }
        return report;

        void Largest(string type, int limit, List<GcArrayLargest> output)
        {
            foreach (var pair in targets.Where(x => x.Value.Type == type).OrderByDescending(x => x.Value.Bytes).ThenBy(x => x.Key).Take(limit))
            {
                var target = pair.Value;
                var refs = new Dictionary<(string Type, string? Category, string? DeclaringType, string? Field), long>();
                foreach (var parent in target.Parents)
                {
                    graph.GetNode((NodeIndex)parent, node);
                    var raw = rawTypeNames[(int)node.TypeIndex];
                    var real = graph.GetAddress((NodeIndex)parent) != 0;
                    string? declaring = null, field = null;
                    if (!real && TryStaticField(raw, out var d, out var f)) { declaring = d; field = f; }
                    var key = (real ? typeNames[(int)node.TypeIndex] : "synthetic_root", real ? null : GcReport.RootCategory(raw) ?? bfsCategories[parent], declaring, field);
                    refs[key] = refs.GetValueOrDefault(key) + 1;
                }
                var referrers = refs.OrderBy(x => x.Key.Type, StringComparer.Ordinal).ThenBy(x => x.Key.Category, StringComparer.Ordinal)
                    .ThenBy(x => x.Key.DeclaringType, StringComparer.Ordinal).ThenBy(x => x.Key.Field, StringComparer.Ordinal)
                    .Select(x => new GcArrayReferrer(x.Key.Type, x.Value, x.Key.Category, x.Key.DeclaringType, x.Key.Field)).ToArray();
                output.Add(new(type, target.Bytes, bfsParents[pair.Key] == -2 ? "unreachable" : bfsCategories[pair.Key] ?? "other",
                    pathFor(pair.Key), referrers, null, report._sizes[(type, target.Bytes)]));
            }
        }
    }

    internal void MatchSizes(GcArrays first)
    {
        Match(LargestByteArrays); Match(LargestCharArrays);
        void Match(List<GcArrayLargest> arrays)
        {
            for (var i = 0; i < arrays.Count; i++) arrays[i] = arrays[i] with { BeforeSameSizeCount = first._sizes.GetValueOrDefault((arrays[i].Type, arrays[i].Bytes)) };
        }
    }

    internal static object Difference(GcArrays first, GcArrays last)
    {
        var firstBuckets = first.Histogram.ToDictionary(x => (x.Type, x.LowerBytes));
        var lastBuckets = last.Histogram.ToDictionary(x => (x.Type, x.LowerBytes));
        var firstParents = first.ParentTypes.ToDictionary(x => (x.ArrayType, x.ParentType));
        var lastParents = last.ParentTypes.ToDictionary(x => (x.ArrayType, x.ParentType));
        var firstPools = first.PoolTypes.ToDictionary(x => x.PoolType);
        var lastPools = last.PoolTypes.ToDictionary(x => x.PoolType);
        var firstStacks = first.StackPaths.ToDictionary(x => (x.RootCategory, x.ArrayType));
        var lastStacks = last.StackPaths.ToDictionary(x => (x.RootCategory, x.ArrayType));
        var histogram = first.Histogram.Select(x => (x.Type, x.LowerBytes, x.UpperExclusiveBytes))
            .Union(last.Histogram.Select(x => (x.Type, x.LowerBytes, x.UpperExclusiveBytes)))
            .OrderBy(x => x.Type, StringComparer.Ordinal).ThenBy(x => x.LowerBytes).Select(key =>
            {
                var a = firstBuckets.GetValueOrDefault((key.Type, key.LowerBytes));
                var b = lastBuckets.GetValueOrDefault((key.Type, key.LowerBytes));
                return new { key.Type, key.LowerBytes, key.UpperExclusiveBytes,
                    before_count = a?.Count ?? 0, after_count = b?.Count ?? 0, count_delta = (b?.Count ?? 0) - (a?.Count ?? 0),
                    before_bytes = a?.Bytes ?? 0, after_bytes = b?.Bytes ?? 0, bytes_delta = (b?.Bytes ?? 0) - (a?.Bytes ?? 0) };
            }).ToArray();
        var parents = first.ParentTypes.Select(x => (x.ArrayType, x.ParentType)).Union(last.ParentTypes.Select(x => (x.ArrayType, x.ParentType)))
            .OrderBy(x => x.ArrayType, StringComparer.Ordinal).ThenBy(x => x.ParentType, StringComparer.Ordinal).Select(key =>
            {
                var a = firstParents.GetValueOrDefault(key);
                var b = lastParents.GetValueOrDefault(key);
                return new { key.ArrayType, key.ParentType,
                    before_count = a?.Count ?? 0, after_count = b?.Count ?? 0, count_delta = (b?.Count ?? 0) - (a?.Count ?? 0),
                    before_bytes = a?.Bytes ?? 0, after_bytes = b?.Bytes ?? 0, bytes_delta = (b?.Bytes ?? 0) - (a?.Bytes ?? 0) };
            }).ToArray();
        var pools = first.PoolTypes.Select(x => x.PoolType).Union(last.PoolTypes.Select(x => x.PoolType))
            .OrderBy(x => x, StringComparer.Ordinal).Select(type =>
            {
                var a = firstPools.GetValueOrDefault(type);
                var b = lastPools.GetValueOrDefault(type);
                return new { PoolType = type,
                    before_object_count = a?.ObjectCount ?? 0, after_object_count = b?.ObjectCount ?? 0,
                    object_count_delta = (b?.ObjectCount ?? 0) - (a?.ObjectCount ?? 0),
                    before_byte_array_count = a?.ByteArrayCount ?? 0, after_byte_array_count = b?.ByteArrayCount ?? 0,
                    byte_array_count_delta = (b?.ByteArrayCount ?? 0) - (a?.ByteArrayCount ?? 0),
                    before_byte_array_bytes = a?.ByteArrayBytes ?? 0, after_byte_array_bytes = b?.ByteArrayBytes ?? 0,
                    byte_array_bytes_delta = (b?.ByteArrayBytes ?? 0) - (a?.ByteArrayBytes ?? 0),
                    before_char_array_count = a?.CharArrayCount ?? 0, after_char_array_count = b?.CharArrayCount ?? 0,
                    char_array_count_delta = (b?.CharArrayCount ?? 0) - (a?.CharArrayCount ?? 0),
                    before_char_array_bytes = a?.CharArrayBytes ?? 0, after_char_array_bytes = b?.CharArrayBytes ?? 0,
                    char_array_bytes_delta = (b?.CharArrayBytes ?? 0) - (a?.CharArrayBytes ?? 0) };
            }).ToArray();
        var stacks = first.StackPaths.Select(x => (x.RootCategory, x.ArrayType)).Union(last.StackPaths.Select(x => (x.RootCategory, x.ArrayType)))
            .OrderBy(x => x.RootCategory, StringComparer.Ordinal).ThenBy(x => x.ArrayType, StringComparer.Ordinal).Select(key =>
            {
                var a = firstStacks.GetValueOrDefault(key);
                var b = lastStacks.GetValueOrDefault(key);
                return new { key.RootCategory, key.ArrayType,
                    before_count = a?.Count ?? 0, after_count = b?.Count ?? 0, count_delta = (b?.Count ?? 0) - (a?.Count ?? 0),
                    before_bytes = a?.Bytes ?? 0, after_bytes = b?.Bytes ?? 0, bytes_delta = (b?.Bytes ?? 0) - (a?.Bytes ?? 0),
                    before_via_pool_count = a?.ViaPoolCount ?? 0, after_via_pool_count = b?.ViaPoolCount ?? 0,
                    via_pool_count_delta = (b?.ViaPoolCount ?? 0) - (a?.ViaPoolCount ?? 0),
                    before_via_pool_bytes = a?.ViaPoolBytes ?? 0, after_via_pool_bytes = b?.ViaPoolBytes ?? 0,
                    via_pool_bytes_delta = (b?.ViaPoolBytes ?? 0) - (a?.ViaPoolBytes ?? 0) };
            }).ToArray();
        return new { Histogram = histogram, ParentTypes = parents, PoolTypes = pools, StackPaths = stacks };
    }

    internal static bool TryStaticField(string value, out string declaringType, out string field)
    {
        declaringType = field = "";
        if (!value.StartsWith("[static var ", StringComparison.Ordinal) || !value.EndsWith(']')) return false;
        var body = value[12..^1];
        var separator = body.LastIndexOf('.');
        if (separator <= 0) return false;
        var type = body[..separator];
        var name = body[(separator + 1)..];
        if (type.Length > 4096 || GcReport.SafeType(type) != type ||
            !Regex.IsMatch(name, @"\A[\p{L}_][\p{L}\p{N}_]*\z", RegexOptions.CultureInvariant)) return false;
        var cursor = 0;
        if (!Type(0) || cursor != type.Length) return false;
        declaringType = type; field = name;
        return true;

        // Fixed tool type spelling only: no assembly qualification or free-form source labels.
        bool Type(int depth)
        {
            if (depth > 64) return false;
            while (true)
            {
                if (cursor >= type.Length || !(char.IsLetter(type[cursor]) || type[cursor] == '_')) return false;
                cursor++;
                while (cursor < type.Length && (char.IsLetterOrDigit(type[cursor]) || type[cursor] == '_')) cursor++;
                if (cursor < type.Length && type[cursor] == '`')
                {
                    cursor++;
                    var start = cursor;
                    while (cursor < type.Length && char.IsAsciiDigit(type[cursor])) cursor++;
                    if (cursor == start) return false;
                }
                if (cursor < type.Length && type[cursor] == '<')
                {
                    cursor++;
                    if (!Type(depth + 1)) return false;
                    while (cursor < type.Length && type[cursor] == ',')
                    {
                        cursor++;
                        while (cursor < type.Length && type[cursor] == ' ') cursor++;
                        if (!Type(depth + 1)) return false;
                    }
                    if (cursor >= type.Length || type[cursor++] != '>') return false;
                }
                if (cursor >= type.Length || type[cursor] is not ('.' or '+')) break;
                cursor++;
            }
            while (cursor < type.Length && type[cursor] == '[')
            {
                cursor++;
                while (cursor < type.Length && type[cursor] == ',') cursor++;
                if (cursor >= type.Length || type[cursor++] != ']') return false;
            }
            return true;
        }
    }

    private static void Add(Dictionary<(string ArrayType, string ParentType), GcTally> target, (string ArrayType, string ParentType) key, long bytes)
    {
        if (!target.TryGetValue(key, out var tally)) target[key] = tally = new();
        tally.Count++; tally.Bytes += bytes;
    }

    private sealed class Target(string type, long bytes)
    {
        public string Type { get; } = type;
        public long Bytes { get; } = bytes;
        public HashSet<int> Parents { get; } = [];
    }
}

internal sealed record GcArrayBucket(string Type, long LowerBytes, long UpperExclusiveBytes, long Count, long Bytes);
internal sealed record GcArrayLargest(string Type, long Bytes, string FirstRootCategory, GcPath FirstRootPath,
    GcArrayReferrer[] DirectReferrers, long? BeforeSameSizeCount, long SameSizeCount);
internal sealed record GcArrayReferrer(string ParentType, long Count, string? RootCategory, string? DeclaringType, string? Field);
internal sealed record GcArrayParent(string ArrayType, string ParentType, long Count, long Bytes);
internal sealed record GcArrayPool(string PoolType, long ObjectCount, long ByteArrayCount, long ByteArrayBytes, long CharArrayCount, long CharArrayBytes);
internal sealed record GcArrayStack(string RootCategory, string ArrayType, long Count, long Bytes, long ViaPoolCount, long ViaPoolBytes);
