using System.Collections.Immutable;
using Train.Content.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes.Editing;

internal sealed partial class DesignEditSession
{
    // Decision note: 2026-10-31-design-editor-direct-editing
    public SceneResult<EditState> Apply(EditOperation operation) => Preview(operation) switch
    {
        SceneResult<IEditPreview>.Success { Value: PreviewValue prepared } => Commit(prepared),
        SceneResult<IEditPreview>.Failure failure => Failure<EditState>(failure.Problems),
        _ => throw new InvalidOperationException("Unexpected edit preparation result.")
    };

    private bool OutsideEnvelope(ImmutableArray<int> path, GridPosition at) =>
        EditLayout.Same(envelopeSource.Specification, root.Specification)
        && envelopeSource.Envelope is { } envelope
        && !Contains(envelope, EditLayout.ToRoot(root, path, at));
}
