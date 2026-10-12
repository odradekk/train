using System.Collections.Immutable;
using C = Train.Content.Contracts;

namespace Train.Content.Loading;

internal sealed class ContentCandidate(C.IContentView view, C.ContentPreparationReport report) : C.IContentCandidate
{
    private bool _consumed;
    public C.IContentView? Basis => null;
    public C.IContentView View { get; } = view;
    public C.ContentChange Changes => C.ContentChange.Presentation | C.ContentChange.CoreInput;
    public C.ContentPreparationReport Report { get; } = report;
    public C.PreparedContentEdit? Edit => null;
    public ImmutableArray<C.ExternalContentWrite> RequiredWrites => [];

    public C.IContentView Activate()
    {
        if (_consumed) throw new InvalidOperationException("内容候选已激活或已释放。");
        _consumed = true;
        return View;
    }

    public void Dispose() => _consumed = true;
}
