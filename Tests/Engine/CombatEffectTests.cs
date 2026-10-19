using System.Collections.Immutable;
using Chickensoft.GoDotTest;
using Godot;
using Train.App.Assets;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Rendering;
using Train.Scenes.Contracts;
using Train.Scenes.ContractSupport;
using Train.Ui;
using static Train.Engine.Tests.Assertions;
using NVector3 = System.Numerics.Vector3;
using NQuaternion = System.Numerics.Quaternion;

namespace Train.Engine.Tests;

public class CombatEffectTests(Node testScene) : TestClass(testScene)
{
    private static readonly SceneSessionId Session = new(Guid.Parse("c4a0d6e2-7b18-4f0a-9c31-6e5d2a8b7041"));
    private static readonly SceneSessionId OtherSession = new(Guid.Parse("8f1e2d3c-4b5a-6978-8091-a2b3c4d5e6f7"));

    [Test]
    public async Task CommittedCuePlaysOnceAndPrepareDoesNot()
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        using var display = Attach(cache);
        var shot = Cue(1, SceneEffectKind.Shot, to: new(8, 1, 0));
        var preview = Frame(1, [], [shot]);
        using (var prepared = display.Prepare(preview))
            Require(prepared is not null, "可显示帧必须产生候选。");
        Require(Count(display.Root, "Shot") == 0 && display.DisplayedFrame is null, "准备不得播放一次性提示。");

        Commit(display, preview);
        var first = One(display, "Shot");
        var identity = first.GetInstanceId();
        var shown = Frame(2, [], [shot], basis: preview.Update.Frame);
        Commit(display, shown);
        Require(Count(display.Root, "Shot") == 1 && GodotObject.IsInstanceValid(first) && first.GetInstanceId() == identity,
            "同一会话中的同一提示只能播放一次。");

        shown = Frame(3, [], [Cue(2, SceneEffectKind.Shot, started: 1d / 60, to: new(1, 0, 0))], basis: shown.Update.Frame);
        Commit(display, shown);
        Require(Count(display.Root, "Shot") == 2, "刚到的提示年龄略小于 0 时仍要播放。");
        shown = Frame(4, [], [Cue(3, SceneEffectKind.Shot)], basis: shown.Update.Frame);
        Commit(display, shown);
        Require(Count(display.Root, "Shot") == 2, "没有终点的射击不得建节点。");

        var expired = Frame(5, [], [Cue(4, SceneEffectKind.Shot, started: 0, to: new(1, 0, 0))], seconds: 1,
            basis: shown.Update.Frame);
        Commit(display, expired);
        Require(Count(display.Root, "Shot") == 2, "提交时已经超过时长的提示不得建节点。");
        Commit(display, Frame(6, [], [Cue(4, SceneEffectKind.Shot, to: new(1, 0, 0))], basis: expired.Update.Frame));
        Require(Count(display.Root, "Shot") == 2, "已记为播放的过期提示不得补播。");

        var replay = Frame(7, [], [shot], session: OtherSession);
        Commit(display, replay);
        Require(!GodotObject.IsInstanceValid(first) && Count(display.Root, "Shot") == 1, "新会话必须清掉旧提示并重播同一 Id。");
        var replayed = One(display, "Shot");
        Commit(display, Frame(8, [], [shot], session: OtherSession, space: 2));
        Require(!GodotObject.IsInstanceValid(replayed) && Count(display.Root, "Shot") == 1, "切换空间必须清掉旧提示并重播同一 Id。");
        await new UiTestInput(TestScene).Frames(2);
    }

    [Test]
    public void PausedSimulationHoldsFragmentsAndOriginShiftKeepsThem()
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        using var display = Attach(cache);
        var cue = Cue(1, SceneEffectKind.Fragments, from: new(2, 3, 4));
        var first = Frame(1, [], [cue]);
        Commit(display, first);
        var fragments = Nodes(display, "Fragment");
        Require(fragments.Count == 6, "碎片提示应放出 6 个方块。");
        var rested = fragments.Select(node => node.Position).ToArray();

        display.AdvanceEffects(Frame(1, [], []), 0.5);
        Require(Positions(fragments).Zip(rested).All(pair => pair.First.DistanceTo(pair.Second) < 0.001f),
            "模拟时间不变时碎片不得移动。");
        display.AdvanceEffects(Frame(1, [], [], seconds: 0.4), 0);
        var fallen = Positions(fragments);
        Require(fallen.Zip(rested).All(pair => pair.First.DistanceTo(pair.Second) > 0.05f), "推进模拟时间后碎片必须下落。");

        var moved = fallen[0];
        Commit(display, Frame(2, [], [], seconds: 0.4, basis: first.Update.Frame, shift: new(-4, 0, 2)));
        Require(fragments[0].Position.DistanceTo(moved + new Vector3(-4, 0, 2)) < 0.001f, "增量提交必须按 ShiftExistingRoots 平移碎片。");
        var shifted = fragments.Select(node => node.Position).ToArray();
        Commit(display, Frame(3, [], [], seconds: 0.4, origin: new(10, 0, -3), shift: new(999, 0, 0)));
        Require(fragments.Select(node => node.Position).Zip(shifted).All(pair =>
            pair.First.DistanceTo(pair.Second + new Vector3(-10, 0, 3)) < 0.001f),
            "同会话同空间的整帧替换必须按原点差平移，而不是 ShiftExistingRoots。");
    }

    [Test]
    public void DamagedLightPulsesStayWithinThreePerRealSecond()
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        using var display = Attach(cache);
        var damaged = new SceneLight(new("#ffcc88"), 6, 1, 1);
        var steady = new SceneLight(new("#ffffff"), 6, 0, 2);
        Commit(display, Frame(1, [Object(1, [damaged]), Object(2, [steady])], []));
        var pulsing = display.Objects[new(1)].Root.GetNode<OmniLight3D>("SceneLight");
        var constant = display.Objects[new(2)].Root.GetNode<OmniLight3D>("SceneLight");
        var ons = new List<double>();
        if (pulsing.LightEnergy > 0) ons.Add(0);
        double real = 0, sim = 0;
        for (var step = 0; step < 120; step++)
        {
            real += 1d / 60;
            sim += 8d / 60;
            var before = pulsing.LightEnergy;
            display.AdvanceEffects(Frame(1, [], [], seconds: sim), 1d / 60);
            if (before <= 0 && pulsing.LightEnergy > 0) ons.Add(real);
            Require(Mathf.IsEqualApprox(constant.LightEnergy, 1), "未受损的灯必须保持点亮。");
        }
        Require(ons.Count >= 4, "受损灯在 8 倍速下必须多次试图点亮，否则额度没有被测到。");
        foreach (var time in ons)
            Require(ons.Count(other => other > time - 1d && other <= time + 1e-9) <= 3,
                $"受损灯每真实秒亮起超过 3 次：{ons.Count} 次，窗口落在 {time:F3}。");
        var energy = pulsing.LightEnergy;
        display.AdvanceEffects(Frame(1, [], [], seconds: sim), 0.5);
        Require(Mathf.IsEqualApprox(pulsing.LightEnergy, energy) && Mathf.IsEqualApprox(constant.LightEnergy, 1),
            "模拟时间不变时灯光状态不得变化。");
    }

    [Test]
    public void TenImpactsShareThreeFlashes()
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        using var display = Attach(cache);
        var impacts = Enumerable.Range(1, 10).Select(id => Cue((ulong)id, SceneEffectKind.Impact)).ToImmutableArray();
        var shown = Frame(1, [], impacts);
        Commit(display, shown);
        Require(Count(display.Root, "Flash") == 3, "同一真实时刻的 10 次命中只能留下 3 次闪光。");
        var more = Enumerable.Range(11, 10).Select(id => Cue((ulong)id, SceneEffectKind.Impact)).ToImmutableArray();
        shown = Frame(2, [], more, basis: shown.Update.Frame);
        Commit(display, shown);
        Require(Count(display.Root, "Flash") == 3, "同一真实秒内的后续命中不得再加闪光。");
        shown = Frame(3, [], [Cue(100, SceneEffectKind.Destruction)], basis: shown.Update.Frame);
        Commit(display, shown);
        Require(Count(display.Root, "Flash") == 3 && Count(display.Root, "Fireball") == 0, "火球和命中闪光共用额度。");
        Require(Count(display.Root, "Fragment") == 18 && Count(display.Root, "Smoke") == 1, "超出闪光额度时碎片和烟仍要播放。");
        display.AdvanceEffects(Frame(3, [], [], seconds: shown.SimulationSeconds), 1);
        Commit(display, Frame(4, [], [Cue(101, SceneEffectKind.Impact)], basis: shown.Update.Frame));
        Require(Count(display.Root, "Flash") == 4, "满一秒后应允许一次新的闪光，且旧闪光仍在。");
    }

    [Test]
    public void RemovingAnObjectDropsItsNodesAndKeepsSharedResources()
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        using var display = Attach(cache);
        var effects = ImmutableArray.Create(
            new PersistentSceneEffect(PersistentEffectKind.Smoke, default, 1, 1),
            new PersistentSceneEffect(PersistentEffectKind.Fire, default, 1, 2),
            new PersistentSceneEffect(PersistentEffectKind.Sparks, default, 1, 3));
        var first = Frame(1, [Object(1, [new SceneLight(new("#ffffff"), 4, 0, 1)], effects)],
            [Cue(1, SceneEffectKind.Fragments)]);
        Commit(display, first);
        var root = display.Objects[new(1)].Root;
        var smoke = root.GetNode<MeshInstance3D>("Smoke");
        var fire = root.GetNode<MeshInstance3D>("Fire");
        var sparks = root.GetNode<MeshInstance3D>("Sparks");
        var light = root.GetNode<OmniLight3D>("SceneLight");
        var mesh = smoke.Mesh;
        var material = smoke.MaterialOverride;
        var fragment = Nodes(display, "Fragment")[0];
        Commit(display, Frame(2, [], [], basis: first.Update.Frame, removed: [new(1)]));
        Require(!GodotObject.IsInstanceValid(smoke) && !GodotObject.IsInstanceValid(fire) &&
            !GodotObject.IsInstanceValid(sparks) && !GodotObject.IsInstanceValid(light), "删除对象必须释放它的灯和持续效果。");
        Require(GodotObject.IsInstanceValid(fragment) && GodotObject.IsInstanceValid(mesh) &&
            GodotObject.IsInstanceValid(material), "已播放的碎片和共享资源必须留下。");
        display.Dispose();
        display.Dispose();
        Require(!GodotObject.IsInstanceValid(fragment) && !GodotObject.IsInstanceValid(mesh) &&
            !GodotObject.IsInstanceValid(material), "释放显示后节点和共享资源都必须失效。");
    }

    [Test]
    public void SmokeCapKeepsTheEightStrongest()
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        using var display = Attach(cache);
        var objects = Enumerable.Range(1, 10).Select(id => Object((ulong)id, effects:
            [new PersistentSceneEffect(PersistentEffectKind.Smoke, default, id, (ulong)id)])).ToList();
        objects[0] = objects[0] with
        {
            Effects =
            [
                .. objects[0].Effects,
                new PersistentSceneEffect(PersistentEffectKind.Fire, default, 1, 1),
                new PersistentSceneEffect(PersistentEffectKind.Leak, default, 1, 1),
                new PersistentSceneEffect(PersistentEffectKind.FacilitySmoke, default, 1, 1)
            ]
        };
        var frame = Frame(1, [.. objects], [Cue(1, SceneEffectKind.Destruction)]);
        Commit(display, frame);
        for (ulong id = 1; id <= 10; id++)
        {
            var smoke = display.Objects[new(id)].Root.GetNodeOrNull<MeshInstance3D>("Smoke");
            if (id <= 2) Require(smoke is null, $"较弱的冒烟 {id} 不应保留。");
            else Require(smoke is not null && Mathf.IsEqualApprox(smoke.GetInstanceShaderParameter("strength").AsSingle(), id),
                $"较强的冒烟 {id} 必须保留。");
        }
        Require(display.Objects[new(1)].Root.GetNodeOrNull("Fire") is not null, "火不受冒烟上限影响。");
        Require(display.Objects[new(1)].Root.GetNodeOrNull("Leak") is null &&
            display.Objects[new(1)].Root.GetNodeOrNull("FacilitySmoke") is null, "本阶段不播放漏液和设施烟。");
        Require(Count(display.Root.GetNode("CombatEffects"), "Smoke") == 1 &&
            display.Objects.Values.Count(item => item.Root.GetNodeOrNull("Smoke") is not null) == 8,
            "摧毁烟柱不占用 8 处冒烟额度。");
    }

    [Test]
    public void ShakeFollowsTheTrainAndTheSwitchClearsIt()
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        using var display = Attach(cache);
        var lead = new VehicleId(7);
        var rear = new VehicleId(8);
        var outsider = new VehicleId(9);
        WorldObjectId Vehicle(VehicleId id) => new WorldObjectId.Instance(new InstanceId.Vehicle(id));
        display.SetFollowedTrain([lead, rear], true);
        var shown = Frame(1, [], [Cue(1, SceneEffectKind.Impact, subject: Vehicle(rear))]);
        Commit(display, shown);
        var offset = display.AdvanceEffects(shown, 0);
        Require(offset.Length() > 0.14f && offset.Length() <= 0.1501f, "跟随列车中任一车辆被命中都要抖动。");
        var faded = display.AdvanceEffects(shown, 0.1);
        Require(faded.Length() < offset.Length() - 0.04f, "抖动必须随真实时间衰减。");
        shown = Frame(2, [], [Cue(2, SceneEffectKind.Impact, subject: Vehicle(outsider))], basis: shown.Update.Frame);
        Commit(display, shown);
        Require(display.AdvanceEffects(shown, 0).DistanceTo(faded) < 0.001f, "列车外的车辆不得重新开始抖动。");
        shown = Frame(3, [], [Cue(3, SceneEffectKind.Impact, subject: new WorldObjectId.Creature(new(1)))],
            basis: shown.Update.Frame);
        Commit(display, shown);
        Require(display.AdvanceEffects(shown, 0).DistanceTo(faded) < 0.001f, "生物被命中不得抖动。");
        shown = Frame(4, [], [Cue(4, SceneEffectKind.Impact, subject: Vehicle(lead))], basis: shown.Update.Frame);
        Commit(display, shown);
        Require(display.AdvanceEffects(shown, 0).Length() > 0.14f, "新的命中必须重新开始抖动。");
        display.SetFollowedTrain([lead, rear], false);
        shown = Frame(5, [], [Cue(5, SceneEffectKind.Impact, subject: Vehicle(lead))], basis: shown.Update.Frame);
        Commit(display, shown);
        Require(display.AdvanceEffects(shown, 0) == Vector3.Zero, "关闭镜头抖动后偏移必须为零。");

        var camera = new WorldCamera();
        TestScene.AddChild(camera);
        try
        {
            var view = new FixedSceneFrame
            {
                Update = new(null, new(Session, new(1), new(1), 1), default, [], [], new([], []), []),
                TerrainHeightResult = new Lookup<double>.Missing()
            };
            camera.DisplayedFrame(view, null);
            var focus = camera.Focus;
            var basis = camera.Camera.Transform.Basis;
            var position = camera.Camera.Position;
            camera.SetShake(new(0.1f, 0, 0));
            Require(camera.Focus == focus && camera.Camera.Position.DistanceTo(position + new Vector3(0.1f, 0, 0)) < 0.001f &&
                SameBasis(camera.Camera.Transform.Basis, basis), "抖动只平移相机，不改变焦点和朝向。");
            camera.ShakeEnabled = false;
            camera.SetShake(new(0.1f, 0, 0));
            Require(camera.Camera.Position.DistanceTo(position) < 0.001f, "相机关闭抖动后必须回到未偏移的位置。");
        }
        finally { camera.Free(); }
    }

    private SceneDisplay Attach(AssetResourceCache cache)
    {
        var display = new SceneDisplay(cache, new FixedContentView()) { Timestamp = () => 0 };
        TestScene.AddChild(display.Root);
        return display;
    }

    private static void Commit(SceneDisplay display, ISceneFrame frame)
    {
        using var candidate = display.Prepare(frame) ?? throw new InvalidOperationException("已配置的可显示帧未产生候选。");
        candidate.Commit();
    }

    private static FixedSceneFrame Frame(ulong revision, ImmutableArray<SceneObject> objects,
        ImmutableArray<SceneEffectCue> effects, double seconds = 0, SceneFrameStamp? basis = null,
        NVector3 shift = default, ScenePoint origin = default, ImmutableArray<SceneObjectId> removed = default,
        SceneSessionId? session = null, ulong space = 1) => new()
    {
        Origin = origin,
        SimulationSeconds = seconds,
        Update = new(basis, new(session ?? Session, new(1), new(space), revision), shift,
            objects, removed.IsDefault ? [] : removed, new([], []), effects)
    };

    private static SceneObject Object(ulong id, ImmutableArray<SceneLight> lights = default,
        ImmutableArray<PersistentSceneEffect> effects = default) => new(new(id), null,
        new(default, NQuaternion.Identity), null, [], lights.IsDefault ? [] : lights, effects.IsDefault ? [] : effects);

    private static SceneEffectCue Cue(ulong id, SceneEffectKind kind, double started = 0, NVector3 from = default,
        NVector3? to = null, WorldObjectId? subject = null) =>
        new(new(id), kind, from, to, null, null, subject, started);

    private static int Count(Node root, string name) => Named(root, name).Count();

    private static List<MeshInstance3D> Nodes(SceneDisplay display, string name) =>
        Named(display.Root, name).OfType<MeshInstance3D>().ToList();

    private static IEnumerable<Node> Named(Node root, string name) =>
        root.FindChildren("*", "", true, false).Where(node => HasName(node, name));

    private static bool HasName(Node node, string name)
    {
        var text = node.Name.ToString();
        if (text == name) return true;
        if (!text.StartsWith(name) || text.Length == name.Length) return false;
        var suffix = text[name.Length];
        return suffix is '@' || char.IsDigit(suffix);
    }

    private static MeshInstance3D One(SceneDisplay display, string name)
    {
        var nodes = Nodes(display, name);
        Require(nodes.Count == 1, $"{name} 应有 1 个，实际 {nodes.Count} 个。");
        return nodes[0];
    }

    private static Vector3[] Positions(IEnumerable<MeshInstance3D> nodes) => nodes.Select(node => node.Position).ToArray();

    private static bool SameBasis(Basis left, Basis right) => left.X.DistanceTo(right.X) < 0.001f &&
        left.Y.DistanceTo(right.Y) < 0.001f && left.Z.DistanceTo(right.Z) < 0.001f;
}
