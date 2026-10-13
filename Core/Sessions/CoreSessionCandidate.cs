using System.Collections.Immutable;
using Train.Core.Contracts;

namespace Train.Core.Sessions;

internal sealed class CoreSessionCandidate : ICoreSessionCandidate
{
    private readonly CoreSession _session;
    private bool _activated;
    private bool _disposed;

    internal CoreSessionCandidate(CoreState state, int retainedRecordingStarts,
        ImmutableArray<CoreProblem> adaptationReport = default)
    {
        _session = new CoreSession(state, retainedRecordingStarts);
        View = _session.Read();
        References = new([], [], [], [state.Content.Identity]);
        AdaptationReport = adaptationReport.IsDefault ? [] : adaptationReport;
    }

    public ICoreReadView View { get; }

    public CoreReferenceManifest References { get; }
    public ImmutableArray<CoreProblem> AdaptationReport { get; }
    public ImmutableArray<MissingCoreReference> MissingReferences => [];

    public ICoreSession Activate()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_activated)
            throw new InvalidOperationException("候选已经激活。");
        _activated = true;
        return _session;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (!_activated)
            _session.Dispose();
    }
}
