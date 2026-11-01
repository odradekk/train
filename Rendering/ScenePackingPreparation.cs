using System.Diagnostics;
using Godot;
using Train.App.Assets;
using Train.Scenes.Contracts;

namespace Train.Rendering;

internal sealed class ScenePackingPreparation(SceneResources resources, ScenePackingPreparation.Costs costs) : IDisposable
{
    internal sealed class Costs
    {
        internal TimeSpan Collect, Start, Dispose;
    }

    private readonly HashSet<SceneMesh> _seen = new(ReferenceEqualityComparer.Instance);
    private readonly List<SceneMesh> _meshes = [];
    private readonly List<AssetLease<ArrayMesh>> _cached = [];
    internal SceneMeshPacking? Job { get; private set; }
    internal bool Started { get; private set; }

    internal bool Collect(SceneVisual visual, SceneDisplay.PreparationSlice slice)
    {
        if (!slice.Start(estimate: costs.Collect)) return false;
        var started = slice.Unlimited ? 0 : slice.Timestamp();
        if (visual is SceneVisual.Geometry geometry && _seen.Add(geometry.Mesh))
        {
            // 对侧准备可随时取消；本次准备须持有未列入打包作业的缓存网格，直到自己的视觉全部接收完。
            if (resources.AcquireCachedGeometry(geometry.Mesh) is { } cached) _cached.Add(cached);
            else _meshes.Add(geometry.Mesh);
        }
        if (!slice.Unlimited) costs.Collect = Stopwatch.GetElapsedTime(started, slice.Timestamp());
        return true;
    }

    internal bool Start(Task? previous, Func<SceneMesh, CancellationToken, PackedSceneMesh> pack,
        SceneDisplay.PreparationSlice slice, out Task? tail)
    {
        tail = previous;
        if (!slice.Start(estimate: costs.Start)) return false;
        var started = slice.Unlimited ? 0 : slice.Timestamp();
        if (_meshes.Count > 0) Job = new([.. _meshes], previous, SceneDisplayDiagnostics.WrapPacking(pack));
        Started = true;
        tail = Job?.Completion ?? previous;
        if (!slice.Unlimited) costs.Start = Stopwatch.GetElapsedTime(started, slice.Timestamp());
        return true;
    }

    internal bool Release(SceneDisplay.PreparationSlice slice)
    {
        if (Job is not null)
        {
            if (!slice.Start(estimate: costs.Dispose)) return false;
            var started = slice.Unlimited ? 0 : slice.Timestamp();
            Job.Dispose();
            if (!slice.Unlimited) costs.Dispose = Stopwatch.GetElapsedTime(started, slice.Timestamp());
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
