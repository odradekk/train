using System.Collections.Immutable;
using System.Text.Json;
using Chickensoft.GoDotTest;
using Chickensoft.GodotTestDriver;
using Godot;
using Train.App;
using Train.App.Assets;
using Train.App.Development;
using Train.App.Saves;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public class ObservationIndependenceTests(Node testScene) : TestClass(testScene)
{
    [Test]
    [Chickensoft.GoDotTest.Timeout(300000)]
    public async Task Real_world_checkpoints_do_not_depend_on_frame_intervals_speed_pause_or_scene_observation()
    {
        var measured = System.Diagnostics.Stopwatch.StartNew();
        using var recipe = JsonDocument.Parse(System.IO.File.ReadAllText(
            ProjectSettings.GlobalizePath("res://examples/basic-through/recipe.json")));
        var reference = await Run(recipe.RootElement, new("reference", [1d / 60], SimulationSpeed.One));
        foreach (var variant in new Variant[]
        {
            new("30Hz-2x", [1d / 30], SimulationSpeed.Two),
            new("120Hz-4x", [1d / 120], SimulationSpeed.Four),
            new("irregular-8x", [0.1, 0.005, 0.023, 1d / 60, 0.04], SimulationSpeed.Eight),
            new("speed-switches-and-pauses", [1d / 60], SimulationSpeed.One, Pauses: true, SwitchSpeed: true),
            new("scene-switches", [1d / 30, 1d / 120], SimulationSpeed.Eight, Scenes: true)
        }) await Run(recipe.RootElement, variant, reference);
        GD.Print($"OBSERVATION_MEASUREMENT case_ms={measured.Elapsed.TotalMilliseconds:F3}");
    }

    [Test]
    [Chickensoft.GoDotTest.Timeout(180000)]
    public async Task Same_saved_world_has_identical_checkpoints_at_one_and_eight_times_speed()
    {
        using var recipe = JsonDocument.Parse(System.IO.File.ReadAllText(
            ProjectSettings.GlobalizePath("res://examples/basic-through/recipe.json")));
        var owned = new HashSet<Guid>();
        try
        {
            SaveEntry saved;
            var fixture = new Fixture(TestScene.GetTree());
            try
            {
                var app = await fixture.LoadAndAddScene<Train.App.App>("res://App/App.tscn");
                await FramesUntil(TestScene, () => !app.Host!.IsBusy, timeout: ContentPreparationTimeout);
                app.SetProcess(false);
                var host = app.Host!;
                var saves = app.Saves!;
                saves.SetAutoSaveInterval(null);
                Require(host.BeginNewGame(new(recipe.RootElement.GetProperty("generation_profile").GetString()!),
                    recipe.RootElement.GetProperty("seed").GetUInt64(),
                    recipe.RootElement.GetProperty("generator_version").GetString()!,
                    host.SessionOptions!).Problem is null, "无法生成测试自己的存档世界。");
                await FramesUntil(TestScene, () => { app._Process(0); return !host.IsBusy; }, timeout: ContentPreparationTimeout);
                var identity = saves.Status!.Identity;
                owned.Add(identity.Id);
                Require(saves.Save() is null, "无法通过生产存档路径保存测试世界。");
                await FramesUntil(TestScene, () => { app._Process(0); return !saves.HasPendingWork; });
                var store = new FileSaveStore(OS.GetUserDataDir());
                var path = store.PathFor(identity.Id);
                saved = new(path, store.ReadHeader(path), false);
                Require(!saves.Status!.Unsaved && saved.Header.Id == identity.Id,
                    "测试存档未完成发布或身份不符。");
            }
            finally { await fixture.Cleanup(); }

            var savedBytes = System.IO.File.ReadAllBytes(saved.Path);
            var reference = await Run(recipe.RootElement,
                new("saved-1x", [1d / 60], SimulationSpeed.One), saved: saved);
            await Run(recipe.RootElement, new("saved-8x", [1d / 60], SimulationSpeed.Eight), reference, saved);
            await Run(recipe.RootElement,
                new("saved-irregular-8x", [0.1, 0.005, 0.023, 1d / 60, 0.04], SimulationSpeed.Eight), reference, saved);
            Require(savedBytes.SequenceEqual(System.IO.File.ReadAllBytes(saved.Path)),
                "倍速变体改写了共同的起始存档。");
        }
        finally { UiTestInput.DeleteSaves(owned); }
    }

    [Test]
    [Chickensoft.GoDotTest.Timeout(900000)]
    public async Task Standard_world_host_observation_matches_direct_core_advance()
    {
        // 参考结果不经过宿主帧驱动：直接用核心和内容工厂恢复同一存档再推进，
        // 所有宿主变体共有的驱动错误也会暴露出来。
        var path = ProjectSettings.GlobalizePath("res://examples/standard-world/save.json");
        var bytes = System.IO.File.ReadAllBytes(path);
        var document = new FileSaveStore(Path.GetDirectoryName(path)!).Read(path);
        var reference = AdvanceDirectly(document, segments: 6, steps: 100);
        foreach (var variant in new Variant[]
        {
            new("standard-1x", [1d / 60], SimulationSpeed.One),
            new("standard-irregular-8x-pauses", [0.1, 0.005, 0.023, 1d / 60, 0.04], SimulationSpeed.Eight, Pauses: true),
            new("standard-scenes-8x", [1d / 30, 1d / 120], SimulationSpeed.Eight, Scenes: true)
        }) await RunStandardWorld(document, variant, reference);
        Require(bytes.SequenceEqual(System.IO.File.ReadAllBytes(path)), "变体改写了已提交的标准世界存档。");
    }

    private static ImmutableArray<CoreCheckpoint> AdvanceDirectly(SaveDocument document, int segments, uint steps)
    {
        var core = Train.Core.CoreFactory.Create();
        var content = Train.Content.ContentFactory.Create();
        var mods = Path.Combine(Path.GetTempPath(), "engine-observation-mods-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(mods);
        try
        {
            using var dispatcher = new GodotMainThreadDispatcher();
            var state = Value(core.DecodeState(document.CorePayload));
            var embedded = Value(content.DecodeCapture(document.ContentPayload));
            var files = new ContentFiles(new Dictionary<ContentRoot, ContentRootLocation>
            {
                [new("builtin")] = new ContentRootLocation.Disk(ProjectSettings.GlobalizePath("res://builtin/core")),
                [new("mods")] = new ContentRootLocation.Disk(mods)
            });
            using var loaded = Value(content.PrepareLoad(new(new(new("builtin"), new("mods"), null,
                ProjectSettings.GetSetting("application/config/version").AsString(), []),
                new ContentLoadPurpose.Restore(embedded, state.References)), files, new GodotAssetInspector(dispatcher)));
            using var candidate = Value(core.PrepareRestore(state, loaded.View.Core,
                new(new("engine-observation", "core-session-v1", "Windows", "x64", "10"), false, 1)));
            Require(candidate.MissingReferences.IsEmpty, "直接恢复标准世界缺少引用。");
            loaded.Activate();
            using var session = candidate.Activate();
            var checkpoints = ImmutableArray.CreateBuilder<CoreCheckpoint>();
            checkpoints.Add(Value(session.Capture(CapturePurpose.Save)).Checkpoint);
            for (var segment = 0; segment < segments; segment++)
            {
                var result = session.Advance(steps);
                Require(result.Fault is null && result.CompletedSteps == steps, $"直接推进失败：{result.Fault}");
                checkpoints.Add(Value(session.Capture(CapturePurpose.Save)).Checkpoint);
            }
            return checkpoints.ToImmutable();
        }
        finally { Directory.Delete(mods, recursive: true); }
    }

    private async Task RunStandardWorld(SaveDocument document, Variant variant, ImmutableArray<CoreCheckpoint> expected)
    {
        var fixture = new Fixture(TestScene.GetTree());
        try
        {
            var app = await fixture.LoadAndAddScene<Train.App.App>("res://App/App.tscn");
            await FramesUntil(TestScene, () => !app.Host!.IsBusy, timeout: ContentPreparationTimeout);
            app.SetProcess(false);
            var host = app.Host!;
            host.AutoPauseOnFocusLoss = false;
            app.Saves!.SetAutoSaveInterval(null);
            Require(app.Saves.StartExample(document) is null, "无法通过生产路径打开标准世界存档。");
            await FramesUntil(TestScene, () =>
            {
                app._Process(0);
                return !host.IsBusy && !app.Saves!.HasPendingWork;
            }, timeout: ContentPreparationTimeout, description: $"{variant.Name} 打开标准世界后等待宿主空闲");
            Require(host.GetStatus() is { PlayerPaused: true, IsFrozen: false }, "标准世界未成功激活。");
            Require(host.SetSpeed(variant.Speed) is null, "无法设置测试倍速。");
            var stopAt = new SimulationPosition(0, 0);
            host.BoundaryCompleted += (_, view, _, _) =>
            {
                if (view.Position == stopAt) host.RequestPauseAfterBoundary();
            };
            var frame = 0;
            for (var index = 0; index < expected.Length; index++)
            {
                await DriveTo(expected[index].Position);
                if (variant.Pauses)
                {
                    foreach (var delta in new[] { 0.2, 0.01, 0.5 }) app._Process(delta);
                    Require(host.GetStatus()!.View.Position == expected[index].Position, "暂停帧推进了模拟。");
                }
                Require(app.Development!.CaptureCheckpoint(variant.Name) is CoreResult<ICoreCapture>.Success,
                    "无法捕获真实核心检查点。");
                var comparison = host.CompareCheckpoints(expected[index], app.Development.Checkpoints[^1].Checkpoint);
                if (comparison is CoreResult<CheckpointComparison>.Success { Value: CheckpointComparison.Different different })
                    throw new InvalidOperationException($"{variant.Name} 与直接推进的首个差异：{different.Difference.Position} · " +
                        $"{different.Difference.Field} · {different.Difference.Expected} → {different.Difference.Actual}");
                Require(comparison is CoreResult<CheckpointComparison>.Success { Value: CheckpointComparison.Equal },
                    $"{variant.Name} 无法比较真实核心检查点：{comparison}");
                if (variant.Scenes) await Observe(index, expected[index].Position);
            }

            async Task DriveTo(SimulationPosition target)
            {
                if (host.GetStatus()!.View.Position == target) return;
                stopAt = target;
                Require(host.SetPaused(false) is null, "无法恢复帧驱动。");
                await FramesUntil(TestScene, () =>
                {
                    app._Process(variant.Intervals[frame++ % variant.Intervals.Length]);
                    var status = host.GetStatus()!;
                    Require(!status.IsFrozen && HostCoordinator.ComparePosition(status.View.Position, target) <= 0,
                        $"{variant.Name} 冻结或越过目标完整边界：{status.View.Position}");
                    return status.View.Position == target;
                }, timeout: TimeSpan.FromSeconds(300), description: $"{variant.Name} 推进到 {target}");
                Require(host.GetStatus()!.PlayerPaused, "目标边界没有停止下一模拟步。");
            }

            // 依次打开节点战斗、线路战斗和跟随场景，再返回地图；场景生成期间核心继续推进半段。
            async Task Observe(int index, SimulationPosition at)
            {
                var scenes = app.WorldScenes!;
                if (index == 3)
                {
                    scenes.ReturnToMap();
                    Require(!scenes.View.Visible, "关闭场景没有返回地图。");
                    return;
                }
                if (index > 3) return;
                var view = host.GetStatus()!.View;
                WorldSceneTarget target = index switch
                {
                    0 => new WorldSceneTarget.Location(view.World.Combats.First(combat => combat.Location is WorldLocation.Node).Location),
                    1 => new WorldSceneTarget.Location(view.World.Combats.First(combat => combat.Location is WorldLocation.Edge).Location),
                    _ => Follow(view)
                };
                var previous = scenes.Display?.DisplayedFrame?.Update.Frame;
                scenes.Open(target);
                stopAt = new(at.CompletedSteps + 50, 0);
                Require(host.SetPaused(false) is null, "场景生成期间无法恢复核心推进。");
                await FramesUntil(TestScene, () =>
                {
                    app._Process(variant.Intervals[frame++ % variant.Intervals.Length]);
                    var status = host.GetStatus()!;
                    Require(!status.IsFrozen && HostCoordinator.ComparePosition(status.View.Position, stopAt) <= 0,
                        "场景生成期间核心冻结或越过下一观察边界。");
                    Require(scenes.View.Visible, "真实场景生成失败或自动关闭。");
                    return scenes.Display?.DisplayedFrame is { Readiness: SceneReadiness.Ready or SceneReadiness.Partial } displayed &&
                        displayed.Update.Frame != previous;
                }, timeout: TimeSpan.FromSeconds(300), description: $"{variant.Name} 打开 {target} 并等待新场景首帧");
            }
        }
        finally { await fixture.Cleanup(); }

        static WorldSceneTarget Follow(ICoreReadView view)
        {
            var train = ((Lookup<TrainView>.Found)view.FindTrain(view.World.Trains
                .First(item => item.Activity == TrainActivity.Running).Id)).Value;
            return new WorldSceneTarget.Follow(train.Id, train.Vehicles[0]);
        }
    }

    private static T Value<T>(CoreResult<T> result) where T : notnull => result is CoreResult<T>.Success success
        ? success.Value : throw new InvalidOperationException($"核心调用失败：{result}");

    private static T Value<T>(ContentResult<T> result) where T : notnull => result is ContentResult<T>.Success success
        ? success.Value : throw new InvalidOperationException($"内容调用失败：{result}");

    private async Task<ImmutableArray<DeveloperCheckpoint>> Run(JsonElement recipe, Variant variant,
        ImmutableArray<DeveloperCheckpoint> expected = default, SaveEntry? saved = null)
    {
        var fixture = new Fixture(TestScene.GetTree());
        try
        {
            // 不设置 CreateHost/CreateScenes：App 使用生产 HostComposition 的三个真实工厂。
            var app = await fixture.LoadAndAddScene<Train.App.App>("res://App/App.tscn");
            await FramesUntil(TestScene, () => !app.Host!.IsBusy, timeout: ContentPreparationTimeout);
            app.SetProcess(false);
            var host = app.Host!;
            host.AutoPauseOnFocusLoss = false;
            if (saved is null)
                Require(host.BeginNewGame(new(recipe.GetProperty("generation_profile").GetString()!),
                    recipe.GetProperty("seed").GetUInt64(), recipe.GetProperty("generator_version").GetString()!,
                    host.SessionOptions!).Problem is null, "真实装配不能开始示例新局。");
            else
            {
                app.Saves!.SetAutoSaveInterval(null);
                Require(app.Saves.Load(saved) is null, "无法通过生产存档路径读取同一测试存档。");
            }
            await FramesUntil(TestScene, () =>
            {
                app._Process(0);
                return !host.IsBusy && !app.Saves!.HasPendingWork;
            }, timeout: ContentPreparationTimeout, description: $"{variant.Name} 读档后等待宿主和存档空闲");
            Require(host.GetStatus() is { PlayerPaused: true, IsFrozen: false }, "示例新局未成功激活。");
            Require(host.SetSpeed(variant.Speed) is null, "无法设置测试倍速。");

            var stopAt = new SimulationPosition(0, 0);
            host.BoundaryCompleted += (_, view, _, _) =>
            {
                if (view.Position == stopAt) host.RequestPauseAfterBoundary();
            };
            var frame = 0;
            var captured = ImmutableArray.CreateBuilder<DeveloperCheckpoint>();
            foreach (var input in recipe.GetProperty("commands").EnumerateArray())
            {
                await AdvanceTo(Position(input.GetProperty("at")));
                Capture();
                if (variant.Pauses)
                {
                    var before = host.GetStatus()!.View.Position;
                    foreach (var delta in new[] { 0.2, 0.01, 0.5 }) app._Process(delta);
                    Require(host.GetStatus()!.View.Position == before, "暂停帧改变了命令的模拟位置。");
                }
                var train = new TrainId(input.GetProperty("train").GetUInt64());
                CoreCommand command = input.GetProperty("kind").GetString() switch
                {
                    "train.replace_run_plan" => new CoreCommand.ReplaceRunPlan(train,
                        new(input.GetProperty("plan").GetProperty("loop").GetBoolean(),
                            input.GetProperty("plan").GetProperty("stops").EnumerateArray().Select(stop =>
                                (PlanStop)new PlanStop.Waypoint(new(stop.GetProperty("node").GetUInt64())))
                            .ToImmutableArray())),
                    "train.start" => new CoreCommand.StartTrain(train),
                    "train.stop" => new CoreCommand.StopTrain(train),
                    var kind => throw new InvalidDataException($"示例含未支持的命令：{kind}")
                };
                var submitted = host.Submit(host.GetStatus()!.Binding, command, CommandSource.AutomationTest);
                Require(submitted is SubmissionResult.Completed { Receipt.Decision: CommandDecision.Accepted },
                    $"{variant.Name} 的示例命令未接受：{submitted}");
                Capture();
            }
            await AdvanceTo(Position(recipe.GetProperty("target")));
            Capture();
            Require(expected.IsDefault || captured.Count == expected.Length, "变体遗漏了检查点。");
            return captured.ToImmutable();

            void Capture()
            {
                Require(app.Development!.CaptureCheckpoint(variant.Name) is CoreResult<ICoreCapture>.Success,
                    "无法捕获真实核心检查点。");
                var actual = app.Development.Checkpoints[^1];
                if (!expected.IsDefault) Compare(host, expected[captured.Count], actual, variant.Name);
                captured.Add(actual);
            }

            async Task AdvanceTo(SimulationPosition target)
            {
                var boundaries = new SortedSet<ulong>();
                if (variant.SwitchSpeed) boundaries.UnionWith([40, 80, 120]);
                if (variant.Scenes) boundaries.UnionWith([30, 60, 90, 120]);
                foreach (var boundary in boundaries.Where(step => step > host.GetStatus()!.View.Position.CompletedSteps &&
                    step < target.CompletedSteps).ToArray())
                {
                    await DriveTo(new(boundary, 0));
                    if (variant.SwitchSpeed && boundary is 40 or 80 or 120)
                    {
                        Require(host.SetPaused(false) is null, "切换倍速前无法恢复运行。");
                        Require(host.SetSpeed(boundary == 40 ? SimulationSpeed.Two :
                            boundary == 80 ? SimulationSpeed.Four : SimulationSpeed.Eight) is null, "运行中切换倍速失败。");
                    }
                    if (variant.Scenes) await Observe(boundary);
                }
                await DriveTo(target);
            }

            async Task DriveTo(SimulationPosition target)
            {
                if (host.GetStatus()!.View.Position == target) return;
                Require(HostCoordinator.ComparePosition(host.GetStatus()!.View.Position, target) < 0,
                    "已经越过输入位置。");
                stopAt = target;
                Require(host.SetPaused(false) is null, "无法恢复帧驱动。");
                await FramesUntil(TestScene, () =>
                {
                    app._Process(variant.Intervals[frame++ % variant.Intervals.Length]);
                    var status = host.GetStatus()!;
                    Require(!status.IsFrozen && HostCoordinator.ComparePosition(status.View.Position, target) <= 0,
                        $"{variant.Name} 冻结或越过目标完整边界：{status.View.Position}");
                    return status.View.Position == target;
                });
                Require(host.GetStatus()!.PlayerPaused, "目标边界没有停止下一模拟步。");
            }

            async Task Observe(ulong step)
            {
                var scenes = app.WorldScenes!;
                if (step == 120)
                {
                    scenes.ReturnToMap();
                    Require(!scenes.View.Visible, "关闭场景没有返回地图。");
                    return;
                }
                if (step is not (30 or 60 or 90)) return;
                var view = host.GetStatus()!.View;
                var previous = scenes.Display?.DisplayedFrame?.Update.Frame;
                var train = ((Lookup<TrainView>.Found)view.FindTrain(new(1))).Value;
                // 跟随可能仍在节点内，先观察列车所在节点，避免正确性用例重复冷建两个节点。
                WorldSceneTarget target = step switch
                {
                    30 => new WorldSceneTarget.Location(new WorldLocation.Edge(view.World.Edges[0].Id, 1)),
                    60 => new WorldSceneTarget.Location(new WorldLocation.Node(train.Position is TrainPosition.Node atNode
                        ? atNode.Id : view.World.Nodes[0].Id)),
                    _ => new WorldSceneTarget.Follow(train.Id, train.Vehicles[0])
                };
                scenes.Open(target);
                stopAt = new(step + 30, 0);
                Require(host.SetPaused(false) is null, "场景生成期间无法恢复核心推进。");
                var firstFrame = System.Diagnostics.Stopwatch.StartNew();
                await FramesUntil(TestScene, () =>
                {
                    var before = host.GetStatus()!.View.Position;
                    app._Process(variant.Intervals[frame++ % variant.Intervals.Length]);
                    var status = host.GetStatus()!;
                    Require(!status.IsFrozen && HostCoordinator.ComparePosition(status.View.Position, stopAt) <= 0,
                        "场景生成期间核心冻结或越过下一观察边界。");
                    Require(status.View.Position == stopAt || status.View.Position.CompletedSteps > before.CompletedSteps,
                        "场景生成阻止了完整模拟步推进。");
                    Require(!status.PlayerPaused || status.View.Position == stopAt,
                        "场景生成在指定边界之前暂停了核心。");
                    Require(scenes.View.Visible, "真实场景生成失败或自动关闭。");
                    return scenes.Display?.DisplayedFrame is { Readiness: SceneReadiness.Ready or SceneReadiness.Partial } displayed &&
                        displayed.Update.Frame != previous;
                }, timeout: TimeSpan.FromSeconds(120),
                    description: $"{variant.Name} 第 {step} 步，打开 {target} 并等待新场景首帧");
                GD.Print($"OBSERVATION_MEASUREMENT variant={variant.Name} step={step} first_frame_ms={firstFrame.Elapsed.TotalMilliseconds:F3} target={target}");
            }
        }
        finally { await fixture.Cleanup(); }
    }

    private static SimulationPosition Position(JsonElement value) =>
        new(value.GetProperty("completed_steps").GetUInt64(), value.GetProperty("sequence").GetUInt32());

    private static void Compare(HostCoordinator host, DeveloperCheckpoint expected, DeveloperCheckpoint actual, string variant)
    {
        Require(expected.Position == actual.Position && expected.WorldSeed == actual.WorldSeed &&
            expected.Runtime == actual.Runtime && expected.InputIdentity == actual.InputIdentity,
            $"{variant} 检查点身份不兼容：{expected.Position} → {actual.Position}");
        var comparison = host.CompareCheckpoints(expected.Checkpoint, actual.Checkpoint);
        if (comparison is CoreResult<CheckpointComparison>.Success { Value: CheckpointComparison.Different different })
            throw new InvalidOperationException($"{variant} 首个差异：{different.Difference.Position} · " +
                $"{different.Difference.Field} · {different.Difference.Expected} → {different.Difference.Actual}");
        Require(comparison is CoreResult<CheckpointComparison>.Success { Value: CheckpointComparison.Equal },
            $"{variant} 无法比较真实核心检查点：{comparison}");
    }

    private sealed record Variant(string Name, double[] Intervals, SimulationSpeed Speed,
        bool Pauses = false, bool SwitchSpeed = false, bool Scenes = false);
}
