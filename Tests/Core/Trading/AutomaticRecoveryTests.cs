using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.Tests.Inventory;
using Train.Core.Tests.World;

namespace Train.Core.Tests.Trading;

[TestClass]
public sealed class AutomaticRecoveryTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void Report_only_goods_can_be_removed_by_publication_and_restore_without_losing_history()
    {
        var good = new GoodId.Voxel(new("fixture:report-only"));
        var reference = new CoreDefinitionReference.Voxel(good.Definition);
        var content = InventoryTradeTests.Content();
        content = content with { Goods = content.Goods.Add(content.Goods[0] with { Good = good }),
            MarketRules = [content.MarketRules[0] with { RecentReportLimit = 10 }] };
        using var session = InventoryTradeTests.Session(content, local: [new(InventoryTradeTests.Clothing, 13)]);
        var settlement = session.State.World.Settlements.Single();
        InventoryFixture.Accept(session, new CoreCommand.StopTrain(new(1)));
        InventoryFixture.Accept(session, new CoreCommand.ReplaceRunPlan(new(1), new(false,
            [new PlanStop.Station(settlement.Node, 10, [new(new AutomaticGoodsAction.Buy(null), good, settlement.Inventory, 1)])])));
        Assert.IsTrue(WorldFixture.Success(session.Capture(CapturePurpose.Save)).References.Definitions
            .Any(d => d.Definition == reference));
        InventoryFixture.Accept(session, new CoreCommand.StartTrain(new(1)));
        InventoryFixture.Accept(session, new CoreCommand.BuyGoods(new(1), settlement.Id, settlement.Inventory,
            InventoryTradeTests.Clothing, 1, new(97)));
        var lease = InventoryFixture.Rent(session);
        InventoryFixture.Accept(session, new CoreCommand.UnloadGoods(new(1), settlement.Id, lease.Inventory,
            InventoryTradeTests.Clothing, 1));
        InventoryFixture.Accept(session, new CoreCommand.ReplaceRunPlan(new(1), new(false,
            [new PlanStop.Station(settlement.Node, 10, [])])));
        InventoryFixture.Accept(session, new CoreCommand.DisbandTrain(new(1)));
        var original = session.Read().Economy.RecentRunReports.Single(r => !r.Totals.IncompleteRules.IsEmpty);
        Assert.AreEqual(new Money(97), original.Totals.PurchasePayments);
        Assert.IsNull(original.Totals.LoadedCapacityRatio);

        var removed = content with { Identity = new(new string('F', 64)),
            Goods = content.Goods.Where(g => g.Good != good).ToImmutableArray() };
        var capture = WorldFixture.Success(session.Capture(CapturePurpose.Save));
        InventoryFixture.Accept(session, new CoreCommand.PublishContent(removed));
        AssertHistory(session);
        var factory = CoreFactory.Create();
        var decoded = WorldFixture.Success(factory.DecodeState(capture.Encode()));
        Assert.AreEqual(capture.Encode(), decoded.Encode());
        Assert.IsInstanceOfType<CheckpointComparison.Equal>(WorldFixture.Success(
            factory.CompareCheckpoints(capture.Checkpoint, decoded.Checkpoint)));
        using var candidate = WorldFixture.Success(factory.PrepareRestore(decoded, removed, WorldFixture.Options));
        Assert.IsTrue(candidate.MissingReferences.IsEmpty);
        using var restored = candidate.Activate();
        AssertHistory(restored);

        void AssertHistory(ICoreSession current)
        {
            var report = current.Read().Economy.RecentRunReports.Single(r => !r.Totals.IncompleteRules.IsEmpty);
            Assert.AreEqual(good, report.Totals.IncompleteRules.Single().Good);
            Assert.AreEqual(original.Totals.PurchasePayments, report.Totals.PurchasePayments);
            Assert.AreEqual(JsonSerializer.Serialize(original), JsonSerializer.Serialize(report));
            Assert.IsFalse(WorldFixture.Success(current.Capture(CapturePurpose.Save)).References.Definitions
                .Any(d => d.Definition == reference));
        }
    }

    [TestMethod]
    [TestCategory("integration")]
    public async Task Servicing_save_resumes_in_another_process_with_identical_trades_reports_and_boundaries()
    {
        var directory = Path.Combine(TestContext.TestRunResultsDirectory!, "automatic-process-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var complete = await Run(directory, "complete");
        var saved = await Run(directory, "save");
        var resumed = await Run(directory, "resume");
        Assert.AreEqual(3, new[] { complete.ProcessId, saved.ProcessId, resumed.ProcessId }.Distinct().Count());
        Assert.IsTrue(new[] { complete.ProcessId, saved.ProcessId, resumed.ProcessId }.All(id => id != Environment.ProcessId));
        Assert.AreEqual(complete.Samples[0], saved.Samples.Single());
        CollectionAssert.AreEqual(complete.Samples, resumed.Samples);
        CollectionAssert.AreEqual(complete.Events, resumed.Events);
        using var prefix = JsonDocument.Parse(saved.Samples.Single().Query);
        Assert.AreEqual(JsonValueKind.Null, prefix.RootElement.GetProperty("RecentRunReports")[0]
            .GetProperty("Totals").GetProperty("LoadedCapacityRatio").ValueKind);
        Assert.AreEqual((int)TrainActivity.Servicing, prefix.RootElement.GetProperty("Activity").GetInt32());
        Assert.AreEqual(307L, prefix.RootElement.GetProperty("CurrentRun").GetProperty("PurchasePayments").GetProperty("Value").GetInt64());
        Assert.AreEqual(1, prefix.RootElement.GetProperty("CurrentRun").GetProperty("IncompleteRules").GetArrayLength());
        using var final = JsonDocument.Parse(resumed.Samples[^1].Query);
        Assert.AreEqual(JsonValueKind.Null, final.RootElement.GetProperty("CurrentRun").ValueKind);
        Assert.IsTrue(final.RootElement.GetProperty("RecentRunReports").GetArrayLength() > 0);
        Assert.IsTrue(resumed.Events.Any(e => e.Contains("AutomaticTradeEvaluated", StringComparison.Ordinal)));
        Assert.IsTrue(resumed.Events.Any(e => e.Contains("RunReported", StringComparison.Ordinal)));
        Directory.Delete(directory, recursive: true);
    }

    [TestMethod]
    public void Automatic_rules_replay_through_production_commands_and_match_each_servicing_boundary()
    {
        using var session = AutomaticProcessDriver.Session();
        AutomaticProcessDriver.Begin(session);
        var captures = new List<ICoreCapture> { WorldFixture.Success(session.Capture(CapturePurpose.Save)) };
        for (var step = 0; step < 10; step++)
        {
            InventoryFixture.Step(session);
            captures.Add(WorldFixture.Success(session.Capture(CapturePurpose.Save)));
        }
        var recording = session.ReadRecording();
        var input = new CoreReplayInput(2, WorldFixture.Options.Runtime, recording.Starts[0].State.Encode(),
            recording.Commands, captures.Select(c => c.Checkpoint).ToImmutableArray(), recording.EndPosition, null);
        using var replay = WorldFixture.Success(CoreFactory.Create().PrepareReplay(input, WorldFixture.Options.Runtime,
            [AutomaticProcessDriver.Content()]));
        foreach (var capture in captures)
        {
            var result = replay.Advance(20, capture.Position);
            Assert.IsNull(result.Difference);
            Assert.IsNull(result.Fault);
            Assert.AreEqual(capture.Encode(), replay.CaptureBoundary().Encode());
        }
        var reports = session.Read().Economy.RecentRunReports;
        Assert.IsTrue(reports.Length > 0);
        Assert.IsTrue(reports.All(report => report.Totals.LoadedCapacityRatio is null));
    }

    [TestMethod]
    [DataRow("current_run")]
    [DataRow("automatic_evaluation_remaining_steps")]
    [DataRow("recent_run_reports")]
    public void Automatic_state_fields_are_required_even_when_nullable(string field)
    {
        using var session = AutomaticProcessDriver.Session();
        AutomaticProcessDriver.Begin(session);
        var root = JsonNode.Parse(WorldFixture.Success(session.Capture(CapturePurpose.Save)).Encode())!;
        var world = root["world"]!;
        var owner = field == "recent_run_reports" ? world : world["trains"]![0]!;
        Assert.IsTrue(owner.AsObject().Remove(field));
        AssertInvalid(root, "/world");
    }

    [TestMethod]
    [DataRow("loaded_units_distance")]
    [DataRow("capacity_distance")]
    [DataRow("unordered_rules")]
    [DataRow("out_of_range_rule")]
    [DataRow("out_of_range_stop")]
    [DataRow("future_start")]
    [DataRow("future_report")]
    [DataRow("negative_ratio")]
    public void Automatic_state_corruption_is_rejected_without_activating_a_session(string corruption)
    {
        using var session = AutomaticProcessDriver.Session();
        AutomaticProcessDriver.Begin(session);
        var root = JsonNode.Parse(WorldFixture.Success(session.Capture(CapturePurpose.Save)).Encode())!;
        var world = root["world"]!;
        var run = world["trains"]![0]!["current_run"]!;
        var field = "/world/trains/current_run";
        switch (corruption)
        {
            case "loaded_units_distance":
            case "capacity_distance":
                run[corruption] = -1;
                break;
            case "unordered_rules":
                var rules = run["incomplete_rules"]!.AsArray();
                Assert.AreEqual(1, rules.Count);
                var earlier = rules[0]!.DeepClone();
                earlier["rule_index"] = 0;
                earlier["good"] = world["trains"]![0]!["plan"]!["stops"]![0]!["goods_rules"]![0]!["good"]!.DeepClone();
                rules.Add(earlier);
                field += "/incomplete_rules";
                break;
            case "out_of_range_rule":
                run["incomplete_rules"]![0]!["rule_index"] = 99;
                field += "/incomplete_rules";
                break;
            case "out_of_range_stop":
                run["incomplete_rules"]![0]!["stop_index"] = 99;
                field += "/incomplete_rules";
                break;
            case "future_start":
                run["started_at"]!["completed_steps"] = ulong.MaxValue;
                field += "/started_at";
                break;
            case "future_report":
            case "negative_ratio":
                for (var step = 0; step < 10; step++) InventoryFixture.Step(session);
                root = JsonNode.Parse(WorldFixture.Success(session.Capture(CapturePurpose.Save)).Encode())!;
                var reports = root["world"]!["recent_run_reports"]!.AsArray();
                Assert.IsTrue(reports.Count > 0);
                if (corruption == "future_report")
                {
                    reports[reports.Count - 1]!["ended_at"]!["completed_steps"] = ulong.MaxValue;
                    field = "/world/recent_run_reports/ended_at";
                }
                else
                {
                    reports[reports.Count - 1]!["totals"]!["loaded_capacity_ratio"] = -1;
                    field = "/world/recent_run_reports/totals";
                }
                break;
        }
        AssertInvalid(root, field);
    }

    private static void AssertInvalid(JsonNode root, string fieldPrefix)
    {
        var result = CoreFactory.Create().DecodeState(root.ToJsonString());
        var failure = Assert.IsInstanceOfType<CoreResult<ICoreCapture>.Failure>(result);
        Assert.AreEqual("invalid_state", failure.Problems.Single().Code);
        Assert.IsTrue(failure.Problems.Single().Field.StartsWith(fieldPrefix, StringComparison.Ordinal),
            failure.Problems.Single().Field);
    }

    private async Task<AutomaticProcessResult> Run(string directory, string mode)
    {
        var output = Path.Combine(directory, mode + ".json");
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = directory };
        foreach (var argument in new[] { typeof(AutomaticRecoveryTests).Assembly.Location, "--automatic-process", mode,
            output, Path.Combine(directory, "state.json") }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new AssertFailedException("无法启动自动交易子进程。");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(TestContext.CancellationToken); }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            await File.WriteAllTextAsync(Path.Combine(directory, mode + ".stdout.log"), await stdout);
            await File.WriteAllTextAsync(Path.Combine(directory, mode + ".stderr.log"), await stderr);
        }
        Assert.AreEqual(0, process.ExitCode, await File.ReadAllTextAsync(Path.Combine(directory, mode + ".stderr.log")));
        var result = JsonSerializer.Deserialize<AutomaticProcessResult>(await File.ReadAllTextAsync(output))!;
        Assert.AreEqual(process.Id, result.ProcessId);
        return result;
    }
}
