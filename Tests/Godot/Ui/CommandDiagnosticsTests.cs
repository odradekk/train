using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.App;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Godot.Tests.Logging;

namespace Train.Godot.Tests.Ui;

[TestClass]
public sealed class CommandDiagnosticsTests
{
    [TestMethod]
    public void UnknownProblemHidesCodeFieldAndRawIdentities()
    {
        var subject = new WorldObjectId.Inventory(new(7));
        var problem = new CoreProblem("future_economy_problem", "quote.total", subject, ["good:coal", "lease:9"]);
        var message = CommandDiagnostics.Describe(problem);
        Assert.AreEqual("无法完成此操作。对象：库存 7。", message);
        Assert.IsFalse(message.Contains(problem.Code));
        Assert.IsFalse(message.Contains(problem.Field));
        Assert.IsFalse(message.Contains(subject.ToString()));
        Assert.IsFalse(message.Contains("关联"));
        foreach (var identity in problem.RelatedIdentities) Assert.IsFalse(message.Contains(identity));
    }

    [TestMethod]
    public void KnownSentencesStayInChineseWithoutTheirCodes()
    {
        foreach (var (code, sentence) in Sentences)
        {
            var message = CommandDiagnostics.Describe(new CoreProblem(code, "field", null, []));
            Assert.AreEqual(sentence, message, code);
            Assert.IsFalse(message.Contains(code), code);
            Assert.IsFalse(message.Contains("field"), code);
        }
    }

    [TestMethod]
    public void GoodsUseDisplayNamesOnlyWhenContentResolvesThem()
    {
        var subject = new WorldObjectId.Settlement(new(7));
        var rail = new GoodId.Voxel(new("fixture:rail"));
        var wood = new GoodId.Composite(new("fixture:wood"));
        var assembly = new GoodId.Assembly(new("fixture:hash"));
        var problem = new CoreProblem("insufficient_goods", "Sources", subject,
            ["voxel:fixture:rail", "quote:buy", "composite:fixture:wood", "assembly:fixture:hash", "config_invalid"]);
        var named = CommandDiagnostics.Describe(problem, Content(rail, "铁轨", wood, "木材", assembly, "总成"));
        Assert.AreEqual("货物不足：铁轨、木材、总成。对象：聚居地 7。", named);
        Assert.IsFalse(named.Contains("fixture:"));
        Assert.IsFalse(named.Contains("voxel:"));
        Assert.IsFalse(named.Contains("quote:buy"));
        Assert.IsFalse(named.Contains("config_invalid"));
        Assert.IsFalse(named.Contains("关联"));

        var hidden = CommandDiagnostics.Describe(problem);
        Assert.AreEqual("货物不足。对象：聚居地 7。", hidden);
        Assert.IsFalse(hidden.Contains("铁轨"));
        Assert.IsFalse(hidden.Contains("fixture:rail"));

        var unresolved = CommandDiagnostics.Describe(problem, new FixedContentView
        {
            Goods = new Dictionary<GoodId, ContentLookup<GoodPresentation>>
            {
                [rail] = new ContentLookup<GoodPresentation>.NotFound(),
                [wood] = new ContentLookup<GoodPresentation>.NotFound(),
                [assembly] = new ContentLookup<GoodPresentation>.NotFound()
            }
        });
        Assert.AreEqual("货物不足。对象：聚居地 7。", unresolved);
        Assert.IsFalse(unresolved.Contains("fixture:rail"));
    }

    [TestMethod]
    public void EveryObjectKindHasADistinctReadableLabelAndPreservesItsNumber()
    {
        const ulong number = 1234567890;
        (WorldObjectId Subject, string Label)[] subjects =
        [
            (new WorldObjectId.Node(new(number)), "节点"), (new WorldObjectId.Edge(new(number)), "线路"),
            (new WorldObjectId.Train(new(number)), "列车"), (new WorldObjectId.Instance(new InstanceId.Vehicle(new(number))), "车辆"),
            (new WorldObjectId.Instance(new InstanceId.Building(new(number))), "建筑"), (new WorldObjectId.Settlement(new(number)), "聚居地"),
            (new WorldObjectId.Inventory(new(number)), "库存"), (new WorldObjectId.Job(new(number)), "任务"),
            (new WorldObjectId.Combat(new(number)), "战斗"), (new WorldObjectId.Creature(new(number)), "生物"),
            (new WorldObjectId.CreatureGroup(new(number)), "生物群"), (new WorldObjectId.Lair(new(number)), "巢穴")
        ];
        var messages = subjects.Select(item => CommandDiagnostics.Describe(new CoreProblem("object_missing", "subject", item.Subject, []))).ToArray();
        for (var index = 0; index < subjects.Length; index++)
        {
            Assert.AreEqual($"对象已不存在。对象：{subjects[index].Label} {number}。", messages[index]);
            Assert.IsFalse(messages[index].Contains("object_missing"));
            Assert.IsFalse(messages[index].Contains('{'));
            Assert.IsFalse(messages[index].Contains("Id ="));
            Assert.IsFalse(messages[index].Contains(subjects[index].Subject.GetType().Name));
        }
        Assert.AreEqual(subjects.Length, messages.Distinct().Count());
    }

    [TestMethod]
    public void UnknownObjectKindUsesReadableFallback()
    {
        var subject = new FutureObject();
        var message = CommandDiagnostics.Describe(new CoreProblem("object_missing", "subject", subject, []));
        Assert.AreEqual("对象已不存在。对象：未知对象。", message);
        Assert.IsFalse(message.Contains(subject.GetType().Name));
    }

    [TestMethod]
    public void ProblemWithoutSubjectDoesNotInventAnObject()
    {
        var message = CommandDiagnostics.Describe(new CoreProblem("busy", "command", null, []));
        Assert.AreEqual("当前操作尚未结束，请稍后再试。", message);
        Assert.IsFalse(message.Contains("对象"));
    }

    [TestMethod]
    public void RejectedCommandReportsChineseTextAndKeepsProblemFieldsInTheLog()
    {
        using var logging = LoggerSession.CreateMemory();
        var money = new CoreProblem("insufficient_money", "wallet", new WorldObjectId.Settlement(new(1)), ["quote:buy"]);
        var capacity = new CoreProblem("insufficient_capacity", "inventory", new WorldObjectId.Inventory(new(2)), ["lease:3"]);
        var rejected = new SubmissionResult.Completed(new(5, new(0, 0), new(0, 0),
            new CommandDecision.Rejected([money, capacity])), [], null);
        var message = CommandDiagnostics.Report(logging.Logger, rejected);
        Assert.AreEqual("资金不足。对象：聚居地 1。；可用容量不足。对象：库存 2。", message);
        Assert.IsFalse(message!.Contains("insufficient_"));
        Assert.IsFalse(message.Contains("quote:buy"));
        var log = logging.Memory.Snapshot().Single(item => item.EventId == "train_command_failed");
        Assert.AreEqual(message, log.Message);
        AssertProblem(log.Attributes, 0, money);
        AssertProblem(log.Attributes, 1, capacity);
    }

    [TestMethod]
    public void FaultTextOmitsCodeAndField()
    {
        var fault = new CoreFault("internal_error", new(1, 2), "advance", "inventory", new WorldObjectId.Train(new(3)));
        var message = CommandDiagnostics.Describe(fault);
        Assert.AreEqual("核心模拟发生故障。", message);
        Assert.IsFalse(message.Contains(fault.Code));
        Assert.IsFalse(message.Contains(fault.Field));
        Assert.IsFalse(message.Contains(fault.Operation));
    }

    private static void AssertProblem(IReadOnlyDictionary<string, object?> attributes, int index, CoreProblem problem)
    {
        var recorded = (IReadOnlyDictionary<string, object?>)((IReadOnlyList<object?>)attributes["problems"]!)[index]!;
        var expected = CommandDiagnostics.ProblemRecord(problem);
        Assert.AreEqual(expected["code"], recorded["code"]);
        Assert.AreEqual(expected["field"], recorded["field"]);
        Assert.AreEqual(expected["subject"], recorded["subject"]);
        CollectionAssert.AreEqual(((string[])expected["related"]!).ToArray(),
            ((IReadOnlyList<object?>)recorded["related"]!).Cast<string>().ToArray());
    }

    private static FixedContentView Content(GoodId first, string firstName, GoodId second, string secondName, GoodId third, string thirdName) => new()
    {
        Goods = new Dictionary<GoodId, ContentLookup<GoodPresentation>>
        {
            [first] = Present(first, firstName),
            [second] = Present(second, secondName),
            [third] = Present(third, thirdName)
        }
    };

    private static ContentLookup<GoodPresentation> Present(GoodId good, string name) =>
        new ContentLookup<GoodPresentation>.Found(new(good, new(new(name, ""), null, null, false), null, false));

    private static readonly (string Code, string Sentence)[] Sentences =
    [
        ("editing_active", "请先结束设计编辑。"),
        ("busy", "当前操作尚未结束，请稍后再试。"),
        ("simulation_session_unavailable", "没有活动模拟会话。"),
        ("content_changed", "对象所属对局已更换，操作未提交。"),
        ("frozen", "核心模拟已冻结，不能执行此操作。"),
        ("no_route", "没有可达路径。"),
        ("object_missing", "对象已不存在。"),
        ("content_missing", "所需内容缺失。"),
        ("invalid_state", "状态不符合操作要求。"),
        ("invalid_design", "设计不能用于此操作。"),
        ("node_occupied", "节点已被占据。"),
        ("plot_occupied", "地块已被占用。"),
        ("conditions_not_met", "条件未满足。"),
        ("not_owned", "对象不属于玩家。"),
        ("instance_locked", "对象已锁定。"),
        ("instance_destroyed", "对象已毁坏。"),
        ("train_not_stationary_at_node", "列车未停在该节点。"),
        ("access_denied", "没有访问权限。"),
        ("insufficient_goods", "货物不足。"),
        ("insufficient_capacity", "可用容量不足。"),
        ("insufficient_money", "资金不足。"),
        ("price_condition_failed", "当前价格不满足已确认的总额条件。"),
        ("identity_exhausted", "对象或命令编号已耗尽。"),
        ("unsupported_format", "不支持此格式。"),
        ("incompatible_replay", "复现资料与当前运行环境不兼容。"),
        ("generation_failed", "世界生成失败。"),
        ("output_blocked", "产出无法进入库存。"),
        ("inputs_missing", "原料或可采储量不足。"),
        ("power_unavailable", "电力不足。"),
        ("labor_unavailable", "劳动力不足。"),
        ("platform_required", "列车未在站台停稳。"),
        ("platform_wait", "正在等待空闲站台。"),
        ("track_wait", "正在等待可进入的铁轨。"),
        ("control_missing", "列车没有可用的控制。"),
        ("traction_missing", "列车没有可用牵引力。"),
        ("overloaded", "列车过重，无法起步。"),
        ("train_not_at_settlement", "列车不在该聚居地。"),
        ("capability_zero", "设施当前没有可用能力。"),
        ("restock_shortage", "当地备货将低于补货目标。"),
        ("insufficient_demand", "买方需求不足。"),
        ("vehicles_not_colocated", "车辆不在同一地点。"),
        ("vehicle_in_train", "车辆已编入列车。"),
        ("vehicle_duplicate", "编组中有重复车辆。"),
        ("composition_empty", "编组不能为空。"),
        ("plan_empty", "运行计划没有停靠点。"),
        ("plan_degenerate", "循环计划的停靠不能全部相同。"),
        ("plan_duplicate_good", "同一停靠的货物重复。"),
        ("plan_missing", "列车没有运行计划。"),
        ("plan_completed", "单次计划已经跑完。"),
        ("settlement_unavailable", "列车不在可解编的聚居地。"),
        ("inventory_not_empty", "列车库存未清空。"),
        ("duplicate_source", "材料来源重复。"),
        ("not_for_sale", "这辆车不出售。"),
        ("replay_read_only", "正在重放，不能修改。")
    ];

    private sealed record FutureObject : WorldObjectId
    {
        internal FutureObject() : base(new WorldObjectId.Inventory(new(0))) { }
    }
}
