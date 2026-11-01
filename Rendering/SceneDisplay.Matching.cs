using System.Collections.Immutable;
using Train.Scenes.Contracts;

namespace Train.Rendering;

internal sealed partial class SceneDisplay
{
    private bool InitializePreparation(Preparation preparation, PreparationSlice slice, bool spaceChanged)
    {
        var update = preparation.Frame.Update;
        if (preparation.Objects is null)
        {
            if (!slice.Start()) return false;
            preparation.Objects = (update.Base is null ? ImmutableDictionary<SceneObjectId, DisplayObject>.Empty : _objects).ToBuilder();
            if (update.Base is not null && update.ShiftExistingRoots != System.Numerics.Vector3.Zero)
                preparation.ShiftRoots = _rootObjects.GetEnumerator();
        }
        while (preparation.ShiftRoots is { } roots)
        {
            if (!slice.Start()) return false;
            if (!roots.MoveNext()) { roots.Dispose(); preparation.ShiftRoots = null; break; }
            var item = _objects[roots.Current];
            preparation.Objects[item.Source.Id] = item with { Source = item.Source with
            { Pose = item.Source.Pose with { Position = item.Source.Pose.Position + update.ShiftExistingRoots } } };
        }
        for (; preparation.Removed < update.Removed.Length; preparation.Removed++)
        {
            if (!slice.Start()) return false;
            preparation.Objects.Remove(update.Removed[preparation.Removed]);
        }
        for (; preparation.MatchUpsert < update.Upserts.Length; preparation.MatchUpsert++)
        {
            var source = update.Upserts[preparation.MatchUpsert];
            using var interval = SceneDisplayDiagnostics.Begin("Prepare.MatchVisuals", source.Id);
            if (preparation.Matching is null)
            {
                if (!slice.Start()) return false;
                preparation.Matching = new(_objects.GetValueOrDefault(source.Id), source.Visuals);
            }
            var matching = preparation.Matching;
            while (!matching.Complete)
            {
                if (!slice.Start()) return false;
                matching.Advance();
            }
            preparation.ReusedVisuals.Add(source.Id, matching.Matched);
            preparation.Matching = null;
        }
        if (!preparation.Started)
        {
            Observe?.Invoke(new(_candidate, update.Frame, null, SceneDisplayStage.FramePreparationStarted,
                [.. update.Upserts.Where(source => preparation.ReusedVisuals[source.Id].Any(visual => visual is null))
                    .Select(source => source.Id)]));
            preparation.Started = true;
        }
        if (!spaceChanged && !preparation.Packing.Started)
        {
            for (; preparation.PackingUpsert < update.Upserts.Length; preparation.PackingUpsert++)
            {
                var source = update.Upserts[preparation.PackingUpsert];
                var reused = preparation.ReusedVisuals[source.Id];
                for (; preparation.PackingVisual < source.Visuals.Length; preparation.PackingVisual++)
                {
                    using var interval = SceneDisplayDiagnostics.Begin("Prepare.CollectPacking", source.Id,
                        preparation.PackingVisual, source.Visuals[preparation.PackingVisual] is
                            SceneVisual.Geometry geometry ? geometry.Mesh : null);
                    if (reused[preparation.PackingVisual] is not null)
                    {
                        if (!slice.Start()) return false;
                    }
                    else if (!preparation.Packing.Collect(source.Visuals[preparation.PackingVisual], slice)) return false;
                }
                preparation.PackingVisual = 0;
            }
            using (SceneDisplayDiagnostics.Begin("Prepare.StartPacking"))
                if (!preparation.Packing.Start(_preparationPackingTail, PackMesh, slice, out _preparationPackingTail)) return false;
        }
        return true;
    }

    private bool ValidatePreparation(Preparation preparation, PreparationSlice slice)
    {
        var update = preparation.Frame.Update;
        var next = preparation.Objects!;
        for (; preparation.ValidateUpsert < update.Upserts.Length; preparation.ValidateUpsert++)
        {
            if (!slice.Start()) return false;
            if (update.Upserts[preparation.ValidateUpsert].Parent is { } parent && !next.ContainsKey(parent))
                throw new InvalidOperationException("显示对象的父对象不存在。");
        }
        if (update.Base is not null)
            for (; preparation.ValidateRemoved < update.Removed.Length; preparation.ValidateRemoved++)
            {
                if (preparation.ValidationChildren is null)
                {
                    if (!slice.Start()) return false;
                    if (!_children.TryGetValue(update.Removed[preparation.ValidateRemoved], out var children)) continue;
                    preparation.ValidationChildren = children.GetEnumerator();
                }
                var descendants = preparation.ValidationChildren;
                while (true)
                {
                    if (!slice.Start()) return false;
                    if (!descendants.MoveNext()) break;
                    if (next.TryGetValue(descendants.Current, out var surviving) &&
                        surviving.Source.Parent is { } parent && !next.ContainsKey(parent))
                        throw new InvalidOperationException("显示对象的父对象不存在。");
                }
                descendants.Dispose();
                preparation.ValidationChildren = null;
            }
        return true;
    }

    private sealed class VisualMatching(DisplayObject? displayed, ImmutableArray<SceneVisual> visuals)
    {
        internal PreparedVisual?[] Matched { get; } = new PreparedVisual?[visuals.Length];
        private readonly Dictionary<SceneMesh, Queue<PreparedVisual>> _geometry = new(ReferenceEqualityComparer.Instance);
        private int _previous, _index;
        internal bool Complete => _previous == (displayed?.Visuals.Length ?? 0) && _index == visuals.Length;

        internal void Advance()
        {
            if (displayed is not null && _previous < displayed.Visuals.Length)
            {
                var previous = displayed.Visuals[_previous++];
                if (previous.Visual is SceneVisual.Geometry source)
                {
                    if (!_geometry.TryGetValue(source.Mesh, out var available)) _geometry.Add(source.Mesh, available = new());
                    available.Enqueue(previous);
                }
                return;
            }
            var visual = visuals[_index];
            if (visual is SceneVisual.Geometry geometry)
            {
                if (_geometry.TryGetValue(geometry.Mesh, out var available) && available.TryDequeue(out var previous))
                    Matched[_index] = previous;
            }
            else if (displayed is not null && _index < displayed.Visuals.Length && displayed.Visuals[_index].CanUpdate(visual))
                Matched[_index] = displayed.Visuals[_index];
            _index++;
        }
    }

}
