using System.Collections.Immutable;
using System.Diagnostics;
using Godot;
using Train.Scenes.Contracts;

namespace Train.Rendering;

internal sealed partial class SceneDisplay
{
    private Retirement? _retirement;
    private ulong _retirementFrame;
    private readonly RetirementCosts _retirementCosts = new();

    private bool ChangesSpace(SceneFrameStamp frame) => DisplayedFrame is { } displayed &&
        (displayed.Update.Frame.Session != frame.Session || displayed.Update.Frame.Space != frame.Space);

    private void AdvanceRetirement(long started, TimeSpan? budget, bool singleStep)
    {
        if (_retirement is null || _retirementFrame == Engine.GetProcessFrames()) return;
        if (_retirement.Release(new(this, started, budget, false), _retirementCosts, singleStep))
            _retirement = null;
    }

    private void ReleasePrevious(ImmutableDictionary<SceneObjectId, DisplayObject> old,
        ImmutableDictionary<SceneObjectId, DisplayObject> next, HashSet<SceneObjectId> changed,
        FogGroup? oldFog, bool defer)
    {
        var retirement = defer ? new Retirement() : null;
        foreach (var id in changed)
        {
            if (!old.TryGetValue(id, out var item)) continue;
            var surviving = next.GetValueOrDefault(id);
            foreach (var visual in item.Visuals)
            {
                if (surviving is not null && surviving.Visuals.Contains(visual)) continue;
                if (retirement is null) visual.Dispose();
                else
                {
                    visual.Node.GetParent()?.RemoveChild(visual.Node);
                    retirement.Visuals.Add(visual);
                }
            }
            if (surviving is null)
            {
                if (retirement is null) item.Root.Free();
                else retirement.Roots.Add(item.Root);
            }
        }
        if (!ReferenceEquals(oldFog, _fog) && oldFog is not null)
        {
            if (retirement is null || !oldFog.HasReleaseWork) oldFog.Dispose();
            else
            {
                oldFog.DetachRetired();
                retirement.Fog = oldFog;
            }
        }
        if (retirement is not { IsEmpty: false }) return;
        // Prepare 在积压清完前不接入下一个空间，最多保留一份旧显示帧的资源。
        _retirement = retirement;
        _retirementFrame = Engine.GetProcessFrames();
    }

    private sealed class Retirement : IDisposable
    {
        internal List<PreparedVisual> Visuals { get; } = [];
        internal List<Node3D> Roots { get; } = [];
        internal FogGroup? Fog;
        internal bool IsEmpty => Visuals.Count == 0 && Roots.Count == 0 && Fog is null;

        internal bool Release(PreparationSlice slice, RetirementCosts costs, bool singleStep)
        {
            while (Visuals.Count > 0)
            {
                if (!Visuals[^1].Release(slice, costs, singleStep)) return false;
                Visuals.RemoveAt(Visuals.Count - 1);
                if (singleStep) return IsEmpty;
            }
            while (Roots.Count > 0)
            {
                if (!costs.Free(slice, Roots[^1])) return false;
                Roots.RemoveAt(Roots.Count - 1);
                if (singleStep) return IsEmpty;
            }
            if (Fog is not null)
            {
                if (!Fog.Release(slice, costs, singleStep)) return false;
                Fog = null;
            }
            return true;
        }

        public void Dispose()
        {
            foreach (var visual in Visuals) visual.Dispose();
            foreach (var root in Roots) root.Free();
            Fog?.Dispose();
            Visuals.Clear(); Roots.Clear(); Fog = null;
        }
    }

    internal sealed class RetirementCosts
    {
        private TimeSpan _node, _resource;

        internal bool Free(PreparationSlice slice, Node node)
        {
            if (!slice.Start(_node)) return false;
            var started = slice.Timestamp();
            node.Free();
            var elapsed = Stopwatch.GetElapsedTime(started, slice.Timestamp());
            if (elapsed > _node) _node = elapsed;
            return true;
        }

        internal bool Release(PreparationSlice slice, IDisposable resource)
        {
            if (!slice.Start(_resource)) return false;
            var started = slice.Timestamp();
            resource.Dispose();
            var elapsed = Stopwatch.GetElapsedTime(started, slice.Timestamp());
            if (elapsed > _resource) _resource = elapsed;
            return true;
        }
    }
}
