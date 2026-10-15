using System.Collections.Immutable;
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
        if (args[0].StartsWith("trade-", StringComparison.Ordinal)) return await Trades(args);
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
                    Submit(new CoreCommand.AcquireLease(new LeaseRequest.Renew(leaseId, 6), new(1000))); Sample();
                    foreach (var duration in new[] { 4UL, 39UL, 36UL })
                    {
                        Submit(new CoreCommand.AcquireLease(new LeaseRequest.Renew(leaseId, duration), new(1000)), accepted: false); Sample();
                    }
                    Submit(new CoreCommand.UnloadGoods(train.Id, settlement.Id, leaseInventory, InventoryFixture.Voxel, 1)); Sample();
                    for (var step = 0; step < 6; step++) { Step(); Sample(); }
                }
                await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new InventoryProcessResult(Environment.ProcessId,
                    samples.ToArray(), prefixReceipts, prefixEvents, receipts.ToArray(), events.ToArray())), new UTF8Encoding(false));
            }
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
    }

    internal static CoreContentProjection TradeContent()
    {
        var content = InventoryTradeTests.Content(value: 1, prices: new(new(0), new(10000), new(10000), new(0)));
        return content with
        {
            Designs = content.Designs.Select(d => d.Design == WorldFixture.Station ? d with
            { Parts = d.Parts.Select(p => p with { Capabilities = p.Capabilities.Add(new PartCapability.Construction(1, PlotSize.Small, SizeClass.Small)) }).ToImmutableArray() } : d).ToImmutableArray(),
            MarketRules = [content.MarketRules[0] with
            { VehiclePurchaseLimits = [new(SizeClass.Small, 1)], VehicleBuyMultiplier = new(10000), VehicleSellMultiplier = new(10000) }]
        };
    }

    internal static CoreSession TradeSession() => InventoryTradeTests.Session(TradeContent(),
        local: [new(InventoryFixture.Voxel, 3)], cargo: [new(InventoryTradeTests.Clothing, 3)]);

    private static async Task<int> Trades(string[] args)
    {
        try
        {
            var mode = args[0];
            var factory = CoreFactory.Create();
            using var candidate = mode == "trade-resume" ? WorldFixture.Success(factory.PrepareRestore(
                WorldFixture.Success(factory.DecodeState(await File.ReadAllTextAsync(args[2]))), TradeContent(), WorldFixture.Options)) : null;
            using var session = candidate is null ? TradeSession() : (CoreSession)candidate.Activate();
            var receipts = new List<string>();
            var events = new List<string>();
            var samples = new List<InventoryProcessSample>();
            var town = session.State.World.Settlements.Single();
            void Submit(CoreCommand command, bool accepted = true)
            {
                var result = InventoryFixture.Submit(session, command);
                if (accepted) Assert.IsInstanceOfType<CommandDecision.Accepted>(result.Receipt.Decision, result.Diagnostic?.Message);
                else Assert.IsInstanceOfType<CommandDecision.Rejected>(result.Receipt.Decision);
                receipts.Add(CoreCheckpoints.ReceiptText(result.Receipt));
                events.AddRange(result.Events.Select(Event));
            }
            void Sample()
            {
                var capture = WorldFixture.Success(session.Capture(CapturePurpose.Save));
                var settlement = ((Lookup<SettlementView>.Found)session.Read().FindSettlement(town.Id)).Value;
                samples.Add(new(capture.Position, capture.Encode(), capture.Checkpoint.CanonicalText, capture.Checkpoint.Sha256,
                    JsonSerializer.Serialize(new { settlement.VehiclesForSale, settlement.VehiclePurchaseCapacities,
                        Goods = settlement.Market.Goods.Select(g => new { Good = Core.World.WorldReferences.GoodKey(g.Good),
                            g.Stock, g.RestockTarget, g.SellableQuantity, g.ReceivableQuantity }), session.Read().Economy.Wallet })));
            }
            if (mode != "trade-resume")
            {
                Submit(new CoreCommand.BuyGoods(new(1), town.Id, town.Inventory, InventoryFixture.Voxel, 1, new(1)));
                Submit(new CoreCommand.SellGoods(new(1), town.Id, town.Inventory, InventoryTradeTests.Clothing, 1, new(1)));
                Submit(new CoreCommand.BuyGoods(new(1), town.Id, town.Inventory, InventoryFixture.Voxel, long.MaxValue, new(long.MaxValue)), false);
                Submit(new CoreCommand.SellGoods(new(1), town.Id, town.Inventory, InventoryTradeTests.Clothing, 1000, new(0)), false);
            }
            Sample();
            var prefixReceipts = receipts.ToArray();
            var prefixEvents = events.ToArray();
            receipts.Clear(); events.Clear();
            if (mode == "trade-save") await File.WriteAllTextAsync(args[2], samples[0].Payload, new UTF8Encoding(false));
            else
            {
                Submit(new CoreCommand.AcquireLease(new LeaseRequest.Rent(town.Id, 3, 3), new(1000)));
                var lease = session.State.World.Leases.Single();
                Submit(new CoreCommand.UnloadGoods(new(1), town.Id, lease.Inventory, InventoryFixture.Voxel, 1));
                Submit(new CoreCommand.UnloadGoods(new(1), town.Id, lease.Inventory, InventoryTradeTests.Clothing, 2));
                Submit(new CoreCommand.StopTrain(new(1)));
                Submit(new CoreCommand.DisbandTrain(new(1)));
                Submit(new CoreCommand.SellVehicle(town.Id, new(1), new(0))); Sample();
                Submit(new CoreCommand.SellVehicle(town.Id, new(1), new(0)), false); Sample();
                Submit(new CoreCommand.BuyVehicle(town.Id, new(1), new(1000))); Sample();
                Submit(new CoreCommand.BuyVehicle(town.Id, new(1), new(1000)), false); Sample();
            }
            await File.WriteAllTextAsync(args[1], JsonSerializer.Serialize(new InventoryProcessResult(Environment.ProcessId,
                samples.ToArray(), prefixReceipts, prefixEvents, receipts.ToArray(), events.ToArray())), new UTF8Encoding(false));
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
