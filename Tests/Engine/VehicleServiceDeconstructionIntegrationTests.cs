using System.Text.Json.Nodes;
using Chickensoft.GoDotTest;
using Godot;
using Train.App;
using Train.App.Assets;
using Train.App.Logging;
using Train.App.Recording;
using Train.App.Saves;
using Train.Core.Contracts;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public class VehicleServiceDeconstructionIntegrationTests(Node testScene) : TestClass(testScene)
{
    [Test]
    public async Task Owned_deconstruction_pause_survives_disk_restore_and_strict_replay_without_duplicate_returns()
    {
        var directory = ContentFixture.CopyBuiltIn();
        try
        {
            PrepareServiceWorldContent(directory);
            using var dispatcher = new GodotMainThreadDispatcher();
            using var logger = new TrainLogger(Path.Combine(directory, "logs"), LogLevel.Info, new GodotLogOutput());
            using var host = ContentFixture.CreateHost(logger, directory, dispatcher);
            using var restored = ContentFixture.CreateHost(logger, directory, dispatcher);
            async Task Pump(HostCoordinator current, TimeSpan? timeout = null) => await FramesUntil(TestScene, () =>
            { current.ProcessFrame(0); return !current.IsBusy; }, dispatcher, timeout);
            Require(host.BeginNewGame(new("core:basic-through-world"), 1, "stage1-world-v1", host.SessionOptions!).Problem is null, "拆解新局请求失败。");
            await Pump(host, ContentPreparationTimeout);
            var initial = Saved(host);
            // 公开生成入口只创建 NPC 聚居地，占领命令尚未实现；仅此初始夹具调整归属，随即交给真实恢复校验。
            var ownedPayload = OwnedStartingSave(initial.Core.Encode());
            Require(host.BeginRestore(ownedPayload, initial.Content.Encode(), host.SessionOptions!, initial.Identity).Problem is null, "自有初始存档恢复请求失败。");
            await Pump(host, ContentPreparationTimeout);
            var view = host.GetStatus()!.View;
            var train = Found(view.FindTrain(view.World.Trains.Single().Id));
            var settlement = Found(view.FindSettlement(((NodeOccupant.Settlement)view.World.Nodes.Single(n => n.Id == ((TrainPosition.Node)train.Position).Id).Occupant).Id));
            Require(settlement.Relation == SettlementRelation.PlayerOwned, "真实恢复未建立自有起始聚居地。");
            Accept(host, new CoreCommand.DisbandTrain(train.Id));
            var target = train.Vehicles.Select(id => Found(host.GetStatus()!.View.FindInstance(new InstanceId.Vehicle(id))))
                .Single(instance => instance.Design == host.Content!.Blueprints.Single(b => b.Display.Name == "拆解最小车辆").Design);
            var facility = settlement.Plots.Select(p => p.Occupant).OfType<PlotOccupant.Building>()
                .Select(p => new FacilityKey(p.Id, CapabilityKind.Construction))
                .Select(key => (Key: key, Quote: host.GetStatus()!.View.GetJobQuote(new JobRequest.Dismantle(key, target.Id, 0), JobFunding.OwnInventory)))
                .First(item => item.Quote is CoreResult<JobQuote>.Success { Value.Blockers.IsEmpty: true }).Key;
            var started = Accept(host, new CoreCommand.StartOwnedJob(new JobRequest.Dismantle(facility, target.Id, 0)));
            var job = started.Events.Select(e => e.Event).OfType<CoreEvent.ConstructionStarted>().Single().Job.Id;
            Require(Found(host.GetStatus()!.View.FindSettlement(settlement.Id)).OwnedJobs.Contains(job), "真实自有任务未列入聚居地查询。");
            Require(host.Step(1).Completed == 1, "拆解首步失败。");
            Accept(host, new CoreCommand.SetDeconstructionPaused(job, true));
            var paused = ((JobView.Construction)Found(host.GetStatus()!.View.FindJob(job))).Value;
            var inventory = Found(host.GetStatus()!.View.FindInventory(settlement.Inventory));
            var reservation = inventory.Reservations.Single(r => r.Job == job);
            Require(reservation.Capacity > 0 && !reservation.ExpectedReturns.IsEmpty, "拆解未预留真实返还容量。");
            await SaveAndRestore();
            Require(host.Step(3).Completed == 3 && restored.Step(3).Completed == 3, "暂停步进失败。");
            Require(((JobView.Construction)Found(host.GetStatus()!.View.FindJob(job))).Value.ProgressWork == paused.ProgressWork
                && Saved(host).Core.Encode() == Saved(restored).Core.Encode(), "暂停恢复后施工推进或核心状态分歧。");
            Accept(host, new CoreCommand.SetDeconstructionPaused(job, false));
            Accept(restored, new CoreCommand.SetDeconstructionPaused(job, false));
            CoreEvent.ConstructionCompleted? completion = null;
            host.BoundaryCompleted += (_, _, _, events) => completion ??= events.Select(e => e.Event).OfType<CoreEvent.ConstructionCompleted>().SingleOrDefault(e => e.Job == job);
            for (var step = 0; step < 600 && completion is null; step++)
            {
                Require(host.Step(1).Completed == 1 && restored.Step(1).Completed == 1, "恢复拆解推进失败。");
                if (step % 50 == 49) await TestScene.ToSignal(TestScene.GetTree(), SceneTree.SignalName.ProcessFrame);
            }
            Require(completion is not null && host.GetStatus()!.View.FindInstance(target.Id) is Lookup<InstanceView>.Missing
                && host.GetStatus()!.View.FindJob(job) is Lookup<JobView>.Missing, "拆解没有删除实例及任务。");
            Require(completion!.ReturnedGoods.SequenceEqual(reservation.ExpectedReturns), "完整车辆的拆解返还与核心预约不一致。");
            Require(Saved(host).Core.Encode() == Saved(restored).Core.Encode(), "磁盘恢复后的拆解终态不一致。");
            var after = Found(host.GetStatus()!.View.FindInventory(settlement.Inventory));
            Require(after.Reservations.All(r => r.Job != job), "拆解完成未释放返还预约。");
            foreach (var returned in completion!.ReturnedGoods)
                Require(Quantity(after, returned.Good) == Quantity(inventory, returned.Good) + returned.Quantity, "拆解返还未恰好进入当地自有库存一次。");
            var final = Saved(host).Core.Encode();
            var recording = Recorded(host);
            var path = Path.Combine(directory, "deconstruction.recording.zip");
            RecordingPackage.Capture(recording.Window, recording.Content, recording.Runtime).Write(path);
            Require(restored.BeginReplay(path).Problem is null, "拆解严格重放请求失败。");
            await Pump(restored);
            ReplayAdvanceResult? replay = null;
            for (var batch = 0; batch < 20 && replay?.Stop != ReplayStop.EndOfRecording; batch++)
            {
                replay = restored.AdvanceReplay(50);
                Require(replay is { Difference: null, Fault: null }, "拆解严格重放分歧：" + replay);
                await TestScene.ToSignal(TestScene.GetTree(), SceneTree.SignalName.ProcessFrame);
            }
            Require(replay?.Stop == ReplayStop.EndOfRecording && Saved(restored).Core.Encode() == final,
                "拆解严格重放终态不同或重复返还。");

            async Task SaveAndRestore()
            {
                var capture = Saved(host);
                var store = new FileSaveStore(directory);
                var path = Path.Combine(directory, "paused.save.json");
                Require(store.Write(path, capture.Encode(capture.Identity, DateTimeOffset.UtcNow), false).Published, "拆解存档未发布。");
                var reportRoot = Path.GetDirectoryName(System.Environment.GetEnvironmentVariable("TRAIN_ENGINE_REPORT"))!;
                File.Copy(path, Path.Combine(reportRoot, "vehicle-service-paused.save.json"), true);
                var document = store.Read(path);
                Require(restored.BeginRestore(document.CorePayload, document.ContentPayload, host.SessionOptions!, capture.Identity).Problem is null, "拆解恢复请求失败。");
                await Pump(restored, ContentPreparationTimeout);
                Require(Saved(restored).Core.Encode() == capture.Core.Encode(), "拆解存档恢复重复改变任务、库存或返还。");
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    internal static void PrepareServiceWorldContent(string directory)
    {
        File.WriteAllText(Path.Combine(directory, "resources", "blueprints", "engine-deconstruction.blueprint.json"), """
            {"schema_version":3,"id":"engine-deconstruction","category":"vehicle","simulation":{"size_class":"small"},
             "display":{"name":"拆解最小车辆","description":"测试输入"},
             "layout":{"palette":[{"voxel":"bogie"},{"voxel":"structural-metal"}],"cells":[[0,0,0,0],[0,1,0,1]]}}
            """);
        var path = Path.Combine(directory, "resources", "generation-profiles", "basic-through-world.generation-profile.json");
        var profile = JsonNode.Parse(File.ReadAllText(path))!;
        profile["start"]!["trains"] = JsonNode.Parse("""[{"vehicles":["small-locomotive","engine-deconstruction"],"inventory":[]}]""");
        var configuration = profile["configuration"]!;
        configuration["width_meters"] = 600;
        configuration["height_meters"] = 600;
        configuration["candidate_probability"] = 1;
        configuration["settlement_ratio"] = 1;
        configuration["lair_ratio"] = 0;
        configuration["lair_weights"] = new JsonArray();
        configuration["initial_railway"]!["additional_cycle_edges"] = 0;
        configuration["lake_elevation_threshold"] = -100;
        configuration["river_band_half_width"] = 0;
        configuration["start_constraints"]!["minimum_reachable_non_hostile_settlements"] = 0;
        configuration["start_constraints"]!["minimum_reachable_empty_nodes"] = 0;
        configuration["start_constraints"]!["required_affordable_commission_design"] = "engine-deconstruction";
        configuration["npc_initialization"]!["minimum_for_sale_vehicles"] = 0;
        configuration["npc_initialization"]!["maximum_for_sale_vehicles"] = 0;
        configuration["npc_initialization"]!["for_sale_vehicle_candidates"] = new JsonArray();
        configuration["npc_initialization"]!["output_reserve_hours"] = 0;
        File.WriteAllText(path, profile.ToJsonString());
        foreach (var templatePath in Directory.EnumerateFiles(Path.Combine(directory, "resources", "settlement-templates"), "*.json"))
        {
            var template = JsonNode.Parse(File.ReadAllText(templatePath))!;
            template["plots"] = JsonNode.Parse("""
                [{"id":1,"size":"small_station","at":[-104,1,-176],"orientation":"px_py","building":"small-station"},
                 {"id":2,"size":"medium","at":[-224,1,0],"orientation":"px_py","building":"vehicle-workshop"},
                 {"id":3,"size":"small","at":[0,1,0],"orientation":"px_py","building":"small-warehouse"},
                 {"id":4,"size":"medium","at":[224,1,0],"orientation":"px_py","building":"power-station"}]
                """);
            template["founding"] = JsonNode.Parse("""[{"plot":1,"building":"small-station"}]""");
            template["production_plans"] = new JsonArray();
            template["inventory"] = JsonNode.Parse("""[{"voxel":"bogie","quantity":16},{"voxel":"structural-metal","quantity":100},{"voxel":"fuel","quantity":100}]""");
            File.WriteAllText(templatePath, template.ToJsonString());
        }
        var usedSimulationBlueprints = new HashSet<string>(StringComparer.Ordinal)
        {
            "small-station", "vehicle-workshop", "small-warehouse", "power-station",
            "small-locomotive", "small-freight-wagon", "engine-deconstruction"
        };
        foreach (var blueprintPath in Directory.EnumerateFiles(Path.Combine(directory, "resources", "blueprints"), "*.json"))
        {
            var blueprint = JsonNode.Parse(File.ReadAllText(blueprintPath))!;
            var category = blueprint["category"]!.GetValue<string>();
            var id = blueprint["id"]!.GetValue<string>();
            if (category is "vehicle" or "building" && !usedSimulationBlueprints.Contains(id))
                File.Delete(blueprintPath);
            else if (category == "scene_model")
            {
                // 服务测试只需要场景引用和真实资产边界，保留大场景装饰会在每个重放内容世代重复求值。
                var layout = blueprint["layout"]!;
                layout["cells"] = new JsonArray(layout["cells"]!.AsArray()[0]!.DeepClone());
                File.WriteAllText(blueprintPath, blueprint.ToJsonString());
            }
        }
    }

    private static string OwnedStartingSave(string payload)
    {
        var root = JsonNode.Parse(payload)!;
        var world = root["world"]!;
        var settlements = world["settlements"]!.AsArray();
        foreach (var settlement in settlements) settlement!["relation"] = (int)SettlementRelation.PlayerOwned;
        foreach (var node in world["nodes"]!.AsArray())
            if (node!["occupant"]?["$kind"]?.GetValue<string>() == "settlement") node["occupant"]!["relation"] = (int)SettlementRelation.PlayerOwned;
        foreach (var instance in world["instances"]!.AsArray()) instance!["ownership"] = (int)SettlementRelation.PlayerOwned;
        return root.ToJsonString();
    }

    private static SubmissionResult.Completed Accept(HostCoordinator host, CoreCommand command)
    {
        var result = host.Submit(host.GetStatus()!.Binding, command, CommandSource.AutomationTest);
        Require(result is SubmissionResult.Completed { Receipt.Decision: CommandDecision.Accepted }, "真实拆解命令被拒绝：" + result);
        return (SubmissionResult.Completed)result;
    }
    private static T Found<T>(Lookup<T> result) where T : notnull => result is Lookup<T>.Found found ? found.Value
        : throw new InvalidOperationException("真实拆解对象不存在：" + result);
    private static SaveCapture Saved(HostCoordinator host)
    {
        var saved = host.CaptureSave();
        if (saved.Failure is not null || saved.Capture is not { } capture)
            throw new InvalidOperationException("保存失败：" + saved.Failure?.Code + " " + saved.Failure?.Message);
        return capture;
    }
    private static RecordingSnapshot Recorded(HostCoordinator host)
    {
        var binding = host.GetStatus()!.Binding;
        if (host.ReadRecording(binding) is not { } window) throw new InvalidOperationException("没有可录制的窗口。");
        var recorded = host.CaptureRecording(binding, window);
        if (recorded.Failure is not null || recorded.Snapshot is not { } snapshot)
            throw new InvalidOperationException("录制失败：" + recorded.Failure?.Code + " " + recorded.Failure?.Message);
        return snapshot;
    }
    private static long Quantity(InventoryView inventory, GoodId good) => inventory.Stacks.SingleOrDefault(s => s.Good == good)?.Quantity ?? 0;
}
