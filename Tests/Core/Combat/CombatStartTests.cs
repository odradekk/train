using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.Sessions;
using Train.Core.Tests.World;
using Train.Core.World;

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
        var expected = new[]
        {
            new InstanceId.Vehicle(Train(session, train).Vehicles.Single()),
            Station(session, hostile),
            ArmedBuilding(session, hostile)
        };
        CollectionAssert.AreEquivalent(expected,
            view.Participants.Select(participant => ((CombatUnitId.Instance)participant.Unit).Id).ToArray());
        var location = ((Lookup<LocationView>.Found)session.Read().FindLocation(combat.Location)).Value;
        CollectionAssert.Contains(location.Combats.ToArray(), combat.Id);
        Assert.AreEqual(3, view.Participants.Length);
        Assert.AreEqual(TrainActivity.Combat, Train(session, train).Activity);
        var started = events.OfType<CoreEvent.CombatStarted>().Single();
        Assert.AreEqual(combat.Id, started.Combat);
        CollectionAssert.AreEquivalent(expected,
            started.Participants.Select(participant => ((CombatUnitId.Instance)participant).Id).ToArray());
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

    [TestMethod]
    public void Combat_end_returns_an_unretreated_train_to_its_plan()
    {
        using var session = CombatFixture.SessionWithContent(FastContent(100, 10));
        var hostile = CombatFixture.HostileNode(session);
        var train = new TrainId(1);
        var start = CombatFixture.StartNode(session, train);
        CombatFixture.Accepted(session.Submit(new CoreCommand.ReplaceRunPlan(train,
            new(false, [new PlanStop.Waypoint(hostile), new PlanStop.Waypoint(start)])), CommandSource.AutomationTest));
        CombatFixture.Accepted(session.Submit(new CoreCommand.StartTrain(train), CommandSource.AutomationTest));
        CoreEvent[] batch = [];
        for (var step = 0; step < 1000 && batch.Length == 0; step++)
        {
            var result = session.Advance(1);
            Assert.IsNull(result.Fault);
            var events = result.Events.Select(envelope => envelope.Event).ToArray();
            if (events.OfType<CoreEvent.CombatEnded>().Any()) batch = events;
        }
        Assert.IsNotEmpty(batch);
        var names = batch.Select(value => value.GetType().Name).ToArray();
        var endedIndex = Array.IndexOf(names, nameof(CoreEvent.CombatEnded));
        var resumedIndex = endedIndex < 0 ? -1 : Array.IndexOf(names, nameof(CoreEvent.TrainActivityChanged), endedIndex + 1);
        Assert.IsTrue(endedIndex >= 0 && resumedIndex > endedIndex);
        var resumed = batch.OfType<CoreEvent.TrainActivityChanged>().Last();
        Assert.AreEqual(TrainActivity.Combat, resumed.Before);
        Assert.AreEqual(TrainActivity.Running, resumed.After);
        Assert.IsTrue(batch.OfType<CoreEvent.TrainActivityChanged>().Any(value =>
            value.After == TrainActivity.Combat));

        CombatFixture.AdvanceUntil(session, view => view.World.Combats.IsEmpty
            && view.FindTrain(train) is Lookup<TrainView>.Found { Value.Position: TrainPosition.Node node }
            && node.Id == start, 2000);
        Assert.IsEmpty(session.Read().World.Combats);
    }

    [TestMethod]
    public void Combat_freezes_and_resumes_station_dwell()
    {
        using var baseSession = CombatFixture.SessionWithContent(FastContent(60, 100, structureOnly: true));
        var hostile = CombatFixture.HostileNode(baseSession);
        var train = new TrainId(1);
        var start = CombatFixture.StartNode(baseSession, train);
        using var session = DockAtHostile(baseSession, hostile, TrainActivity.Servicing, 120,
            new(false, [new PlanStop.Station(hostile, 120, []), new PlanStop.Waypoint(start)]));
        CombatFixture.AdvanceUntil(session, view => view.World.Combats.Any(), 2000);
        Assert.AreEqual(TrainActivity.Combat, Train(session, train).Activity);
        Assert.AreEqual(119UL, Train(session, train).RemainingDwellSteps);
        var quiet = session.Advance(30);
        Assert.IsNull(quiet.Fault);
        Assert.AreEqual(TrainActivity.Combat, Train(session, train).Activity);
        Assert.AreEqual(119UL, Train(session, train).RemainingDwellSteps);
        var ended = session.Advance(30);
        Assert.IsNull(ended.Fault);
        Assert.IsTrue(ended.Events.Select(envelope => envelope.Event).OfType<CoreEvent.CombatEnded>().Any());
        Assert.AreEqual(TrainActivity.Servicing, Train(session, train).Activity);
        Assert.AreEqual(119UL, Train(session, train).RemainingDwellSteps);
        session.Advance(1);
        Assert.AreEqual(118UL, Train(session, train).RemainingDwellSteps);
        CombatFixture.AdvanceUntil(session, view => view.FindTrain(train) is
            Lookup<TrainView>.Found { Value.Position: TrainPosition.Node node } && node.Id == start, 3000);
    }

    [TestMethod]
    public void Stopped_participant_is_rejected_by_participation_not_activity()
    {
        using var baseSession = CombatFixture.Session(damage: 1);
        var hostile = CombatFixture.HostileNode(baseSession);
        var train = new TrainId(1);
        using var session = DockAtHostile(baseSession, hostile, TrainActivity.Stopped, 0, null);
        CombatFixture.AdvanceUntil(session, view => view.World.Combats.Any());
        Assert.AreEqual(TrainActivity.Stopped, Train(session, train).Activity);
        var result = (SubmissionResult.Completed)session.Submit(new CoreCommand.StartTrain(train),
            CommandSource.AutomationTest);
        var rejected = (CommandDecision.Rejected)result.Receipt.Decision;
        Assert.AreEqual("invalid_state", rejected.Problems.Single().Code);
        Assert.AreEqual("Train.Activity", rejected.Problems.Single().Field);
        Assert.IsNull(session.Read().Fault);
    }

    private static CoreSession DockAtHostile(CoreSession source, NodeId hostile, TrainActivity activity,
        ulong dwell, RunPlan? plan)
    {
        var state = source.State;
        var train = state.World.Trains.Single();
        var node = ((Lookup<NodeView>.Found)source.Read().FindNode(hostile)).Value;
        var stop = node.ApproachLengthMeters + node.PlatformLengthMeters;
        var world = state.World with
        {
            Trains =
            [
                train with
                {
                    Position = new TrainPosition.Node(hostile, null, null, null, null,
                        NodeTravelPhase.Docked, stop, 1),
                    Activity = activity, Plan = plan, NextStopIndex = plan is null ? null : 0,
                    RemainingDwellSteps = dwell, SpeedMetersPerSecond = 0
                }
            ]
        };
        return new CoreSession(state with { World = world });
    }

    private static CoreContentProjection FastContent(long vehicleDamage, long buildingStructure, bool structureOnly = false)
        => CombatFixture.Content(damage: 1) with
        {
            Designs = CombatFixture.Content(damage: 1).Designs.Select(design =>
                design.Design == CombatFixture.ArmedVehicle ? design with
                {
                    Parts = design.Parts.Select(part => part with
                    {
                        Capabilities = part.Capabilities.Select(capability => capability is PartCapability.Weapon
                            ? (PartCapability)new PartCapability.Weapon(vehicleDamage, 1) : capability).ToImmutableArray()
                    }).ToImmutableArray()
                } : design.Design == CombatFixture.ArmedBuilding ? design with
                {
                    MaxStructureDurability = buildingStructure, StructureHitWeight = 1,
                    Parts = design.Parts.Select(part => part with
                    {
                        HitWeight = structureOnly ? 0 : part.HitWeight,
                        Capabilities = [new PartCapability.Weapon(1, 1)]
                    }).ToImmutableArray()
                } : design).ToImmutableArray()
        };

    private static CombatView Combat(ICoreSession session, CombatId id)
        => ((Lookup<CombatView>.Found)session.Read().FindCombat(id)).Value;

    private static InstanceId Station(ICoreSession session, NodeId node)
        => Instance(session, node, WorldFixture.Station);

    private static InstanceId ArmedBuilding(ICoreSession session, NodeId node)
        => Instance(session, node, CombatFixture.ArmedBuilding);

    private static InstanceId Instance(ICoreSession session, NodeId node, DesignHash design)
        => ((Lookup<LocationView>.Found)session.Read().FindLocation(new WorldLocation.Node(node))).Value.Instances
            .Single(instance => ((Lookup<InstanceView>.Found)session.Read().FindInstance(instance)).Value.Design == design);

    private static TrainView Train(ICoreSession session, TrainId id)
        => ((Lookup<TrainView>.Found)session.Read().FindTrain(id)).Value;
}
