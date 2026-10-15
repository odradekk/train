using System.Collections.Immutable;
using System.Text.Json;
using Chickensoft.GoDotTest;
using Chickensoft.GodotTestDriver;
using Godot;
using Train.App;
using Train.App.Development;
using Train.Core.Contracts;
using Train.Scenes.Contracts;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public class ObservationIndependenceTests(Node testScene) : TestClass(testScene)
{
    [Test]
    public async Task Real_world_checkpoints_do_not_depend_on_frame_intervals_speed_pause_or_scene_observation()
    {
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
    }

    private async Task<ImmutableArray<DeveloperCheckpoint>> Run(JsonElement recipe, Variant variant,
        ImmutableArray<DeveloperCheckpoint> expected = default)
    {
        var fixture = new Fixture(TestScene.GetTree());
        GD.Print($"[DEBUG-481] {variant.Name} run start {Time.GetTicksMsec()}");
        try
        {
            // 不设置 CreateHost/CreateScenes：App 使用生产 HostComposition 的三个真实工厂。
            var app = await fixture.LoadAndAddScene<Train.App.App>("res://App/App.tscn");
            await FramesUntil(TestScene, () => !app.Host!.IsBusy);
            app.SetProcess(false);
            var host = app.Host!;
            host.AutoPauseOnFocusLoss = false;
            Require(host.BeginNewGame(new(recipe.GetProperty("generation_profile").GetString()!),
                recipe.GetProperty("seed").GetUInt64(), recipe.GetProperty("generator_version").GetString()!,
                host.SessionOptions!).Problem is null, "真实装配不能开始示例新局。");
            await FramesUntil(TestScene, () => { app._Process(0); return !host.IsBusy; });
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
                GD.Print($"[DEBUG-481] {variant.Name} observe {step} start {Time.GetTicksMsec()} train={train.Position}");
                scenes.Open(step switch
                {
                    30 => new WorldSceneTarget.Location(new WorldLocation.Edge(view.World.Edges[0].Id, 1)),
                    60 => new WorldSceneTarget.Location(new WorldLocation.Node(view.World.Nodes[0].Id)),
                    _ => new WorldSceneTarget.Follow(train.Id, train.Vehicles[0])
                });
                stopAt = new(step + 30, 0);
                Require(host.SetPaused(false) is null, "场景生成期间无法恢复核心推进。");
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
                });
                GD.Print($"[DEBUG-481] {variant.Name} observe {step} ready {Time.GetTicksMsec()}");
            }
        }
        finally
        {
            GD.Print($"[DEBUG-481] {variant.Name} cleanup start {Time.GetTicksMsec()}");
            await fixture.Cleanup();
            GD.Print($"[DEBUG-481] {variant.Name} cleanup done {Time.GetTicksMsec()}");
        }
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
