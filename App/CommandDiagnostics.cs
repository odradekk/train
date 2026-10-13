using Train.App.Logging;
using Train.Core.Contracts;

namespace Train.App;

internal static class CommandDiagnostics
{
    internal static string? Report(TrainLogger logger, SubmissionResult result)
    {
        var message = result switch
        {
            SubmissionResult.NotAccepted rejected => Describe(rejected.Reason),
            SubmissionResult.Completed { Receipt.Decision: CommandDecision.Rejected rejected } =>
                string.Join("；", rejected.Problems.Select(Describe)),
            SubmissionResult.Completed { Receipt.Decision: CommandDecision.Faulted failed } =>
                $"核心模拟发生故障，已冻结：{failed.Fault.Code}/{failed.Fault.Field}。",
            _ => null
        };
        var receipt = (result as SubmissionResult.Completed)?.Receipt;
        var problems = result switch
        {
            SubmissionResult.NotAccepted rejected => new[] { rejected.Reason },
            SubmissionResult.Completed { Receipt.Decision: CommandDecision.Rejected rejected } => rejected.Problems.ToArray(),
            _ => []
        };
        var fault = (receipt?.Decision as CommandDecision.Faulted)?.Fault;
        var diagnostic = (result as SubmissionResult.Completed)?.Diagnostic;
        logger.Log(fault is not null ? LogLevel.Error : message is null ? LogLevel.Info : LogLevel.Warning, "train",
            message is null ? "train_command_accepted" : "train_command_failed",
            message ?? "列车命令已接受。",
            new Dictionary<string, object?> { ["command_id"] = receipt?.Number,
                ["decision"] = receipt?.Decision.GetType().Name ?? "NotAccepted",
                ["problems"] = problems.Select(problem => new Dictionary<string, object?>
                {
                    ["code"] = problem.Code, ["field"] = problem.Field,
                    ["subject"] = problem.Subject?.ToString(), ["related"] = problem.RelatedIdentities.ToArray()
                }).ToArray(),
                ["fault_code"] = fault?.Code, ["fault_field"] = fault?.Field,
                ["fault_subject"] = fault?.Subject?.ToString(), ["fault_operation"] = fault?.Operation,
                ["fault_completed_steps"] = fault?.Position.CompletedSteps,
                ["fault_sequence"] = fault?.Position.Sequence,
                ["diagnostic_message"] = diagnostic?.Message, ["diagnostic_stack_trace"] = diagnostic?.StackTrace });
        return message;
    }

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
