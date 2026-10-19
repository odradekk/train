using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.App;
using Train.App.Notifications;
using Train.Core.Contracts;
using Train.Core.ContractSupport;

namespace Train.Godot.Tests.Host;

[TestClass]
public sealed class CombatNotificationTests
{
    [TestMethod]
    public void Production_policy_stops_the_rest_of_a_four_times_frame_and_slows_to_one()
    {
        var policy = new NotificationPolicy();
        using var fixture = new HostFixture(policy.Decide);
        var session = fixture.ReadyHost();
        Queue(session, 1, new CoreEvent.CombatStarted(new(1), new WorldLocation.Node(new(1)), []));
        Queue(session, 2, null);
        fixture.Host.SetSpeed(SimulationSpeed.Four);
        fixture.Host.SetPaused(false);
        fixture.Host.ProcessFrame(SimulationTime.StepSeconds);
        var status = fixture.Host.GetStatus()!;
        Assert.AreEqual(1, session.AdvanceCount);
        Assert.AreEqual(SimulationSpeed.One, status.Speed);
        Assert.IsFalse(status.PlayerPaused);
        fixture.AssertNoUnexpectedFailures();
    }

    [TestMethod]
    public void Encounter_set_to_pause_does_not_run_the_next_step()
    {
        var policy = new NotificationPolicy();
        policy.SetAction(NotificationCategory.Encounter, NotificationAction.Pause);
        using var fixture = new HostFixture(policy.Decide);
        var session = fixture.ReadyHost();
        Queue(session, 1, new CoreEvent.CombatJoined(new(1), new CombatUnitId.Instance(new InstanceId.Vehicle(new(7)))));
        Queue(session, 2, null);
        fixture.Host.SetSpeed(SimulationSpeed.Four);
        fixture.Host.SetPaused(false);
        fixture.Host.ProcessFrame(SimulationTime.StepSeconds);
        fixture.Host.ProcessFrame(SimulationTime.StepSeconds);
        var status = fixture.Host.GetStatus()!;
        Assert.AreEqual(1, session.AdvanceCount);
        Assert.IsTrue(status.PlayerPaused);
        Assert.AreEqual(SimulationSpeed.Four, status.Speed);
        fixture.AssertNoUnexpectedFailures();
    }

    private static void Queue(ScriptedCoreSession session, ulong step, CoreEvent? item)
    {
        ImmutableArray<EventEnvelope> events = item is null ? [] : [new(new(step, 0), 0, item)];
        session.QueueAdvance(new(1, new(step, 0), events, null, null), new FixedCoreReadView(new(step, 0), HostCoreScenario.Input));
    }
}
