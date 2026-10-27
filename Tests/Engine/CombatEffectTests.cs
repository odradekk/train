using System.Collections.Immutable;
using Chickensoft.GoDotTest;
using Godot;
using Train.App.Assets;
using Train.App.Measurement;
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
        Require(first.Scale.X >= 0.2f - 0.001f && first.Scale.Y >= 0.2f - 0.001f, "没有相机时弹道粗细至少 0.2 格。");
        display.AdvanceEffects(preview, 0, new Vector3(0, 0, 48));
        var near = first.Scale.X;
        display.AdvanceEffects(preview, 0, new Vector3(0, 0, 240));
        Require(near >= 0.2f - 0.001f && first.Scale.X > near + 0.2f, "240 格外的弹道必须比 48 格外更粗。");
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
    public void LongShotNearCameraStaysWithinFourScreenPixels()
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        using var display = Attach(cache);
        var camera = new Camera3D { Fov = 60 };
        var windowSize = DisplayServer.WindowGetSize();
        var viewport = new SubViewport { Size = windowSize.Y > 0 ? windowSize : new(1920, 1080) };
        TestScene.AddChild(viewport);
        viewport.AddChild(camera);
        try
        {
            var near = new Vector3(0, 0, -100);
            var far = new Vector3(3200, 0, -100);
            var frame = Frame(1, [], [Cue(1, SceneEffectKind.Shot,
                from: new(far.X, far.Y, far.Z), to: new(near.X, near.Y, near.Z))]);
            Commit(display, frame);
            display.AdvanceEffects(frame, 0, camera.GlobalPosition);
            var shot = One(display, "Shot");
            var halfWidth = shot.GlobalBasis.Y * 0.5f;
            var pixels = camera.UnprojectPosition(near + halfWidth)
                .DistanceTo(camera.UnprojectPosition(near - halfWidth));
            Require(pixels > 3.9f && pixels <= 4.01f,
                $"长弹道近端的投影宽度应保持 4 像素，实际 {pixels} 像素。");
        }
        finally { viewport.Free(); }
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
    public void FlickerClockStaysWithinRealTimeAndDamageDecidesTheLight()
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        using var display = Attach(cache);
        Commit(display, Frame(1,
        [
            Object(1, [new SceneLight(new("#ffcc88"), 6, 1, 1)]),
            Object(2, [new SceneLight(new("#ffffff"), 6, 0, 1)]),
            Object(3, [new SceneLight(new("#ffcc88"), 6, 0.2f, 1)])
        ], []));
        var heavy = display.Objects[new(1)].Root.GetNode<OmniLight3D>("SceneLight");
        var steady = display.Objects[new(2)].Root.GetNode<OmniLight3D>("SceneLight");
        var mild = display.Objects[new(3)].Root.GetNode<OmniLight3D>("SceneLight");
        var samples = 0;
        var heavyOn = 0;
        var mildOff = 0;
        var heavyOffWhenMildOff = 0;
        double real = 0, sim = 0, sample = 0.2;
        var start = display.FlickerSeconds;
        while (display.FlickerSeconds < 0.2 + 19 * 0.4)
        {
            real += 1d / 60;
            sim += 8d / 60;
            display.AdvanceEffects(Frame(1, [], [], seconds: sim), 1d / 60);
            Require(Mathf.IsEqualApprox(steady.LightEnergy, 1), "未受损的灯必须保持点亮。");
            if (display.FlickerSeconds + 1e-9 < sample) continue;
            samples++;
            var heavyLit = heavy.LightEnergy > 0;
            var mildLit = mild.LightEnergy > 0;
            if (heavyLit) heavyOn++;
            if (!mildLit)
            {
                mildOff++;
                if (!heavyLit) heavyOffWhenMildOff++;
            }
            sample += 0.4;
        }
        Require(display.FlickerSeconds - start <= real + 1e-6 && display.FlickerSeconds - start > real - 0.05,
            "8 倍速下闪烁时钟不得快于真实时间。");
        Require(samples == 20 && heavyOn > 0 && heavyOn < samples && mildOff < samples - heavyOn,
            "重伤要比轻伤更常熄灭，并且两种状态都出现。");
        Require(heavyOffWhenMildOff == mildOff, "同一散列下，轻伤熄灭时重伤也必须熄灭。");
        var clock = display.FlickerSeconds;
        var energy = heavy.LightEnergy;
        display.AdvanceEffects(Frame(1, [], [], seconds: sim), 0.5);
        Require(Mathf.IsEqualApprox((float)display.FlickerSeconds, (float)clock) &&
            Mathf.IsEqualApprox(heavy.LightEnergy, energy) && Mathf.IsEqualApprox(steady.LightEnergy, 1),
            "模拟时间暂停时闪烁时钟和灯光都不得变化。");
    }

    [Test]
    public void ImpactsReserveFireballsWithinEveryRollingRealSecond()
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        using var display = Attach(cache);
        FixedSceneFrame? shown = null;
        ulong cueId = 1;
        var created = new List<(double Time, bool Impact)>();
        var previousTime = 0d;
        foreach (var (time, impacts, destructions, expectedFlashes, expectedFireballs) in new[]
        {
            (0d, 10, 1, 2, 1), (.5, 10, 1, 0, 0), (1d, 0, 4, 0, 3), (1.5, 10, 1, 0, 0),
            (2d, 10, 0, 2, 0), (2.5, 10, 1, 0, 1), (3d, 10, 1, 2, 0), (3.5, 0, 2, 0, 1)
        })
        {
            // 模拟时间固定，旧节点不会过期；数量差只统计本次真实时刻的创建。
            if (shown is not null) display.AdvanceEffects(shown, time - previousTime);
            var flashes = Count(display.Root, "Flash");
            var fireballs = Count(display.Root, "Fireball");
            var cues = Enumerable.Range(0, impacts).Select(_ => Cue(cueId++, SceneEffectKind.Impact))
                .Concat(Enumerable.Range(0, destructions).Select(_ => Cue(cueId++, SceneEffectKind.Destruction))).ToImmutableArray();
            var frame = Frame((shown?.Update.Frame.Revision ?? 0) + 1, [], cues, basis: shown?.Update.Frame);
            Commit(display, frame);
            var newFlashes = Count(display.Root, "Flash") - flashes;
            var newFireballs = Count(display.Root, "Fireball") - fireballs;
            Require(newFlashes == expectedFlashes && newFireballs == expectedFireballs,
                $"真实时刻{time}的命中/火球创建数为{newFlashes}/{newFireballs}，应为{expectedFlashes}/{expectedFireballs}。");
            created.AddRange(Enumerable.Repeat((time, true), newFlashes));
            created.AddRange(Enumerable.Repeat((time, false), newFireballs));
            var window = created.Where(item => time - item.Time < 1).ToArray();
            Require(window.Length <= 3 && window.Count(item => item.Impact) <= 2,
                "任意滚动真实秒内，闪光加火球最多3次，普通命中最多2次。");
            shown = frame;
            previousTime = time;
        }
        Require(Count(display.Root, "Fragment") == 11 * 18 && Count(display.Root, "Smoke") == 11,
            "摧毁超过闪光额度时，碎片和烟仍要播放。");
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
            new PersistentSceneEffect(PersistentEffectKind.Sparks, default, 1, 3),
            new PersistentSceneEffect(PersistentEffectKind.Leak, default, .5f, 4),
            new PersistentSceneEffect(PersistentEffectKind.FacilitySmoke, default, .25f, 5));
        var first = Frame(1, [Object(1, [new SceneLight(new("#ffffff"), 4, 0, 1)], effects)],
            [Cue(1, SceneEffectKind.Fragments)]);
        Commit(display, first);
        var root = display.Objects[new(1)].Root;
        var smoke = root.GetNode<MeshInstance3D>("Smoke");
        var fire = root.GetNode<MeshInstance3D>("Fire");
        var sparks = root.GetNode<MeshInstance3D>("Sparks");
        var leak = root.GetNode<MeshInstance3D>("Leak");
        var facility = root.GetNode<MeshInstance3D>("FacilitySmoke");
        var light = root.GetNode<OmniLight3D>("SceneLight");
        var mesh = smoke.Mesh;
        var meshId = mesh.GetInstanceId();
        var material = smoke.MaterialOverride;
        var materialId = material.GetInstanceId();
        Texture2D? mask;
        using (var parameter = ((ShaderMaterial)material).GetShaderParameter("particle_mask"))
            mask = parameter.AsGodotObject() as Texture2D;
        Require(mask is not null && mask.GetWidth() == 128 && mask.GetHeight() == 128,
            "特效必须加载正式的四通道遮罩。");
        var maskId = mask!.GetInstanceId();
        using (var fireMask = ((ShaderMaterial)fire.MaterialOverride).GetShaderParameter("particle_mask"))
            Require(maskId == fireMask.AsGodotObject().GetInstanceId() &&
                smoke.MaterialOverride == facility.MaterialOverride,
                "烟、设施烟和火必须复用正式遮罩，同种外观复用材质。");
        var fragment = Nodes(display, "Fragment")[0];
        Commit(display, Frame(2, [], [], basis: first.Update.Frame, removed: [new(1)]));
        Require(!GodotObject.IsInstanceValid(smoke) && !GodotObject.IsInstanceValid(fire) &&
            !GodotObject.IsInstanceValid(sparks) && !GodotObject.IsInstanceValid(light) &&
            !GodotObject.IsInstanceValid(leak) && !GodotObject.IsInstanceValid(facility), "删除对象必须释放它的灯和持续效果。");
        Require(GodotObject.IsInstanceValid(fragment) && GodotObject.IsInstanceValid(mesh) &&
            GodotObject.IsInstanceValid(material), "已播放的碎片和共享资源必须留下。");
        display.Dispose();
        display.Dispose();
        Require(!GodotObject.IsInstanceValid(fragment), "释放显示后碎片节点必须失效。");
        Require(!GodotObject.IsInstanceValid(mesh) && !GodotObject.IsInstanceIdValid(meshId),
            "释放显示后 look 网格必须在主线程销毁。");
        Require(!GodotObject.IsInstanceValid(material) && !GodotObject.IsInstanceIdValid(materialId),
            "释放显示后 look 材质必须在主线程销毁。");
        Require(!GodotObject.IsInstanceValid(mask) && !GodotObject.IsInstanceIdValid(maskId),
            "释放显示后粒子遮罩必须在主线程销毁。");
    }

    [Test]
    public void SmokeCapKeepsTheEightStrongestDamageAndFacilitySources()
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        using var display = Attach(cache);
        var objects = Enumerable.Range(1, 10).Select(id => Object((ulong)id, effects:
            [new PersistentSceneEffect(PersistentEffectKind.Smoke, default, id <= 4 ? id * .05f : id * .1f, (ulong)id)])).ToList();
        objects[0] = objects[0] with
        {
            Effects =
            [
                .. objects[0].Effects,
                new PersistentSceneEffect(PersistentEffectKind.Fire, default, 1, 1),
                new PersistentSceneEffect(PersistentEffectKind.Leak, default, 1, 1),
                new PersistentSceneEffect(PersistentEffectKind.FacilitySmoke, default, .25f, 1)
            ]
        };
        objects[1] = objects[1] with { Effects = objects[1].Effects.Add(
            new PersistentSceneEffect(PersistentEffectKind.FacilitySmoke, default, .25f, 2)) };
        var frame = Frame(1, [.. objects], [Cue(1, SceneEffectKind.Destruction)]);
        Commit(display, frame);
        for (ulong id = 1; id <= 10; id++)
        {
            var smoke = display.Objects[new(id)].Root.GetNodeOrNull<MeshInstance3D>("Smoke");
            if (id <= 4) Require(smoke is null, $"较弱的冒烟 {id} 不应保留。");
            else Require(smoke is not null && Mathf.IsEqualApprox(smoke.GetInstanceShaderParameter("strength").AsSingle(), id * .1f),
                $"较强的冒烟 {id} 必须保留。");
        }
        Require(display.Objects[new(1)].Root.GetNodeOrNull("Fire") is not null, "火不受冒烟上限影响。");
        Require(display.Objects[new(1)].Root.GetNodeOrNull("Leak") is not null,
            "漏液不占冒烟额度，必须与火和烟并存。");
        Require(Count(display.Root.GetNode("CombatEffects"), "Smoke") == 1 &&
            display.Objects.Values.Sum(item => item.Root.GetChildren().Count(child =>
                child.Name.ToString() is "Smoke" or "FacilitySmoke")) == 8 &&
            display.Objects.Values.Count(item => item.Root.GetNodeOrNull("FacilitySmoke") is not null) == 2,
            "损伤烟和设施烟共用 8 处额度，摧毁烟柱不占用额度。");
    }

    [Test]
    public void PersistentEffectsKeepTheirSimulationPhaseAtLargeTimesAndWhenTheSceneReopens()
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        using var first = Attach(cache);
        using var reopened = Attach(cache);
        var kinds = Enum.GetValues<PersistentEffectKind>();
        var source = Object(1, effects: [.. kinds.Select((kind, index) =>
            new PersistentSceneEffect(kind, new(index, 3, 0), .5f, (ulong)(41 + index)))]);
        const double initial = 1_000_000_000.125;
        const double current = initial + .125;
        Commit(first, Frame(1, [source], [], seconds: initial));
        first.AdvanceEffects(Frame(2, [], [], seconds: current), 0);
        Commit(reopened, Frame(1, [source], [], seconds: current, session: OtherSession));
        foreach (var kind in kinds)
        {
            var a = first.Objects[new(1)].Root.GetNode<MeshInstance3D>(kind.ToString());
            var b = reopened.Objects[new(1)].Root.GetNode<MeshInstance3D>(kind.ToString());
            static float Phase(MeshInstance3D node)
            {
                var elapsed = ((ShaderMaterial)node.MaterialOverride).GetShaderParameter("simulation_seconds").AsSingle()
                    - node.GetInstanceShaderParameter("start_seconds").AsSingle();
                var cycles = elapsed * .35f;
                return cycles - MathF.Floor(cycles);
            }
            var expected = (float)(current * .35 - Math.Floor(current * .35));
            Require(a.Position == b.Position && MathF.Abs(Phase(a) - Phase(b)) < .0001f &&
                MathF.Abs(Phase(a) - expected) < .0001f,
                $"{kind} 在大模拟时刻推进并重开，必须保留着色器 float32 下的模拟相位。");
            Require(Mathf.IsEqualApprox(a.GetInstanceShaderParameter("seed").AsSingle(),
                b.GetInstanceShaderParameter("seed").AsSingle()), "重开场景不得改变持续效果的种子。");
        }
    }

    [Test]
    public void BuildingsCollapseDownwardWithoutUsingTheVehicleExplosionFlashBudget()
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        using var display = Attach(cache);
        var ground = new TerrainCollisionPatch(new(1), new NVector3(-10, 0, -10), 2, 2, 20, [2, 2, 2, 2]);
        var environment = new SceneEnvironment([], [ground]);
        var shown = Frame(1, [], [.. Enumerable.Range(1, 3).Select(id => Cue((ulong)id,
            SceneEffectKind.Destruction, from: new(0, 6, 0),
            subject: new WorldObjectId.Instance(new InstanceId.Building(new((ulong)id))))) ], environment: environment);
        Commit(display, shown);
        Require(Count(display.Root, "Fireball") == 0 && Count(display.Root, "Flash") == 0 &&
            Count(display.Root, "Dust") == 3, "建筑倒塌应扬起灰尘，不产生火球或闪光。");
        var cubes = Nodes(display, "Collapse");
        var initial = Positions(cubes);
        Require(initial.All(position => position.Y >= 2 && position.Y <= 6) && initial.Any(position => position.Y > 4),
            "碎块应从实际建筑中心到地面之间开始坠落。");
        display.AdvanceEffects(Frame(2, [], [], seconds: .1), 0);
        Require(Positions(cubes).Zip(initial).All(pair => pair.First.Y < pair.Second.Y),
            "建筑碎块必须从一开始就向下坠落。");
        display.AdvanceEffects(Frame(3, [], [], seconds: 1), 0);
        Require(Positions(cubes).All(position => position.Y >= 2) &&
            Positions(cubes).Any(position => Mathf.IsEqualApprox(position.Y, 2)),
            "建筑 cue 没有 FragmentCollision 时，碎块也必须停在取样地面上。");
        shown = Frame(3, [], [Cue(20, SceneEffectKind.Destruction, started: 1,
                subject: new WorldObjectId.Instance(new InstanceId.Vehicle(new(7)))),
            Cue(21, SceneEffectKind.Impact, started: 1), Cue(22, SceneEffectKind.Impact, started: 1),
            Cue(23, SceneEffectKind.Impact, started: 1)], seconds: 1, basis: shown.Update.Frame);
        Commit(display, shown);
        Require(Count(display.Root, "Fireball") == 1 && Count(display.Root, "Smoke") == 1 &&
            Count(display.Root, "Flash") == 2 && Count(display.Root, "Fragment") == 18,
            "车辆仍爆炸，建筑倒塌不消耗每真实秒三次的闪光额度。");
        display.AdvanceEffects(Frame(4, [], [], seconds: 4), 0);
        Require(Count(display.Root, "Collapse") == 0 && Count(display.Root, "Dust") == 0 &&
            Count(display.Root, "Fragment") == 0 && Count(display.Root, "Smoke") == 0,
            "倒塌与爆炸结束后不得留下残骸或持久效果。");
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
        display.SetFollowedTrain([lead, rear]);
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

    [Test]
    public void FragmentsRestOnBilinearGroundAndFallThroughWithoutIt()
    {
        var sloped = new TerrainCollisionPatch(new(1), NVector3.Zero, 2, 2, 2, [0, 2, 4, 6]);
        var sampled = CombatEffects.GroundHeight(new(1, 0, 1), [sloped]);
        Require(sampled is not null && Mathf.IsEqualApprox(sampled.Value, 3), "补丁高度必须按双线性插值。");
        Require(CombatEffects.GroundHeight(new(80, 0, 80), [sloped]) is null, "补丁外不得取样。");

        var wide = new TerrainCollisionPatch(new(2), new NVector3(-200, 0, -200), 21, 21, 20,
            Enumerable.Repeat(2f, 21 * 21).ToImmutableArray());
        var environment = new SceneEnvironment([], [wide]);
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        using var display = Attach(cache);
        var shown = Frame(1, [Object(1)],
        [
            Cue(1, SceneEffectKind.Fragments, from: new(0, 6, 0), collision: new VoxelCollision.FullBox()),
            Cue(2, SceneEffectKind.Fragments, from: new(1, 6, 0), collision: new VoxelCollision.LocalBox(default, new(1, 1, 1))),
            Cue(3, SceneEffectKind.Fragments, from: new(-1, 6, 0), collision: new VoxelCollision.None()),
            Cue(4, SceneEffectKind.Fragments, from: new(0, 6, 1)),
            Cue(5, SceneEffectKind.Fragments, from: new(500, 6, 500), collision: new VoxelCollision.FullBox())
        ], environment: environment);
        Commit(display, shown);
        var advanced = Frame(2, [Object(1)], [], seconds: 1.6, basis: shown.Update.Frame, environment: environment);
        display.AdvanceEffects(advanced, 0);
        var nodes = Nodes(display, "Fragment");
        var landed = nodes.Where(node => Mathf.IsEqualApprox(node.Position.Y, 2)).ToArray();
        var falling = nodes.Where(node => node.Position.Y < 1).ToArray();
        Require(nodes.Count == 30 && landed.Length == 12 && falling.Length == 18,
            "整箱和局部箱都停在补丁高度上，None、空碰撞和补丁外继续下落。");
        var parked = landed.Select(node => node.Position).ToArray();
        display.AdvanceEffects(Frame(3, [Object(1)], [], seconds: 2.2, basis: shown.Update.Frame, environment: environment), 0);
        Require(landed.Select(node => node.Position).Zip(parked).All(pair => pair.First.DistanceTo(pair.Second) < 0.001f),
            "落地后不得继续下落。");
        var held = nodes.Select(node => node.Position).ToArray();
        Commit(display, Frame(4, [Object(1) with { Pose = new(new(40, 0, 0), NQuaternion.Identity) }], [],
            seconds: 2.2, basis: shown.Update.Frame, environment: environment));
        Require(nodes.Select(node => node.Position).Zip(held).All(pair => pair.First.DistanceTo(pair.Second) < 0.001f),
            "对象姿态变化不得带动已经放出的碎片。");
    }

    [Test]
    [Chickensoft.GoDotTest.Timeout(180000)]
    public async Task SyntheticCombatLoadBuildsAndReleasesEffects()
    {
        await new UiTestInput(TestScene).Frames(1);
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        using var display = Attach(cache);
        var tree = TestScene.GetTree();
        var nodes = new HashSet<Node>();
        var resources = new HashSet<GodotObject>();
        void Track(Node node)
        {
            if (!ReferenceEquals(node, display.Root) && !display.Root.IsAncestorOf(node)) return;
            nodes.Add(node);
            if (node is MeshInstance3D { Mesh: { } mesh }) resources.Add(mesh);
            if (node is not GeometryInstance3D { MaterialOverride: { } material }) return;
            resources.Add(material);
            if (material is ShaderMaterial { Shader: { } shader } shaderMaterial)
            {
                resources.Add(shader);
                using var particleMask = shaderMaterial.GetShaderParameter("particle_mask");
                if (particleMask.AsGodotObject() is Texture2D texture) resources.Add(texture);
            }
        }
        foreach (var node in display.Root.FindChildren("*", "", true, false).OfType<Node>()) Track(node);
        Track(display.Root);
        void OnNodeAdded(Node node) => Track(node);
        tree.NodeAdded += OnNodeAdded;
        try
        {
            var objects = LoadObjects();
            var shown = Sustain(display, objects, steps: 24);
            Require(display.Objects.Values.Count(item => item.Root.GetNodeOrNull("Smoke") is not null) == 8, "持续负载仍只保留 8 处烟。");
            Require(display.Objects.Values.Count(item => item.Root.GetNodeOrNull("Fire") is not null) == 24, "持续负载必须保留火。");
            Require(display.Objects.Values.Count(item => item.Root.GetNodeOrNull("Sparks") is not null) == 24, "持续负载必须保留火花。");
            Require(display.Objects.Values.Count(item => item.Root.GetNodeOrNull("SceneLight") is not null) == 24, "持续负载必须保留灯。");
            Require(Count(display.Root, "Shot") > 0 && Count(display.Root, "Fragment") > 0 && Count(display.Root, "Smoke") > 0,
                "持续负载中弹道、碎片和摧毁烟必须同时存在。");
            Expire(display, objects, shown.Frame, shown.Sim);
            var capture = System.Environment.GetEnvironmentVariable("TRAIN_UI_CAPTURE_DIR");
            if (!string.IsNullOrWhiteSpace(capture) && DisplayServer.GetName() != "headless")
                await RecordWindowedLoad(display, objects, capture);
        }
        finally { tree.NodeAdded -= OnNodeAdded; }
        Require(nodes.Count > 0 && resources.OfType<Mesh>().Any() && resources.OfType<Material>().Any() &&
            resources.OfType<Shader>().Any(),
            $"负载快照不完整：节点 {nodes.Count}、网格 {resources.OfType<Mesh>().Count()}、" +
            $"材质 {resources.OfType<Material>().Count()}、着色器 {resources.OfType<Shader>().Count()}。");
        display.Dispose();
        cache.Dispose();
        await new UiTestInput(TestScene).Frames(2);
        var leaked = nodes.Concat(resources).Where(GodotObject.IsInstanceValid).ToArray();
        Require(leaked.Length == 0, $"负载释放后仍有 {leaked.Length} 个本用例建立的对象或资源有效：" +
            string.Join("、", leaked.Take(5).Select(item => item switch
            {
                Node node => $"{node.GetType().Name} Name={node.Name}",
                Resource resource => $"{resource.GetType().Name} Class={resource.GetClass()} ResourceName={resource.ResourceName}",
                _ => item.GetType().Name
            })));
    }

    [Test]
    public void FastForwardHoldsShotsAndFlashesForSixRenderedFrames()
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        using var display = Attach(cache);
        var shown = Frame(1, [],
            [Cue(1, SceneEffectKind.Shot, to: new(8, 1, 0)), Cue(2, SceneEffectKind.Impact, from: new(1, 1, 0))]);
        Commit(display, shown);
        double sim = 0;
        var visible = 0;
        for (var frame = 1; frame <= 8; frame++)
        {
            sim += 8d / 60;
            shown = Frame((ulong)frame + 1, [], [], seconds: sim, basis: shown.Update.Frame);
            Commit(display, shown);
            display.AdvanceEffects(shown, 1d / 60);
            var shots = Nodes(display, "Shot");
            var flashes = Nodes(display, "Flash");
            if (frame == 8)
            {
                Require(shots.Count == 0 && flashes.Count == 0, "8× 下可见期结束后必须删除射击和闪光。");
                continue;
            }
            Require(shots.Count == 1 && flashes.Count == 1, $"8× 第 {frame} 帧的射击和闪光必须还在。");
            Require(Alpha(shots[0]) > 0 && Alpha(flashes[0]) > 0, $"8× 第 {frame} 帧的透明度必须非零。");
            if (frame > 6) continue;
            Require(Alpha(shots[0]) > 0.01f && Alpha(flashes[0]) > 0.01f,
                $"8× 第 {frame} 帧的射击和闪光必须清晰可见。");
            visible++;
        }
        Require(visible == 6, $"8× 下至少需要 6 个连续清晰绘制帧，实际 {visible}。");
        Require(Mathf.IsEqualApprox((float)display.FlickerSeconds, (float)(8d / 60)),
            "8× 下表现时钟必须每帧只前进 1/60 真实秒。");
    }

    [Test]
    public void OneTimeKeepsTheNaturalShotAndFlashFade()
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        using var display = Attach(cache);
        var shown = Frame(1, [],
            [Cue(1, SceneEffectKind.Shot, to: new(8, 1, 0)), Cue(2, SceneEffectKind.Impact, from: new(1, 1, 0))]);
        Commit(display, shown);
        double sim = 0;
        for (var frame = 1; frame <= 9; frame++)
        {
            sim += 1d / 60;
            shown = Frame((ulong)frame + 1, [], [], seconds: sim, basis: shown.Update.Frame);
            Commit(display, shown);
            display.AdvanceEffects(shown, 1d / 60);
            var shots = Nodes(display, "Shot");
            var flashes = Nodes(display, "Flash");
            var flashExpected = frame <= 7;
            var shotExpected = frame <= 8;
            Require(flashes.Count == (flashExpected ? 1 : 0), $"1× 第 {frame} 帧的闪光寿命必须不变。");
            Require(shots.Count == (shotExpected ? 1 : 0), $"1× 第 {frame} 帧的射击寿命必须不变。");
            if (flashExpected)
                Require(Mathf.IsEqualApprox(Alpha(flashes[0]), NaturalAlpha(flashes[0])),
                    $"1× 第 {frame} 帧的闪光淡出不得改变。");
            if (shotExpected)
                Require(Mathf.IsEqualApprox(Alpha(shots[0]), NaturalAlpha(shots[0])),
                    $"1× 第 {frame} 帧的射击淡出不得改变。");
        }
    }

    [Test]
    public void PausedSimulationFreezesTheVisibleFloor()
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        using var display = Attach(cache);
        var shown = Frame(1, [],
            [Cue(1, SceneEffectKind.Shot, to: new(8, 1, 0)), Cue(2, SceneEffectKind.Impact, from: new(1, 1, 0))]);
        Commit(display, shown);
        double sim = 0;
        for (var frame = 1; frame <= 4; frame++)
        {
            sim += 8d / 60;
            shown = Frame((ulong)frame + 1, [], [], seconds: sim, basis: shown.Update.Frame);
            Commit(display, shown);
            display.AdvanceEffects(shown, 1d / 60);
        }
        var shot = Nodes(display, "Shot").Single();
        var flash = Nodes(display, "Flash").Single();
        var alpha = Alpha(shot);
        var flashAlpha = Alpha(flash);
        var clock = display.FlickerSeconds;
        for (var frame = 0; frame < 30; frame++)
            display.AdvanceEffects(Frame(100 + (ulong)frame, [], [], seconds: sim), 1d / 60);
        Require(Nodes(display, "Shot").Count == 1 && Nodes(display, "Flash").Count == 1,
            "模拟停住时节点必须保留。");
        Require(Mathf.IsEqualApprox((float)display.FlickerSeconds, (float)clock) &&
            Mathf.IsEqualApprox(Alpha(shot), alpha) && Mathf.IsEqualApprox(Alpha(flash), flashAlpha),
            "模拟停住时表现时钟和透明度都不得变化。");
        for (var frame = 1; frame <= 4; frame++)
        {
            sim += 8d / 60;
            shown = Frame(140 + (ulong)frame, [], [], seconds: sim, basis: shown.Update.Frame);
            Commit(display, shown);
            display.AdvanceEffects(shown, 1d / 60);
        }
        Require(Nodes(display, "Shot").Count == 0 && Nodes(display, "Flash").Count == 0,
            "模拟恢复后可见期必须继续走完并删除节点。");
    }

    [Test]
    public void FlashCapStillLimitsCreationsWhileFlashesLiveLonger()
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        using var display = Attach(cache);
        ulong cue = 1;
        ulong revision = 1;
        var shown = Frame(revision, [], [.. Enumerable.Range(0, 10).Select(_ =>
            Cue(cue++, SceneEffectKind.Impact, from: new(1, 1, 0)))]);
        Commit(display, shown);
        Require(Count(display.Root, "Flash") == 2, "10 次命中在闪光额度内只能创建 2 个闪光。");
        double sim = 0;
        for (var frame = 1; frame <= 6; frame++)
        {
            sim += 8d / 60;
            shown = Frame(++revision, [], [], seconds: sim, basis: shown.Update.Frame);
            Commit(display, shown);
            display.AdvanceEffects(shown, 1d / 60);
        }
        Require(Count(display.Root, "Flash") == 2, "闪光节点必须活过 0.10 真实秒。");
        shown = Frame(++revision, [], [.. Enumerable.Range(0, 10).Select(_ =>
            Cue(cue++, SceneEffectKind.Impact, from: new(2, 1, 0), started: sim))], seconds: sim, basis: shown.Update.Frame);
        Commit(display, shown);
        display.AdvanceEffects(shown, 0);
        Require(Count(display.Root, "Flash") == 2,
            $"同一滚动真实秒内，新命中仍受闪光上限约束（实际 {Count(display.Root, "Flash")}）。");
        for (var frame = 0; frame < 60; frame++)
        {
            sim += 8d / 60;
            shown = Frame(++revision, [], [], seconds: sim, basis: shown.Update.Frame);
            Commit(display, shown);
            display.AdvanceEffects(shown, 1d / 60);
        }
        Require(Count(display.Root, "Flash") == 0, "旧闪光必须已经结束。");
        shown = Frame(++revision, [], [.. Enumerable.Range(0, 10).Select(_ =>
            Cue(cue++, SceneEffectKind.Impact, from: new(3, 1, 0), started: sim))], seconds: sim, basis: shown.Update.Frame);
        Commit(display, shown);
        display.AdvanceEffects(shown, 0);
        Require(Count(display.Root, "Flash") == 2,
            $"滚动窗口结束后必须重新允许 2 次命中闪光（实际 {Count(display.Root, "Flash")}）。");
    }

    [Test]
    public void FastForwardKeepsOtherEffectsOnTheirSimulationLifetime()
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        using var display = Attach(cache);
        var shown = Frame(1, [], [Cue(1, SceneEffectKind.Destruction, from: new(1, 1, 0))]);
        Commit(display, shown);
        Require(Count(display.Root, "Fireball") == 1,
            $"车辆摧毁必须创建火球（实际 {Count(display.Root, "Fireball")}）。");
        double sim = 0;
        for (var frame = 1; frame <= 3; frame++)
        {
            sim += 8d / 60;
            shown = Frame((ulong)frame + 1, [], [], seconds: sim, basis: shown.Update.Frame);
            Commit(display, shown);
            display.AdvanceEffects(shown, 1d / 60);
        }
        Require(Count(display.Root, "Fireball") == 0, "火球必须仍按 0.4 模拟秒删除，不得套用最短可见期。");
        Require(Count(display.Root, "Fragment") > 0 && Count(display.Root, "Smoke") == 1,
            "碎片和烟的寿命不得改变。");
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
        SceneSessionId? session = null, ulong space = 1, SceneEnvironment? environment = null) => new()
    {
        Origin = origin,
        SimulationSeconds = seconds,
        Update = new(basis, new(session ?? Session, new(1), new(space), revision), shift,
            objects, removed.IsDefault ? [] : removed, environment ?? new([], []), effects)
    };

    private static SceneObject Object(ulong id, ImmutableArray<SceneLight> lights = default,
        ImmutableArray<PersistentSceneEffect> effects = default) => new(new(id), null,
        new(default, NQuaternion.Identity), null, [], lights.IsDefault ? [] : lights, effects.IsDefault ? [] : effects);

    private static SceneEffectCue Cue(ulong id, SceneEffectKind kind, double started = 0, NVector3 from = default,
        NVector3? to = null, WorldObjectId? subject = null, VoxelCollision? collision = null) =>
        new(new(id), kind, from, to, null, collision, subject, started);

    private static (FixedSceneFrame Frame, double Sim) Sustain(SceneDisplay display, ImmutableArray<SceneObject> objects, int steps)
    {
        FixedSceneFrame? shown = null;
        double sim = 0;
        ulong revision = 1;
        ulong cueId = 1;
        for (var step = 0; step < steps; step++)
        {
            var cues = new List<SceneEffectCue>
            {
                Cue(cueId++, SceneEffectKind.Shot, started: sim, from: new(-2, 1, 0), to: new(2, 1, 0)),
                Cue(cueId++, SceneEffectKind.Impact, started: sim, from: new(0, 1, 0)),
                Cue(cueId++, SceneEffectKind.Fragments, started: sim, from: new(0, 2, 0), collision: new VoxelCollision.FullBox())
            };
            if (step % 24 == 0)
                cues.Add(Cue(cueId++, SceneEffectKind.Destruction, started: sim, from: new(0, 1, 0)));
            var frame = Frame(revision++, objects, [.. cues], seconds: sim, basis: shown?.Update.Frame);
            Commit(display, frame);
            shown = frame;
            sim += 1d / 12;
            display.AdvanceEffects(Frame(revision, objects, [], seconds: sim, basis: shown.Update.Frame), 1d / 12);
        }
        return (shown!, sim);
    }

    private static void Expire(SceneDisplay display, ImmutableArray<SceneObject> objects, FixedSceneFrame shown, double sim,
        SceneSessionId? session = null)
    {
        var end = Frame(900, [], [], seconds: sim + 4, basis: shown.Update.Frame,
            removed: [.. objects.Select(item => item.Id)], session: session);
        Commit(display, end);
        display.AdvanceEffects(Frame(901, [], [], seconds: sim + 4, basis: end.Update.Frame, session: session), 0.5);
        Require(display.Objects.Count == 0 && Count(display.Root, "Shot") == 0 && Count(display.Root, "Fragment") == 0 &&
            Count(display.Root, "Flash") == 0 && Count(display.Root, "Fireball") == 0 && Count(display.Root, "Smoke") == 0,
            "负载结束后对象、弹道、碎片、闪光和烟都必须消失。");
    }

    private static ImmutableArray<SceneObject> LoadObjects()
    {
        var objects = new SceneObject[24];
        for (var index = 0; index < 24; index++)
        {
            var id = (ulong)(index + 1);
            var angle = index / 24f * Mathf.Tau;
            objects[index] = new(new(id), null, new(new(Mathf.Cos(angle) * 3, 0, Mathf.Sin(angle) * 3), NQuaternion.Identity),
                null, [], [new SceneLight(new("#ffcc88"), 8, index == 0 ? 0.8f : 0, id)],
                [
                    new PersistentSceneEffect(PersistentEffectKind.Smoke, default, 1, id),
                    new PersistentSceneEffect(PersistentEffectKind.Fire, default, 1, id),
                    new PersistentSceneEffect(PersistentEffectKind.Sparks, default, 1, id)
                ]);
        }
        return [.. objects];
    }

    private async Task RecordWindowedLoad(SceneDisplay display, ImmutableArray<SceneObject> objects, string directory)
    {
        var window = TestScene.GetWindow();
        window.Mode = Window.ModeEnum.Windowed;
        window.Size = new Vector2I(1280, 720);
        var camera = new Camera3D { Current = true, Position = new Vector3(3.2f, 2.4f, 4.6f) };
        var mesh = new BoxMesh { Size = new Vector3(0.7f, 0.7f, 0.7f) };
        var material = new StandardMaterial3D { AlbedoColor = new(0.55f, 0.55f, 0.52f) };
        var box = new MeshInstance3D { Mesh = mesh, MaterialOverride = material, Position = new(0.15f, 0.35f, -0.85f) };
        TestScene.AddChild(camera);
        TestScene.AddChild(box);
        camera.LookAt(new Vector3(0, 1, 0), Vector3.Up);
        try
        {
            SceneObject PictureObject(ulong id, Vector3 at, params PersistentSceneEffect[] effects) => new(new(id), null,
                new(new NVector3(at.X, at.Y, at.Z), NQuaternion.Identity), null, [],
                id == 1 ? [new SceneLight(new("#ffcc88"), 6, 0, 1)] : [], [.. effects]);
            var picture = Frame(1,
            [
                PictureObject(1, new(0, 0.6f, 0), new PersistentSceneEffect(PersistentEffectKind.Smoke, default, 1, 1)),
                PictureObject(2, new(-1.3f, 0.5f, 0.2f), new PersistentSceneEffect(PersistentEffectKind.Fire, default, 1, 2)),
                PictureObject(3, new(0.55f, 1.2f, 1.15f), new PersistentSceneEffect(PersistentEffectKind.Sparks, default, 1, 3))
            ],
            [
                Cue(1, SceneEffectKind.Shot, from: new(-1.6f, 1.85f, 0.2f), to: new(1.6f, 1.85f, 0.2f)),
                Cue(2, SceneEffectKind.Fragments, from: new(-0.2f, 1.15f, 1.35f), collision: new VoxelCollision.FullBox())
            ], session: OtherSession);
            Commit(display, picture);
            var visible = Frame(2, picture.Update.Upserts, [], seconds: 0.08, basis: picture.Update.Frame, session: OtherSession);
            display.AdvanceEffects(visible, 0, camera.GlobalPosition);
            await TestScene.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            await TestScene.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            Directory.CreateDirectory(directory);
            using var image = window.GetTexture().GetImage();
            Require(image.SavePng(Path.Combine(directory, "combat-effects.png")) == Error.Ok, "战斗特效截图保存失败。");
            var first = await SampleLoad(display, objects, camera, 1, picture);
            var eighth = await SampleLoad(display, objects, camera, 8, first.Frame);
            File.WriteAllText(Path.Combine(directory, "combat-load.json"), System.Text.Json.JsonSerializer.Serialize(new
            {
                note = "合成特效负载，不是目录里的繁忙战斗。繁忙战斗要等 #351 和 #356，由 #303 测量。",
                speed1 = first.Report,
                speed8 = eighth.Report
            }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            Expire(display, objects, eighth.Frame, eighth.Sim, OtherSession);
        }
        finally
        {
            if (GodotObject.IsInstanceValid(box)) box.Free();
            if (GodotObject.IsInstanceValid(camera)) camera.Free();
            mesh.Dispose();
            material.Dispose();
        }
    }

    private async Task<(FixedSceneFrame Frame, double Sim, object Report)> SampleLoad(SceneDisplay display,
        ImmutableArray<SceneObject> objects, Camera3D camera, int speed, FixedSceneFrame shown)
    {
        var times = new List<double>();
        var marks = new List<object>();
        double sim = shown.SimulationSeconds;
        double hit = sim;
        double destruction = sim;
        ulong revision = shown.Update.Frame.Revision + 1;
        ulong cueId = 1_000 + (ulong)speed * 100_000;
        var process = System.Diagnostics.Process.GetCurrentProcess();
        var cpuStart = process.TotalProcessorTime;
        var wall = System.Diagnostics.Stopwatch.StartNew();
        double elapsed = 0;
        double previous = 0;
        const double warmup = 5;
        const double sample = 30;
        var marked = new bool[2];
        object Snapshot(string phase) => new
        {
            phase,
            videoMemUsed = RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.VideoMemUsed),
            objects = Performance.GetMonitor(Performance.Monitor.ObjectCount),
            nodes = Performance.GetMonitor(Performance.Monitor.ObjectNodeCount),
            resources = Performance.GetMonitor(Performance.Monitor.ObjectResourceCount)
        };
        while (elapsed < warmup + sample)
        {
            await TestScene.ToSignal(TestScene.GetTree(), SceneTree.SignalName.ProcessFrame);
            var now = wall.Elapsed.TotalSeconds;
            var realDelta = Math.Max(0.001, now - previous);
            previous = now;
            elapsed += realDelta;
            sim += realDelta * speed;
            var cues = new List<SceneEffectCue>();
            while (hit <= sim)
            {
                cues.Add(Cue(cueId++, SceneEffectKind.Shot, started: hit, from: new(-2, 1, 0), to: new(2, 1, 0)));
                cues.Add(Cue(cueId++, SceneEffectKind.Impact, started: hit, from: new(0, 1, 0)));
                cues.Add(Cue(cueId++, SceneEffectKind.Fragments, started: hit, from: new(0, 2, 0),
                    collision: new VoxelCollision.FullBox()));
                hit += 1d / 12;
            }
            while (destruction <= sim)
            {
                cues.Add(Cue(cueId++, SceneEffectKind.Destruction, started: destruction, from: new(0, 1, 0)));
                destruction += 2;
            }
            if (cues.Count > 0)
            {
                var frame = Frame(revision++, objects, [.. cues], seconds: sim, basis: shown.Update.Frame, session: OtherSession);
                Commit(display, frame);
                shown = frame;
            }
            display.AdvanceEffects(Frame(revision, objects, [], seconds: sim, basis: shown.Update.Frame, session: OtherSession),
                realDelta, camera.GlobalPosition);
            if (elapsed < warmup) continue;
            times.Add(realDelta);
            var index = !marked[0] ? 0 : !marked[1] && elapsed >= warmup + sample / 2 ? 1 : -1;
            if (index >= 0)
            {
                marked[index] = true;
                process.Refresh();
                marks.Add(Snapshot(index == 0 ? "start" : "middle"));
            }
        }
        process.Refresh();
        marks.Add(Snapshot("end"));
        var cpu = (process.TotalProcessorTime - cpuStart).TotalSeconds / Math.Max(wall.Elapsed.TotalSeconds, 0.001);
        var distribution = FrameMeasurements.Distribution(times);
        var report = new
        {
            speed,
            frames = times.Count,
            p50Seconds = distribution.P50,
            p95Seconds = distribution.P95,
            p99Seconds = distribution.P99,
            maxSeconds = distribution.Maximum,
            processCpu = cpu,
            processorCount = System.Environment.ProcessorCount,
            samples = marks
        };
        return (shown, sim, report);
    }

    private static float Alpha(MeshInstance3D node) => Alpha(node, hold: true);

    private static float NaturalAlpha(MeshInstance3D node) => Alpha(node, hold: false);

    private static float Alpha(MeshInstance3D node, bool hold)
    {
        var material = (ShaderMaterial)node.MaterialOverride;
        var elapsed = Mathf.Max(material.GetShaderParameter("simulation_seconds").AsSingle() -
            node.GetInstanceShaderParameter("start_seconds").AsSingle(), 0);
        var lifetime = node.GetInstanceShaderParameter("lifetime").AsSingle();
        var phase = lifetime <= 0 ? 1 : Mathf.Clamp(elapsed / Mathf.Max(lifetime, .001f), 0, 1);
        var holdSeconds = node.GetInstanceShaderParameter("hold_seconds").AsSingle();
        if (hold && holdSeconds > 0)
            phase = Mathf.Min(phase, Mathf.Clamp(
                (material.GetShaderParameter("flicker_seconds").AsSingle() -
                    node.GetInstanceShaderParameter("hold_start").AsSingle()) / holdSeconds, 0, 1));
        return 1 - Mathf.SmoothStep(.72f, 1, phase);
    }

    private static int Count(Node root, string name)
    {
        var effects = root.Name == "CombatEffects" ? root : root.GetNode("CombatEffects");
        return effects.GetNodeOrNull(name)?.GetChildCount() ?? 0;
    }

    private static List<MeshInstance3D> Nodes(SceneDisplay display, string name) =>
        display.Root.GetNode("CombatEffects").GetNode(name).GetChildren().OfType<MeshInstance3D>().ToList();

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
