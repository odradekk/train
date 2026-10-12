using System.Collections.Immutable;
using Train.Core.Contracts;

namespace Train.Core.Sessions;

internal sealed class CoreSessionCandidate : ICoreSessionCandidate
{
    private readonly CoreSession _session;
    private bool _activated;
    private bool _disposed;

    internal CoreSessionCandidate(CoreState state)
    {
        _session = new CoreSession(state);
        View = _session.Read();
    }

    public ICoreReadView View { get; }

    public CoreReferenceManifest References
        => throw new NotImplementedException("References 尚未实现。");
    public ImmutableArray<CoreProblem> AdaptationReport
        => throw new NotImplementedException("AdaptationReport 尚未实现。");
    public ImmutableArray<MissingCoreReference> MissingReferences
        => throw new NotImplementedException("MissingReferences 尚未实现。");

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
