using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.Sessions;
using Train.Core.Tests.World;
using static Train.Core.Tests.Settlements.FoundingFixture;

namespace Train.Core.Tests.Settlements;

[TestClass]
public sealed class FoundingTests
{
    [TestMethod]
    public void Preview_and_acceptance_agree_and_reserve_final_owner_before_completion()
    {
        var content = Content;
        var original = content.SettlementTypes[0];
        var alternate = original with { Id = new("fixture:alternate"), MinimumInitialPopulation = 27, MaximumInitialPopulation = 27 };
        content = content with
        {
            SettlementTypes = [original, alternate, original with { Id = new("fixture:no-template"), Weight = int.MaxValue }],
            SettlementTemplates = [content.SettlementTemplates[0], content.SettlementTemplates[0] with
                { Id = new("fixture:alternate-template"), Type = alternate.Id, FoundingBuildings = [new(13, Shed)] }]
        };
        using var session = Session(content);
        var before = session.Read();
        var preview = WorldFixture.Success(before.PreviewSettlement(Node(session)));
        var result = Submit(session, new CoreCommand.FoundSettlement(Node(session), before.World.Trains[0].Id));
        var accepted = (CommandDecision.Accepted)result.Receipt.Decision;
        var job = Job(session.Read(), ((WorldObjectId.Job)accepted.Created.Single()).Id);
        var reference = accepted.SettlementCopies.Single();
        var source = content.SettlementTemplates.Single(t => t.Id == reference.SourceTemplate);
        var type = content.SettlementTypes.Single(t => t.Id == source.Type);
        Assert.AreEqual(type.MinimumInitialPopulation, preview.InitialResidents);
        Assert.AreEqual(source.Id, job.SourceTemplateId);
        Assert.AreEqual(job.ReservedSettlementId, reference.Owner);
        Assert.AreEqual(preview.DurationTicks, job.FrozenDurationTicks);
        CollectionAssert.AreEqual(preview.RequiredMaterials.ToArray(), job.InvestedGoods.ToArray());
        var expected = source.FoundingBuildings.SelectMany(b => content.Designs.Single(d => d.Design == b.Design).BillOfMaterials)
            .GroupBy(g => g.Good).Select(g => new GoodsQuantity(g.Key, g.Sum(q => q.Quantity))).ToArray();
        CollectionAssert.AreEquivalent(expected, preview.RequiredMaterials.ToArray());
        Assert.IsTrue(preview.Blockers.IsEmpty);
        Assert.IsTrue(preview.ExpectedConsumption.All(g => g.UnitsPerSecond == preview.InitialResidents));
        Assert.IsInstanceOfType<Lookup<SettlementView>.Missing>(session.Read().FindSettlement(reference.Owner));
        var capture = WorldFixture.Success(session.Capture(CapturePurpose.Save));
        CollectionAssert.AreEqual(reference.Plots.ToArray(), capture.References.Templates.Single(t => t.Owner == reference.Owner).Plots.ToArray());
        Assert.IsTrue(capture.References.Definitions.Any(d => d.Definition == new CoreDefinitionReference.SettlementType(type.Id)));
        Assert.AreEqual(session.State.World.Next.Settlement - 1, reference.Owner.Value);
        Assert.AreEqual((JobId?)null, before.World.Nodes.Single(n => n.Id == Node(session)).FoundingJob);
    }

    [TestMethod]
    [DataRow("goods", "insufficient_goods")]
    [DataRow("node", "node_occupied")]
    [DataRow("lair", "node_occupied")]
    [DataRow("speed", "train_not_stationary_at_node")]
    [DataRow("location", "train_not_stationary_at_node")]
    [DataRow("owner", "not_owned")]
    [DataRow("job-id", "identity_exhausted")]
    [DataRow("settlement-id", "identity_exhausted")]
    [DataRow("reserved", "busy")]
    public void Rejection_preserves_inventory_nodes_and_all_identity_sources(string scenario, string code)
    {
        using var initial = Session(stock: scenario == "goods" ? 4 : 10);
        var state = initial.State;
        var node = Node(initial);
        if (scenario == "reserved") Start(initial);
        else state = state with { World = state.World with
        {
            Next = state.World.Next with
            {
                Job = scenario == "job-id" ? ulong.MaxValue : state.World.Next.Job,
                Settlement = scenario == "settlement-id" ? ulong.MaxValue : state.World.Next.Settlement
            },
            Instances = state.World.Instances.Select(i => scenario == "owner" && i.Id is InstanceId.Vehicle
                ? i with { Ownership = SettlementRelation.NonHostile } : i).ToImmutableArray(),
            Trains = state.World.Trains.Select(t => t with
            {
                SpeedMetersPerSecond = scenario == "speed" ? 1 : 0,
                Position = scenario == "location" ? WorldFixture.Node(state.World.Settlements[0].Node) : t.Position
            }).ToImmutableArray(),
            Nodes = state.World.Nodes.Select(n => n.Id != node ? n : n with
            {
                Occupant = scenario switch
                {
                    "lair" => new NodeOccupant.Lair(new(123)),
                    "node" => new NodeOccupant.Settlement(state.World.Settlements[0].Id, SettlementRelation.NonHostile),
                    _ => n.Occupant
                }
            }).ToImmutableArray()
        } };
        using var session = new CoreSession(scenario == "reserved" ? initial.State : state);
        var before = session.State;
        var result = Submit(session, new CoreCommand.FoundSettlement(node, before.World.Trains[0].Id));
        Assert.AreEqual(code, ((CommandDecision.Rejected)result.Receipt.Decision).Problems[0].Code);
        Assert.AreSame(before.World, session.State.World);
        Assert.AreEqual(before.NextCommandNumber + 1, session.State.NextCommandNumber);
        Assert.AreEqual(1, session.ReadRecording().Commands.Length);
        Assert.IsTrue(result.Events.IsEmpty);
    }

    [TestMethod]
    [DataRow(NodeTravelPhase.Approaching)]
    [DataRow(NodeTravelPhase.Waiting)]
    [DataRow(NodeTravelPhase.Docked)]
    [DataRow(NodeTravelPhase.Departing)]
    public void Any_zero_speed_node_phase_is_allowed_and_completion_adapts_trains_at_exact_tick(NodeTravelPhase phase)
    {
        using var initial = Session();
        var state = initial.State;
        using var session = new CoreSession(state with { World = state.World with
        {
            Trains = state.World.Trains.Select(t => t with { Position = ((TrainPosition.Node)t.Position) with
            {
                Phase = phase, NextEdge = phase == NodeTravelPhase.Departing
                    ? state.World.Edges.First(e => e.Start == ((TrainPosition.Node)t.Position).Id || e.End == ((TrainPosition.Node)t.Position).Id).Id : null
            } }).ToImmutableArray()
        } });
        var node = Node(session);
        var job = Start(session);
        Assert.AreEqual(5L, session.State.World.Inventories.Single(i => i.Id == session.State.World.Trains[0].Inventory).Goods.Single().Quantity);
        var early = Step(session, 2);
        Assert.IsFalse(early.Events.Any(e => e.Event is CoreEvent.SettlementFounded));
        Assert.AreEqual(2UL, Job(session.Read(), job.Id).ElapsedTicks);
        Assert.IsInstanceOfType<NodeOccupant.Empty>(session.Read().World.Nodes.Single(n => n.Id == node).Occupant);
        var completion = Step(session);
        var founded = completion.Events.Select(e => e.Event).OfType<CoreEvent.SettlementFounded>().Single();
        Assert.AreEqual(job.ReservedSettlementId, founded.Settlement);
        Assert.AreEqual(10L, founded.Residents);
        Assert.IsInstanceOfType<Lookup<JobView>.Missing>(session.Read().FindJob(job.Id));
        var view = session.Read();
        var settlement = ((Lookup<SettlementView>.Found)view.FindSettlement(founded.Settlement)).Value;
        Assert.AreEqual(SettlementRelation.PlayerOwned, settlement.Relation);
        Assert.IsInstanceOfType<PopulationExecution.NotStarted>(settlement.Population.Execution);
        Assert.AreEqual(2, settlement.Plots.Count(p => p.Occupant is PlotOccupant.Building));
        Assert.IsTrue(settlement.Plots.Single(p => p.TemplatePlotNumber == 21).Occupant is PlotOccupant.Empty);
        Assert.IsTrue(((Lookup<InventoryView>.Found)view.FindInventory(settlement.Inventory)).Value.Stacks.IsEmpty);
        Assert.AreEqual(0, session.State.World.Settlements.Single(s => s.Id == founded.Settlement).Template.ProductionPlans.Length);
        Assert.IsNull(view.World.Nodes.Single(n => n.Id == node).FoundingJob);
        Assert.AreEqual(1, view.World.Nodes.Single(n => n.Id == node).Platforms);
        var position = (TrainPosition.Node)view.World.Trains[0].Position;
        Assert.AreEqual(phase == NodeTravelPhase.Docked ? WorldFixture.ApproachLength + WorldFixture.PlatformLength / 2 : WorldFixture.ApproachLength,
            position.DistanceMeters, 1e-12);
        Assert.IsInstanceOfType<CoreResult<ICoreCapture>.Success>(CoreFactory.Create().DecodeState(WorldFixture.Success(session.Capture(CapturePurpose.Save)).Encode()));
    }

    [TestMethod]
    public void Task_does_not_lock_or_depend_on_material_train_and_ignores_updated_template_and_population()
    {
        var content = Content;
        using var session = Session(content);
        var job = Start(session);
        Assert.IsTrue(((Lookup<TrainView>.Found)session.Read().FindTrain(new(1))).Value.Vehicles.All(v =>
            ((Lookup<InstanceView>.Found)session.Read().FindInstance(new InstanceId.Vehicle(v))).Value.Lock == InstanceLock.None));
        var changed = content with
        {
            Identity = new(new string('F', 64)),
            ConstructionRules = [content.ConstructionRules[0] with { FoundingDurationTicks = 100 }],
            SettlementTypes = [content.SettlementTypes[0] with { MinimumInitialPopulation = 77, MaximumInitialPopulation = 77 }],
            SettlementTemplates = [content.SettlementTemplates[0] with { FoundingBuildings = [] }]
        };
        Assert.IsInstanceOfType<CommandDecision.Accepted>(Submit(session, new CoreCommand.PublishContent(changed)).Receipt.Decision);
        var other = session.State.World.Settlements[0].Node;
        using var departed = new CoreSession(session.State with { World = session.State.World with
            { Trains = session.State.World.Trains.Select(t => t with { Position = WorldFixture.Node(other) }).ToImmutableArray() } });
        Step(departed, 3);
        var settlement = ((Lookup<SettlementView>.Found)departed.Read().FindSettlement(job.ReservedSettlementId)).Value;
        Assert.AreEqual(10L, settlement.Population.Residents);
        Assert.AreEqual(2, settlement.Plots.Count(p => p.Occupant is PlotOccupant.Building));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Unavailable_population_fails_only_preview(bool missingGood)
    {
        using var initial = Session();
        var state = initial.State;
        var selected = state.Content.PopulationRules[0];
        var missing = missingGood ? new CoreDefinitionReference.Voxel(((GoodId.Voxel)selected.ConsumptionGoods[0].Good).Definition)
            : (CoreDefinitionReference)new CoreDefinitionReference.Rule(selected.Id);
        using var session = new CoreSession(state with { Content = state.Content with { MissingReferences = [new(missing, "")] } });
        var preview = session.Read().PreviewSettlement(Node(session));
        Assert.IsInstanceOfType<CoreResult<SettlementFoundingPreview>.Failure>(preview);
        Assert.IsTrue(((CoreResult<SettlementFoundingPreview>.Failure)preview).Problems.All(p => p.Code == "content_missing" && p.Field.Length > 0));
        Start(session);
        var completed = Step(session, 3);
        Assert.IsTrue(completed.Events.Any(e => e.Event is CoreEvent.SettlementFounded));
    }

    [TestMethod]
    public void Restore_without_the_original_type_completes_with_frozen_population()
    {
        using var original = Session();
        var job = Start(original);
        Step(original);
        var factory = CoreFactory.Create();
        var saved = WorldFixture.Success(original.Capture(CapturePurpose.Save));
        var changed = Content with { Identity = new(new string('F', 64)), SettlementTypes = [],
            MissingReferences = [new(new CoreDefinitionReference.SettlementType(Content.SettlementTypes[0].Id), "")] };
        using var candidate = WorldFixture.Success(factory.PrepareRestore(WorldFixture.Success(factory.DecodeState(saved.Encode())), changed, WorldFixture.Options));
        using var restored = candidate.Activate();
        var completed = Step(restored, 2).Events.Select(e => e.Event).OfType<CoreEvent.SettlementFounded>().Single();
        Assert.AreEqual(job.ReservedSettlementId, completed.Settlement);
        Assert.AreEqual(10L, completed.Residents);
    }

    [TestMethod]
    [DataRow("reserved-owner")]
    [DataRow("node")]
    [DataRow("duration")]
    [DataRow("population")]
    [DataRow("high-water")]
    [DataRow("plot")]
    public void Damaged_founding_state_is_rejected(string damage)
    {
        using var session = Session();
        Start(session);
        var json = JsonNode.Parse(WorldFixture.Success(session.Capture(CapturePurpose.Save)).Encode())!;
        var world = json["world"]!;
        var job = world["founding_jobs"]![0]!;
        switch (damage)
        {
            case "reserved-owner": job["reserved_settlement_id"]!["value"] = 1; break;
            case "node": world["nodes"]!.AsArray().Single(n => n!["founding_job"] is not null)!["founding_job"] = null; break;
            case "duration": job["frozen_duration_ticks"] = 0; break;
            case "population": job["initial_residents"] = 0; break;
            case "high-water": world["next"]!["settlement"] = job["reserved_settlement_id"]!["value"]!.GetValue<ulong>(); break;
            case "plot": job["template"]!["founding_buildings"]![0]!["plot_number"] = 999; break;
        }
        var decoded = CoreFactory.Create().DecodeState(json.ToJsonString());
        Assert.IsInstanceOfType<CoreResult<ICoreCapture>.Failure>(decoded);
        Assert.AreEqual("invalid_state", ((CoreResult<ICoreCapture>.Failure)decoded).Problems[0].Code);
    }

    [TestMethod]
    public void Strict_replay_preserves_accepted_and_rejected_founding_receipts()
    {
        using var session = Session();
        Start(session);
        Assert.IsInstanceOfType<CommandDecision.Rejected>(Submit(session,
            new CoreCommand.FoundSettlement(Node(session), new(1))).Receipt.Decision);
        Step(session, 3);
        var expected = WorldFixture.Success(session.Capture(CapturePurpose.Save));
        var window = session.ReadRecording();
        var factory = CoreFactory.Create();
        using var replay = WorldFixture.Success(factory.PrepareReplay(new(2, WorldFixture.Options.Runtime,
            window.Starts[0].State.Encode(), window.Commands, window.Checkpoints, window.EndPosition, null), WorldFixture.Options.Runtime, [Content]));
        var result = replay.Advance(uint.MaxValue);
        Assert.AreEqual(ReplayStop.EndOfRecording, result.Stop);
        Assert.IsNull(result.Difference);
        Assert.AreEqual(expected.Checkpoint.CanonicalText, replay.CaptureBoundary().Checkpoint.CanonicalText);
    }
}
