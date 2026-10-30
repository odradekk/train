using System.Collections.Immutable;
using Chickensoft.GoDotTest;
using Chickensoft.GodotTestDriver;
using Train.Scenes.Contracts;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class WorldSceneTests
{
    [Test]
    public async Task Candidate_waiting_for_packing_lends_its_remaining_budget_to_active_uploads() =>
        await Waiting_packing_lends_remaining_budget(candidateFirst: true);

    [Test]
    public async Task Active_waiting_for_packing_lends_its_remaining_budget_to_candidate_uploads() =>
        await Waiting_packing_lends_remaining_budget(candidateFirst: false);

    private async Task Waiting_packing_lends_remaining_budget(bool candidateFirst)
    {
        var fixture = new Fixture(TestScene.GetTree());
        var first = Frame(1);
        var idle = Frame(2, first.Update.Frame, objects: []);
        var activeMesh = Mesh with { };
        var candidateMesh = Mesh with { };
        var active = Frame(3, candidateFirst ? first.Update.Frame : idle.Update.Frame,
            objects: Enumerable.Range(2, 6).Select(id => Object((ulong)id,
                new SceneVisual.Geometry(activeMesh, new(false, 0, 0, false)))).ToImmutableArray());
        var candidate = Frame(10, space: 2,
            objects: Enumerable.Range(20, 10).Select(id => Object((ulong)id,
                new SceneVisual.Geometry(candidateMesh, new(false, 0, 0, false)))).ToImmutableArray()).Update;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task packing = Task.CompletedTask;
        try
        {
            var app = await Ready(fixture, new Setup(), Factory(binding => Session(binding,
                candidateFirst ? [Success(first), Success(active)] : [Success(first), Success(idle), Success(active)],
                candidates: candidateFirst
                    ? [new SceneResult<SceneUpdate?>.Success(null), new SceneResult<SceneUpdate?>.Success(candidate)]
                    : [new SceneResult<SceneUpdate?>.Success(null), new SceneResult<SceneUpdate?>.Success(candidate),
                        new SceneResult<SceneUpdate?>.Success(candidate)])));
            var world = app.WorldScenes!;
            world.Open(Target);
            var display = world.Display!;
            display.Timestamp = () => 0;
            world.ProcessFrame(0, false);
            if (candidateFirst) await ScenePackingTestDriver.StartFormal(display, active, TestScene);
            else
            {
                display.ReceiveCandidate(candidate);
                var primeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var primeRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var originalPackMesh = display.PackMesh;
                display.PackMesh = (mesh, cancellation) =>
                {
                    primeEntered.TrySetResult();
                    primeRelease.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellation).GetAwaiter().GetResult();
                    return originalPackMesh(mesh, cancellation);
                };
                try
                {
                    await new UiTestInput(TestScene).Frames(1);
                    display.PrepareCandidate(TimeSpan.FromMilliseconds(4));
                    await primeEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));
                    world.ProcessFrame(0, false);
                    Require(ReferenceEquals(display.DisplayedFrame, idle) &&
                        display.CandidateProgress(candidate.Frame) is (0, 10),
                        "先执行一次候选优先调度而不上传资源，使下一帧正式侧先执行。");
                }
                finally
                {
                    primeRelease.TrySetResult();
                    await display.CandidatePacking.WaitAsync(TimeSpan.FromSeconds(30));
                    display.PackMesh = originalPackMesh;
                }
            }
            var packMesh = display.PackMesh;
            display.PackMesh = (mesh, cancellation) =>
            {
                entered.TrySetResult();
                release.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellation).GetAwaiter().GetResult();
                return packMesh(mesh, cancellation);
            };
            if (candidateFirst)
            {
                display.ReceiveCandidate(candidate);
                display.PrepareCandidate(TimeSpan.FromMilliseconds(4));
                packing = display.CandidatePacking;
            }
            else
            {
                using var prepared = display.Prepare(active);
                Require(prepared is null, "正式侧必须等待受控后台打包。");
                packing = display.PreparationPacking;
            }
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            var activePrepared = 0;
            display.Observe = item =>
            {
                if (item.Stage == Train.Rendering.SceneDisplayStage.ResourcesPrepared && item.Frame == active.Update.Frame)
                    activePrepared++;
            };
            // 只有后执行侧的每次资源准备计费1ms，先执行侧始终停在后台打包入口。
            display.Timestamp = () => (candidateFirst ? activePrepared :
                display.CandidateProgress(candidate.Frame)!.Value.Prepared) *
                (System.Diagnostics.Stopwatch.Frequency / 1000);
            await new UiTestInput(TestScene).Frames(1);
            Require(world.View.Interactive, "共享预算测试必须从已可操作的正式场景进入交替调度。");
            world.ProcessFrame(0, false);
            Require(!packing.IsCompleted && display.IsPreparing && display.IsPreparingCandidate &&
                ReferenceEquals(display.DisplayedFrame, candidateFirst ? first : idle),
                "先执行侧必须仍在等待后台数组，两侧均未提交，旧正式帧保持显示。");
            Require(candidateFirst ? activePrepared == 4 && display.CandidateProgress(candidate.Frame) is (0, 10)
                    : activePrepared == 0 && display.CandidateProgress(candidate.Frame) is (4, 10),
                $"先执行侧等待打包时，后执行侧必须借用共享4ms预算，超过2ms后仍继续准备资源；正式已准备{activePrepared}，候选进度{display.CandidateProgress(candidate.Frame)}。");
        }
        finally
        {
            release.TrySetResult();
            try { await packing.WaitAsync(TimeSpan.FromSeconds(30)); }
            finally { await fixture.Cleanup(); }
        }
    }

    [Test]
    public async Task Candidate_without_resource_work_lends_its_budget_to_the_active_update()
    {
        var fixture = new Fixture(TestScene.GetTree());
        var first = Frame(1);
        var idle = Frame(2, first.Update.Frame, objects: []);
        var active = Frame(3, idle.Update.Frame,
            objects: Enumerable.Range(2, 4).Select(id => Object((ulong)id)).ToImmutableArray());
        var full = Frame(10, space: 2, objects: [Object(20) with { Visuals = [] }]).Update;
        var delta = Frame(11, full.Frame, space: 2, objects: [Object(21) with { Visuals = [] }]).Update;
        try
        {
            var app = await Ready(fixture, new Setup(), Factory(binding => Session(binding,
                [Success(first), Success(idle), Success(active)], candidates:
                [new SceneResult<SceneUpdate?>.Success(null), new SceneResult<SceneUpdate?>.Success(full),
                    new SceneResult<SceneUpdate?>.Success(delta)])));
            var world = app.WorldScenes!;
            world.Open(Target);
            world.Display!.Timestamp = () => 0;
            world.ProcessFrame(0, false);
            var activePrepared = 0;
            world.Display.Observe = item =>
            {
                if (item.Stage == Train.Rendering.SceneDisplayStage.ResourcesPrepared) activePrepared++;
            };
            world.Display.Timestamp = () => activePrepared * System.Diagnostics.Stopwatch.Frequency * 3 / 4000;
            await new UiTestInput(TestScene).Frames(1);
            world.ProcessFrame(0, false);
            Require(world.Display.PreparedCandidate == full.Frame, "先完成无视觉的候选，并进入活动侧先执行的下一帧。");
            await new UiTestInput(TestScene).Frames(1);
            world.ProcessFrame(0, false);
            Require(activePrepared == 4 && world.Display.PreparedCandidate == delta.Frame &&
                ReferenceEquals(world.Display.DisplayedFrame, active),
                "候选无需创建资源时，活动侧即使先执行也应借用剩余4ms预算完成更新。");
        }
        finally { await fixture.Cleanup(); }
    }

    [Test]
    public async Task Idle_active_update_lends_its_budget_to_the_prebuilt_candidate()
    {
        var fixture = new Fixture(TestScene.GetTree());
        var first = Frame(1);
        var active = Frame(2, first.Update.Frame, objects: []);
        var candidate = Frame(10, space: 2,
            objects: Enumerable.Range(20, 10).Select(id => Object((ulong)id)).ToImmutableArray()).Update;
        try
        {
            var app = await Ready(fixture, new Setup(), Factory(binding => Session(binding,
                [Success(first), Success(active)], candidates:
                [new SceneResult<SceneUpdate?>.Success(null), new SceneResult<SceneUpdate?>.Success(candidate)])));
            var world = app.WorldScenes!;
            world.Open(Target);
            world.Display!.Timestamp = () => 0;
            world.ProcessFrame(0, false);
            world.Display.ReceiveCandidate(candidate);
            // 每完成一个候选视觉收取1ms，活动空更新不消耗资源时间。
            world.Display.Timestamp = () => (world.Display.CandidateProgress(candidate.Frame)?.Prepared ?? 0) *
                (System.Diagnostics.Stopwatch.Frequency / 1000);
            await new UiTestInput(TestScene).Frames(1);
            world.ProcessFrame(0, false);
            Require(world.Display.CandidateProgress(candidate.Frame) is (4, 10) &&
                ReferenceEquals(world.Display.DisplayedFrame, active),
                "活动侧空闲时，候选即使先执行也应借用完整4ms，而非停在2ms配额。");
        }
        finally { await fixture.Cleanup(); }
    }

    [Test]
    public async Task Active_uploads_and_prebuilt_candidate_both_progress_under_the_shared_budget()
    {
        var fixture = new Fixture(TestScene.GetTree());
        var first = Frame(1);
        var active = Frame(2, first.Update.Frame,
            objects: Enumerable.Range(2, 6).Select(id => Object((ulong)id)).ToImmutableArray());
        var candidate = Frame(10, space: 2,
            objects: Enumerable.Range(20, 40).Select(id => Object((ulong)id)).ToImmutableArray()).Update;
        try
        {
            var app = await Ready(fixture, new Setup(), Factory(binding => Session(binding,
                [Success(first), Success(active), Success(active)], candidates:
                [new SceneResult<SceneUpdate?>.Success(null), new SceneResult<SceneUpdate?>.Success(candidate),
                    new SceneResult<SceneUpdate?>.Success(candidate)])));
            var world = app.WorldScenes!;
            world.Open(Target);
            world.Display!.Timestamp = () => 0;
            world.ProcessFrame(0, false);
            world.Display.ReceiveCandidate(candidate);
            var activePrepared = 0;
            world.Display.Observe = item =>
            {
                if (item.Stage == Train.Rendering.SceneDisplayStage.ResourcesPrepared) activePrepared++;
            };
            world.Display.Timestamp = () => (activePrepared +
                (world.Display.CandidateProgress(candidate.Frame)?.Prepared ?? 0)) *
                System.Diagnostics.Stopwatch.Frequency / 2000;
            await new UiTestInput(TestScene).Frames(1);
            world.ProcessFrame(0, false);
            var prepared = world.Display.CandidateProgress(candidate.Frame)!.Value.Prepared;
            Require(activePrepared > 0 && activePrepared < 6 && prepared > 0 && prepared < 40 && world.Display.IsPreparing &&
                ReferenceEquals(world.Display.DisplayedFrame, first),
                "活动上传仍未完成时，候选也必须获得准备配额。");
            await new UiTestInput(TestScene).Frames(1);
            world.ProcessFrame(0, false);
            Require(activePrepared == 6 && world.Display.CandidateProgress(candidate.Frame)!.Value.Prepared > prepared &&
                world.Display.IsPreparingCandidate && ReferenceEquals(world.Display.DisplayedFrame, active),
                "交替先后后两方必须继续推进，活动更新可在候选尚未完成时提交。");
        }
        finally { await fixture.Cleanup(); }
    }

}
