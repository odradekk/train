namespace Train.Scenes;

internal sealed class FarTerrainPermission
{
    private readonly object gate = new();
    private readonly List<FarTerrainWorker> workers = [];
    private bool allowed;
    internal bool Allowed => Volatile.Read(ref allowed);

    internal void Register(FarTerrainWorker worker) { lock (gate) workers.Add(worker); }
    internal void Remove(FarTerrainWorker worker) { lock (gate) workers.Remove(worker); }
    internal void SetAllowed(bool value)
    {
        Volatile.Write(ref allowed, value);
        if (!value) return;
        FarTerrainWorker[] current;
        lock (gate) current = workers.ToArray();
        // Never take a worker lock while holding the permission lock: disposal removes its registration.
        foreach (var worker in current) worker.Resume();
    }
}
