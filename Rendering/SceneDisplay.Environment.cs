using System.Collections.Immutable;
using System.Diagnostics;
using Godot;
using Train.Scenes.Contracts;

namespace Train.Rendering;

internal sealed partial class SceneDisplay
{
    internal sealed class FogGroup : IDisposable
    {
        private sealed record Box(BoxMesh Mesh, MeshInstance3D Node);
        internal ImmutableArray<SceneBounds> Regions { get; }
        internal Node3D Root { get; }
        internal ShaderMaterial Material => _material;
        private readonly Dictionary<(SceneBounds Bounds, int Occurrence), (Box Box, int Index)> _boxes;
        private readonly Dictionary<SceneBounds, int> _occurrences;
        private readonly bool[] _kept;
        private List<Box> _added, _removed, _all;
        private List<Box> _owned;
        private FogGroup? _source;
        private int _region, _removal;
        private TimeSpan _reuseCost, _creationCost, _removalCost;
        private bool _ownsRoot, _ownsMaterial, _activated;
        private bool _disposed;
        private readonly ShaderMaterial _material;

        internal FogGroup(ImmutableArray<SceneBounds> regions, ShaderMaterial material, FogGroup? source = null)
        {
            _material = material;
            _source = source;
            _reuseCost = source?._reuseCost ?? default;
            _creationCost = source?._creationCost ?? default;
            Root = source?.Root ?? new() { Name = "LoadingFog" };
            _ownsRoot = _ownsMaterial = source is null;
            Regions = regions;
            _boxes = new(regions.Length);
            _occurrences = new(regions.Length);
            _added = new(regions.Length);
            _all = new(regions.Length);
            _removed = new(source?._owned.Count ?? 0);
            _kept = new bool[source?._owned.Count ?? 0];
            _owned = _added;
        }

        internal bool Prepare(PreparationSlice slice)
        {
            for (; _region < Regions.Length; _region++)
            {
                var region = Regions[_region];
                var occurrence = _occurrences.GetValueOrDefault(region);
                var key = (region, occurrence);
                (Box Box, int Index) reused = default;
                var reuse = _source is { } source && source._boxes.TryGetValue(key, out reused);
                if (!slice.Start(estimate: reuse ? _reuseCost : _creationCost)) return false;
                var started = slice.Unlimited ? 0 : slice.Timestamp();
                _occurrences[region] = occurrence + 1;
                Box box;
                if (reuse)
                {
                    box = reused.Box!;
                    _kept[reused.Index] = true;
                }
                else
                {
                    var mesh = new BoxMesh { Size = ToGodot(region.Maximum - region.Minimum) };
                    box = new(mesh, new()
                    {
                        Mesh = mesh, MaterialOverride = _material,
                        CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                        Position = ToGodot((region.Minimum + region.Maximum) / 2)
                    });
                    _added.Add(box);
                }
                _boxes.Add(key, (box, _all.Count));
                _all.Add(box);
                if (!slice.Unlimited)
                {
                    var elapsed = Stopwatch.GetElapsedTime(started, slice.Timestamp());
                    if (reuse) _reuseCost = elapsed;
                    else _creationCost = elapsed;
                }
            }
            if (_source is { } previous)
                for (; _removal < _kept.Length; _removal++)
                {
                    if (!slice.Start(estimate: _removalCost)) return false;
                    var started = slice.Timestamp();
                    if (!_kept[_removal]) _removed.Add(previous._owned[_removal]);
                    _removalCost = Stopwatch.GetElapsedTime(started, slice.Timestamp());
                }
            return true;
        }

        internal void Activate()
        {
            if (_activated) return;
            // 准备只借用未变的矩形；提交才转交所有权，取消准备不能触碰 D。
            if (_source is { } source)
            {
                source._owned = _removed;
                source._ownsRoot = source._ownsMaterial = false;
                _source = null;
            }
            foreach (var box in _added) Root.AddChild(box.Node);
            _owned = _all;
            _added = []; _removed = []; _all = [];
            _ownsRoot = _ownsMaterial = _activated = true;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_ownsRoot) Root.Free();
            foreach (var box in _owned)
            {
                if (GodotObject.IsInstanceValid(box.Node)) box.Node.Free();
                box.Mesh.Dispose();
            }
            if (_ownsMaterial) _material.Dispose();
        }
    }

    internal sealed class DebugLineGroup : IDisposable
    {
        internal ImmutableArray<SceneDebugLine> Lines { get; }
        internal MeshInstance3D Root { get; } = new()
        {
            Name = "DebugLines", CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
        };
        private readonly ImmediateMesh _mesh = new();
        private readonly StandardMaterial3D _material = new()
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            VertexColorUseAsAlbedo = true,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha
        };

        internal DebugLineGroup(ImmutableArray<SceneDebugLine> lines)
        {
            Lines = lines;
            Root.Mesh = _mesh;
            _mesh.SurfaceBegin(Mesh.PrimitiveType.Lines, _material);
        }

        private int _line;
        private TimeSpan _lineCost;
        private bool _complete;
        internal bool Prepare(PreparationSlice slice)
        {
            if (_complete) return true;
            for (; _line < Lines.Length; _line++)
            {
                if (!slice.Start(estimate: _lineCost)) return false;
                var started = slice.Timestamp();
                var line = Lines[_line];
                _mesh.SurfaceSetColor(new(line.Color.X, line.Color.Y, line.Color.Z, line.Color.W));
                _mesh.SurfaceAddVertex(ToGodot(line.From));
                _mesh.SurfaceAddVertex(ToGodot(line.To));
                _lineCost = Stopwatch.GetElapsedTime(started, slice.Timestamp());
            }
            if (!slice.Start()) return false;
            _mesh.SurfaceEnd();
            _complete = true;
            return true;
        }

        public void Dispose()
        {
            Root.Free();
            _mesh.Dispose();
            _material.Dispose();
        }
    }

}
