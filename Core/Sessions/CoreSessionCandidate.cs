using System.Collections.Immutable;
using Train.Core.Contracts;
using Train.Core.World;

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
        References = WorldReferences.Create(state.World, state.Content);
        AdaptationReport = adaptationReport.IsDefault ? [] : adaptationReport;
        MissingReferences = References.Definitions.Where(d => d.WasMissing)
            .Select(d => new MissingCoreReference(d.Definition, d.Field)).ToImmutableArray();
        MissingReferences = MissingReferences.AddRange(References.Templates
            .Where(t => state.Content.MissingReferences.Any(m => m.Reference == new CoreDefinitionReference.Template(t.SourceTemplate)))
            .Select(t => new MissingCoreReference(new CoreDefinitionReference.Template(t.SourceTemplate), $"World.Settlements[{t.Owner.Value}].Template")));
    }

    public ICoreReadView View { get; }

    public CoreReferenceManifest References { get; }
    public ImmutableArray<CoreProblem> AdaptationReport { get; }
    public ImmutableArray<MissingCoreReference> MissingReferences { get; }

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
