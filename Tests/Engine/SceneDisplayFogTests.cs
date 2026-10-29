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
    public async Task Fog_reuse_does_not_replace_the_creation_cost_and_rejected_duplicates_resume_once()
    {
        await new UiTestInput(TestScene).Frames(2);
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        using var display = Attach(cache, new FixedContentView());
        var retained = new SceneBounds(NVector3.Zero, NVector3.One);
        var inserted = new SceneBounds(new(2), new(3));
        var duplicate = new SceneBounds(new(4), new(5));
        var repeated = Enumerable.Repeat(retained, 12).ToImmutableArray();
        var first = Frame(1, [], fog: repeated);
        await Commit(display, first);
        var root = display.Root.GetNode<Node3D>("LoadingFog");
        var old = root.GetChildren().Cast<MeshInstance3D>().ToArray();
        await new UiTestInput(TestScene).Frames(1);
        var before = OrphanIds();
        long now = 0;
        var created = false;
        display.Timestamp = () =>
        {
            now += Stopwatch.Frequency / 20000;
            if (!created && OrphanIds().Except(before).Any(id => GodotObject.InstanceFromId(id) is MeshInstance3D))
            {
                created = true;
                now += Stopwatch.Frequency / 1000;
            }
            return now;
        };
        var next = Frame(2, [], first.Update.Frame, fog: repeated.Prepend(inserted).Append(duplicate).Append(duplicate).ToImmutableArray());
        using (var partial = display.Prepare(next))
            Require(partial is null, "低成本复用可以继续，但剩余额度不足已知新增成本时必须暂停。");
        var added = OrphanIds().Except(before).Select(GodotObject.InstanceFromId).OfType<MeshInstance3D>().ToArray();
        Require(added.Length == 1 && added[0].Position == new Vector3(2.5f, 2.5f, 2.5f),
            "低成本复用可以继续，但预算不足的两个重复矩形都不能提前创建。");
        Require(ReferenceEquals(display.DisplayedFrame, first) && root.GetChildCount() == 12 &&
            old.All(node => node.GetParent() == root), "分帧准备不得修改旧D雾。");
        await new UiTestInput(TestScene).Frames(1);
        display.Timestamp = () => 0;
        using var prepared = await Prepare(display, next);
        Require(root.GetChildCount() == 12, "完整准备也必须等提交后才显示新增雾。");
        prepared.Commit();
        var boxes = root.GetChildren().Cast<MeshInstance3D>().ToArray();
        Require(boxes.Length == 15 && old.All(node => node.GetParent() == root) &&
            boxes.Count(node => node.Position == new Vector3(4.5f, 4.5f, 4.5f)) == 2 &&
            added[0].GetParent() == root, "被拒绝的矩形恢复后必须各创建一次，保留所有旧重复项并原子提交。");
    }

    [Test]
    public async Task Fog_creation_cost_survives_later_preparations_while_reuse_still_fits()
    {
        await new UiTestInput(TestScene).Frames(2);
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        using var display = Attach(cache, new FixedContentView());
        using var burner = Attach(cache, new FixedContentView());
        var retained = new SceneBounds(NVector3.Zero, NVector3.One);
        var inserted = new SceneBounds(new(2), new(3));
        var duplicate = new SceneBounds(new(4), new(5));
        var first = Frame(1, [], fog: [retained, retained]);
        var empty = Frame(1, []);
        await Commit(display, first);
        await Commit(burner, empty);
        await new UiTestInput(TestScene).Frames(1);
        var before = OrphanIds();
        long now = 0;
        display.Timestamp = () =>
        {
            if (OrphanIds().Except(before).Any(id => GodotObject.InstanceFromId(id) is MeshInstance3D))
                now = Stopwatch.Frequency * 3 / 1000;
            return now;
        };
        var training = Frame(2, [], first.Update.Frame, fog: [inserted, retained, retained]);
        using (var trained = await Prepare(display, training))
            trained.Commit();
        var root = display.Root.GetNode<Node3D>("LoadingFog");
        var old = root.GetChildren().Cast<MeshInstance3D>().ToArray();
        await new UiTestInput(TestScene).Frames(1);
        SpendPreparationTime(burner, empty, 2);
        display.Timestamp = () => 0;
        var reordered = Frame(3, [], training.Update.Frame, fog: [retained, inserted, retained]);
        using (var reused = display.Prepare(reordered))
        {
            Require(reused is not null, "剩余2ms虽然不足新建，仍须允许只复用已有雾的准备完成。");
            reused!.Commit();
        }
        before = OrphanIds();
        var next = Frame(4, [], reordered.Update.Frame, fog: [retained, inserted, retained, duplicate, duplicate]);
        using (var partial = display.Prepare(next))
            Require(partial is null, "后续准备仍须保留3ms新增成本，不能在剩余2ms开始新建。");
        Require(!OrphanIds().Except(before).Select(GodotObject.InstanceFromId).OfType<MeshInstance3D>().Any() &&
            ReferenceEquals(display.DisplayedFrame, reordered) && old.All(node => node.GetParent() == root),
            "拒绝新建不能分配矩形或改变D的已有资源。");
        await new UiTestInput(TestScene).Frames(1);
        using var prepared = await Prepare(display, next);
        prepared.Commit();
        Require(root.GetChildCount() == 5 && old.All(node => node.GetParent() == root) &&
            root.GetChildren().Cast<MeshInstance3D>().Count(node => node.Position == new Vector3(4.5f, 4.5f, 4.5f)) == 2,
            "跨准备和拒绝重试后，重复矩形不能跳过或重复创建。");
    }

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
