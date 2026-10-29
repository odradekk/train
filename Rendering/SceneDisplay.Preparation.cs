using System.Collections.Immutable;
using System.Diagnostics;
using Godot;
using Train.App.Assets;
using Train.Scenes.Contracts;

namespace Train.Rendering;

internal sealed partial class SceneDisplay
{
    internal PreparedFrame? Prepare(ISceneFrame frame, TimeSpan? budget = null)
    {
        _cache.RequireMainThread();
        if (_candidatePromoted && !HasCandidateFor(frame.Update.Frame)) CancelCandidate();
        if (frame.Readiness == SceneReadiness.Loading ||
            frame.Update.Base is { } baseline && baseline != DisplayedFrame?.Update.Frame)
        {
            CancelPreparation();
            return null;
        }
        if (_preparation is { Frame: var previousFrame } &&
            (previousFrame.Update.Base != frame.Update.Base ||
             previousFrame.Update.Frame.Session != frame.Update.Frame.Session ||
             previousFrame.Update.Frame.Request != frame.Update.Frame.Request ||
             previousFrame.Update.Frame.Space != frame.Update.Frame.Space))
            CancelPreparation();
        ResetPreparationBudget();
        var started = Timestamp();
        try
        {
            if (_preparation is null) { _preparation = new(frame); _candidate++; }
            var preparation = _preparation;
            frame = preparation.Frame;
            var update = frame.Update;
            var spaceChanged = DisplayedFrame is { } displayed
                ? displayed.Update.Frame.Session != update.Frame.Session || displayed.Update.Frame.Space != update.Frame.Space
                : _completeInitialFrame;
            // 新空间正式首帧仍一次准备并原子提交，后续调用共享每进程帧的额度。
            var slice = new PreparationSlice(this, started, budget, spaceChanged);
            if (!InitializePreparation(preparation, slice, spaceChanged)) return null;
            preparation.Packing?.ThrowIfFailed();
            var next = preparation.Objects!;
            for (; preparation.Upsert < update.Upserts.Length; preparation.Upsert++)
            {
                var source = update.Upserts[preparation.Upsert];
                _objects.TryGetValue(source.Id, out var previous);
                if (preparation.Visuals is null)
                {
                    if (!slice.Start()) return null;
                    if (previous is null) _pendingRoots.Add(source.Id, new Node3D { Name = $"Object{source.Id.Value}" });
                    preparation.Visuals = new PreparedVisual[source.Visuals.Length];
                }
                var root = previous?.Root ?? _pendingRoots[source.Id];
                var visuals = preparation.Visuals;
                var reusedVisuals = preparation.ReusedVisuals[source.Id];
                for (; preparation.Visual < visuals.Length; preparation.Visual++)
                {
                    var i = preparation.Visual;
                    if (!slice.Start(resource: reusedVisuals[i] is null,
                        estimate: reusedVisuals[i] is null ? VisualCost(source.Visuals[i]) : default)) return null;
                    if (reusedVisuals[i] is { } reused) visuals[i] = reused;
                    else
                    {
                        preparation.NeedsResources = true;
                        var resourceStarted = Timestamp();
                        var visual = TakeCandidateVisual(update.Frame, (source.Id, i), source.Visuals[i]) ??
                            (spaceChanged ? _resources.Prepare(source.Visuals[i]) :
                                _resources.TryPrepare(source.Visuals[i], preparation.Packing));
                        if (visual is null) return null;
                        _pendingVisuals.Add(visual);
                        visuals[i] = visual;
                        RecordVisualCost(source.Visuals[i], resourceStarted);
                    }
                }
                next[source.Id] = new(source, root, visuals);
                if (preparation.NeedsResources)
                    Observe?.Invoke(new(_candidate, update.Frame, source.Id, SceneDisplayStage.ResourcesPrepared));
                preparation.Visuals = null;
                preparation.Visual = 0;
                preparation.NeedsResources = false;
            }
            if (!ValidatePreparation(preparation, slice)) return null;
            if (!preparation.FogChecked)
            {
                var regions = update.Environment.FoggedRegions;
                preparation.FogChanged |= _fog is null || _fog.Regions.Length != regions.Length;
                if (!preparation.FogChanged)
                    for (; preparation.FogComparison < regions.Length; preparation.FogComparison++)
                    {
                        if (!slice.Start()) return null;
                        if (_fog!.Regions[preparation.FogComparison] == regions[preparation.FogComparison]) continue;
                        preparation.FogChanged = true;
                        break;
                    }
                preparation.FogChecked = true;
            }
            if (preparation.FogChanged && _pendingFog is null)
            {
                if (!slice.Start()) return null;
                _pendingFog = new(update.Environment.FoggedRegions, _fog?.Material ?? _resources.ConstructionFogMaterial(), _fog);
            }
            if (_pendingFog is not null && !_pendingFog.Prepare(slice)) return null;
            if (!preparation.LinesChecked)
            {
                var lines = frame.DebugLines;
                preparation.LinesChanged |= !lines.IsEmpty && (_debugLines is null || _debugLines.Lines.Length != lines.Length);
                if (!preparation.LinesChanged && !lines.IsEmpty)
                    for (; preparation.LineComparison < lines.Length; preparation.LineComparison++)
                    {
                        if (!slice.Start()) return null;
                        if (_debugLines!.Lines[preparation.LineComparison] == lines[preparation.LineComparison]) continue;
                        preparation.LinesChanged = true;
                        break;
                    }
                preparation.LinesChecked = true;
            }
            if (preparation.LinesChanged && _pendingDebugLines is null)
            {
                if (!slice.Start()) return null;
                _pendingDebugLines = new(frame.DebugLines);
            }
            if (_pendingDebugLines is not null && !_pendingDebugLines.Prepare(slice)) return null;
            if (preparation.Packing is { } packing)
            {
                if (!slice.Start()) return null;
                packing.Dispose();
                preparation.Packing = null;
            }
            while (preparation.CachedGeometry.Count > 0)
            {
                if (!slice.Start()) return null;
                var index = preparation.CachedGeometry.Count - 1;
                preparation.CachedGeometry[index].Dispose();
                preparation.CachedGeometry.RemoveAt(index);
            }
            if (!slice.Start(estimate: _finalizationCost)) return null;
            var finalizationStarted = Timestamp();
            var prepared = new PreparedFrame(this, _candidate, frame, next.ToImmutable(), _pendingVisuals,
                _pendingRoots, _pendingFog ?? _fog!, _pendingFog is not null,
                frame.DebugLines.IsEmpty ? null : _pendingDebugLines ?? _debugLines, _pendingDebugLines is not null);
            _pendingFog = null;
            _pendingDebugLines = null;
            _pendingVisuals = [];
            _pendingRoots = [];
            _preparation = null;
            Observe?.Invoke(new(_candidate, update.Frame, null, SceneDisplayStage.FramePrepared));
            _finalizationCost = Stopwatch.GetElapsedTime(finalizationStarted, Timestamp());
            return prepared;
        }
        catch
        {
            CancelPreparation();
            throw;
        }
        finally
        {
            var elapsed = Stopwatch.GetElapsedTime(started, Timestamp());
            _preparationTime += elapsed;
            Measure?.Invoke(SceneDisplayWork.Prepare, elapsed);
        }
    }

    private sealed class Preparation(ISceneFrame frame)
    {
        internal ISceneFrame Frame { get; } = frame;
        internal ImmutableDictionary<SceneObjectId, DisplayObject>.Builder? Objects;
        internal IEnumerator<SceneObjectId>? ShiftRoots, ValidationChildren;
        internal int Removed, MatchUpsert, PackingUpsert, PackingVisual, Upsert, Visual;
        internal VisualMatching? Matching;
        internal PreparedVisual[]? Visuals;
        internal bool NeedsResources, Started, PackingStarted;
        internal int ValidateUpsert, ValidateRemoved;
        internal bool FogChecked, FogChanged, LinesChecked, LinesChanged;
        internal int FogComparison, LineComparison;
        internal SceneMeshPacking? Packing;
        internal List<AssetLease<ArrayMesh>> CachedGeometry { get; } = [];
        internal Dictionary<SceneObjectId, PreparedVisual?[]> ReusedVisuals { get; } = [];
        internal List<SceneObjectId> Required { get; } = [];
        internal HashSet<SceneMesh> SeenMeshes { get; } = new(ReferenceEqualityComparer.Instance);
        internal List<SceneMesh> Meshes { get; } = [];
    }

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
            if (matching.NeedsResources) preparation.Required.Add(source.Id);
            preparation.Matching = null;
        }
        if (!preparation.Started)
        {
            if (!slice.Start()) return false;
            Observe?.Invoke(new(_candidate, update.Frame, null, SceneDisplayStage.FramePreparationStarted,
                [.. preparation.Required]));
            preparation.Started = true;
        }
        if (!spaceChanged && !preparation.PackingStarted)
        {
            for (; preparation.PackingUpsert < update.Upserts.Length; preparation.PackingUpsert++)
            {
                var source = update.Upserts[preparation.PackingUpsert];
                var reused = preparation.ReusedVisuals[source.Id];
                for (; preparation.PackingVisual < source.Visuals.Length; preparation.PackingVisual++)
                {
                    if (!slice.Start()) return false;
                    if (reused[preparation.PackingVisual] is not null ||
                        source.Visuals[preparation.PackingVisual] is not SceneVisual.Geometry geometry ||
                        !preparation.SeenMeshes.Add(geometry.Mesh)) continue;
                    if (_resources.AcquireCachedGeometry(geometry.Mesh) is { } cached) preparation.CachedGeometry.Add(cached);
                    else preparation.Meshes.Add(geometry.Mesh);
                }
                preparation.PackingVisual = 0;
            }
            if (!slice.Start()) return false;
            if (preparation.Meshes.Count > 0)
            {
                preparation.Packing = new([.. preparation.Meshes], _preparationPackingTail, PackMesh);
                _preparationPackingTail = preparation.Packing.Completion;
            }
            preparation.PackingStarted = true;
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
        internal bool NeedsResources { get; private set; }
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
            NeedsResources |= Matched[_index] is null;
            _index++;
        }
    }

    private static bool _budgetAdvanced;

    private static void ResetPreparationBudget()
    {
        var processFrame = Engine.GetProcessFrames();
        if (_budgetFrame == processFrame) return;
        _budgetFrame = processFrame;
        _preparationTime = TimeSpan.Zero;
        _budgetAdvanced = false;
    }

    internal readonly struct PreparationSlice(SceneDisplay owner, long started, TimeSpan? budget, bool unlimited)
    {
        internal long Timestamp() => owner.Timestamp();

        internal bool Start(bool resource = false, TimeSpan estimate = default)
        {
            if (unlimited) { _budgetAdvanced = true; return true; }
            // 零配额保留仅收集后台打包输入的调用方式。
            if (resource && budget == TimeSpan.Zero) return false;
            var elapsed = Stopwatch.GetElapsedTime(started, owner.Timestamp());
            var allowance = budget is { } value && value > TimeSpan.Zero ? value : PreparationBudget;
            if (_budgetAdvanced && (elapsed >= allowance || _preparationTime + elapsed >= PreparationBudget ||
                elapsed + estimate > allowance || _preparationTime + elapsed + estimate > PreparationBudget)) return false;
            _budgetAdvanced = true;
            return true;
        }
    }
    private TimeSpan _geometryCost, _modelCost, _wheelsetCost, _missingCost, _finalizationCost;

    private TimeSpan VisualCost(SceneVisual visual) => visual switch
    {
        SceneVisual.Geometry => _geometryCost,
        SceneVisual.Model => _modelCost,
        SceneVisual.Wheelset => _wheelsetCost,
        SceneVisual.Missing => _missingCost,
        _ => throw new ArgumentOutOfRangeException(nameof(visual))
    };

    private void RecordVisualCost(SceneVisual visual, long started)
    {
        var elapsed = Stopwatch.GetElapsedTime(started, Timestamp());
        switch (visual)
        {
            case SceneVisual.Geometry: _geometryCost = elapsed; break;
            case SceneVisual.Model: _modelCost = elapsed; break;
            case SceneVisual.Wheelset: _wheelsetCost = elapsed; break;
            case SceneVisual.Missing: _missingCost = elapsed; break;
        }
    }

}
