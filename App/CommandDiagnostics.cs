using System.Collections.Immutable;
using Train.App.Logging;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Ui;

namespace Train.App;

internal static class CommandDiagnostics
{
    internal static string? Report(TrainLogger logger, SubmissionResult result, IContentView? content = null)
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
        var message = string.Join("\n", problems.Select(problem => Describe(problem, content)));
        var receipt = (result as SubmissionResult.Completed)?.Receipt;
        logger.Log(LogLevel.Warning, "train", "train_command_failed", message,
            new Dictionary<string, object?> { ["command_id"] = receipt?.Number,
                ["decision"] = receipt?.Decision.GetType().Name ?? "NotAccepted",
                ["problems"] = problems.Select(ProblemRecord).ToArray() });
        return message;
    }

    internal static string LogProblem(TrainLogger logger, string category, string eventId, CoreProblem problem, IContentView? content = null)
    {
        var message = Describe(problem, content);
        logger.Warning(category, eventId, message, new Dictionary<string, object?> { ["problems"] = new[] { ProblemRecord(problem) } });
        return message;
    }

    internal static Dictionary<string, object?> ProblemRecord(CoreProblem problem) => new()
    {
        ["code"] = problem.Code, ["field"] = problem.Field,
        ["subject"] = problem.Subject?.ToString(), ["related"] = problem.RelatedIdentities.ToArray()
    };

    internal static string Describe(CoreFault _) => "核心模拟发生故障。";

    internal static string Describe(CoreProblem problem, IContentView? content = null)
    {
        var sentence = Sentence(problem.Code);
        var goods = GoodNames(problem.RelatedIdentities, content);
        if (goods.Count > 0) sentence = $"{sentence.TrimEnd('。')}：{string.Join("、", goods)}。";
        return problem.Subject is null ? sentence : $"{sentence}对象：{DescribeSubject(problem.Subject)}。";
    }

    private static List<string> GoodNames(ImmutableArray<string> identities, IContentView? content)
    {
        var names = new List<string>();
        if (content is null || identities.IsDefaultOrEmpty) return names;
        foreach (var identity in identities)
        {
            if (Good(identity) is not { } good || content.FindGood(good) is not ContentLookup<GoodPresentation>.Found)
                continue;
            names.Add(WorldPanels.GoodName(good, content));
        }
        return names;
    }

    private static GoodId? Good(string identity)
    {
        if (identity.StartsWith("voxel:", StringComparison.Ordinal))
            return new GoodId.Voxel(new(identity["voxel:".Length..]));
        if (identity.StartsWith("composite:", StringComparison.Ordinal))
            return new GoodId.Composite(new(identity["composite:".Length..]));
        if (identity.StartsWith("assembly:", StringComparison.Ordinal))
            return new GoodId.Assembly(new(identity["assembly:".Length..]));
        return null;
    }

    private static string Sentence(string code) => code switch
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
        "invalid_design" => "设计不能用于此操作。",
        "node_occupied" => "节点已被占据。",
        "plot_occupied" => "地块已被占用。",
        "conditions_not_met" => "条件未满足。",
        "not_owned" => "对象不属于玩家。",
        "instance_locked" => "对象已锁定。",
        "instance_destroyed" => "对象已毁坏。",
        "train_not_stationary_at_node" => "列车未停在该节点。",
        "access_denied" => "没有访问权限。",
        "insufficient_goods" => "货物不足。",
        "insufficient_capacity" => "可用容量不足。",
        "insufficient_money" => "资金不足。",
        "price_condition_failed" => "当前价格不满足已确认的总额条件。",
        "identity_exhausted" => "对象或命令编号已耗尽。",
        "unsupported_format" => "不支持此格式。",
        "incompatible_replay" => "复现资料与当前运行环境不兼容。",
        "generation_failed" => "世界生成失败。",
        "output_blocked" => "产出无法进入库存。",
        "inputs_missing" => "原料或可采储量不足。",
        "power_unavailable" => "电力不足。",
        "labor_unavailable" => "劳动力不足。",
        "platform_required" => "列车不在本站停靠：先在运行计划里把本站设为停靠站，等列车在站台停稳后再操作。",
        "platform_wait" => "正在等待空闲站台。",
        "track_wait" => "正在等待可进入的铁轨。",
        "control_missing" => "列车没有可用的控制。",
        "traction_missing" => "列车没有可用牵引力。",
        "overloaded" => "列车过重，无法起步。",
        "train_not_at_settlement" => "列车不在该聚居地。",
        "capability_zero" => "设施当前没有可用能力。",
        "restock_shortage" => "当地备货将低于补货目标。",
        "insufficient_demand" => "买方需求不足。",
        "vehicles_not_colocated" => "车辆不在同一地点。",
        "vehicle_in_train" => "车辆已编入列车。",
        "vehicle_duplicate" => "编组中有重复车辆。",
        "composition_empty" => "编组不能为空。",
        "plan_empty" => "运行计划没有停靠点。",
        "plan_degenerate" => "循环计划的停靠不能全部相同。",
        "plan_duplicate_good" => "同一停靠的货物重复。",
        "plan_missing" => "列车没有运行计划。",
        "plan_completed" => "单次计划已经跑完。",
        "settlement_unavailable" => "列车不在可解编的聚居地。",
        "inventory_not_empty" => "列车库存未清空。",
        "duplicate_source" => "材料来源重复。",
        "not_for_sale" => "这辆车不出售。",
        "replay_read_only" => "正在重放，不能修改。",
        _ => "无法完成此操作。"
    };

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
