using System.Collections.Immutable;
using Train.App.Logging;
using Train.Core.Contracts;

namespace Train.App;

internal static class CommandDiagnostics
{
    internal static string? Report(TrainLogger logger, SubmissionResult result)
    {
        if (result is SubmissionResult.Completed { Receipt.Decision: CommandDecision.Faulted failed })
            return Describe(failed.Fault);
        ImmutableArray<CoreProblem> problems = result switch
        {
            SubmissionResult.NotAccepted rejected => [rejected.Reason],
            SubmissionResult.Completed { Receipt.Decision: CommandDecision.Rejected rejected } => rejected.Problems,
            _ => default
        };
        if (problems.IsDefault) return null;
        var message = string.Join("；", problems.Select(Describe));
        var receipt = (result as SubmissionResult.Completed)?.Receipt;
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
            "editing_active" => "请先结束设计编辑。",
            "busy" => "当前操作尚未结束，请稍后再试。",
            "simulation_session_unavailable" => "没有活动模拟会话。",
            "content_changed" => "对象所属对局已更换，操作未提交。",
            "frozen" => "核心模拟已冻结，不能执行此操作。",
            "no_route" => "没有可达路径。",
            "object_missing" => "对象已不存在。",
            "content_missing" => "所需内容缺失。",
            "invalid_state" => "状态不符合操作要求。",
            "not_owned" => "对象不属于玩家。",
            "access_denied" => "没有访问权限。",
            "insufficient_goods" => "货物不足。",
            "insufficient_capacity" => "可用容量不足。",
            "insufficient_money" => "资金不足。",
            "price_condition_failed" => "当前价格不满足已确认的总额条件。",
            "identity_exhausted" => "对象或命令编号已耗尽。",
            "unsupported_format" => "不支持此格式。",
            "incompatible_replay" => "复现资料与当前运行环境不兼容。",
            "generation_failed" => "世界生成失败。",
            _ => ""
        };
        var subject = problem.Subject is null ? "" : $"；对象：{DescribeSubject(problem.Subject)}";
        var related = problem.RelatedIdentities.IsEmpty ? "" : $"；关联：{string.Join("、", problem.RelatedIdentities)}";
        return $"{explanation}（{problem.Code}/{problem.Field}{subject}{related}）";
    }

    private static string DescribeSubject(WorldObjectId subject) => subject switch
    {
        WorldObjectId.Node node => $"节点 {node.Id.Value}",
        WorldObjectId.Edge edge => $"线路 {edge.Id.Value}",
        WorldObjectId.Train train => $"列车 {train.Id.Value}",
        WorldObjectId.Instance { Id: InstanceId.Vehicle vehicle } => $"车辆 {vehicle.Id.Value}",
        WorldObjectId.Instance { Id: InstanceId.Building building } => $"建筑 {building.Id.Value}",
        WorldObjectId.Settlement settlement => $"聚居地 {settlement.Id.Value}",
        WorldObjectId.Inventory inventory => $"库存 {inventory.Id.Value}",
        WorldObjectId.Job job => $"任务 {job.Id.Value}",
        WorldObjectId.Combat combat => $"战斗 {combat.Id.Value}",
        WorldObjectId.Creature creature => $"生物 {creature.Id.Value}",
        WorldObjectId.CreatureGroup group => $"生物群 {group.Id.Value}",
        WorldObjectId.Lair lair => $"巢穴 {lair.Id.Value}",
        _ => "未知对象"
    };
}
