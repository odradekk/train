using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.Sessions;
using Train.Core.Tests.World;
using static Train.Core.Tests.Production.ProductionFixture;

namespace Train.Core.Tests.Production;

[TestClass]
public sealed class ProductionRecoveryTests
{
    [TestMethod]
    public void Inventory_transfers_emit_state_changes_at_the_command_boundary()
    {
        var state = State(Content(stock: [new(Input, 1)]));
        var settlement = state.World.Settlements.Single();
        // 自有聚居地的创建归后续任务；这里恢复合法状态，验证已有装卸命令。
        var world = state.World with
        {
            Settlements = [settlement with { Relation = SettlementRelation.PlayerOwned }],
            Nodes = state.World.Nodes.Select(n => n.Id != settlement.Node ? n : n with
                { Occupant = new NodeOccupant.Settlement(settlement.Id, SettlementRelation.PlayerOwned) }).ToImmutableArray(),
            Instances = state.World.Instances.Select(i => i.Id is not InstanceId.Building ? i : i with
                { Ownership = SettlementRelation.PlayerOwned }).ToImmutableArray(),
            Inventories = state.World.Inventories.Select(i => i.Owner is not InventoryOwner.Train ? i : i with
                { Goods = [new(Input, 1)] }).ToImmutableArray()
        };
        var saved = WorldFixture.Success(CoreFactory.Create().DecodeState(new CoreCapture(state with { World = world }).Encode()));
        using var candidate = WorldFixture.Success(CoreFactory.Create().PrepareRestore(saved, state.Content, WorldFixture.Options));
        using var session = candidate.Activate();
        Accept(new CoreCommand.ReplaceRunPlan(new(1), new(false, [new PlanStop.Station(settlement.Node, 100, [])])));
        Accept(new CoreCommand.StartTrain(new(1)));
        Step(session);
        var old = session.Read();
        Assert.AreEqual(ProductionState.WaitingForInputs, Read(old).State);
        var unloaded = Accept(new CoreCommand.UnloadGoods(new(1), Settlement, LocalInventory, Input, 1));
        var started = unloaded.Events.Select(e => e.Event).OfType<CoreEvent.ProductionStateChanged>().Single();
        Assert.AreEqual(ProductionState.WaitingForInputs, started.Before);
        Assert.AreEqual(ProductionState.Running, started.After);
        Assert.IsNull(Read(session).Batch);
        Assert.AreEqual(ProductionState.WaitingForInputs, Read(old).State);
        var loaded = Accept(new CoreCommand.LoadGoods(new(1), Settlement, LocalInventory, Input, 1));
        var waiting = loaded.Events.Select(e => e.Event).OfType<CoreEvent.ProductionStateChanged>().Single();
        Assert.AreEqual(ProductionState.Running, waiting.Before);
        Assert.AreEqual(ProductionState.WaitingForInputs, waiting.After);

        SubmissionResult.Completed Accept(CoreCommand command)
        {
            var result = (SubmissionResult.Completed)session.Submit(command, CommandSource.AutomationTest);
            Assert.IsInstanceOfType<CommandDecision.Accepted>(result.Receipt.Decision);
            return result;
        }
    }

    [TestMethod]
    public void Last_allocation_round_trips_and_publication_does_not_recalculate_it()
    {
        var content = Content(stock: [new(Input, 2), new(Fuel, 2)], work: 4, rate: 120,
            powerPerWork: 2, power: 30);
        using var candidate = Prepare(content);
        using var session = (CoreSession)candidate.Activate();
        Step(session, 2);
        var old = session.Read();
        var runtime = session.State.World.Settlements.Single().ProductionFacilities.Single();
        Assert.AreEqual(15d, runtime.LastActualWorkPerSecond);
        Assert.AreEqual(240d, runtime.LastPowerDemand);
        Assert.AreEqual(30d, runtime.LastActualSupply);
        CollectionAssert.AreEqual(new[] { WorkLimit.Power }, runtime.LastLimits.ToArray());
        var saved = WorldFixture.Success(session.Capture(CapturePurpose.Save));
        var decoded = WorldFixture.Success(CoreFactory.Create().DecodeState(saved.Encode()));
        Assert.AreEqual(saved.Encode(), decoded.Encode());
        using var restoredCandidate = WorldFixture.Success(CoreFactory.Create().PrepareRestore(decoded, content, WorldFixture.Options));
        using var restored = (CoreSession)restoredCandidate.Activate();
        Assert.AreEqual(15d, Read(restored).ActualWorkPerSecond);
        CollectionAssert.AreEqual(Read(old).Limits.ToArray(), Read(restored).Limits.ToArray());
        Assert.AreEqual(runtime.LastPowerDemand, restored.State.World.Settlements.Single().ProductionFacilities.Single().LastPowerDemand);
        Assert.AreEqual(runtime.LastActualSupply, restored.State.World.Settlements.Single().ProductionFacilities.Single().LastActualSupply);
        Step(session);
        Step(restored);
        Assert.AreEqual(WorldFixture.Success(session.Capture(CapturePurpose.Save)).Encode(),
            WorldFixture.Success(restored.Capture(CapturePurpose.Save)).Encode());
        var updated = Changed(content) with { Processes = [content.Processes[0] with
            { Revision = new(new string('A', 64)), PowerPerWork = 100 }] };
        var last = restored.State.World.Settlements.Single().ProductionFacilities.Single();
        Publish(restored, updated);
        Assert.AreEqual(last, restored.State.World.Settlements.Single().ProductionFacilities.Single());
        Assert.AreEqual(15d, Read(old).ActualWorkPerSecond);
        Assert.AreEqual(.25, Read(old).Batch!.ProgressWork, 1e-12);
    }

    [TestMethod]
    public void Missing_process_pauses_retains_batch_and_returns_after_reinstallation()
    {
        var content = Content();
        using var candidate = Prepare(content);
        using var session = candidate.Activate();
        Step(session, 2);
        var saved = WorldFixture.Success(session.Capture(CapturePurpose.Save));
        var missing = Changed(content) with { Processes = [], MissingReferences =
            [new(new CoreDefinitionReference.Process(Process), "process")] };
        using var restoredCandidate = WorldFixture.Success(CoreFactory.Create().PrepareRestore(
            WorldFixture.Success(CoreFactory.Create().DecodeState(saved.Encode())), missing, WorldFixture.Options));
        Assert.IsTrue(restoredCandidate.MissingReferences.Any(m => m.Reference == new CoreDefinitionReference.Process(Process)));
        using var restored = restoredCandidate.Activate();
        Assert.AreEqual(ProductionState.ProcessMissing, Read(restored).State);
        Step(restored, 3);
        Assert.AreEqual(1d, Read(restored).Batch!.ProgressWork);
        Assert.AreEqual(0d, Read(restored).ActualWorkPerSecond);
        Assert.AreEqual(8L, Stock(restored, Input));
        var paused = WorldFixture.Success(restored.Capture(CapturePurpose.Save));
        Assert.AreEqual(paused.Encode(), WorldFixture.Success(CoreFactory.Create().DecodeState(paused.Encode())).Encode());
        Publish(restored, Changed(content, 'A'));
        Step(restored);
        Assert.IsNull(Read(restored).Batch);
        Assert.AreEqual(3L, Stock(restored, Product));
        Assert.AreEqual(8L, Stock(restored, Input));
    }

    [TestMethod]
    public void Missing_process_precedes_disabled_zero_capability_and_wrong_conditions()
    {
        var content = Content(enabled: false);
        content = content with { Designs = content.Designs.Select(d => d.Design != WorldFixture.FactoryBuilding ? d : d with
            { Parts = d.Parts.Select(p => p with { Capabilities = [] }).ToImmutableArray() }).ToImmutableArray(),
            Processes = [content.Processes[0] with { AllowedTerrainTypes = [WorldFixture.Hills] }] };
        using var candidate = Prepare(content);
        using var session = candidate.Activate();
        var saved = WorldFixture.Success(session.Capture(CapturePurpose.Save));
        var missing = Changed(content) with { Processes = [], MissingReferences =
            [new(new CoreDefinitionReference.Process(Process), "process")] };
        using var restoredCandidate = WorldFixture.Success(CoreFactory.Create().PrepareRestore(saved, missing, WorldFixture.Options));
        using var restored = restoredCandidate.Activate();
        Step(restored);
        Assert.AreEqual(ProductionState.ProcessMissing, Read(restored).State);
        Assert.IsNull(Read(restored).Batch);
        Assert.AreEqual(10L, Stock(restored, Input));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Missing_frozen_or_buffered_outputs_enter_inventory_as_unusable_stacks(bool completeBeforeSaving)
    {
        var content = Content(stock: [new(Input, 2)], work: 1, capacity: 1);
        using var candidate = Prepare(content);
        using var session = candidate.Activate();
        Step(session);
        var updated = Changed(content) with { Processes = [content.Processes[0] with
        {
            Revision = new(new string('A', 64)),
            Details = new ProcessDetails.Production(new("fixture:production"), [new(Product, 1)], [new(OtherProduct, 1)])
        }] };
        Publish(session, updated);
        if (completeBeforeSaving) Step(session);
        var saved = WorldFixture.Success(session.Capture(CapturePurpose.Save));
        var missing = Changed(updated, '9') with
        {
            Goods = updated.Goods.Where(g => g.Good != Product).ToImmutableArray(),
            MissingReferences = [new(new CoreDefinitionReference.Voxel(((GoodId.Voxel)Product).Definition), "product")]
        };
        using var restoredCandidate = WorldFixture.Success(CoreFactory.Create().PrepareRestore(
            WorldFixture.Success(CoreFactory.Create().DecodeState(saved.Encode())), missing, WorldFixture.Options));
        Assert.IsTrue(restoredCandidate.MissingReferences.Any(m => m.Reference == new CoreDefinitionReference.Voxel(((GoodId.Voxel)Product).Definition)));
        using var restored = restoredCandidate.Activate();
        Step(restored);
        Assert.AreEqual(1L, Stock(restored, Product));
        Assert.IsTrue(ReadInventory(restored).Stacks.Single(s => s.Good == Product).DefinitionMissing);
        Assert.AreEqual(2L, Read(restored).OutputBuffer.Single().Quantity);
        Assert.AreEqual(ProductionState.OutputBlocked, Read(restored).State);
        var expanded = Changed(missing, '8') with { Designs = missing.Designs.Select(d => d.Design != WorldFixture.Station ? d : d with
        { Parts = d.Parts.Select(p => p with { Capabilities = p.Capabilities.Select(c => c is PartCapability.Storage
            ? new PartCapability.Storage(10) : c).ToImmutableArray() }).ToImmutableArray() }).ToImmutableArray() };
        Publish(restored, expanded);
        Step(restored);
        Assert.AreEqual(3L, Stock(restored, Product));
        Assert.IsTrue(Read(restored).OutputBuffer.IsEmpty);
        Step(restored);
        Assert.IsNull(Read(restored).Batch);
        Assert.AreEqual(ProductionState.WaitingForInputs, Read(restored).State);
        Publish(restored, Changed(expanded, '7') with { Goods = updated.Goods, MissingReferences = [] });
        Step(restored);
        Assert.IsNotNull(Read(restored).Batch);
        Assert.AreEqual(2L, Stock(restored, Product));
    }

    [TestMethod]
    public void Publication_cannot_remove_frozen_output_or_change_process_kind()
    {
        var content = Content();
        using var candidate = Prepare(content);
        using var session = candidate.Activate();
        Step(session);
        var updated = Changed(content) with { Processes = [content.Processes[0] with
        { Details = new ProcessDetails.Production(new("fixture:production"), [new(Input, 2)], [new(OtherProduct, 3)]) }] };
        Publish(session, updated);
        var removed = Changed(updated, '9') with { Goods = updated.Goods.Where(g => g.Good != Product).ToImmutableArray() };
        var rejected = (SubmissionResult.Completed)session.Submit(new CoreCommand.PublishContent(removed), CommandSource.AutomationTest);
        Assert.IsInstanceOfType<CommandDecision.Rejected>(rejected.Receipt.Decision);
        Assert.AreEqual(updated.Identity, session.Read().InputIdentity);
        Assert.IsTrue(((CommandDecision.Rejected)rejected.Receipt.Decision).Problems.Any(p => p.Field.Contains("FrozenOutputs", StringComparison.Ordinal)));
        var changedKind = Changed(updated, '8') with { Processes = [updated.Processes[0] with
            { Details = new ProcessDetails.Extraction(new("fixture:production"), new(Input, 1)) }] };
        var kindResult = (SubmissionResult.Completed)session.Submit(new CoreCommand.PublishContent(changedKind), CommandSource.AutomationTest);
        Assert.IsInstanceOfType<CommandDecision.Rejected>(kindResult.Receipt.Decision);
        Assert.AreEqual(updated.Identity, session.Read().InputIdentity);
        Assert.AreEqual(0d, Read(session).Batch!.ProgressWork);
    }

    [TestMethod]
    [DataRow("last_actual_work_per_second", "-1")]
    [DataRow("last_power_demand", "1e999")]
    [DataRow("last_actual_supply", "999")]
    [DataRow("last_limits", "[1,1]")]
    [DataRow("last_limits", "[99]")]
    [DataRow("building", "{\"value\":999}")]
    public void Invalid_saved_allocation_is_rejected(string field, string value)
    {
        using var candidate = Prepare(Content(stock: [new(Input, 2), new(Fuel, 2)], powerPerWork: 1));
        using var session = candidate.Activate();
        Step(session, 2);
        var json = JsonNode.Parse(WorldFixture.Success(session.Capture(CapturePurpose.Save)).Encode())!;
        json["world"]!["settlements"]![0]!["production_facilities"]![0]![field] = JsonNode.Parse(value);
        var failure = (CoreResult<ICoreCapture>.Failure)CoreFactory.Create().DecodeState(json.ToJsonString());
        Assert.AreEqual("invalid_state", failure.Problems.Single().Code);
        StringAssert.Contains(failure.Problems.Single().Field, "production_facilities");
    }

    [TestMethod]
    public void Missing_batch_fields_and_simultaneous_batch_and_buffer_are_rejected()
    {
        using var candidate = Prepare();
        using var session = candidate.Activate();
        Step(session);
        var saved = WorldFixture.Success(session.Capture(CapturePurpose.Save)).Encode();
        var missing = JsonNode.Parse(saved)!;
        missing["world"]!["settlements"]![0]!["production_facilities"]![0]!["batch"]!.AsObject().Remove("frozen_power_per_work");
        Assert.IsInstanceOfType<CoreResult<ICoreCapture>.Failure>(CoreFactory.Create().DecodeState(missing.ToJsonString()));
        var both = JsonNode.Parse(saved)!;
        var runtime = both["world"]!["settlements"]![0]!["production_facilities"]![0]!;
        runtime["output_buffer"] = runtime["batch"]!["frozen_outputs"]!.DeepClone();
        var failure = (CoreResult<ICoreCapture>.Failure)CoreFactory.Create().DecodeState(both.ToJsonString());
        Assert.AreEqual("invalid_state", failure.Problems.Single().Code);
        StringAssert.Contains(failure.Problems.Single().Field, "output_buffer");
    }
}
