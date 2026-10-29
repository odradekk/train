using System.Diagnostics;
using Train.Scenes.Contracts;

namespace Train.Rendering;

internal sealed partial class SceneDisplay
{
    private SceneUpdate? _candidateUpdate;
    private bool _candidatePromoted;
    private SceneFrameStamp? _candidateScope;
    private readonly Dictionary<(SceneObjectId Object, int Index), SceneVisual> _candidateSources = [];
    private readonly Dictionary<SceneObjectId, int> _candidateSourceCounts = [];
    private readonly LinkedList<(SceneObjectId Object, int Index)> _candidatePending = [];
    private readonly Dictionary<(SceneObjectId Object, int Index), LinkedListNode<(SceneObjectId Object, int Index)>> _candidatePendingNodes = [];
    private readonly Dictionary<(SceneObjectId Object, int Index), PreparedVisual> _candidateVisuals = [];
    private ScenePackingPreparation? _candidatePacking;
    private Task? _candidatePackingTail;
    private bool _candidatePackingCursorInitialized;
    private LinkedListNode<(SceneObjectId Object, int Index)>? _candidatePackingCursor;
    internal Task CandidatePacking => _candidatePacking?.Job?.Completion ?? Task.CompletedTask;
    internal SceneFrameStamp? PreparedCandidate { get; private set; }
    internal SceneFrameStamp? CandidateScope => _candidateScope;
    internal bool IsPreparingCandidate => _candidateUpdate is not null;
    internal int PendingCandidateVisuals => _candidatePending.Count;

    internal bool HasCandidateFor(SceneFrameStamp frame) => _candidateScope is { } candidate &&
        candidate.Session == frame.Session && candidate.Request == frame.Request && candidate.Space == frame.Space;

    internal (int Prepared, int Received)? CandidateProgress(SceneFrameStamp frame) => HasCandidateFor(frame)
        ? (_candidateVisuals.Count, _candidateSources.Count) : null;

    internal void ReceiveCandidate(SceneUpdate update)
    {
        _cache.RequireMainThread();
        if (_candidateUpdate?.Frame == update.Frame || PreparedCandidate == update.Frame) return;
        if (!HasCandidateFor(update.Frame)) CancelCandidate();
        if (update.Base is { } baseline && baseline != PreparedCandidate)
        {
            CancelCandidate();
            return;
        }
        CancelCandidatePacking();
        var upserted = update.Upserts.Select(item => item.Id).ToHashSet();
        if (update.Base is null)
        {
            foreach (var id in _candidateSourceCounts.Keys.ToArray())
                if (!upserted.Contains(id)) RemoveCandidateObject(id);
        }
        foreach (var id in update.Removed)
            if (!upserted.Contains(id)) RemoveCandidateObject(id);
        foreach (var source in update.Upserts)
        {
            var oldCount = _candidateSourceCounts.GetValueOrDefault(source.Id);
            for (var index = source.Visuals.Length; index < oldCount; index++)
                RemoveCandidateSource((source.Id, index));
            for (var index = 0; index < source.Visuals.Length; index++)
            {
                var key = (source.Id, index);
                var visual = source.Visuals[index];
                _candidateSources[key] = visual;
                if (_candidateVisuals.TryGetValue(key, out var prepared))
                {
                    if (prepared.CanUpdate(visual)) continue;
                    prepared.Dispose();
                    _candidateVisuals.Remove(key);
                }
                if (!_candidatePendingNodes.ContainsKey(key))
                    _candidatePendingNodes.Add(key, _candidatePending.AddLast(key));
            }
            _candidateSourceCounts[source.Id] = source.Visuals.Length;
        }
        _candidateScope = update.Frame;
        _candidateUpdate = update;
    }

    internal void PrepareCandidate(TimeSpan budget)
    {
        _cache.RequireMainThread();
        ResetPreparationBudget();
        var started = Timestamp();
        var pauseStarted = GC.GetTotalPauseDuration();
        try
        {
            if (_candidateUpdate is not { } update) return;
            var slice = new PreparationSlice(this, started, budget, false);
            if (_candidatePacking is null)
            {
                if (!slice.Start()) return;
                _candidatePacking = new(_resources);
            }
            if (!_candidatePacking.Started)
            {
                if (!_candidatePackingCursorInitialized)
                {
                    if (!slice.Start()) return;
                    _candidatePackingCursor = _candidatePending.First;
                    _candidatePackingCursorInitialized = true;
                }
                while (_candidatePackingCursor is { } cursor)
                {
                    if (!slice.Start()) return;
                    _candidatePacking.Collect(_candidateSources[cursor.Value]);
                    _candidatePackingCursor = cursor.Next;
                }
                if (!slice.Start()) return;
                _candidatePackingTail = _candidatePacking.Start(_candidatePackingTail, PackMesh);
            }
            _candidatePacking.Job?.ThrowIfFailed();
            while (_candidatePending.First is { } pending)
            {
                var key = pending.Value;
                var source = _candidateSources[key];
                if (!slice.Start(estimate: VisualCost(source))) return;
                var resourceStarted = Timestamp();
                var visual = _resources.TryPrepare(source, _candidatePacking.Job);
                if (visual is null) return;
                _candidateVisuals.Add(key, visual);
                _candidatePending.Remove(pending);
                _candidatePendingNodes.Remove(key);
                RecordVisualCost(source, resourceStarted);
            }
            if (!_candidatePacking.Release(slice)) return;
            if (!slice.Start()) return;
            PreparedCandidate = update.Frame;
            _candidateUpdate = null;
            CancelCandidatePacking();
        }
        finally
        {
            var pause = GC.GetTotalPauseDuration() - pauseStarted;
            var ended = Timestamp();
            var elapsed = Stopwatch.GetElapsedTime(started, ended);
            _preparationTime += elapsed;
            Measure?.Invoke(SceneDisplayWork.PrepareCandidate, elapsed, pause);
        }
    }

    private void RemoveCandidateObject(SceneObjectId id)
    {
        if (!_candidateSourceCounts.Remove(id, out var count)) return;
        for (var index = 0; index < count; index++) RemoveCandidateSource((id, index));
    }

    private void RemoveCandidateSource((SceneObjectId Object, int Index) key)
    {
        _candidateSources.Remove(key);
        if (_candidateVisuals.Remove(key, out var visual)) visual.Dispose();
        if (_candidatePendingNodes.Remove(key, out var pending)) _candidatePending.Remove(pending);
    }

    private PreparedVisual? CandidateVisual(SceneFrameStamp frame, (SceneObjectId Object, int Index) key, SceneVisual visual)
    {
        if (!HasCandidateFor(frame) || !_candidateVisuals.TryGetValue(key, out var prepared) || !prepared.CanUpdate(visual))
            return null;
        return prepared;
    }

    internal void CancelCandidate()
    {
        _cache.RequireMainThread();
        CancelCandidatePacking();
        foreach (var visual in _candidateVisuals.Values) visual.Dispose();
        _candidateVisuals.Clear();
        _candidateSources.Clear();
        _candidateSourceCounts.Clear();
        _candidatePending.Clear();
        _candidatePendingNodes.Clear();
        _candidateUpdate = null;
        _candidateScope = null;
        _candidatePromoted = false;
        PreparedCandidate = null;
    }

    private void CancelCandidatePacking()
    {
        _candidatePacking?.Dispose();
        _candidatePacking = null;
        _candidatePackingCursorInitialized = false;
        _candidatePackingCursor = null;
    }
}
