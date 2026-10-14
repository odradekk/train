using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Train.Core.Contracts;
using Train.Core.Sessions;
using Train.Core.Tests.World;
using Train.Core.World;

namespace Train.Core.Tests.Inventory;

[TestClass]
public sealed class InventoryDecodeTests
{
    [TestMethod]
    public void Lease_and_reservation_survive_capture_and_checkpoint_round_trip()
    {
        var capture = new CoreCapture(State());
        var restored = WorldFixture.Success(CoreFactory.Create().DecodeState(capture.Encode()));
        Assert.AreEqual(capture.Encode(), restored.Encode());
        Assert.AreEqual(capture.Checkpoint, restored.Checkpoint);
        Assert.IsTrue(restored.References.Definitions.Any(d => d.Field.Contains(".Reservations[9].ExpectedReturns[", StringComparison.Ordinal)));
    }

    [TestMethod]
    [DataRow("leases")]
    [DataRow("reservations")]
    [DataRow("lease")]
    public void New_state_fields_are_required(string field)
    {
        var root = JsonNode.Parse(new CoreCapture(State()).Encode())!;
        var world = root["world"]!;
        var owner = field switch { "leases" => world, "lease" => world["next"]!, _ => world["inventories"]![0]! };
        owner.AsObject().Remove(field);
        AssertFailure(root.ToJsonString(), "/world");
    }

    [TestMethod]
    public void Decode_rejects_lease_corruption_and_globally_duplicate_reservation_jobs()
    {
        var state = State();
        var world = state.World;
        Invalid(world with { Leases = [world.Leases[0] with { Capacity = 0 }] }, "/world/leases/capacity");
        Invalid(world with { Leases = [world.Leases[0] with { ExpiresAtStep = 0 }] }, "/world/leases/expires_at_step");
        Invalid(world with { Leases = [world.Leases[0] with { Paid = new(-1) }] }, "/world/leases/paid");
        Invalid(world with { Next = world.Next with { Lease = 1 } }, "/world/next/lease");
        Invalid(world with { Inventories = world.Inventories.SetItem(0, world.Inventories[0] with { Reservations = world.Inventories[^1].Reservations }) }, "/world/inventories/reservations/job");
        var inventory = world.Inventories[^1];
        Invalid(world with { Inventories = world.Inventories.SetItem(world.Inventories.Length - 1,
            inventory with { Reservations = [inventory.Reservations[0] with { Capacity = 1 }] }) }, "/world/inventories/reservations/expected_returns");

        void Invalid(WorldState invalid, string field) => AssertFailure(new CoreCapture(state with { World = invalid }).Encode(), field);
    }

    private static CoreState State()
    {
        using var session = InventoryFixture.Session();
        var lease = InventoryFixture.Rent(session);
        var state = session.State;
        var world = state.World;
        return state with { World = world with
        {
            Inventories = world.Inventories.Select(i => i.Id == lease.Inventory ? i with
                { Reservations = [new(new(9), 3, [new(new GoodId.Voxel(WorldFixture.Material), 2)])] } : i).ToImmutableArray()
        } };
    }

    private static void AssertFailure(string payload, string field)
        => Assert.AreEqual(field, ((CoreResult<ICoreCapture>.Failure)CoreFactory.Create().DecodeState(payload)).Problems.Single().Field);
}
