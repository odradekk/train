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
        StringAssert.Contains(message, subject.ToString());
        foreach (var identity in problem.RelatedIdentities) StringAssert.Contains(message, identity);
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
}
