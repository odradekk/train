using System.Collections.Immutable;
using Chickensoft.GoDotTest;
using Chickensoft.GodotTestDriver;
using Godot;
using Train.Scenes.Contracts;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class WorldSceneTests
{
    [Test]
    public async Task Promoted_partial_candidate_reuses_later_batches_and_retains_the_remainder_until_space_changes()
    {
        var fixture = new Fixture(TestScene.GetTree());
        var first = Frame(1);
        var idle = Frame(2, first.Update.Frame, objects: []);
        var candidate = Frame(10, space: 2,
            objects: Enumerable.Range(20, 10).Select(id => Object((ulong)id)).ToImmutableArray()).Update;
        var promoted = Frame(11, space: 2, objects: [Object(20)]);
        var batch = Frame(12, promoted.Update.Frame, space: 2, objects: [Object(21)]);
        var moved = Frame(13, space: 3, objects: []);
        try
        {
            var app = await Ready(fixture, new Setup(), Factory(binding => Session(binding,
                [Success(first), Success(idle), Success(promoted), Success(batch), Success(moved)], candidates:
                [new SceneResult<SceneUpdate?>.Success(null), new SceneResult<SceneUpdate?>.Success(candidate),
                    new SceneResult<SceneUpdate?>.Success(null), new SceneResult<SceneUpdate?>.Success(null),
                    new SceneResult<SceneUpdate?>.Success(null)])));
            var world = app.WorldScenes!;
            world.Open(Target);
            var display = world.Display!;
            display.Timestamp = () => 0;
            world.ProcessFrame(0, false);
            display.ReceiveCandidate(candidate);
            var before = OrphanRoots();
            // 旧帧节点退役后会变成孤立节点，不能把它们当成候选新建资源。
            before.UnionWith(display.Objects.Values.SelectMany(item =>
                item.Visuals.Select(visual => visual.Node.GetInstanceId()).Append(item.Root.GetInstanceId())));
            display.Timestamp = () => (display.CandidateProgress(candidate.Frame)?.Prepared ?? 0) *
                (System.Diagnostics.Stopwatch.Frequency / 1000);
            await new UiTestInput(TestScene).Frames(1);
            world.ProcessFrame(0, false);
            var candidateNodes = OrphanRoots().Except(before).Select(GodotObject.InstanceFromId).OfType<Node3D>().ToArray();
            Require(display.CandidateProgress(candidate.Frame) is (4, 10) && candidateNodes.Length == 4,
                "切换前必须只准备部分候选视觉，并保留未准备的来源。");
            var candidateIds = candidateNodes.Select(node => node.GetInstanceId()).ToHashSet();
            var seen = new HashSet<ulong>();
            display.Timestamp = () =>
            {
                seen.UnionWith(OrphanRoots().Except(before));
                return 0;
            };
            await new UiTestInput(TestScene).Frames(1);
            world.ProcessFrame(0, false);
            var active = display.Objects[new(20)];
            Require(ReferenceEquals(display.DisplayedFrame, promoted) && world.View.Interactive &&
                candidateNodes.Any(node => ReferenceEquals(node, active.Visuals[0].Node)),
                "切换帧必须直接复用已准备的候选视觉。");
            Require(seen.Except(candidateIds).All(id => id == active.Root.GetInstanceId()),
                "正式准备后不得再次创建候选视觉；只允许新增正式对象根节点。");
            Require(candidateNodes.Where(node => !ReferenceEquals(node, active.Visuals[0].Node))
                .All(GodotObject.IsInstanceValid) && display.CandidateProgress(candidate.Frame) is (3, 10) &&
                display.PendingCandidateVisuals == 6,
                "晋升首帧提交后必须保留未取用的视觉；未完成的候选准备不能继续派工。");
            await new UiTestInput(TestScene).Frames(1);
            world.ProcessFrame(0, false);
            var later = display.Objects[new(21)];
            Require(ReferenceEquals(display.DisplayedFrame, batch) &&
                candidateNodes.Any(node => ReferenceEquals(node, later.Visuals[0].Node)) &&
                display.CandidateProgress(candidate.Frame) is (2, 10) && display.PendingCandidateVisuals == 6,
                "后续接入批次必须复用候选视觉，并继续保留尚未取用的资源。");
            Require(seen.Except(candidateIds).All(id => id == active.Root.GetInstanceId() || id == later.Root.GetInstanceId()),
                "后续批次也只能新增正式对象根，不能重做候选资源准备。");
            var unused = candidateNodes.Where(node => node.GetParent() is null).ToArray();
            Require(unused.Length == 2, "未接入的两个已准备视觉必须继续留在树外。");
            world.ProcessFrame(0, false);
            Require(ReferenceEquals(display.DisplayedFrame, moved) && unused.All(node => !GodotObject.IsInstanceValid(node)) &&
                !display.IsPreparingCandidate && display.CandidateProgress(candidate.Frame) is null,
                "Space 改变时必须释放全部尚未取用的候选视觉和来源。");
        }
        finally { await fixture.Cleanup(); }

        static HashSet<ulong> OrphanRoots()
        {
            using var nodes = (Godot.Collections.Array)Node.GetOrphanNodeIds();
            return nodes.Select(id => id.AsUInt64()).Where(id =>
                GodotObject.InstanceFromId(id) is Node3D node && node.GetParent() is null).ToHashSet();
        }
    }
}
