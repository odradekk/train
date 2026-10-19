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
        foreach (var code in Codes)
        {
            var message = CommandDiagnostics.Describe(new CoreProblem(code, "field", null, []));
            Assert.AreNotEqual("无法完成此操作。", message, code);
            Assert.IsFalse(message.Contains(code), code);
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
        Assert.AreEqual("资金不足。对象：聚居地 1。\n可用容量不足。对象：库存 2。", message);
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

    private static readonly string[] Codes =
    [
        "editing_active", "busy", "simulation_session_unavailable", "content_changed", "frozen", "no_route",
        "object_missing", "content_missing", "invalid_state", "invalid_design", "node_occupied", "plot_occupied",
        "conditions_not_met", "not_owned", "instance_locked", "instance_destroyed", "train_not_stationary_at_node",
        "access_denied", "insufficient_goods", "insufficient_capacity", "insufficient_money", "price_condition_failed",
        "identity_exhausted", "unsupported_format", "incompatible_replay", "generation_failed", "output_blocked",
        "inputs_missing", "power_unavailable", "labor_unavailable", "platform_required", "platform_wait", "track_wait",
        "control_missing", "traction_missing", "overloaded", "train_not_at_settlement", "capability_zero",
        "restock_shortage", "insufficient_demand", "vehicles_not_colocated", "vehicle_in_train", "vehicle_duplicate",
        "composition_empty", "plan_empty", "plan_degenerate", "plan_duplicate_good", "plan_missing", "plan_completed",
        "settlement_unavailable", "inventory_not_empty", "duplicate_source", "not_for_sale", "replay_read_only"
    ];

    private sealed record FutureObject : WorldObjectId
    {
        internal FutureObject() : base(new WorldObjectId.Inventory(new(0))) { }
    }
}
