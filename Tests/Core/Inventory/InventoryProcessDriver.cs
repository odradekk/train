using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.Sessions;
using Train.Core.Tests.World;

namespace Train.Core.Tests.Inventory;

internal static class InventoryProcessDriver
{
    internal static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var mode = args[0];
            var output = args[1];
            var statePath = args[2];
            CoreSession session;
            if (mode == "resume")
            {
                var factory = CoreFactory.Create();
                using var candidate = WorldFixture.Success(factory.PrepareRestore(
                    WorldFixture.Success(factory.DecodeState(await File.ReadAllTextAsync(statePath))),
                    InventoryFixture.Content(), WorldFixture.Options));
                session = (CoreSession)candidate.Activate();
            }
            else session = InventoryFixture.Session();
            using (session)
            {
                var receipts = new List<string>();
                var events = new List<string>();
                void Submit(CoreCommand command, bool accepted = true)
                {
                    var result = InventoryFixture.Submit(session, command);
                    if (accepted) Assert.IsInstanceOfType<CommandDecision.Accepted>(result.Receipt.Decision);
                    else Assert.IsInstanceOfType<CommandDecision.Rejected>(result.Receipt.Decision);
                    receipts.Add(CoreCheckpoints.ReceiptText(result.Receipt));
                    events.AddRange(result.Events.Select(Event));
                }
                void Step() => events.AddRange(InventoryFixture.Step(session).Events.Select(Event));

                var settlement = session.State.World.Settlements.Single();
                var train = session.State.World.Trains.Single();
                if (mode != "resume")
                {
                    Submit(new CoreCommand.ReplaceRunPlan(train.Id, new(false, [new PlanStop.Station(settlement.Node, 100, [])])));
                    Submit(new CoreCommand.StartTrain(train.Id));
                    Step();
                    Submit(new CoreCommand.AcquireLease(new LeaseRequest.Rent(settlement.Id, 20, 3), new(1000)));
                    var inventory = session.State.World.Leases.Single().Inventory;
                    foreach (var good in InventoryFixture.Goods)
                        Submit(new CoreCommand.UnloadGoods(train.Id, settlement.Id, inventory, good, 2));
                    Step(); Step();
                }
                var leaseId = session.State.World.Leases.Single().Id;
                var leaseInventory = InventoryFixture.Lease(session, leaseId).Inventory;
                var samples = new List<InventoryProcessSample>();
                void Sample()
                {
                    var capture = WorldFixture.Success(session.Capture(CapturePurpose.Save));
                    samples.Add(new(capture.Position, capture.Encode(), capture.Checkpoint.CanonicalText,
                        capture.Checkpoint.Sha256, Query(session, train.Inventory, leaseId)));
                }
                Sample();
                var prefixReceipts = receipts.ToArray();
                var prefixEvents = events.ToArray();
                receipts.Clear(); events.Clear();
                if (mode == "save") await File.WriteAllTextAsync(statePath, samples[0].Payload, new UTF8Encoding(false));
                else
                {
                    Step(); Sample();
                    Submit(new CoreCommand.UnloadGoods(train.Id, settlement.Id, leaseInventory, InventoryFixture.Voxel, 1), accepted: false); Sample();
                    Submit(new CoreCommand.LoadGoods(train.Id, settlement.Id, leaseInventory, InventoryFixture.Voxel, 1)); Sample();
                    Submit(new CoreCommand.AcquireLease(new LeaseRequest.Renew(leaseId, 3), new(0)), accepted: false); Sample();
                    Submit(new CoreCommand.AcquireLease(new LeaseRequest.Renew(leaseId, 3), new(1000))); Sample();
                    Submit(new CoreCommand.UnloadGoods(train.Id, settlement.Id, leaseInventory, InventoryFixture.Voxel, 1)); Sample();
                    for (var step = 0; step < 3; step++) { Step(); Sample(); }
                }
                await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new InventoryProcessResult(Environment.ProcessId,
                    samples.ToArray(), prefixReceipts, prefixEvents, receipts.ToArray(), events.ToArray())), new UTF8Encoding(false));
            }
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
    }

    private static string Query(ICoreSession session, InventoryId trainInventory, LeaseId leaseId)
    {
        var lease = InventoryFixture.Lease(session, leaseId);
        return JsonSerializer.Serialize(new
        {
            Lease = lease,
            Inventories = new[] { trainInventory, lease.Inventory }.Select(id =>
            {
                var inventory = InventoryFixture.ReadInventory(session, id);
                return new { inventory.Id, Owner = JsonSerializer.Serialize(inventory.Owner, inventory.Owner.GetType()),
                    Location = JsonSerializer.Serialize(inventory.Location, inventory.Location.GetType()),
                    inventory.Access, inventory.Capacity, inventory.Used, inventory.Reserved, inventory.Available,
                    inventory.GoodsMassTons, inventory.Reservations,
                    Stacks = inventory.Stacks.Select(stack => new { Good = JsonSerializer.Serialize(stack.Good, stack.Good.GetType()),
                        stack.Quantity, stack.DefinitionMissing }).ToArray() };
            }).ToArray()
        });
    }

    private static string Event(EventEnvelope envelope) => JsonSerializer.Serialize(new
    { envelope.Position, envelope.Ordinal, Kind = envelope.Event.GetType().Name,
        Value = envelope.Event is CoreEvent.GoodsTransferred transfer
            ? JsonSerializer.Serialize(new { transfer.Action, Good = JsonSerializer.Serialize(transfer.Good, transfer.Good.GetType()),
                transfer.Quantity, transfer.Source, transfer.Destination, transfer.ActualAmount, transfer.Train, transfer.Settlement })
            : JsonSerializer.Serialize(envelope.Event, envelope.Event.GetType()) });
}

internal sealed record InventoryProcessSample(SimulationPosition Position, string Payload, string CanonicalText,
    string Sha256, string Query);
internal sealed record InventoryProcessResult(int ProcessId, InventoryProcessSample[] Samples,
    string[] PrefixReceipts, string[] PrefixEvents, string[] Receipts, string[] Events);
