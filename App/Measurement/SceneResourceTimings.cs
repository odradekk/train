using Train.Rendering;

namespace Train.App.Measurement;

internal sealed record SceneResourceFrame(ulong ProcessFrame, double At, string Phase, double FrameSeconds,
    double PreparationSeconds, double CandidatePreparationSeconds, double CommitSeconds, bool AfterOperable)
{
    public double PreparationTotalSeconds => PreparationSeconds + CandidatePreparationSeconds;
}

internal sealed class SceneResourceTimings
{
    private double _preparation, _candidate, _commit, _lastDraw;
    private string? _operation;
    private bool _operable;

    internal void Measure(SceneDisplayWork work, TimeSpan elapsed)
    {
        switch (work)
        {
            case SceneDisplayWork.Prepare: _preparation += elapsed.TotalSeconds; break;
            case SceneDisplayWork.PrepareCandidate: _candidate += elapsed.TotalSeconds; break;
            case SceneDisplayWork.Commit: _commit += elapsed.TotalSeconds; break;
        }
    }

    internal SceneResourceFrame? Draw(ulong frame, double at, string phase, string? operation, bool interactive)
    {
        if (_operation != operation)
        {
            _operation = operation;
            _operable = false;
        }
        // 首次完整提交且可操作的 draw 仍属于打开；之后不因 Loading 或空间切换重新排除帧。
        var sample = _lastDraw > 0 ? new SceneResourceFrame(frame, at, phase, at - _lastDraw,
            _preparation, _candidate, _commit, _operable) : null;
        if (operation is not null && interactive) _operable = true;
        _lastDraw = at;
        _preparation = _candidate = _commit = 0;
        return sample;
    }
}
