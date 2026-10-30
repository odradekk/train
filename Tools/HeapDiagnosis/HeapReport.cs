using Microsoft.Diagnostics.Runtime;

namespace Train.HeapDiagnosis;

internal sealed class HeapReport
{
    public string Attribution => "Readable objects are counted once using the first enumerated strong-root path. Groups are not exclusive retained sizes. Missing roots or references can make reachability incomplete; inspect Issues. Paths retain at most the final 128 steps; PathTruncated marks an omitted prefix.";
    public Dictionary<string, Tally> Types { get; } = [];
    public Dictionary<string, Tally> States { get; } = [];
    public Dictionary<string, Tally> FirstPathGroups { get; } = [];
    public Dictionary<string, Tally> LargeArrayGroups { get; } = [];
    public Dictionary<string, Tally> ContentAndFactories { get; } = [];
    public Dictionary<string, long> Issues { get; } = [];
    public List<ObjectPath> ContentViews { get; } = [];
    public List<ContentViewGroup> ContentViewFirstPathGroups { get; } = [];
    public List<ObjectPath> LargeArrays { get; } = [];
    public List<RecordingState> Recordings { get; } = [];

    internal HeapReport(string dump, string dac, bool preflight)
    {
        using var target = DataTarget.LoadDump(dump);
        using var runtime = target.ClrVersions.Single().CreateRuntime(dac);
        var heap = runtime.Heap;
        var viewType = preflight ? "Train.HeapDiagnosis.ContentView" : "Train.Content.Loading.ContentView";
        if (!heap.CanWalkHeap) Issue("HeapWalk", typeof(InvalidDataException).FullName!);
        Scan(() => heap.VerifyHeap(), "VerifyHeap", corruption => Issue("VerifyHeap:" + corruption.Kind, corruption.GetType().FullName!));
        var objects = new Dictionary<ulong, ObjectData>();
        Scan(() => heap.EnumerateObjects(carefully: true), "Objects", obj =>
        {
            if (!obj.IsValid || obj.Type?.Name is not string name)
            {
                Issue("InvalidObject", obj.Type?.Name ?? "<unknown-type>");
                return;
            }
            if (!objects.TryAdd(obj.Address, new(obj, name, obj.Size, obj.IsFree))) Issue("DuplicateObject", name);
        });
        if (objects.Count == 0) Issue("MissingObjects", "<unknown-type>");
        var parents = new Dictionary<ulong, Mark>();
        var queue = new Queue<ulong>();
        void Discover(ulong address, ulong parent, string field, string rootKind, byte flags, ulong view)
        {
            if (!objects.TryGetValue(address, out var child) || child.Free)
            {
                Issue("MissingReference", child.Name ?? "<unknown-type>");
                return;
            }
            if (parents.ContainsKey(address)) return;
            if (child.Name.Contains("ArrayPool", StringComparison.Ordinal)) flags |= 1;
            if (field.StartsWith("t_", StringComparison.Ordinal) || rootKind.Contains("ThreadStatic", StringComparison.Ordinal)) flags |= 2;
            if (child.Name.Contains("Content", StringComparison.Ordinal)) flags |= 4;
            if (child.Name == viewType) view = address;
            parents.Add(address, new(parent, field, rootKind, flags, view));
            queue.Enqueue(address);
        }
        Scan(() => heap.EnumerateRoots(), "Roots", root =>
        {
            if (!root.Object.IsNull) Discover(root.Object.Address, 0, "<root>", root.RootKind.ToString(), 0, 0);
        });
        while (queue.TryDequeue(out var address))
        {
            var parent = parents[address];
            Scan(() => objects[address].Object.EnumerateReferencesWithFields(carefully: true, considerDependantHandles: true),
                "References:" + objects[address].Name, reference =>
                {
                    if (!reference.Object.IsNull) Discover(reference.Object.Address, address, reference.Field?.Name ??
                        (reference.IsDependentHandle ? "<dependent-handle>" : "<array-element>"),
                        parent.RootKind, parent.Flags, parent.View);
                });
        }
        (List<PathStep> Steps, bool Truncated) Path(ulong address)
        {
            var path = new List<PathStep>();
            while (parents.TryGetValue(address, out var mark))
            {
                if (path.Count == 128) { path.Reverse(); return (path, true); }
                path.Add(new(objects[address].Name, mark.Field));
                address = mark.Parent;
            }
            path.Reverse();
            return (path, false);
        }
        var serviceType = preflight ? "Train.HeapDiagnosis.RecordingService" : "Train.App.Recording.RecordingService";
        var services = objects.Values.Where(o => parents.ContainsKey(o.Object.Address) && o.Name == serviceType).ToArray();
        if (services.Length == 0) Issue("MissingTarget", serviceType);
        var currentContent = new HashSet<ulong>();
        foreach (var service in services)
        {
            var hostOk = Read(() => RequiredObject(service.Object, "_host"), "HostField", out var host);
            if (hostOk && !preflight && Read(() => RequiredObject(host, "_content"), "CurrentContentField", out var content))
                currentContent.Add(content.Address);
            if (!Read(() => service.Object.ReadObjectField("_pending"), "PendingField", out var pending))
            {
                Recordings.Add(new(null, null, null));
                continue;
            }
            if (pending.IsNull) { Recordings.Add(new(true, null, null)); continue; }
            bool? same = null;
            bool? completed = null;
            if (hostOk && Read(() => Binding(RequiredObject(host, "_active")), "CurrentBindingField", out var current) &&
                Read(() => Binding(RequiredObject(pending, "<Snapshot>k__BackingField")), "SnapshotBindingField", out var binding))
                same = binding == current;
            // Task.IsCompleted uses the final-state bits in System.Private.CoreLib.
            if (Read(() => RequiredObject(pending, "<Work>k__BackingField").ReadField<int>("m_stateFlags"), "WorkField", out var flags))
                completed = (flags & 0x01600000) != 0;
            Recordings.Add(new(false, same, completed));
        }
        var viewTotals = objects.Values.Where(o => o.Name == viewType).ToDictionary(o => o.Object.Address, _ => new Tally());
        if (viewTotals.Count == 0) Issue("MissingTarget", viewType);
        foreach (var (address, obj) in objects)
        {
            var reachable = parents.TryGetValue(address, out var mark);
            var state = obj.Free ? "Free" : reachable ? "strong-root-reachable" : "unrooted";
            var group = obj.Free ? "Free" : !reachable ? "unrooted" : (mark.Flags & 1) != 0 ? "pool" :
                (mark.Flags & 2) != 0 ? "TLS" : (mark.Flags & 4) != 0 ? "content" : mark.RootKind;
            Add(Types, obj.Name, obj.Size);
            Add(States, state, obj.Size);
            Add(FirstPathGroups, group, obj.Size);
            if (obj.Name.Contains("ContentView", StringComparison.Ordinal) || obj.Name.Contains("Factory", StringComparison.Ordinal))
                Add(ContentAndFactories, state + ":" + obj.Name, obj.Size);
            if (reachable && mark.View != 0)
            {
                var total = viewTotals[mark.View];
                total.Count++;
                total.Bytes = checked(total.Bytes + (long)obj.Size);
            }
            if (obj.Name == viewType)
            {
                var path = Path(address);
                ContentViews.Add(new(obj.Name, obj.Size, state, currentContent.Contains(address) ? "current" : "non-current", path.Steps, path.Truncated));
            }
            if ((obj.Name == "System.Byte[]" || obj.Name == "System.Char[]") && obj.Size >= 1_048_576)
            {
                var path = Path(address);
                LargeArrays.Add(new(obj.Name, obj.Size, state, group, path.Steps, path.Truncated));
                Add(LargeArrayGroups, group + ":" + obj.Name, obj.Size);
            }
        }
        foreach (var (address, total) in viewTotals)
        {
            var path = Path(address);
            ContentViewFirstPathGroups.Add(new(viewType, currentContent.Contains(address) ? "current" : "non-current",
                total.Count, total.Bytes, path.Steps, path.Truncated));
        }
        if (preflight)
        {
            var expected = new HashSet<RecordingState> { new(true, null, null), new(false, true, true), new(false, false, false) };
            if (Issues.Count != 0 || Recordings.Count != 3 || !expected.SetEquals(Recordings) ||
                ContentViews.Count != 2 || ContentViews.Any(v => v.Path.Count == 0) || LargeArrays.Count != 4 ||
                LargeArrays.Any(a => a.Path.Count == 0) ||
                !Types.TryGetValue("System.Collections.Generic.LinkedListNode<System.Int32>", out var nodes) ||
                nodes.Count != 2 * ContentView.ChainLength || ContentViewFirstPathGroups.Count != 2 ||
                ContentViewFirstPathGroups.Any(group => group.Count != ContentView.ChainLength + 4) ||
                States.Values.Sum(t => t.Count) != objects.Count || FirstPathGroups.Values.Sum(t => t.Count) != objects.Count ||
                FirstPathGroups.Values.Sum(t => t.Bytes) != Types.Values.Sum(t => t.Bytes)) throw new InvalidDataException();
        }
    }

    private void Issue(string stage, string type)
    {
        var key = stage + ":" + type;
        Issues.TryGetValue(key, out var count);
        Issues[key] = count + 1;
    }

    private bool Read<T>(Func<T> read, string stage, out T value)
    {
        try { value = read(); return true; }
        catch (Exception error) { Issue(stage, error.GetType().FullName!); value = default!; return false; }
    }

    private void Scan<T>(Func<IEnumerable<T>> source, string stage, Action<T> visit)
    {
        try
        {
            foreach (var value in source())
                try { visit(value); }
                catch (Exception error) { Issue(stage, error.GetType().FullName!); }
        }
        catch (Exception error) { Issue(stage, error.GetType().FullName!); }
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

    private static void Add(Dictionary<string, Tally> data, string key, ulong size)
    {
        if (!data.TryGetValue(key, out var tally)) data.Add(key, tally = new());
        tally.Count++;
        tally.Bytes = checked(tally.Bytes + (long)size);
    }

    private readonly record struct ObjectData(ClrObject Object, string Name, ulong Size, bool Free);
    private readonly record struct Mark(ulong Parent, string Field, string RootKind, byte Flags, ulong View);
}

internal sealed class Tally
{
    public long Count { get; set; }
    public long Bytes { get; set; }
}
internal sealed record PathStep(string Type, string Field);
internal sealed record ObjectPath(string Type, ulong Bytes, string State, string Category, List<PathStep> Path, bool PathTruncated);
internal sealed record RecordingState(bool? PendingIsNull, bool? SnapshotBindingEqualsCurrent, bool? WorkIsCompleted);
internal sealed record ContentViewGroup(string Type, string Category, long Count, long Bytes, List<PathStep> Path, bool PathTruncated);
