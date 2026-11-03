using Train.Content.Contracts;
using Train.Core.Contracts;

namespace Train.App.Editing;

internal sealed partial class EditorService
{
    private Task<ContentResult<DesignEvaluation>> EvaluateAsync(IContentView content, DesignDraft draft)
    {
        CompositeDefinitionId? identity = Target is DraftTarget.PackageDefinition target
            ? new(target.Package.Value + ":" + target.LocalId) : null;
        return Task.Run(() => Host.ContentFactory.EvaluateDesign(content, draft, identity));
    }
}
