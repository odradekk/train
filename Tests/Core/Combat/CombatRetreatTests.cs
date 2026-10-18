using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Combat;
using Train.Core.Contracts;
using Train.Core.Railway;
using Train.Core.Sessions;
using Train.Core.World;

namespace Train.Core.Tests.Combat;

[TestClass]
public sealed class CombatRetreatTests
{
    [TestMethod]
    public void Encounter_retreat_disengages_without_firing_and_returns_to_a_friendly_settlement()
    {
        var content = RetreatSeconds(CombatFixture.Content(damage: 1), 1);
        using var session = CombatFixture.SessionWithContent(content);
        var hostile = CombatFixture.HostileNode(session);
        var train = new TrainId(1);
        CombatFixture.Accepted(session.Submit(new CoreCommand.SetTrainEngagement(train,
            new(TargetPriority.ArmedFirst, TargetScope.All, EncounterAction.Retreat, 0)), CommandSource.AutomationTest));
        CombatFixture.Send(session, hostile, train);
        var events = CombatFixture.AdvanceUntilEvent(session, value => value is CoreEvent.CombatRetreatStarted);
        var started = events.OfType<CoreEvent.CombatRetreatStarted>().Single();
        Assert.AreEqual(60UL, started.DisengagementSteps);
        Assert.AreEqual(TrainActivity.Retreating, Train(session, train).Activity);
        Assert.IsTrue(events.OfType<CoreEvent.TrainActivityChanged>().Any(value =>
            value.Train == train && value.After == TrainActivity.Retreating));
        var participant = CombatFixture.Participant(session, started.Unit);
        Assert.AreEqual(60UL, participant.RemainingDisengagementSteps);
        Assert.IsTrue(participant.Retreating);
        session.Advance(1);
        Assert.AreEqual(59UL, CombatFixture.Participant(session, started.Unit).RemainingDisengagementSteps);

        var quiet = session.Advance(58);
        Assert.IsNull(quiet.Fault);
        Assert.IsFalse(quiet.Events.Select(envelope => envelope.Event).OfType<CoreEvent.CombatHits>().Any());
        var second = session.Advance(1);
        Assert.IsNull(second.Fault);
        var hits = second.Events.Select(envelope => envelope.Event).OfType<CoreEvent.CombatHits>().Single();
        Assert.IsFalse(hits.Hits.Any(hit => hit.Source is AttackSource.Weapon weapon
            && weapon.Instance.Instance is InstanceId.Vehicle));
        Assert.IsTrue(hits.Hits.Any(hit => hit.Source is AttackSource.Weapon weapon
            && weapon.Instance.Instance is InstanceId.Building));
        var waiting = Train(session, train);
        Assert.AreEqual(TrainActivity.Retreating, waiting.Activity);
        Assert.AreEqual(hostile, ((TrainPosition.Node)waiting.Position).Id);
    }

    [TestMethod]
    public void Retreat_completes_on_arrival_and_keeps_the_plan()
    {
        var content = RetreatSeconds(CombatFixture.Content(damage: 1), 1);
        using var session = CombatFixture.SessionWithContent(content);
        var hostile = CombatFixture.HostileNode(session);
        var train = new TrainId(1);
        CombatFixture.Accepted(session.Submit(new CoreCommand.SetTrainEngagement(train,
            new(TargetPriority.ArmedFirst, TargetScope.All, EncounterAction.Retreat, 0)), CommandSource.AutomationTest));
        CombatFixture.Send(session, hostile, train);
        CombatFixture.AdvanceUntilEvent(session, value => value is CoreEvent.CombatRetreatStarted);
        var before = Train(session, train);
        var events = CombatFixture.AdvanceUntil(session, view => view.World.Combats.IsEmpty
            && view.FindTrain(train) is Lookup<TrainView>.Found { Value.Activity: TrainActivity.Stopped }, 600);
        var completed = events.OfType<CoreEvent.RetreatCompleted>().Single(value => value.Train == train);
        Assert.AreNotEqual(hostile, completed.Node);
        var names = events.Select(value => value.GetType().Name).ToArray();
        var completedIndex = Array.LastIndexOf(names, nameof(CoreEvent.RetreatCompleted));
        var arrivedIndex = Array.LastIndexOf(names, nameof(CoreEvent.TrainArrived), completedIndex);
        Assert.IsTrue(arrivedIndex >= 0 && arrivedIndex < completedIndex);
        Assert.AreEqual(completed.Node, events.OfType<CoreEvent.TrainArrived>().Last().Node);
        var location = ((Lookup<LocationView>.Found)session.Read().FindLocation(
            new WorldLocation.Node(completed.Node))).Value;
        var occupant = (NodeOccupant.Settlement)location.Occupant;
        Assert.AreNotEqual(SettlementRelation.Hostile, occupant.Relation);
        var after = Train(session, train);
        Assert.AreEqual(TrainActivity.Stopped, after.Activity);
        Assert.AreEqual(before.Plan, after.Plan);
        Assert.AreEqual(before.NextStopIndex, after.NextStopIndex);
    }

    [TestMethod]
    public void Repeated_retreat_is_accepted_without_changing_the_countdown()
    {
        var content = RetreatSeconds(CombatFixture.Content(damage: 1), 1);
        using var session = CombatFixture.SessionWithContent(content);
        var hostile = CombatFixture.HostileNode(session);
        var train = new TrainId(1);
        CombatFixture.Accepted(session.Submit(new CoreCommand.SetTrainEngagement(train,
            new(TargetPriority.ArmedFirst, TargetScope.All, EncounterAction.Retreat, 0)), CommandSource.AutomationTest));
        CombatFixture.Send(session, hostile, train);
        var started = CombatFixture.AdvanceUntilEvent(session, value => value is CoreEvent.CombatRetreatStarted)
            .OfType<CoreEvent.CombatRetreatStarted>().Single();
        var remaining = CombatFixture.Participant(session, started.Unit).RemainingDisengagementSteps;
        var result = (SubmissionResult.Completed)session.Submit(new CoreCommand.Retreat(train),
            CommandSource.AutomationTest);
        Assert.IsInstanceOfType<CommandDecision.Accepted>(result.Receipt.Decision);
        Assert.IsEmpty(result.Events);
        Assert.AreEqual(remaining, CombatFixture.Participant(session, started.Unit).RemainingDisengagementSteps);
    }

    [TestMethod]
    public void Threshold_retreats_only_strictly_below_the_survivor_structure_ratio()
    {
        var content = RetreatSeconds(ThresholdContent(buildingDamage: 10), 1);
        using var session = CombatFixture.SessionWithContent(content);
        var hostile = CombatFixture.HostileNode(session);
        var train = new TrainId(1);
        CombatFixture.Accepted(session.Submit(new CoreCommand.SetTrainEngagement(train,
            new(TargetPriority.ArmedFirst, TargetScope.All, EncounterAction.Engage, .9)), CommandSource.AutomationTest));
        CombatFixture.Send(session, hostile, train);
        var first = CombatFixture.AdvanceUntilEvent(session, value => value is CoreEvent.CombatHits);
        Assert.IsFalse(first.OfType<CoreEvent.CombatRetreatStarted>().Any());
        var instance = Instance(session, new InstanceId.Vehicle(Train(session, train).Vehicles.Single()));
        Assert.AreEqual(90, instance.Structure.EffectiveCurrent);
        Assert.AreEqual(TrainActivity.Combat, Train(session, train).Activity);
        var second = session.Advance(60);
        Assert.IsNull(second.Fault);
        var events = second.Events.Select(envelope => envelope.Event).ToArray();
        Assert.IsTrue(events.OfType<CoreEvent.CombatRetreatStarted>().Any());
        Assert.AreEqual(80, Instance(session,
            new InstanceId.Vehicle(Train(session, train).Vehicles.Single())).Structure.EffectiveCurrent);
    }

    [TestMethod]
    public void Threshold_ignores_destroyed_vehicles_when_measuring_the_survivors()
    {
        var content = RetreatSeconds(ThresholdContent(buildingDamage: 100), 1);
        using var session = CombatFixture.SessionWithContent(content, vehicles: 2);
        var hostile = CombatFixture.HostileNode(session);
        var train = new TrainId(1);
        CombatFixture.Accepted(session.Submit(new CoreCommand.SetTrainEngagement(train,
            new(TargetPriority.ArmedFirst, TargetScope.All, EncounterAction.Engage, .9)), CommandSource.AutomationTest));
        CombatFixture.Send(session, hostile, train);
        var events = CombatFixture.AdvanceUntilEvent(session, value => value is CoreEvent.TrainCompositionChanged changed
            && changed.Train == train && changed.Vehicles.Length == 1);
        Assert.IsFalse(events.OfType<CoreEvent.CombatRetreatStarted>().Any());
        Assert.AreEqual(TrainActivity.Combat, Train(session, train).Activity);
        Assert.AreEqual(1, Train(session, train).Vehicles.Length);
        var survivor = Instance(session,
            new InstanceId.Vehicle(Train(session, train).Vehicles.Single()));
        Assert.AreEqual(100, survivor.Structure.EffectiveCurrent);
    }

    [TestMethod]
    public void Ending_the_combat_finishes_disengagement_and_the_train_leaves()
    {
        var content = ThresholdContent(buildingDamage: 1) with
        {
            Designs = ThresholdContent(buildingDamage: 1).Designs.Select(design =>
                design.Design == CombatFixture.ArmedVehicle ? design with
                {
                    Parts = design.Parts.Select(part => part with
                    {
                        Capabilities = part.Capabilities.Select(capability => capability is PartCapability.Weapon
                            ? (PartCapability)new PartCapability.Weapon(100, 1) : capability).ToImmutableArray()
                    }).ToImmutableArray()
                } : design.Design == CombatFixture.ArmedBuilding ? design with
                {
                    MaxStructureDurability = 10,
                    Parts = design.Parts.Select(part => part with
                    {
                        MaxDurability = 10, Capabilities = [new PartCapability.Weapon(1, 1)]
                    }).ToImmutableArray()
                } : design).ToImmutableArray()
        };
        using var session = CombatFixture.SessionWithContent(content, trains: 2);
        var hostile = CombatFixture.HostileNode(session);
        var retreating = new TrainId(1);
        var attacker = new TrainId(2);
        CombatFixture.Accepted(session.Submit(new CoreCommand.SetTrainEngagement(retreating,
            new(TargetPriority.ArmedFirst, TargetScope.All, EncounterAction.Retreat, 0)), CommandSource.AutomationTest));
        CombatFixture.Send(session, hostile, retreating);
        CombatFixture.AdvanceUntilEvent(session, value => value is CoreEvent.CombatRetreatStarted);
        CombatFixture.Send(session, hostile, attacker);
        var ended = CombatFixture.AdvanceUntilEvent(session, value => value is CoreEvent.CombatEnded, 600);
        Assert.IsTrue(ended.OfType<CoreEvent.CombatEnded>().Any());
        Assert.AreEqual(TrainActivity.Retreating, Train(session, retreating).Activity);
        Assert.IsNotNull(session.State.World.Trains.Single(item => item.Id == retreating).RetreatDestination);
        foreach (var command in new CoreCommand[]
                 {
                     new CoreCommand.StartTrain(retreating), new CoreCommand.StopTrain(retreating)
                 })
        {
            var rejected = (CommandDecision.Rejected)((SubmissionResult.Completed)session.Submit(command,
                CommandSource.AutomationTest)).Receipt.Decision;
            Assert.AreEqual("invalid_state", rejected.Problems.Single().Code);
        }
        var steps = 0;
        CoreEvent.RetreatCompleted? completed = null;
        while (steps < 800 && completed is null)
        {
            var result = session.Advance(1);
            Assert.IsNull(result.Fault);
            steps++;
            completed = result.Events.Select(envelope => envelope.Event).OfType<CoreEvent.RetreatCompleted>()
                .FirstOrDefault(value => value.Train == retreating);
        }
        Assert.IsNotNull(completed);
        Assert.AreEqual(TrainActivity.Stopped, Train(session, retreating).Activity);
    }

    [TestMethod]
    public void Retreat_without_a_destination_waits_and_reports_no_route_until_one_appears()
    {
        var content = RetreatSeconds(CombatFixture.Content(damage: 1), .05);
        using var session = CombatFixture.SessionWithContent(content);
        var hostile = CombatFixture.HostileNode(session);
        var train = new TrainId(1);
        CombatFixture.Accepted(session.Submit(new CoreCommand.SetTrainEngagement(train,
            new(TargetPriority.ArmedFirst, TargetScope.All, EncounterAction.Retreat, 0)), CommandSource.AutomationTest));
        CombatFixture.Send(session, hostile, train);
        CombatFixture.AdvanceUntilEvent(session, value => value is CoreEvent.CombatRetreatStarted);
        var state = session.State;
        var friendly = state.World.Settlements.First(settlement => settlement.Relation != SettlementRelation.Hostile);
        var world = state.World with
        {
            Settlements = state.World.Settlements.Select(settlement => settlement.Relation == SettlementRelation.Hostile
                ? settlement : settlement with { Relation = SettlementRelation.Hostile }).ToImmutableArray()
        };
        var combat = world.Combats.Single();
        var index = -1;
        for (var position = 0; position < combat.Controllers.Length; position++)
            if (combat.Controllers[position].Controller is CombatController.Train) { index = position; break; }
        Assert.IsTrue(index >= 0);
        world = world with
        {
            Combats = world.Combats.Select(item => item.Id != combat.Id ? item : item with
            {
                Controllers = item.Controllers.SetItem(index,
                    item.Controllers[index] with { RemainingDisengagementSteps = 0 })
            }).ToImmutableArray()
        };
        var step = state.Position.CompletedSteps + 1;
        var advanced = CombatOperations.Advance(world, state.Content, step);
        var waiting = advanced.World.Trains.Single(item => item.Id == train);
        Assert.AreEqual(TrainActivity.Retreating, waiting.Activity);
        Assert.IsNull(waiting.RetreatDestination);
        Assert.IsTrue(TrainOperations.Read(advanced.World, state.Content, waiting).Blockers
            .Any(problem => problem.Code == "no_route"));
        var retried = CombatOperations.Advance(advanced.World with
        {
            Settlements = advanced.World.Settlements.Select(settlement => settlement.Id == friendly.Id
                ? settlement with { Relation = friendly.Relation } : settlement).ToImmutableArray()
        }, state.Content, step + 1);
        Assert.AreEqual(friendly.Node, retried.World.Trains.Single(item => item.Id == train).RetreatDestination);
    }

    private static CoreContentProjection RetreatSeconds(CoreContentProjection content, double seconds)
        => content with { CombatRules = [content.CombatRules[0] with { RetreatSeconds = seconds }] };

    private static CoreContentProjection ThresholdContent(long buildingDamage)
        => CombatFixture.Content(damage: 10) with
        {
            Designs = CombatFixture.Content(damage: 10).Designs.Select(design =>
                design.Design == CombatFixture.ArmedVehicle ? design with
                {
                    StructureHitWeight = 1,
                    Parts = design.Parts.Select(part => part with { HitWeight = 0 }).ToImmutableArray()
                } : design.Design == CombatFixture.ArmedBuilding ? design with
                {
                    MaxStructureDurability = 1000,
                    Parts = design.Parts.Select(part => part with
                    {
                        MaxDurability = 1000, HitWeight = 0,
                        Capabilities = [new PartCapability.Weapon(buildingDamage, 1)]
                    }).ToImmutableArray()
                } : design).ToImmutableArray()
        };

    private static TrainView Train(ICoreSession session, TrainId id)
        => ((Lookup<TrainView>.Found)session.Read().FindTrain(id)).Value;

    private static InstanceView Instance(ICoreSession session, InstanceId id)
        => ((Lookup<InstanceView>.Found)session.Read().FindInstance(id)).Value;
}
