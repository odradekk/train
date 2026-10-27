using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Combat;
using Train.Core.Contracts;
using Train.Core.Railway;
using Train.Core.Sessions;
using Train.Core.Tests.Railway;
using Train.Core.Tests.World;
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
    public void Retreat_completes_on_arrival_at_the_nearest_candidate_and_keeps_the_plan()
    {
        var content = RetreatSeconds(CombatFixture.Content(damage: 1), 1);
        using var session = CombatFixture.SessionWithContent(content);
        var hostile = CombatFixture.HostileNode(session);
        var train = new TrainId(1);
        CombatFixture.Accepted(session.Submit(new CoreCommand.SetTrainEngagement(train,
            new(TargetPriority.ArmedFirst, TargetScope.All, EncounterAction.Retreat, 0)), CommandSource.AutomationTest));
        CombatFixture.Send(session, hostile, train);
        CombatFixture.AdvanceUntilEvent(session, value => value is CoreEvent.CombatRetreatStarted);
        var world = session.State.World;
        var speed = TrainOperations.Capabilities(world, session.State.Content, world.Trains.Single())
            .MaximumSpeedMetersPerSecond;
        var expected = world.Settlements.Where(settlement => settlement.Node != hostile
                && settlement.Relation is SettlementRelation.PlayerOwned or SettlementRelation.NonHostile)
            .Select(settlement => (settlement.Node,
                Route: TrainRouting.FindRoute(world.Nodes, world.Edges, hostile, settlement.Node, speed)))
            .Where(item => item.Route is not null)
            .Select(item => (item.Node, Time: item.Route!.Value.DistanceMeters / speed))
            .OrderBy(item => item.Time).ThenBy(item => item.Node.Value).First().Node;
        var before = Train(session, train);
        var events = CombatFixture.AdvanceUntil(session, view => view.World.Combats.IsEmpty
            && view.FindTrain(train) is Lookup<TrainView>.Found { Value.Activity: TrainActivity.Stopped }, 600);
        var completed = events.OfType<CoreEvent.RetreatCompleted>().Single(value => value.Train == train);
        Assert.AreEqual(expected, completed.Node);
        Assert.AreNotEqual(hostile, completed.Node);
        var names = events.Select(value => value.GetType().Name).ToArray();
        var completedIndex = Array.LastIndexOf(names, nameof(CoreEvent.RetreatCompleted));
        var arrivedIndex = Array.LastIndexOf(names, nameof(CoreEvent.TrainArrived), completedIndex);
        Assert.IsTrue(arrivedIndex >= 0 && arrivedIndex < completedIndex);
        Assert.AreEqual(completed.Node, events.OfType<CoreEvent.TrainArrived>().Last().Node);
        var stoppedIndex = Array.IndexOf(names, nameof(CoreEvent.TrainActivityChanged), completedIndex);
        Assert.IsTrue(stoppedIndex > completedIndex);
        Assert.AreEqual(TrainActivity.Stopped, ((CoreEvent.TrainActivityChanged)events[stoppedIndex]).After);
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
    public void Equal_travel_time_chooses_the_smaller_node_id()
    {
        using var session = CombatFixture.SessionWithContent(CombatFixture.Content());
        var state = session.State;
        var world = state.World;
        var hostile = CombatFixture.HostileNode(session);
        var friendly = world.Settlements.First(settlement => settlement.Relation is SettlementRelation.PlayerOwned
            or SettlementRelation.NonHostile);
        var speed = TrainOperations.Capabilities(world, state.Content, world.Trains.Single())
            .MaximumSpeedMetersPerSecond;
        var route = TrainRouting.FindRoute(world.Nodes, world.Edges, hostile, friendly.Node, speed);
        Assert.IsNotNull(route);
        var node = new NodeId(world.Nodes.Max(item => item.Id.Value) + 1);
        var settlementId = new SettlementId(world.Settlements.Max(item => item.Id.Value) + 1);
        var source = world.Nodes.Single(item => item.Id == friendly.Node);
        var addedNode = source with
        {
            Id = node, Occupant = new NodeOccupant.Settlement(settlementId, SettlementRelation.NonHostile)
        };
        var addedSettlement = friendly with { Id = settlementId, Node = node };
        var edge = new EdgeMapItem(new EdgeId(world.Edges.Max(item => item.Id.Value) + 1), hostile, node,
            route.Value.DistanceMeters, source.Terrain, 1, 1, [], []);
        var settlements = world.Settlements.Select(settlement => settlement.Node == hostile
                || settlement.Id == friendly.Id ? settlement : settlement with { Relation = SettlementRelation.Hostile })
            .ToImmutableArray();
        var modified = world with
        {
            Nodes = world.Nodes.Add(addedNode), Edges = world.Edges.Add(edge),
            Settlements = [addedSettlement, .. settlements]
        };
        Assert.AreEqual(friendly.Node, CombatOperations.ChooseDestination(modified, state.Content,
            world.Trains.Single(), new WorldLocation.Node(hostile)));
    }

    [TestMethod]
    public void A_nearer_unreachable_settlement_is_skipped_for_a_reachable_candidate()
    {
        var state = TrainDispatchFixture.State(1);
        var world = state.World;
        var source = world.Nodes[0];
        var friendly = world.Settlements.Single();
        var farther = new NodeId(3);
        var origin = new NodeId(2);
        var fartherSettlement = friendly with { Id = new(2), Node = farther };
        var modified = world with
        {
            Nodes = world.Nodes.Add(source with { Id = farther,
                Occupant = new NodeOccupant.Settlement(fartherSettlement.Id, fartherSettlement.Relation) }),
            Edges = [world.Edges[0] with { LengthMeters = 1, Breaks = [new(new(1), 1, 0.5)] },
                world.Edges[0] with { Id = new(2), Start = origin, End = farther, LengthMeters = 10 }],
            Settlements = [friendly, fartherSettlement]
        };

        Assert.AreEqual(farther, CombatOperations.ChooseDestination(modified, state.Content,
            world.Trains.Single(), new WorldLocation.Node(origin)));
    }

    [TestMethod]
    public void Retreat_retries_after_the_only_track_is_repaired_and_completes()
    {
        var state = RailwayWorkFixture.Prepared(1, 1, 1, 1, 1, 2.0 / 60);
        var player = RailwayWorkFixture.Player(state);
        var train = state.World.Trains.Single() with
        {
            Position = WorldFixture.Node(new(2)), Activity = TrainActivity.Retreating
        };
        var world = state.World with
        {
            Edges = [state.World.Edges[0] with { Breaks = [new(new(1), 1, 0.5)] }],
            Trains = [train], Next = state.World.Next with { Break = 2 }
        };
        using var session = new CoreSession(state with { World = world });
        TrainDispatchFixture.Step(session);
        Assert.IsNull(session.State.World.Trains.Single().RetreatDestination);
        Assert.AreEqual(TrainActivity.Retreating, Train(session, train.Id).Activity);
        CombatFixture.Accepted(session.Submit(new CoreCommand.RepairRail(new(1), new(1), player.Id),
            CommandSource.AutomationTest));
        CombatFixture.AdvanceUntilEvent(session, value => value is CoreEvent.RailRepaired);
        CombatFixture.AdvanceUntil(session,
            _ => session.State.World.Trains.Single().RetreatDestination == player.Node, 10);
        Assert.AreEqual(TrainActivity.Retreating, Train(session, train.Id).Activity);
        var events = CombatFixture.AdvanceUntilEvent(session,
            value => value is CoreEvent.RetreatCompleted, 1000);
        Assert.AreEqual(player.Node, events.OfType<CoreEvent.RetreatCompleted>().Single().Node);
        Assert.AreEqual(TrainActivity.Stopped, Train(session, train.Id).Activity);
        Assert.AreEqual(player.Node, ((TrainPosition.Node)Train(session, train.Id).Position).Id);
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
        var plan = new CoreCommand.ReplaceRunPlan(train, new(false,
            [new PlanStop.Waypoint(CombatFixture.StartNode(session, train))]));
        foreach (var command in new CoreCommand[] { plan, new CoreCommand.DisbandTrain(train) })
        {
            var rejected = (CommandDecision.Rejected)((SubmissionResult.Completed)session.Submit(command,
                CommandSource.AutomationTest)).Receipt.Decision;
            Assert.AreEqual("invalid_state", rejected.Problems.Single().Code);
            Assert.AreEqual("Train.Activity", rejected.Problems.Single().Field);
        }
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
        var content = RetreatSeconds(ThresholdContent(buildingDamage: 1), 20) with
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
        var retreated = CombatFixture.AdvanceUntilEvent(session, value => value is CoreEvent.CombatRetreatStarted)
            .OfType<CoreEvent.CombatRetreatStarted>().Single();
        CombatFixture.Send(session, hostile, attacker);
        ulong? remainingBeforeEnd = null;
        CoreEvent.CombatEnded? ended = null;
        for (var step = 0; step < 800 && ended is null; step++)
        {
            remainingBeforeEnd = CombatFixture.Participant(session, retreated.Unit).RemainingDisengagementSteps;
            var result = session.Advance(1);
            Assert.IsNull(result.Fault);
            ended = result.Events.Select(envelope => envelope.Event).OfType<CoreEvent.CombatEnded>().FirstOrDefault();
        }
        Assert.IsNotNull(ended);
        Assert.IsTrue(remainingBeforeEnd > 0);
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
    public void Retreat_without_a_destination_waits_at_the_node_and_retries_every_step()
    {
        var content = CombatFixture.Content(damage: 1) with
        {
            Designs = CombatFixture.Content(damage: 1).Designs.Select(design =>
                design.Design == CombatFixture.ArmedBuilding ? design with
                {
                    MaxStructureDurability = 5,
                    Parts = design.Parts.Select(part => part with
                    {
                        MaxDurability = 1000, Capabilities = [new PartCapability.Weapon(1, 1)]
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
        CombatFixture.AdvanceUntil(session, view => view.FindTrain(attacker) is Lookup<TrainView>.Found
            { Value: { Position: TrainPosition.Node node } } && node.Id == hostile);
        var node = ((Lookup<NodeView>.Found)session.Read().FindNode(hostile)).Value;
        foreach (var edge in node.AdjacentEdges)
        {
            var tracks = ((Lookup<EdgeView>.Found)session.Read().FindEdge(edge)).Value.Map.Tracks;
            for (var track = 1; track <= tracks; track++)
                CombatFixture.Accepted(session.Submit(new CoreCommand.DebugAddRailBreak(edge, track, 0),
                    CommandSource.AutomationTest));
        }
        var ended = CombatFixture.AdvanceUntilEvent(session, value => value is CoreEvent.CombatEnded, 1000);
        Assert.IsTrue(ended.OfType<CoreEvent.CombatEnded>().Any());
        Assert.AreEqual(TrainActivity.Retreating, Train(session, retreating).Activity);
        Assert.IsNull(session.State.World.Trains.Single(item => item.Id == retreating).RetreatDestination);
        var waiting = Train(session, retreating);
        Assert.AreEqual(hostile, ((TrainPosition.Node)waiting.Position).Id);
        Assert.IsTrue(waiting.Blockers.Any(problem => problem.Code == "no_route"));
        for (var step = 0; step < 600; step++)
        {
            var result = session.Advance(1);
            Assert.IsNull(result.Fault);
            var train = Train(session, retreating);
            Assert.AreEqual(TrainActivity.Retreating, train.Activity);
            Assert.AreEqual(hostile, ((TrainPosition.Node)train.Position).Id);
            Assert.IsNull(session.State.World.Trains.Single(item => item.Id == retreating).RetreatDestination);
            Assert.IsTrue(train.Blockers.Any(problem => problem.Code == "no_route"));
        }
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
