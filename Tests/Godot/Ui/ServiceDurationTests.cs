using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using Train.Ui;

namespace Train.Godot.Tests.Ui;

[TestClass]
public sealed class ServiceDurationTests
{
    [TestMethod]
    public void Short_duration_rounds_up_to_whole_seconds()
    {
        var view = View(0);
        Assert.AreEqual("1 秒", EconomyPanels.ServiceDuration(view, .1));
        Assert.AreEqual("59 秒", EconomyPanels.ServiceDuration(view, 59));
    }

    [TestMethod]
    public void Long_duration_shows_game_completion_time_across_midnight()
    {
        Assert.AreEqual("第1天 00:01", EconomyPanels.ServiceDuration(View(0), 60));
        Assert.AreEqual("第2天 00:01", EconomyPanels.ServiceDuration(View(24 * 60 * 60 - 30), 90));
    }

    [TestMethod]
    public void Job_without_actual_work_rate_has_no_completion_estimate()
    {
        var job = new ConstructionJobView(new(1), ConstructionKind.Repair, new(1),
            new(new(1), CapabilityKind.Construction),
            new ConstructionTarget.Existing(new InstanceId.Vehicle(new(1)), new("test-design")),
            ConstructionState.ResourceBlocked, 0, 100, 20, .2, 0, [], [], []);
        Assert.AreEqual("当前未施工", EconomyPanels.ServiceRemainingTime(job, View(0)));
    }

    private static ICoreReadView View(ulong seconds) =>
        new FixedCoreReadView(new(seconds * (ulong)SimulationTime.StepsPerSecond, 0), HostCoreScenario.Input);
}
