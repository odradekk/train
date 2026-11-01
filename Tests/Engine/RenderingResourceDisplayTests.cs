using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Chickensoft.GoDotTest;
using Godot;
using Train.App.Assets;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Engine.Tests.Assets;
using Train.Rendering;
using Train.Scenes.Contracts;
using Train.Scenes.ContractSupport;
using static Train.Engine.Tests.Assertions;
using NVector2 = System.Numerics.Vector2;
using NVector3 = System.Numerics.Vector3;

namespace Train.Engine.Tests;

public partial class RenderingResourceTests
{
    [Test]
    public async Task Display_delta_preserves_children_on_reparent_and_updates_inherited_fog_after_parent_changes()
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        using var display = new SceneDisplay(cache, new FixedContentView());
        TestScene.AddChild(display.Root);
        await new UiTestInput(TestScene).Frames(1);
        display.Timestamp = () => 0;
        var visual = new SceneVisual.Geometry(Triangle(new SceneMaterial.BuiltIn(BuiltInMaterial.RailSteel)),
            new(false, 0, 0, false));
        var vehicle = new WorldObjectId.Instance(new InstanceId.Vehicle(new(7)));
        var parent = Object(1, null) with { Subject = vehicle, Lights = [new(new("#ffffff"), 3, 0, 1)] };
        var child = Object(2, new(1));
        var other = Object(3, null);
        var first = Frame(1, null, [parent, child, other]);
        using (var prepared = await Prepare(display, first)) prepared.Commit();
        display.UpdateConstructionFog([new(7)]);
        var childRoot = display.Objects[new(2)].Root;
        var childVisual = display.Objects[new(2)].Visuals[0];
        var followedShader = Material(childVisual).Shader;
        var oldParent = display.Objects[new(1)].Root;
        var light = oldParent.GetChildren().OfType<OmniLight3D>().Single();
        Require(ReferenceEquals(display.FindRoot(vehicle)?.Root, oldParent), "车辆根查询必须返回实际显示对象。");
        using (var abandoned = display.Prepare(Frame(2, first.Update.Frame, [parent with { Subject = null }]))!) { }
        display.UpdateConstructionFog([new(7)]);
        Require(ReferenceEquals(display.DisplayedFrame, first) && Material(childVisual).Shader == followedShader &&
            ReferenceEquals(childRoot.GetParent(), oldParent) && ReferenceEquals(display.FindRoot(vehicle)?.Root, oldParent),
            "放弃已准备帧不能提前改变对象索引、层级或雾状态。");

        var invalid = Frame(2, first.Update.Frame, [], [new(1)]);
        var rejected = false;
        try { using var unexpected = display.Prepare(invalid); }
        catch (InvalidOperationException) { rejected = true; }
        Require(rejected && ReferenceEquals(display.DisplayedFrame, first) &&
            ReferenceEquals(childRoot.GetParent(), oldParent) && GodotObject.IsInstanceValid(light),
            "删除仍有旧子对象的父必须在准备阶段失败，不能改变显示层级或释放旧灯光。");

        var changedParent = Frame(3, first.Update.Frame, [parent with { Subject = null }]);
        using (var prepared = display.Prepare(changedParent)!) prepared.Commit();
        display.UpdateConstructionFog([new(7)]);
        var normalShader = Material(childVisual).Shader;
        Require(normalShader != followedShader && ReferenceEquals(childRoot.GetParent(), oldParent) &&
            display.FindRoot(vehicle) is null,
            "父Subject改变时，即使子对象未Upsert也必须更新根查询和继承的雾状态。");

        var reparented = Frame(4, changedParent.Update.Frame, [child with { Parent = new(3) },
            other with { Subject = vehicle }], [new(1)]);
        using (var prepared = display.Prepare(reparented)!) prepared.Commit();
        display.UpdateConstructionFog([new(7)]);
        Require(ReferenceEquals(display.Objects[new(2)].Root, childRoot) &&
            ReferenceEquals(childRoot.GetParent(), display.Objects[new(3)].Root) &&
            ReferenceEquals(display.FindRoot(vehicle)?.Root, display.Objects[new(3)].Root) &&
            Material(childVisual).Shader == followedShader && !GodotObject.IsInstanceValid(oldParent) &&
            !GodotObject.IsInstanceValid(light),
            "删除旧父并换父必须复用存活子对象，更新雾继承，并释放旧父和灯光。");
        display.UpdateConstructionFog([]);
        Require(Material(childVisual).Shader == normalShader, "跟随集合变化必须重算未Upsert的子对象。");
        var beforeObjects = display.Objects;
        var idle = Frame(5, reparented.Update.Frame, []);
        using (var prepared = display.Prepare(idle)!) prepared.Commit();
        Require(ReferenceEquals(beforeObjects, display.Objects) &&
            ReferenceEquals(display.Objects[new(2)].Root, childRoot),
            "没有对象变更或原点平移的增量必须复用对象表和节点，不能复制全部对象。");

        SceneObject Object(ulong id, SceneObjectId? parentId) => new(new(id), parentId,
            new(NVector3.Zero, System.Numerics.Quaternion.Identity), null, [visual], [], []);
        static FixedSceneFrame Frame(ulong revision, SceneFrameStamp? baseline, ImmutableArray<SceneObject> objects,
            ImmutableArray<SceneObjectId> removed = default) => new()
        {
            Update = new(baseline, new(new(Guid.Empty), new(1), new(2), revision),
                NVector3.Zero, objects, removed.IsDefault ? [] : removed, new([], []), []), Readiness = SceneReadiness.Ready
        };
    }

    [Test]
    public async Task Candidate_pending_work_tracks_latest_sources_without_accumulating_replacements()
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        using var display = new SceneDisplay(cache, new FixedContentView());
        var visual = new SceneVisual.Geometry(Triangle(new SceneMaterial.BuiltIn(BuiltInMaterial.RailSteel)),
            new(false, 0, 0, false));
        var unavailable = new SceneVisual.Model(new("unavailable-replaced-candidate"), [], null,
            new(false, 0, 0, false), default);
        var first = Update(1, Enumerable.Range(1, 10).Select(id => Object((ulong)id,
            id == 10 ? unavailable : visual)).ToImmutableArray());
        display.ReceiveCandidate(first);
        await new UiTestInput(TestScene).Frames(1);
        display.Timestamp = () => (display.CandidateProgress(first.Frame)?.Prepared ?? 0) *
            (System.Diagnostics.Stopwatch.Frequency / 1000);
        await ScenePackingTestDriver.StartCandidate(display, TestScene);
        await AdvanceCandidate(display, () => display.CandidateProgress(first.Frame) is (>= 2, 10));
        var preparedCount = display.CandidateProgress(first.Frame)!.Value.Prepared;
        Require(preparedCount < 10 && display.PendingCandidateVisuals == 10 - preparedCount,
            "预算中断后，待准备项必须与剩余来源一一对应。");
        display.ReceiveCandidate(first);
        Require(display.PendingCandidateVisuals == 10 - preparedCount, "重复帧不能重复排队。");

        SceneUpdate latest = first;
        for (ulong revision = 2; revision <= 20; revision++)
        {
            latest = Update(revision, [Object(1, visual), Object(2, visual), Object(10, visual)]);
            display.ReceiveCandidate(latest);
            Require(display.CandidateProgress(latest.Frame) is (2, 3) && display.PendingCandidateVisuals == 1,
                "全量替换应删除撤销项、复用兼容资源，并只保留一次尚未准备的同键来源。");
        }
        await new UiTestInput(TestScene).Frames(1);
        display.Timestamp = () => 0;
        await AdvanceCandidate(display, () => display.PreparedCandidate == latest.Frame);
        Require(display.PreparedCandidate == latest.Frame && display.PendingCandidateVisuals == 0 &&
            display.CandidateProgress(latest.Frame) is (3, 3),
            "队列必须准备同键的最新来源，不能读取已被替换的不可用模型。");

        var delta = Update(21, [Object(11, visual)]) with { Base = latest.Frame, Removed = [new(10)] };
        display.ReceiveCandidate(delta);
        Require(display.CandidateProgress(delta.Frame) is (2, 3) && display.PendingCandidateVisuals == 1,
            "增量必须只撤销指定对象并排入新增视觉。");
        await AdvanceCandidate(display, () => display.PreparedCandidate == delta.Frame);
        Require(display.CandidateProgress(delta.Frame) is (3, 3) && display.PreparedCandidate == delta.Frame && display.PendingCandidateVisuals == 0,
            "增量完成后待准备队列必须为空。");
        display.CancelCandidate();
        Require(display.PendingCandidateVisuals == 0 && display.CandidateProgress(delta.Frame) is null,
            "取消候选必须清除全部待准备来源。");

        SceneObject Object(ulong id, SceneVisual source) => new(new(id), null,
            new(NVector3.Zero, System.Numerics.Quaternion.Identity), null, [source], [], []);
        static SceneUpdate Update(ulong revision, ImmutableArray<SceneObject> objects) =>
            new(null, new(new(Guid.Empty), new(1), new(2), revision), NVector3.Zero, objects, [], new([], []), []);
    }

    private async Task<SceneDisplay.PreparedFrame> Prepare(SceneDisplay display, ISceneFrame frame)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(30))
        {
            if (display.Prepare(frame) is { } prepared) return prepared;
            await display.PreparationPacking.WaitAsync(TimeSpan.FromSeconds(30));
            await new UiTestInput(TestScene).Frames(1);
        }
        throw new InvalidOperationException("显示准备超时。");
    }

    private async Task AdvanceCandidate(SceneDisplay display, Func<bool> reached)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!reached() && watch.Elapsed < TimeSpan.FromSeconds(30))
        {
            display.PrepareCandidate(TimeSpan.FromMilliseconds(4));
            await display.CandidatePacking.WaitAsync(TimeSpan.FromSeconds(30));
            if (!reached()) await new UiTestInput(TestScene).Frames(1);
        }
        Require(reached(), "候选必须在真实后续帧内达到预期进度。");
    }
}
