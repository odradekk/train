using Train.Scenes.Contracts;

namespace Train.App.Editing;

internal sealed partial class EditorService
{
    internal SceneResult<IEditPreview>? Preview(EditOperation operation)
    {
        if (Session is null || SceneUnavailable || IsBusy) return null;
        try
        {
            return Session.Preview(operation);
        }
        catch (Exception error)
        {
            _logger.Warning("editor", "editor_preview_failed", error.Message, exception: error);
            return new SceneResult<IEditPreview>.Faulted(new("editor_preview_failed", SceneStage.Edit, null, null, null, null, error.Message));
        }
    }
}
