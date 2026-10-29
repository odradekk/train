using Godot;
using Train.App.Assets;
using Train.Scenes.Contracts;

namespace Train.Rendering;

internal sealed class ScenePackingPreparation(SceneResources resources) : IDisposable
{
    private readonly HashSet<SceneMesh> _seen = new(ReferenceEqualityComparer.Instance);
    private readonly List<SceneMesh> _meshes = [];
    private readonly List<AssetLease<ArrayMesh>> _cached = [];
    internal SceneMeshPacking? Job { get; private set; }
    internal bool Started { get; private set; }

    internal void Collect(SceneVisual visual)
    {
        if (visual is not SceneVisual.Geometry geometry || !_seen.Add(geometry.Mesh)) return;
        // 对侧准备可随时取消；本次准备须持有未列入打包作业的缓存网格，直到自己的视觉全部接收完。
        if (resources.AcquireCachedGeometry(geometry.Mesh) is { } cached) _cached.Add(cached);
        else _meshes.Add(geometry.Mesh);
    }

    internal Task? Start(Task? previous, Func<SceneMesh, CancellationToken, PackedSceneMesh> pack)
    {
        if (_meshes.Count > 0) Job = new([.. _meshes], previous, pack);
        Started = true;
        return Job?.Completion ?? previous;
    }

    internal bool Release(SceneDisplay.PreparationSlice slice)
    {
        if (Job is not null)
        {
            if (!slice.Start()) return false;
            Job.Dispose();
            Job = null;
        }
        while (_cached.Count > 0)
        {
            if (!slice.Start()) return false;
            ReleaseLastLease();
        }
        return true;
    }

    private void ReleaseLastLease()
    {
        var last = _cached.Count - 1;
        _cached[last].Dispose();
        _cached.RemoveAt(last);
    }

    public void Dispose()
    {
        Job?.Dispose();
        Job = null;
        while (_cached.Count > 0) ReleaseLastLease();
    }
}
