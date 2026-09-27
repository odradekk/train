using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Domain.Commands;
using Train.Domain.Composites;
using Train.Domain.Playground;
using Train.Domain.Rails;
using Train.Domain.Voxels;

namespace Train.Tests;

[TestClass]
public sealed class WorldEditPersistenceTests
{
    private static readonly WorldVoxelCoord Probe = WorldEditOrderingTests.Probe;
    private static readonly WorldVoxelCoord Other = new(61, 3, 20);
    private static readonly VoxelDefinitionId Ore = WorldEditOrderingTests.Ore;

    [TestMethod]
    public void Paused_edits_and_pending_structural_edits_survive_save_and_continue_identically_after_restore()
    {
        using var writer = PlaygroundHarness.Establish(PlaygroundConfigurations.StraightRailTrainId);
        var session = writer.Session;
        Assert.IsTrue(session.StartTrain(RailHeading.AlongAlignment).Succeeded);
        Assert.IsTrue(session.AdvanceSimulation(20).Succeeded);
        var shell = PausedEditsWithPendingStructure(session);
        var boundary = session.EditBoundary;
        var pending = session.QueryPendingEdits();
        var logCount = session.QueryEditLog().Count;
        var fingerprint = WorldSaveAuthority.Fingerprint(session);

        var saved = session.Save("Edits");
        Assert.AreEqual(WorldSaveStatus.Saved, saved.Status);
        Assert.AreEqual(WorldSaveStatus.Saved, session.Save("Edits Again").Status);
        Assert.AreEqual(boundary, session.EditBoundary);
        CollectionAssert.AreEqual(pending.ToArray(), session.QueryPendingEdits().ToArray());
        Assert.AreEqual(logCount, session.QueryEditLog().Count);
        Assert.AreEqual(fingerprint, WorldSaveAuthority.Fingerprint(session));
        var json = JsonNode.Parse(File.ReadAllText(writer.Store.PathFor(saved.SaveId!.Value)))!.AsObject();
        Assert.AreEqual(3, json["world_edits"]!["pending"]!.AsArray().Count);
        Assert.AreEqual(boundary.Sequence, json["world_edits"]!["boundary_sequence"]!.GetValue<ulong>());

        using var restarted = PlaygroundHarness.Content(userData: writer.UserData);
        var reader = new VoxelDevelopmentSession(restarted.Catalog, VoxelWorldIdentity.Slice4, restarted.Store, restarted.Composites);
        restarted.Attach(reader);
        Assert.AreEqual(WorldSaveStatus.Loaded, reader.Load(saved.SaveId.Value).Status);
        Assert.AreEqual(fingerprint, WorldSaveAuthority.Fingerprint(reader));
        Assert.AreEqual(boundary, reader.EditBoundary);
        Assert.AreEqual(session.NextEditId, reader.NextEditId);
        CollectionAssert.AreEqual(pending.ToArray(), reader.QueryPendingEdits().ToArray());
        Assert.AreEqual(0, reader.QueryEditLog().Count);

        foreach (var continued in new[] { session, reader })
        {
            Assert.IsTrue(continued.AdvanceSimulation(10).Succeeded);
            Assert.AreEqual("Added", continued.SubmitEdit(new VoxelAddEdit(Other, Ore, VoxelOrientation.Identity)).Status);
        }

        var writerTail = session.QueryEditLog().Skip(logCount).ToArray();
        CollectionAssert.AreEqual(writerTail, reader.QueryEditLog().ToArray());
        CollectionAssert.AreEqual(
            new SimulationPosition?[] { new(23, 0), new(23, 1), new(25, 0), new(30, 0) },
            writerTail.Select(item => item.ExecutedAt).ToArray());
        CollectionAssert.AreEqual(new[] { "Valid", "Damaged", "Valid", "Added" }, writerTail.Select(item => item.Status).ToArray());
        Assert.AreEqual(WorldSaveAuthority.Fingerprint(session), WorldSaveAuthority.Fingerprint(reader));
        Assert.IsTrue(reader.QueryComposite(shell)!.Parts.Any(part => part.Cell == HollowShellRefit.FirstAdd));
        Assert.AreEqual(2, reader.QueryComposites().Count(vehicle => vehicle.DefinitionId == HollowShellFixture.DefinitionId));
    }

    [TestMethod]
    public void Pending_edit_whose_target_was_destroyed_before_save_is_restored_and_rejected_on_time()
    {
        using var writer = PlaygroundHarness.Establish(PlaygroundConfigurations.StraightRailEmptyId);
        var session = writer.Session;
        var bridge = session.Manufacture(BridgeFixture.DefinitionId).VehicleId!.Value;
        var refit = new VehicleRefitEdit(bridge, new[]
        {
            CompositeRefitOperation.Add(new LocalVoxelCoord(3, 2, 1), HollowShellRefit.MarkerId, VoxelOrientation.Identity)
        });
        Assert.AreEqual("Pending", session.SubmitEdit(refit, 5).Status);
        Assert.AreEqual("Removed", session.SubmitEdit(new VehicleDamageEdit(new[]
        {
            new CompositeDamageTarget(bridge, BridgeFixture.BridgeCell, 100),
            new CompositeDamageTarget(bridge, BridgeFixture.LeftCoreCell, 100),
            new CompositeDamageTarget(bridge, BridgeFixture.RightCoreCell, 100)
        })).Status);
        Assert.IsNull(session.QueryComposite(bridge));
        var saved = session.Save("Destroyed Target");
        Assert.AreEqual(WorldSaveStatus.Saved, saved.Status);

        using var restarted = PlaygroundHarness.Content(userData: writer.UserData);
        var reader = new VoxelDevelopmentSession(restarted.Catalog, VoxelWorldIdentity.Slice4, restarted.Store, restarted.Composites);
        restarted.Attach(reader);
        Assert.AreEqual(WorldSaveStatus.Loaded, reader.Load(saved.SaveId!.Value).Status);
        Assert.AreEqual(refit, reader.QueryPendingEdits().Single().Edit);

        Assert.IsTrue(reader.AdvanceSimulation(4).Succeeded);
        Assert.AreEqual(1, reader.QueryPendingEdits().Count);
        Assert.IsTrue(reader.AdvanceSimulation(1).Succeeded);
        var rejected = reader.QueryEditLog().Single();
        Assert.AreEqual(WorldEditState.Rejected, rejected.State);
        Assert.AreEqual("UnknownVehicle", rejected.Status);
        Assert.AreEqual(new SimulationPosition(5, 0), rejected.ExecutedAt);
        Assert.AreEqual(0, reader.QueryPendingEdits().Count);
    }

    [TestMethod]
    public void Corrupt_or_unknown_edit_queues_reject_the_load_and_keep_the_session()
    {
        using var harness = PlaygroundHarness.Establish(PlaygroundConfigurations.StraightRailEmptyId);
        var session = harness.Session;
        var shell = session.Manufacture(HollowShellFixture.DefinitionId, WorldEditOrderingTests.ShellPose).VehicleId!.Value;
        Assert.IsTrue(session.AdvanceSimulation(2).Succeeded);
        session.SubmitEdit(new VehicleRefitEdit(shell, new[]
        {
            CompositeRefitOperation.Add(HollowShellRefit.FirstAdd, HollowShellRefit.MarkerId, VoxelOrientation.Identity)
        }), 5);
        session.SubmitEdit(new VoxelAddEdit(Probe, Ore, VoxelOrientation.Identity), 6);
        var saved = session.Save("Queue");
        var path = harness.Store.PathFor(saved.SaveId!.Value);
        var json = File.ReadAllText(path);
        var fingerprint = WorldSaveAuthority.Fingerprint(session);

        JsonObject Queue(JsonObject root) => root["world_edits"]!.AsObject();
        JsonObject First(JsonObject root) => Queue(root)["pending"]![0]!.AsObject();
        var cases = new (string Name, Action<JsonObject> Mutate, WorldSaveStatus Expected)[]
        {
            ("never allocated vehicle", root => First(root)["edit"]!["vehicle_id"] = root["next_entity_id"]!.GetValue<ulong>(), WorldSaveStatus.InvalidDelta),
            ("target at saved step", root => First(root)["target_step"] = 2, WorldSaveStatus.InvalidDelta),
            ("id not yet issued", root => First(root)["edit_id"] = Queue(root)["next_edit_id"]!.GetValue<ulong>(), WorldSaveStatus.InvalidDelta),
            ("zero next id", root => Queue(root)["next_edit_id"] = 0, WorldSaveStatus.InvalidDelta),
            ("accepted after boundary", root => First(root)["accepted_sequence"] = Queue(root)["boundary_sequence"]!.GetValue<ulong>(), WorldSaveStatus.InvalidDelta),
            ("out of acceptance order", root =>
            {
                var list = Queue(root)["pending"]!.AsArray();
                var head = list[0]!.DeepClone();
                list.RemoveAt(0);
                list.Add(head);
            }, WorldSaveStatus.InvalidDelta),
            ("bad orientation", root => Queue(root)["pending"]![1]!["edit"]!["orientation"] = "sideways", WorldSaveStatus.InvalidDelta),
            ("unknown kind", root => First(root)["edit"]!["kind"] = "vehicle.explode", WorldSaveStatus.UnknownFormat),
            ("unknown input version", root => First(root)["edit"]!["input_format_version"] = 2, WorldSaveStatus.UnknownFormat),
            ("unknown queue field", root => Queue(root)["priority"] = 1, WorldSaveStatus.UnknownFormat),
            ("missing queue", root => root.Remove("world_edits"), WorldSaveStatus.Truncated),
            ("queue in predecessor format", root => root["save_format_version"] = WorldSaveDocument.PredecessorFormatVersion, WorldSaveStatus.UnknownFormat)
        };

        foreach (var (name, mutate, expected) in cases)
        {
            var root = JsonNode.Parse(json)!.AsObject();
            mutate(root);
            File.WriteAllText(path, root.ToJsonString());
            Assert.AreEqual(expected, session.Load(saved.SaveId.Value).Status, name);
            Assert.AreEqual(fingerprint, WorldSaveAuthority.Fingerprint(session), name);
        }

        File.WriteAllText(path, json);
        Assert.AreEqual(WorldSaveStatus.Loaded, session.Load(saved.SaveId.Value).Status);
        Assert.AreEqual(fingerprint, WorldSaveAuthority.Fingerprint(session));
    }

    [TestMethod]
    public void Exhausted_input_and_transaction_counters_are_refused_explicitly()
    {
        using var harness = PlaygroundHarness.Establish(PlaygroundConfigurations.StraightRailEmptyId);
        var session = harness.Session;
        var saved = session.Save("Counters");
        var path = harness.Store.PathFor(saved.SaveId!.Value);
        var json = File.ReadAllText(path);

        LoadWith(root => root["world_edits"]!["next_edit_id"] = ulong.MaxValue);
        var fingerprint = WorldSaveAuthority.Fingerprint(session);
        Assert.AreEqual(WorldEditRefusal.InputExhausted, session.SubmitEdit(new VoxelAddEdit(Probe, Ore, VoxelOrientation.Identity)).Refusal);
        Assert.AreEqual(WorldCommandStatus.InputExhausted, session.Add(Probe, Ore, VoxelOrientation.Identity).Status);
        Assert.AreEqual(CompositeDamageStatus.InputExhausted, session.Damage(new EntityId(1), HollowShellRefit.FirstAdd, 10).Status);
        Assert.AreEqual(ConstructionStatus.InputExhausted, session.Manufacture(HollowShellFixture.DefinitionId).Status);
        Assert.AreEqual(ulong.MaxValue, session.NextEditId);
        Assert.AreEqual(fingerprint, WorldSaveAuthority.Fingerprint(session));

        LoadWith(root => root["world_edits"]!["boundary_sequence"] = ulong.MaxValue);
        Assert.AreEqual(WorldEditRefusal.InputExhausted, session.SubmitEdit(new VoxelAddEdit(Probe, Ore, VoxelOrientation.Identity)).Refusal);
        Assert.IsFalse(session.Query(Probe).IsOccupied);
        Assert.IsTrue(session.AdvanceSimulation(1).Succeeded);
        Assert.AreEqual("Added", session.SubmitEdit(new VoxelAddEdit(Probe, Ore, VoxelOrientation.Identity)).Status);
        Assert.AreEqual(new SimulationPosition(1, 1), session.EditBoundary);

        LoadWith(root => root["transaction_count"] = ulong.MaxValue - 1);
        var manufacture = session.SubmitEdit(new VehicleManufactureEdit(HollowShellFixture.DefinitionId, WorldEditOrderingTests.ShellPose));
        Assert.AreEqual(WorldEditState.Rejected, manufacture.Record!.State);
        Assert.AreEqual("TransactionExhausted", manufacture.Status);
        Assert.AreEqual(0, session.QueryComposites().Count);
        Assert.AreEqual(ulong.MaxValue - 1, session.TransactionCount);

        void LoadWith(Action<JsonObject> mutate)
        {
            var root = JsonNode.Parse(json)!.AsObject();
            mutate(root);
            File.WriteAllText(path, root.ToJsonString());
            Assert.AreEqual(WorldSaveStatus.Loaded, session.Load(saved.SaveId.Value).Status);
        }
    }

    /// <summary>Three immediate edits at one paused boundary, then refit and damage queued at +3 and a manufacture at +5.</summary>
    internal static EntityId PausedEditsWithPendingStructure(VoxelDevelopmentSession session)
    {
        var step = session.SimulationStep;
        Assert.AreEqual("Added", session.SubmitEdit(new VoxelAddEdit(Probe, Ore, VoxelOrientation.Identity)).Status);
        Assert.AreEqual("Damaged", session.SubmitEdit(new VoxelDamageEdit(new[] { new VoxelDamageTarget(Probe, 25) })).Status);
        var manufactured = session.SubmitEdit(new VehicleManufactureEdit(HollowShellFixture.DefinitionId, WorldEditOrderingTests.ShellPose));
        Assert.AreEqual("Valid", manufactured.Status);
        var shell = ((CompositeManufactureResult)manufactured.Result!).VehicleId!.Value;
        Assert.AreEqual("Pending", session.SubmitEdit(new VehicleRefitEdit(shell, new[]
        {
            CompositeRefitOperation.Add(HollowShellRefit.FirstAdd, HollowShellRefit.MarkerId, VoxelOrientation.Identity)
        }), step + 3).Status);
        Assert.AreEqual("Pending", session.SubmitEdit(new VehicleDamageEdit(new[]
        {
            new CompositeDamageTarget(shell, HollowShellRefit.FirstAdd, 25)
        }), step + 3).Status);
        Assert.AreEqual("Pending", session.SubmitEdit(
            new VehicleManufactureEdit(HollowShellFixture.DefinitionId, CompositeTransform.Translation(60, 6, 36)),
            step + 5).Status);
        Assert.AreEqual(new SimulationPosition(step, 6), session.EditBoundary);
        return shell;
    }
}
