using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.Sessions;

namespace Train.Core.Tests.Combat;

[TestClass]
public sealed class CombatStartTests
{
    [TestMethod]
    public void Armed_train_reaches_a_hostile_node_and_starts_one_combat()
    {
        using var session = CombatFixture.Session();
        var hostile = CombatFixture.HostileNode(session);
        var train = new TrainId(1);
        CombatFixture.Send(session, hostile, train);

        var events = CombatFixture.AdvanceUntil(session, view => view.World.Combats.Any());
        var combat = session.Read().World.Combats.Single();
        Assert.AreEqual(new WorldLocation.Node(hostile), combat.Location);
        Assert.AreEqual(1u, combat.Id.Value);
        var view = Combat(session, combat.Id);
        Assert.IsTrue(view.Participants.Length >= 2);
        Assert.IsTrue(view.Participants.All(participant => participant.Unit is CombatUnitId.Instance));
        var location = ((Lookup<LocationView>.Found)session.Read().FindLocation(combat.Location)).Value;
        CollectionAssert.Contains(location.Combats.ToArray(), combat.Id);
        Assert.AreEqual(location.Instances.Length, view.Participants.Length);
        Assert.AreEqual(TrainActivity.Combat, Train(session, train).Activity);
        var started = events.OfType<CoreEvent.CombatStarted>().Single();
        Assert.AreEqual(combat.Id, started.Combat);
        Assert.AreEqual(view.Participants.Length, started.Participants.Length);
    }

    [TestMethod]
    public void A_hostile_settlement_without_defence_does_not_start_combat()
    {
        using var session = CombatFixture.Session(armedBuilding: false);
        var hostile = CombatFixture.HostileNode(session);
        var train = new TrainId(1);
        CombatFixture.Send(session, hostile, train);
        CombatFixture.AdvanceUntil(session, view => Train(session, train).Position is TrainPosition.Node node
            && node.Id == hostile);
        Assert.IsEmpty(session.Read().World.Combats);
        var result = session.Advance(30);
        Assert.IsNull(result.Fault);
        Assert.IsEmpty(session.Read().World.Combats);
    }

    [TestMethod]
    public void A_later_train_joins_the_active_combat_at_the_node()
    {
        using var session = CombatFixture.Session(trains: 2);
        var hostile = CombatFixture.HostileNode(session);
        var first = new TrainId(1);
        var second = new TrainId(2);
        CombatFixture.Send(session, hostile, first);
        CombatFixture.AdvanceUntil(session, view => view.World.Combats.Any());
        var combat = session.Read().World.Combats.Single();
        CombatFixture.Send(session, hostile, second);
        var secondVehicles = Train(session, second).Vehicles;
        var events = CombatFixture.AdvanceUntil(session,
            view => Combat(session, combat.Id).Participants.Any(participant =>
                participant.Unit is CombatUnitId.Instance { Id: InstanceId.Vehicle vehicle }
                && secondVehicles.Contains(vehicle.Id)));
        Assert.AreEqual(1, session.Read().World.Combats.Length);
        var joined = events.OfType<CoreEvent.CombatJoined>()
            .Where(item => item.Combat == combat.Id).Select(item => item.Participant).ToArray();
        Assert.IsTrue(secondVehicles.All(vehicle => joined.Contains(new CombatUnitId.Instance(
            new InstanceId.Vehicle(vehicle)))));
        Assert.AreEqual(TrainActivity.Combat, Train(session, second).Activity);
    }

    [TestMethod]
    public void Participating_train_rejects_movement_and_plan_commands()
    {
        using var session = CombatFixture.Session();
        var hostile = CombatFixture.HostileNode(session);
        var train = new TrainId(1);
        CombatFixture.Send(session, hostile, train);
        CombatFixture.AdvanceUntil(session, view => view.World.Combats.Any());
        var plan = new RunPlan(false, [new PlanStop.Waypoint(CombatFixture.StartNode(session, train))]);
        foreach (var command in new CoreCommand[]
        {
            new CoreCommand.StartTrain(train),
            new CoreCommand.StopTrain(train),
            new CoreCommand.ReplaceRunPlan(train, plan),
            new CoreCommand.DisbandTrain(train)
        })
        {
            var result = (SubmissionResult.Completed)session.Submit(command, CommandSource.AutomationTest);
            var rejected = (CommandDecision.Rejected)result.Receipt.Decision;
            Assert.AreEqual("invalid_state", rejected.Problems.Single().Code);
            Assert.AreEqual("Train.Activity", rejected.Problems.Single().Field);
        }
        Assert.IsNull(session.Read().Fault);
    }

    [TestMethod]
    public void Retreat_command_emits_the_activity_change()
    {
        using var session = CombatFixture.Session(damage: 1);
        var hostile = CombatFixture.HostileNode(session);
        var train = new TrainId(1);
        CombatFixture.Send(session, hostile, train);
        CombatFixture.AdvanceUntil(session, view => view.World.Combats.Any());
        var result = (SubmissionResult.Completed)session.Submit(new CoreCommand.Retreat(train), CommandSource.AutomationTest);
        Assert.IsInstanceOfType<CommandDecision.Accepted>(result.Receipt.Decision);
        var changed = result.Events.Select(envelope => envelope.Event).OfType<CoreEvent.TrainActivityChanged>()
            .Single(value => value.Train == train);
        Assert.AreEqual(TrainActivity.Combat, changed.Before);
        Assert.AreEqual(TrainActivity.Retreating, changed.After);
        Assert.IsTrue(result.Events.Select(envelope => envelope.Event).OfType<CoreEvent.CombatRetreatStarted>()
            .Any(value => value.Unit is CombatUnitId.Instance));
    }

    private static CombatView Combat(ICoreSession session, CombatId id)
        => ((Lookup<CombatView>.Found)session.Read().FindCombat(id)).Value;

    private static TrainView Train(ICoreSession session, TrainId id)
        => ((Lookup<TrainView>.Found)session.Read().FindTrain(id)).Value;
}
