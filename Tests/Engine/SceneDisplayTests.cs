using System.Collections.Immutable;
using Chickensoft.GoDotTest;
using Godot;
using Train.App.Assets;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Rendering;
using Train.Scenes.Contracts;
using Train.Scenes.ContractSupport;
using static Train.Engine.Tests.Assertions;
using NVector3 = System.Numerics.Vector3;
using NQuaternion = System.Numerics.Quaternion;

namespace Train.Engine.Tests;

public class SceneDisplayTests(Node testScene) : TestClass(testScene)
{
    private static readonly SceneSessionId Session = new(Guid.Parse("7dd364aa-df8f-4144-b345-fc130ea11ca9"));

    [Test]
    public void Candidates_publish_only_on_commit_and_abandoned_resources_are_released()
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        using var display = Attach(cache, new FixedContentView());
        var first = Frame(1, [Object(1, Mesh())]);
        using (var prepared = Prepare(display, first))
        {
            Require(display.DisplayedFrame is null && display.Objects.Count == 0,
                "准备首帧不得提前发布对象或显示帧。");
            prepared.Commit();
        }
        Require(ReferenceEquals(first, display.DisplayedFrame), "提交后首帧未成为显示帧。");
        var oldRoot = display.Objects[new(1)].Root;
        var oldMesh = MeshOf(display.Objects[new(1)]);
        var abandoned = Frame(2, [Object(2, Mesh())], first.Update.Frame);
        var candidate = Prepare(display, abandoned);
        var candidateRoot = candidate.Objects[new(2)].Root;
        var candidateMesh = MeshOf(candidate.Objects[new(2)]);
        Require(ReferenceEquals(first, display.DisplayedFrame) && display.Objects.Count == 1,
            "准备增量时旧显示组必须保持完整。");
        Require(candidateRoot.GetParent() is null, "候选对象不能在提交前挂入显示树。");
        candidate.Dispose();
        Require(!GodotObject.IsInstanceValid(candidateRoot) && !GodotObject.IsInstanceValid(candidateMesh),
            "未提交候选的节点和独占网格必须释放。");
        Require(GodotObject.IsInstanceValid(oldRoot) && GodotObject.IsInstanceValid(oldMesh),
            "丢弃候选不能释放旧显示组。");
        Require(ReferenceEquals(first, display.DisplayedFrame), "丢弃候选不能改变显示帧。");
        using var next = Prepare(display, abandoned);
        next.Commit();
        Require(ReferenceEquals(abandoned, display.DisplayedFrame) && display.Objects.Count == 2,
            "再次准备并提交应完整发布增量。");
    }

    [Test]
    public void Loading_and_wrong_base_preserve_the_last_displayed_frame()
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        using var display = Attach(cache, new FixedContentView());
        var first = Frame(1, [Object(1, Mesh())]);
        Commit(display, first);
        var root = display.Objects[new(1)].Root;
        var loading = Frame(2, [], first.Update.Frame, readiness: SceneReadiness.Loading);
        Require(display.Prepare(loading) is null, "Loading 帧不能提交显示。");
        var wrongBase = first.Update.Frame with { Revision = 999 };
        Require(display.Prepare(Frame(3, [], wrongBase)) is null, "不匹配的 Base 不能准备增量。");
        Require(ReferenceEquals(first, display.DisplayedFrame) && ReferenceEquals(root, display.Objects[new(1)].Root),
            "拒绝候选必须保留最后成功帧和对象。");
    }

    [Test]
    public void Preparation_failure_releases_earlier_leases_and_preserves_old_frame()
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        var asset = Png("candidate-texture");
        var unavailable = new AssetRevision(new string('E', 64));
        var content = new FixedContentView
        {
            Assets = new Dictionary<AssetRevision, ContentLookup<ContentAsset>>
            {
                [asset.Revision] = new ContentLookup<ContentAsset>.Found(asset),
                [unavailable] = new ContentLookup<ContentAsset>.NotFound()
            }
        };
        using var display = Attach(cache, content);
        var first = Frame(1, [Object(1, Mesh())]);
        Commit(display, first);
        var oldRoot = display.Objects[new(1)].Root;
        var oldMesh = MeshOf(display.Objects[new(1)]);
        using var observingLease = cache.AcquireFaceTexture(asset);
        var texture = observingLease.Value;
        var failed = Frame(2,
        [
            Object(2, Mesh(new SceneMaterial.Voxel(asset.Revision, MaterialCategory.BareMetal, null))),
            Object(3, Mesh(new SceneMaterial.Voxel(unavailable, MaterialCategory.BareMetal, null)))
        ], first.Update.Frame);
        var threw = false;
        try { using var unexpected = display.Prepare(failed); }
        catch (InvalidOperationException) { threw = true; }
        Require(threw, "明确不可用的资产必须使准备失败，不能静默接入。");
        Require(ReferenceEquals(first, display.DisplayedFrame) && display.Objects.Count == 1,
            "准备失败不能发布部分对象或新帧。");
        Require(GodotObject.IsInstanceValid(oldRoot) && GodotObject.IsInstanceValid(oldMesh),
            "准备失败不能释放旧显示资源。");
        observingLease.Dispose();
        Require(!GodotObject.IsInstanceValid(texture), "失败前已取得的候选纹理租约没有释放。");
    }

    [Test]
    public void Origin_shift_moves_existing_roots_once_and_reuses_their_meshes()
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        using var display = Attach(cache, new FixedContentView());
        var mesh = Mesh();
        var first = Frame(1, [Object(1, mesh, new(10, 0, 20)), Object(2, mesh, new(2, 0, 3), new(1))]);
        Commit(display, first);
        var oldMesh = MeshOf(display.Objects[new(1)]);
        var identity = oldMesh.GetInstanceId();
        var next = Frame(2, [Object(3, mesh, new(-50, 0, -60))], first.Update.Frame,
            new(-100, 5, 200), new(100, -5, -200));
        using (var candidate = Prepare(display, next))
        {
            Near(display.Objects[new(1)].Root.Position, new(10, 0, 20));
            candidate.Commit();
        }
        Near(display.Objects[new(1)].Root.Position, new(-90, 5, 220));
        Near(display.Objects[new(2)].Root.Position, new(2, 0, 3));
        Near(display.Objects[new(2)].Root.GlobalPosition, new(-88, 5, 223));
        Near(display.Objects[new(3)].Root.Position, new(-50, 0, -60));
        Require(MeshOf(display.Objects[new(1)]).GetInstanceId() == identity,
            "浮动原点平移不能重建旧网格。");
        var moved = Frame(3, [Object(1, mesh, new(-190, 10, 420))], next.Update.Frame,
            new(-100, 5, 200), new(200, -10, -400));
        Commit(display, moved);
        Near(display.Objects[new(1)].Root.Position, new(-190, 10, 420));
        Near(display.Objects[new(3)].Root.Position, new(-150, 5, 140));
        Require(MeshOf(display.Objects[new(1)]).GetInstanceId() == identity,
            "仅更新姿态的 Upsert 必须复用网格。");
    }

    [Test]
    public void Removing_parents_and_children_retires_owned_resources_but_keeps_shared_meshes()
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        using var display = Attach(cache, new FixedContentView());
        var shared = Mesh();
        var first = Frame(1, [Object(1, shared), Object(2, shared, parent: new(1)), Object(3, shared)]);
        Commit(display, first);
        var parent = display.Objects[new(1)].Root;
        var child = display.Objects[new(2)].Root;
        var survivor = display.Objects[new(3)].Root;
        var sharedMesh = MeshOf(display.Objects[new(3)]);
        Commit(display, Frame(2, [], first.Update.Frame, removed: [new(2), new(1)]));
        Require(!GodotObject.IsInstanceValid(parent) && !GodotObject.IsInstanceValid(child),
            "删除父子对象必须分别退役两个节点。");
        Require(GodotObject.IsInstanceValid(sharedMesh) && GodotObject.IsInstanceValid(survivor),
            "移除部分使用者不能释放仍在显示的共享网格。");
        Require(display.Objects.Count == 1 && ReferenceEquals(survivor.GetParent(), display.Root),
            "剩余根对象必须仍在显示树中。");
        var replacement = Frame(3, [Object(4, Mesh())]);
        Commit(display, replacement);
        Require(!GodotObject.IsInstanceValid(survivor) && !GodotObject.IsInstanceValid(sharedMesh),
            "完整替换后最后使用者和共享网格必须退役。");
        Require(display.Objects.Count == 1 && display.Objects.ContainsKey(new(4)),
            "完整替换不能保留旧对象。");
        var lastMesh = MeshOf(display.Objects[new(4)]);
        Commit(display, Frame(4, []));
        Require(!GodotObject.IsInstanceValid(lastMesh) && display.Objects.Count == 0,
            "空完整帧也必须清除旧显示组并释放独占网格。");
    }

    private SceneDisplay Attach(AssetResourceCache cache, FixedContentView content)
    {
        var display = new SceneDisplay(cache, content);
        TestScene.AddChild(display.Root);
        return display;
    }

    private static SceneDisplay.PreparedFrame Prepare(SceneDisplay display, ISceneFrame frame) =>
        display.Prepare(frame) ?? throw new InvalidOperationException("已配置的可显示帧未产生候选。");

    private static void Commit(SceneDisplay display, ISceneFrame frame)
    {
        using var candidate = Prepare(display, frame);
        candidate.Commit();
    }

    private static Mesh MeshOf(SceneDisplay.DisplayObject item) =>
        ((MeshInstance3D)item.Visuals[0].Node.GetChild(0)).Mesh;

    private static FixedSceneFrame Frame(ulong revision, ImmutableArray<SceneObject> objects,
        SceneFrameStamp? @base = null, NVector3 shift = default, ScenePoint origin = default,
        SceneReadiness readiness = SceneReadiness.Ready, ImmutableArray<SceneObjectId> removed = default) => new()
    {
        Origin = origin,
        Readiness = readiness,
        Update = new(@base, new(Session, new(1), new(1), revision), shift, objects,
            removed.IsDefault ? [] : removed, new([], []), [])
    };

    private static SceneObject Object(ulong id, SceneMesh mesh, NVector3 position = default,
        SceneObjectId? parent = null) => new(new(id), parent, new(position, NQuaternion.Identity),
        null, [new SceneVisual.Geometry(mesh, new(false, 0, 0, false))], [], []);

    private static SceneMesh Mesh(SceneMaterial? material = null) => new(
        material ?? new SceneMaterial.BuiltIn(BuiltInMaterial.RailSteel),
        [
            new(NVector3.Zero, NVector3.UnitZ, new(0, 0), new(0, 0), ExposedFaceEdges.None, 0, 0),
            new(NVector3.UnitX, NVector3.UnitZ, new(1, 0), new(1, 0), ExposedFaceEdges.None, 0, 0),
            new(NVector3.UnitY, NVector3.UnitZ, new(0, 1), new(0, 1), ExposedFaceEdges.None, 1, 0)
        ], [0, 1, 2]);

    private static ContentAsset Png(string revision)
    {
        using var image = Image.CreateEmpty(4, 4, false, Image.Format.Rgba8);
        image.Fill(Colors.Red);
        return new(new(revision), ImmutableArray.CreateRange(image.SavePngToBuffer()), new AssetMetadata.Png(4, 4), []);
    }

    private static void Near(Vector3 actual, Vector3 expected) => Require(actual.DistanceTo(expected) < 0.001,
        $"期望坐标 {expected}，实际 {actual}。");
}
