using Microsoft.Diagnostics.Runtime;

namespace Train.HeapDiagnosis;

internal sealed class HeapReport
{
    public string Attribution => "Each reachable object is counted once, using its first enumerated strong-root path; groups are not exclusive retained sizes. Unrooted means no enumerated strong-root path. Free is reported separately.";
    public Dictionary<string, Tally> Types { get; } = [];
    public Dictionary<string, Tally> States { get; } = [];
    public Dictionary<string, Tally> FirstPathGroups { get; } = [];
    public Dictionary<string, Tally> LargeArrayGroups { get; } = [];
    public Dictionary<string, Tally> ContentAndFactories { get; } = [];
    public List<ObjectPath> ContentViews { get; } = [];
    public List<ContentViewGroup> ContentViewFirstPathGroups { get; } = [];
    public List<ObjectPath> LargeArrays { get; } = [];
    public List<RecordingState> Recordings { get; } = [];

    internal HeapReport(string dump, string dac, bool preflight)
    {
        using var target = DataTarget.LoadDump(dump);
        using var runtime = target.ClrVersions.Single().CreateRuntime(dac);
        var heap = runtime.Heap;
        if (!heap.CanWalkHeap || heap.VerifyHeap().Any()) throw new InvalidDataException();
        var objects = heap.EnumerateObjects().ToDictionary(o => o.Address);
        if (objects.Count == 0 || objects.Values.Any(o => !o.IsValid || o.Type is null)) throw new InvalidDataException();
        var parents = new Dictionary<ulong, (ulong Parent, string Field)>();
        var roots = new Dictionary<ulong, string>();
        var queue = new Queue<ClrObject>();
        foreach (var root in heap.EnumerateRoots())
        {
            var obj = root.Object;
            if (obj.IsNull) continue;
            if (!objects.ContainsKey(obj.Address) || obj.IsFree) throw new InvalidDataException();
            if (parents.TryAdd(obj.Address, (0, "<root>")))
            {
                roots.Add(obj.Address, root.RootKind.ToString());
                queue.Enqueue(obj);
            }
        }
        while (queue.TryDequeue(out var obj))
        {
            foreach (var reference in obj.EnumerateReferencesWithFields(carefully: false, considerDependantHandles: true))
            {
                var child = reference.Object;
                if (child.IsNull) continue;
                if (!objects.ContainsKey(child.Address) || child.IsFree) throw new InvalidDataException();
                if (parents.TryAdd(child.Address, (obj.Address, reference.Field?.Name ??
                    (reference.IsDependentHandle ? "<dependent-handle>" : "<array-element>")))) queue.Enqueue(child);
            }
        }
        List<PathStep> Path(ClrObject obj)
        {
            var path = new List<PathStep>();
            var cursor = obj.Address;
            while (parents.TryGetValue(cursor, out var parent))
            {
                path.Add(new(Name(objects[cursor]), parent.Field));
                if (parent.Parent == 0) break;
                cursor = parent.Parent;
            }
            path.Reverse();
            return path;
        }
        string Group(ClrObject obj)
        {
            if (!parents.ContainsKey(obj.Address)) return "unrooted";
            var cursor = obj.Address;
            var pool = false;
            var tls = false;
            var content = false;
            while (true)
            {
                var type = Name(objects[cursor]);
                pool |= type.Contains("ArrayPool", StringComparison.Ordinal);
                tls |= parents[cursor].Field.StartsWith("t_", StringComparison.Ordinal);
                content |= type.Contains("Content", StringComparison.Ordinal);
                if (parents[cursor].Parent == 0) break;
                cursor = parents[cursor].Parent;
            }
            var kind = roots[cursor];
            if (pool) return "pool";
            if (kind.Contains("ThreadStatic", StringComparison.Ordinal) || tls) return "TLS";
            if (content) return "content";
            return kind;
        }
        var prefix = preflight ? "Train.HeapDiagnosis." : "Train.App.";
        var services = objects.Values.Where(o => parents.ContainsKey(o.Address) &&
            Name(o) == prefix + (preflight ? "RecordingService" : "Recording.RecordingService")).ToArray();
        if (services.Length == 0) throw new InvalidDataException();
        var currentContent = new HashSet<ulong>();
        foreach (var service in services)
        {
            var host = RequiredObject(service, "_host");
            var active = RequiredObject(host, "_active");
            if (!preflight) currentContent.Add(RequiredObject(host, "_content").Address);
            var pending = service.ReadObjectField("_pending");
            if (pending.IsNull) { Recordings.Add(new(true, null, null)); continue; }
            var snapshot = RequiredObject(pending, "<Snapshot>k__BackingField");
            var binding = Binding(snapshot);
            var current = Binding(active);
            var work = RequiredObject(pending, "<Work>k__BackingField");
            // Task.IsCompleted checks these final-state bits in System.Private.CoreLib.
            var completed = (work.ReadField<int>("m_stateFlags") & 0x01600000) != 0;
            Recordings.Add(new(false, binding == current, completed));
        }
        var viewType = preflight ? "Train.HeapDiagnosis.ContentView" : "Train.Content.Loading.ContentView";
        var viewTotals = objects.Values.Where(o => Name(o) == viewType).ToDictionary(o => o.Address, _ => new Tally());
        foreach (var obj in objects.Values)
        {
            var name = Name(obj);
            var state = obj.IsFree ? "Free" : parents.ContainsKey(obj.Address) ? "strong-root-reachable" : "unrooted";
            Add(Types, name, obj.Size);
            Add(States, state, obj.Size);
            var group = obj.IsFree ? "Free" : Group(obj);
            Add(FirstPathGroups, group, obj.Size);
            if (name.Contains("ContentView", StringComparison.Ordinal) || name.Contains("Factory", StringComparison.Ordinal))
                Add(ContentAndFactories, state + ":" + name, obj.Size);
            if (name == viewType)
                ContentViews.Add(new(name, obj.Size, state, currentContent.Contains(obj.Address) ? "current" : "non-current", Path(obj)));
            var cursor = obj.Address;
            while (parents.TryGetValue(cursor, out var parent))
            {
                if (viewTotals.TryGetValue(cursor, out var total))
                {
                    total.Count++;
                    total.Bytes = checked(total.Bytes + (long)obj.Size);
                    break;
                }
                cursor = parent.Parent;
            }
            if ((name == "System.Byte[]" || name == "System.Char[]") && obj.Size >= 1_048_576)
            {
                LargeArrays.Add(new(name, obj.Size, state, group, Path(obj)));
                Add(LargeArrayGroups, group + ":" + name, obj.Size);
            }
        }
        foreach (var (address, total) in viewTotals)
            ContentViewFirstPathGroups.Add(new(viewType, currentContent.Contains(address) ? "current" : "non-current",
                total.Count, total.Bytes, Path(objects[address])));
        if (preflight && (Recordings.Count != 1 || Recordings[0] != new RecordingState(false, true, true) ||
            ContentViews.Count != 1 || ContentViews[0].Path.Count == 0 || LargeArrays.Count != 2 || LargeArrays.Any(a => a.Path.Count == 0)))
            throw new InvalidDataException();
    }

    private static Guid Binding(ClrObject owner)
    {
        var binding = owner.ReadValueTypeField("<Binding>k__BackingField");
        if (binding.Type?.Name is not ("Train.App.HostBinding" or "Train.HeapDiagnosis.HostBinding")) throw new InvalidDataException();
        var session = binding.ReadValueTypeField("<Session>k__BackingField");
        if (session.Type?.Name != "System.Guid") throw new InvalidDataException();
        return binding.ReadField<Guid>("<Session>k__BackingField");
    }

    private static ClrObject RequiredObject(ClrObject owner, string field)
    {
        var value = owner.ReadObjectField(field);
        if (!value.IsValid || value.IsNull) throw new InvalidDataException();
        return value;
    }

    private static string Name(ClrObject obj) => obj.Type?.Name ?? throw new InvalidDataException();
    private static void Add(Dictionary<string, Tally> data, string key, ulong size)
    {
        if (!data.TryGetValue(key, out var tally)) data.Add(key, tally = new());
        tally.Count++;
        tally.Bytes = checked(tally.Bytes + (long)size);
    }
}

internal sealed class Tally
{
    public long Count { get; set; }
    public long Bytes { get; set; }
}
internal sealed record PathStep(string Type, string Field);
internal sealed record ObjectPath(string Type, ulong Bytes, string State, string Category, List<PathStep> Path);
internal sealed record RecordingState(bool PendingIsNull, bool? SnapshotBindingEqualsCurrent, bool? WorkIsCompleted);
internal sealed record ContentViewGroup(string Type, string Category, long Count, long Bytes, List<PathStep> Path);
