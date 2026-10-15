using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.Sessions;
using Train.Core.Tests.Inventory;
using Train.Core.Tests.World;

namespace Train.Core.Tests.Trading;

internal static class AutomaticProcessDriver
{
    internal static CoreContentProjection Content()
    {
        var content = InventoryTradeTests.Content();
        return content with { MarketRules = [content.MarketRules[0] with { AutomaticEvaluationIntervalTicks = 3 }] };
    }

    internal static CoreSession Session() => InventoryTradeTests.Session(Content(),
        local: [new(InventoryTradeTests.Clothing, 13)]);

    internal static void Begin(ICoreSession session)
    {
        var town = ((Lookup<SettlementView>.Found)session.Read().FindSettlement(new(1))).Value;
        InventoryFixture.Accept(session, new CoreCommand.StopTrain(new(1)));
        InventoryFixture.Accept(session, new CoreCommand.ReplaceRunPlan(new(1), new(false,
            [new PlanStop.Station(town.Node, 8,
                [new(new AutomaticGoodsAction.Buy(null), InventoryTradeTests.Clothing, town.Inventory, 3),
                 new(new AutomaticGoodsAction.Buy(null), InventoryFixture.Voxel, town.Inventory, 1)])])));
        InventoryFixture.Accept(session, new CoreCommand.StartTrain(new(1)));
        InventoryFixture.Step(session);
        InventoryFixture.Step(session);
    }

    internal static AutomaticSample Sample(ICoreSession session)
    {
        var capture = WorldFixture.Success(session.Capture(CapturePurpose.Save));
        var train = ((Lookup<TrainView>.Found)session.Read().FindTrain(new(1))).Value;
        return new(capture.Encode(), capture.Checkpoint.CanonicalText, JsonSerializer.Serialize(new
        { train.Activity, train.CurrentRun, train.RecentReports,
            Cargo = ((Lookup<InventoryView>.Found)session.Read().FindInventory(train.Inventory)).Value.Stacks.Select(s => new { Good = s.Good.ToString(), s.Quantity }),
            session.Read().Economy.Wallet,
            session.Read().Economy.RecentRunReports }));
    }

    internal static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var factory = CoreFactory.Create();
            using var candidate = args[0] == "resume" ? WorldFixture.Success(factory.PrepareRestore(
                WorldFixture.Success(factory.DecodeState(await File.ReadAllTextAsync(args[2]))), Content(), WorldFixture.Options)) : null;
            using ICoreSession session = candidate is null ? Session() : candidate.Activate();
            if (args[0] != "resume") Begin(session);
            var samples = new List<AutomaticSample> { Sample(session) };
            var events = new List<string>();
            if (args[0] == "save") await File.WriteAllTextAsync(args[2], samples[0].Payload);
            else for (var step = 0; step < 10; step++)
            {
                var result = InventoryFixture.Step(session);
                events.AddRange(result.Events.Select(e => JsonSerializer.Serialize(new
                { e.Position, e.Ordinal, Kind = e.Event.GetType().Name, Value = JsonSerializer.Serialize(e.Event, e.Event.GetType()) })));
                samples.Add(Sample(session));
            }
            await File.WriteAllTextAsync(args[1], JsonSerializer.Serialize(new AutomaticProcessResult(Environment.ProcessId, samples.ToArray(), events.ToArray())));
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
    }
}

internal sealed record AutomaticSample(string Payload, string Checkpoint, string Query);
internal sealed record AutomaticProcessResult(int ProcessId, AutomaticSample[] Samples, string[] Events);
