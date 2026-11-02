using System.Collections.Immutable;
using Train.App.Editing;
using Train.Content.Contracts;
using Train.Scenes.Contracts;

namespace Train.App.Authoring;

internal sealed partial class AuthoringService
{
    private const string InternalError = "操作失败，详情见日志。";

    private string Unexpected(Exception error, string message = InternalError)
    {
        logger.Error("authoring", "authoring_operation_failed", error.Message, exception: error);
        return message;
    }

    private string Describe<T>(ContentResult<T> result) where T : notnull
    {
        if (result is ContentResult<T>.Faulted fault)
        {
            logger.Error("authoring", "authoring_operation_failed", fault.Code + "：" + fault.Message,
                new Dictionary<string, object?> { ["diagnostic_stack_trace"] = fault.StackTrace });
            return InternalError;
        }
        if (result is ContentResult<T>.Failure failure)
        {
            foreach (var diagnostic in failure.Diagnostics)
                logger.Warning("authoring", "authoring_content_diagnostic", EditorService.Describe(diagnostic));
            return string.Join("；", failure.Diagnostics.Select(diagnostic => diagnostic.Message));
        }
        return "";
    }

    private string SceneMessages(ImmutableArray<SceneProblem> problems)
    {
        foreach (var problem in problems)
            logger.Warning("authoring", "authoring_scene_diagnostic", EditorService.Describe(problem));
        return string.Join("；", problems.Select(problem => EditorService.Explain(problem, host.Content)));
    }
}
