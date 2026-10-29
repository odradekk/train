using System.Collections.Immutable;
using System.Diagnostics;
using Chickensoft.GoDotTest;
using Godot;
using Train.App.Assets;
using Train.Content.ContractSupport;
using Train.Scenes.Contracts;
using static Train.Engine.Tests.Assertions;
using NVector3 = System.Numerics.Vector3;

namespace Train.Engine.Tests;

public partial class SceneDisplayTests
{
    [Test]
    public async Task Fog_preparation_yields_without_changing_D_and_cancellation_releases_only_new_boxes()
    {
        await new UiTestInput(TestScene).Frames(2);
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        using var display = Attach(cache, new FixedContentView());
        var retained = new SceneBounds(NVector3.Zero, NVector3.One);
        var removed = new SceneBounds(new(2), new(3));
        var first = Frame(1, [], fog: [retained, retained, removed]);
        await Commit(display, first);
        var root = display.Root.GetNode<Node3D>("LoadingFog");
        var old = root.GetChildren().Cast<MeshInstance3D>().ToArray();
        var regions = Enumerable.Range(4, 128).Select(i => new SceneBounds(new(i), new(i + 1)))
            .Prepend(retained).Append(retained).ToImmutableArray();
        var next = Frame(2, [], first.Update.Frame, fog: regions);
        await new UiTestInput(TestScene).Frames(2);
        var before = OrphanIds();
        long now = 0;
        display.Timestamp = () => now += Stopwatch.Frequency / 10000;
        using var partial = display.Prepare(next);
        Require(partial is null, "大量雾矩形必须按共享预算跨帧准备。");
        Require(ReferenceEquals(display.DisplayedFrame, first) && root.GetChildCount() == 3 &&
            old.All(node => GodotObject.IsInstanceValid(node) && node.GetParent() == root),
            "准备中不得改变 D 的雾或借用矩形的所有权。");
        var added = OrphanIds().Except(before).Select(GodotObject.InstanceFromId).OfType<MeshInstance3D>().ToArray();
        Require(added.Length > 0 && added.Length < 128, "本帧应只准备部分新增矩形。");
        var meshes = added.Select(node => node.Mesh).ToArray();
        display.CancelPreparation();
        display.CancelPreparation();
        Require(added.All(node => !GodotObject.IsInstanceValid(node)) &&
            meshes.All(mesh => !GodotObject.IsInstanceValid(mesh)) &&
            old.All(node => GodotObject.IsInstanceValid(node) && GodotObject.IsInstanceValid(node.Mesh)),
            "反复取消必须恰好释放新增节点及网格，保留 D 的借用资源。");
        await new UiTestInput(TestScene).Frames(2);
        display.Timestamp = () => 0;
        await Commit(display, next);
        Require(root.GetChildCount() == regions.Length && old.Take(2).All(node => node.GetParent() == root) &&
            !GodotObject.IsInstanceValid(old[2]), "续做后原子提交保留重复矩形，释放移除项并显示所有新矩形。");
    }
}
