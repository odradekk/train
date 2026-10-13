using Train.App.Logging;
using Train.Core.Contracts;

namespace Train.App;

internal static class CommandDiagnostics
{
    internal static string? Report(TrainLogger logger, SubmissionResult result)
    {
        if (result is SubmissionResult.Completed { Receipt.Decision: CommandDecision.Faulted failed })
            return Describe(failed.Fault);
        var message = result switch
        {
            SubmissionResult.NotAccepted rejected => Describe(rejected.Reason),
            SubmissionResult.Completed { Receipt.Decision: CommandDecision.Rejected rejected } =>
                string.Join("；", rejected.Problems.Select(Describe)),
            _ => null
        };
        if (message is null) return null;
        var receipt = (result as SubmissionResult.Completed)?.Receipt;
        var problems = result switch
        {
            SubmissionResult.NotAccepted rejected => new[] { rejected.Reason },
            SubmissionResult.Completed { Receipt.Decision: CommandDecision.Rejected rejected } => rejected.Problems.ToArray(),
            _ => []
        };
        logger.Log(LogLevel.Warning, "train", "train_command_failed", message,
            new Dictionary<string, object?> { ["command_id"] = receipt?.Number,
                ["decision"] = receipt?.Decision.GetType().Name ?? "NotAccepted",
                ["problems"] = problems.Select(problem => new Dictionary<string, object?>
                {
                    ["code"] = problem.Code, ["field"] = problem.Field,
                    ["subject"] = problem.Subject?.ToString(), ["related"] = problem.RelatedIdentities.ToArray()
                }).ToArray() });
        return message;
    }

    internal static string Describe(CoreFault fault) => $"核心模拟发生故障：{fault.Code}/{fault.Field}。";

    internal static string Describe(CoreProblem problem)
    {
        var explanation = problem.Code switch
        {
            "busy" => "当前操作尚未结束，请稍后再试。",
            "simulation_session_unavailable" => "没有活动模拟会话。",
            "content_changed" => "对象所属对局已更换，操作未提交。",
            "frozen" => "核心模拟已冻结，不能执行此操作。",
            "no_route" => "没有可达路径。",
            _ => "操作被拒绝。"
        };
        return $"{explanation}（{problem.Code}/{problem.Field}）";
    }
}
