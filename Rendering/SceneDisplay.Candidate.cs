using System.Diagnostics;
using Train.Scenes.Contracts;

namespace Train.Rendering;

internal sealed partial class SceneDisplay
{
    private SceneUpdate? _candidateUpdate;
    private SceneFrameStamp? _candidateScope;
    private readonly Dictionary<(SceneObjectId Object, int Index), SceneVisual> _candidateSources = [];
    private readonly Dictionary<(SceneObjectId Object, int Index), PreparedVisual> _candidateVisuals = [];
    internal SceneFrameStamp? PreparedCandidate { get; private set; }
    internal SceneFrameStamp? CandidateScope => _candidateScope;
    internal bool IsPreparingCandidate => _candidateUpdate is not null;

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
        if (update.Base is null) _candidateSources.Clear();
        var replaced = update.Removed.Concat(update.Upserts.Select(item => item.Id)).ToHashSet();
        foreach (var key in _candidateSources.Keys.Where(key => replaced.Contains(key.Object)).ToArray())
            _candidateSources.Remove(key);
        foreach (var source in update.Upserts)
            for (var index = 0; index < source.Visuals.Length; index++)
                _candidateSources.Add((source.Id, index), source.Visuals[index]);
        foreach (var (key, visual) in _candidateVisuals.ToArray())
            if (!_candidateSources.TryGetValue(key, out var source) || !visual.CanUpdate(source))
            {
                visual.Dispose();
                _candidateVisuals.Remove(key);
            }
        _candidateScope = update.Frame;
        _candidateUpdate = update;
    }

    internal void PrepareCandidate(TimeSpan budget)
    {
        _cache.RequireMainThread();
        if (_candidateUpdate is not { } update) return;
        ResetPreparationBudget();
        var started = Timestamp();
        try
        {
            foreach (var (key, source) in _candidateSources)
            {
                if (_candidateVisuals.ContainsKey(key)) continue;
                var elapsed = Stopwatch.GetElapsedTime(started, Timestamp());
                // 候选始终受预算约束；新空间首帧的例外只属于实际显示帧。
                if (elapsed >= budget || _preparationTime + elapsed >= PreparationBudget) return;
                _candidateVisuals.Add(key, _resources.Prepare(source));
            }
            PreparedCandidate = update.Frame;
            _candidateUpdate = null;
        }
        finally
        {
            _preparationTime += Stopwatch.GetElapsedTime(started, Timestamp());
        }
    }

    private PreparedVisual? TakeCandidateVisual(SceneFrameStamp frame, (SceneObjectId Object, int Index) key, SceneVisual visual)
    {
        if (!HasCandidateFor(frame) || !_candidateVisuals.TryGetValue(key, out var prepared) || !prepared.CanUpdate(visual))
            return null;
        _candidateVisuals.Remove(key);
        return prepared;
    }

    internal void CancelCandidate()
    {
        _cache.RequireMainThread();
        foreach (var visual in _candidateVisuals.Values) visual.Dispose();
        _candidateVisuals.Clear();
        _candidateSources.Clear();
        _candidateUpdate = null;
        _candidateScope = null;
        PreparedCandidate = null;
    }
}
