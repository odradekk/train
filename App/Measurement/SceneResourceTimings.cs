using Train.Rendering;
using Train.Scenes.Contracts;

namespace Train.App.Measurement;

internal sealed record SceneResourceFrame(ulong ProcessFrame, double At, string Phase, double FrameSeconds,
    double PreparationSeconds, double CandidatePreparationSeconds, double CommitSeconds, bool AfterOperable,
    bool ExcludedSpaceChange)
{
    public double PreparationTotalSeconds => PreparationSeconds + CandidatePreparationSeconds;
}

internal sealed class SceneResourceTimings
{
    private double _preparation, _candidate, _commit, _lastDraw;
    private string? _operation;
    private bool _operable;
    private SceneSpaceId? _displayedSpace;

    internal void Measure(SceneDisplayWork work, TimeSpan elapsed)
    {
        switch (work)
        {
            case SceneDisplayWork.Prepare: _preparation += elapsed.TotalSeconds; break;
            case SceneDisplayWork.PrepareCandidate: _candidate += elapsed.TotalSeconds; break;
            case SceneDisplayWork.Commit: _commit += elapsed.TotalSeconds; break;
        }
    }

    internal SceneResourceFrame? Draw(ulong frame, double at, string phase, string? operation, bool interactive,
        SceneSpaceId? displayedSpace)
    {
        if (_operation != operation)
        {
            _operation = operation;
            _operable = false;
        }
        // 新空间正式首帧属于打开；只排除该 draw，不重开整个可操作窗口。
        var excludedSpace = _operable && displayedSpace is { } current && _displayedSpace is { } previous && current != previous;
        var sample = _lastDraw > 0 ? new SceneResourceFrame(frame, at, phase, at - _lastDraw,
            _preparation, _candidate, _commit, _operable && !excludedSpace, excludedSpace) : null;
        if (operation is not null && interactive) _operable = true;
        if (displayedSpace is not null) _displayedSpace = displayedSpace;
        _lastDraw = at;
        _preparation = _candidate = _commit = 0;
        return sample;
    }
}
