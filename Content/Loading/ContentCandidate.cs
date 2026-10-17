using System.Collections.Immutable;
using C = Train.Content.Contracts;

namespace Train.Content.Loading;

internal sealed class ContentCandidate(C.IContentView view, C.ContentPreparationReport report,
    C.IContentView? basis = null, C.ContentChange changes = C.ContentChange.Presentation | C.ContentChange.CoreInput,
    C.PreparedContentEdit? edit = null, ImmutableArray<C.ExternalContentWrite> writes = default) : C.IContentCandidate
{
    private bool _consumed;
    public C.IContentView? Basis { get; } = basis;
    public C.IContentView View { get; } = view;
    public C.ContentChange Changes { get; } = changes;
    public C.ContentPreparationReport Report { get; } = report;
    public C.PreparedContentEdit? Edit { get; } = edit;
    public ImmutableArray<C.ExternalContentWrite> RequiredWrites { get; } = writes.IsDefault ? [] : writes;

    public C.IContentView Activate()
    {
        if (_consumed) throw new InvalidOperationException("内容候选已激活或已释放。");
        _consumed = true;
        return View;
    }

    public void Dispose() => _consumed = true;
}
