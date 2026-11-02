using System.Diagnostics;
using Godot;
using Train.App.Assets;
using Train.Scenes.Contracts;

namespace Train.Rendering;

internal sealed partial class SceneResources
{
    internal static readonly TimeSpan?[] GeometryPreparationCosts = new TimeSpan?[4];
    internal static TimeSpan? CachedGeometryPreparationCost;

    internal PreparedVisual? TryPrepare(SceneVisual.Geometry source, SceneMeshPacking? packing,
        SceneDisplay.PreparationSlice slice, ref GeometryPreparation? pending)
    {
        _cache.RequireMainThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
        var preparation = pending ??= new(this, source);
        try
        {
            var result = preparation.Advance(packing, slice);
            if (result is not null) { preparation.Dispose(); pending = null; }
            return result;
        }
        catch { preparation.Dispose(); pending = null; throw; }
    }

    internal sealed class GeometryPreparation(SceneResources owner, SceneVisual.Geometry source) : IDisposable
    {
        private int _stage;
        private PreparedVisual? _visual;
        private AssetLease<ArrayMesh>? _mesh;
        private ShaderMaterial? _material;
        private PackedSceneMesh? _data;

        internal PreparedVisual? Advance(SceneMeshPacking? packing, SceneDisplay.PreparationSlice slice)
        {
            while (_stage < GeometryPreparationCosts.Length)
            {
                var cached = _stage == 1 && owner.HasGeometry(source.Mesh);
                if (_stage <= 1 && !owner.HasGeometry(source.Mesh) && _data is null)
                {
                    if (packing is not null)
                    {
                        if (!packing.TryTake(source.Mesh, out _data)) return null;
                    }
                    else _data = PackedSceneMesh.Create(source.Mesh, CancellationToken.None);
                }
                var estimate = cached ? CachedGeometryPreparationCost : GeometryPreparationCosts[_stage];
                if (!slice.Start(estimate ?? default,
                    requireFresh: !cached && estimate is null)) return null;
                var started = slice.Timestamp();
                switch (_stage)
                {
                    case 0: _visual = owner.CreateVisual(source); break;
                    case 1:
                        _mesh = _visual!.Own(owner.AcquireGeometry(source.Mesh, _data));
                        _data = null;
                        break;
                    case 2: _material = owner.GeometryMaterial(_visual!, source); break;
                    case 3:
                        _visual!.Node.AddChild(new MeshInstance3D { Mesh = _mesh!.Value, MaterialOverride = _material });
                        break;
                }
                var elapsed = Stopwatch.GetElapsedTime(started, slice.Timestamp());
                if (cached) CachedGeometryPreparationCost = elapsed;
                else GeometryPreparationCosts[_stage] = elapsed;
                _stage++;
            }
            var result = _visual;
            _visual = null;
            return result;
        }

        public void Dispose()
        {
            _visual?.Dispose();
            _visual = null;
            _mesh = null;
            _material = null;
            _data = null;
        }
    }
}
