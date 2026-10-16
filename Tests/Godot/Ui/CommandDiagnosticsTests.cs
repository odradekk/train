using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.App;
using Train.Core.Contracts;
using Train.Godot.Tests.Logging;

namespace Train.Godot.Tests.Ui;

[TestClass]
public sealed class CommandDiagnosticsTests
{
    [TestMethod]
    public void UnknownProblemPreservesCodeFieldSubjectAndRelatedIdentities()
    {
        var subject = new WorldObjectId.Inventory(new(7));
        var problem = new CoreProblem("future_economy_problem", "quote.total", subject, ["good:coal", "lease:9"]);
        var message = CommandDiagnostics.Describe(problem);
        StringAssert.Contains(message, problem.Code);
        StringAssert.Contains(message, problem.Field);
        StringAssert.Contains(message, subject.Id.Value.ToString());
        Assert.IsFalse(message.Contains(subject.ToString()));
        foreach (var identity in problem.RelatedIdentities) StringAssert.Contains(message, identity);
    }

    [TestMethod]
    public void EveryObjectKindHasADistinctReadableLabelAndPreservesItsNumber()
    {
        const ulong number = 1234567890;
        WorldObjectId[] subjects = [new WorldObjectId.Node(new(number)), new WorldObjectId.Edge(new(number)),
            new WorldObjectId.Train(new(number)), new WorldObjectId.Instance(new InstanceId.Vehicle(new(number))),
            new WorldObjectId.Instance(new InstanceId.Building(new(number))), new WorldObjectId.Settlement(new(number)),
            new WorldObjectId.Inventory(new(number)), new WorldObjectId.Job(new(number)), new WorldObjectId.Combat(new(number)),
            new WorldObjectId.Creature(new(number)), new WorldObjectId.CreatureGroup(new(number)), new WorldObjectId.Lair(new(number))];
        var messages = subjects.Select(subject => CommandDiagnostics.Describe(new CoreProblem("object_missing", "subject", subject, []))).ToArray();
        for (var index = 0; index < subjects.Length; index++)
        {
            StringAssert.Contains(messages[index], number.ToString());
            Assert.IsFalse(messages[index].Contains('{'));
            Assert.IsFalse(messages[index].Contains("Id ="));
            Assert.IsFalse(messages[index].Contains("Value ="));
            Assert.IsFalse(messages[index].Contains(subjects[index].GetType().Name));
        }
        Assert.AreEqual(subjects.Length, messages.Distinct().Count());
    }

    [TestMethod]
    public void UnknownObjectKindUsesReadableFallback()
    {
        var subject = new FutureObject();
        var message = CommandDiagnostics.Describe(new CoreProblem("object_missing", "subject", subject, []));
        StringAssert.Contains(message, "未知对象");
        Assert.IsFalse(message.Contains('{'));
        Assert.IsFalse(message.Contains(subject.GetType().Name));
    }

    [TestMethod]
    public void ProblemWithoutSubjectDoesNotInventAnObject()
    {
        var message = CommandDiagnostics.Describe(new CoreProblem("busy", "command", null, []));
        Assert.IsFalse(message.Contains("对象："));
        Assert.IsFalse(message.Contains("未知对象"));
    }

    [TestMethod]
    public void RejectedCommandReportsEveryReason()
    {
        using var logging = LoggerSession.CreateMemory();
        var money = new CoreProblem("insufficient_money", "wallet", new WorldObjectId.Settlement(new(1)), ["quote:buy"]);
        var capacity = new CoreProblem("insufficient_capacity", "inventory", new WorldObjectId.Inventory(new(2)), ["lease:3"]);
        var rejected = new SubmissionResult.Completed(new(5, new(0, 0), new(0, 0),
            new CommandDecision.Rejected([money, capacity])), [], null);
        var message = CommandDiagnostics.Report(logging.Logger, rejected);
        Assert.IsNotNull(message);
        StringAssert.Contains(message, CommandDiagnostics.Describe(money));
        StringAssert.Contains(message, CommandDiagnostics.Describe(capacity));
        var log = logging.Memory.Snapshot().Single(item => item.EventId == "train_command_failed");
        Assert.AreEqual(message, log.Message);
    }

    private sealed record FutureObject : WorldObjectId
    {
        internal FutureObject() : base(new WorldObjectId.Inventory(new(0))) { }
    }
}
