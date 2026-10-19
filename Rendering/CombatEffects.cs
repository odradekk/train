using System.Collections.Immutable;
using Godot;
using Train.App.Assets;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Rendering;

/// <summary>
/// 战斗灯光、持续烟火和一次性提示。网格与材质按外观共享，只在释放时销毁。
/// </summary>
internal sealed class CombatEffects : IDisposable
{
    // 1 格 = 0.5 米，重力 9.8 米/秒² 换成 19.6 格/秒²。
    internal const double Gravity = 19.6;
    internal const int SmokeCap = 8;
    const double ShotSeconds = 0.15;
    const double ImpactSeconds = 0.12;
    const double FragmentSeconds = 2.5;
    const double FireballSeconds = 0.4;
    const double DestructionSmokeSeconds = 3;
    const double ShakeSeconds = 0.25;
    const float ShakeDistance = 0.15f;
    const int FragmentBurst = 6;
    const int DestructionBurst = 18;
    const int RailwayBurst = 4;

    enum Appearance { Smoke, Fire, Sparks, Shot, Flash, Fragment }

    sealed class SharedLook(Mesh mesh, ShaderMaterial material)
    {
        internal Mesh Mesh { get; } = mesh;
        internal ShaderMaterial Material { get; } = material;
    }

    sealed class PersistentNode
    {
        internal MeshInstance3D Node { get; set; } = null!;
        internal SceneObjectId Object { get; init; }
        internal PersistentEffectKind Kind { get; init; }
        internal ulong Seed { get; init; }
    }

    sealed class LiveLight
    {
        internal OmniLight3D Node { get; set; } = null!;
        internal SceneObjectId Object { get; init; }
        internal ulong Seed { get; init; }
        internal float Damage { get; set; }
        internal bool Bright { get; set; }
        internal List<double> Edges { get; } = [];
    }

    sealed class OneShot
    {
        internal MeshInstance3D Node { get; init; } = null!;
        internal double Started { get; init; }
        internal double Lifetime { get; init; }
        internal Vector3 P0 { get; set; }
        internal Vector3 Target { get; set; }
        internal Vector3 Velocity { get; init; }
        internal bool Shot { get; init; }
        internal bool Falls { get; init; }
    }

    readonly AssetResourceCache _cache;
    readonly Shader _shader;
    readonly SharedLook[] _looks;
    readonly List<PersistentNode> _persistent = [];
    readonly List<LiveLight> _lights = [];
    readonly List<OneShot> _oneShots = [];
    readonly HashSet<ulong> _played = [];
    readonly List<double> _flashes = [];
    ImmutableArray<VehicleId> _followed = [];
    bool _shakeEnabled = true;
    double _simulationSeconds;
    double _realSeconds;
    double _shakeLeft;
    Vector3 _shakeDirection;
    bool _disposed;

    internal Node3D Root { get; } = new() { Name = "CombatEffects" };

    internal CombatEffects(AssetResourceCache cache)
    {
        cache.RequireMainThread();
        _cache = cache;
        _shader = ResourceLoader.Load<Shader>("res://Rendering/combat_effect.gdshader", cacheMode: ResourceLoader.CacheMode.Ignore)
            ?? throw new InvalidOperationException("战斗特效着色器无法加载。");
        _looks =
        [
            Look(Appearance.Smoke, new QuadMesh { Size = Vector2.One }),
            Look(Appearance.Fire, new QuadMesh { Size = Vector2.One }),
            Look(Appearance.Sparks, new QuadMesh { Size = Vector2.One }),
            Look(Appearance.Shot, new BoxMesh { Size = Vector3.One }),
            Look(Appearance.Flash, new SphereMesh { Radius = 0.5f, Height = 1, RadialSegments = 8, Rings = 4 }),
            Look(Appearance.Fragment, new BoxMesh { Size = Vector3.One })
        ];
    }

    internal void SetView(ImmutableArray<VehicleId> vehicles, bool shakeEnabled)
    {
        _followed = vehicles;
        _shakeEnabled = shakeEnabled;
    }

    internal void SetVehicles(ImmutableArray<VehicleId> vehicles) => _followed = vehicles;

    internal void Apply(ISceneFrame? previous, ISceneFrame frame,
        IReadOnlyDictionary<SceneObjectId, SceneDisplay.DisplayObject> objects)
    {
        _cache.RequireMainThread();
        _simulationSeconds = frame.SimulationSeconds;
        Align(previous, frame);
        Sync(objects);
        foreach (var cue in frame.Update.Effects) Play(cue);
        WriteTime();
    }

    internal Vector3 Advance(ISceneFrame frame, double realDelta)
    {
        _cache.RequireMainThread();
        _realSeconds += Math.Max(0, realDelta);
        _simulationSeconds = frame.SimulationSeconds;
        WriteTime();
        foreach (var light in _lights)
            if (GodotObject.IsInstanceValid(light.Node)) UpdateLight(light);
        for (var index = _oneShots.Count - 1; index >= 0; index--)
        {
            var shot = _oneShots[index];
            if (!GodotObject.IsInstanceValid(shot.Node) || Age(shot.Started) >= shot.Lifetime)
            {
                if (GodotObject.IsInstanceValid(shot.Node)) shot.Node.Free();
                _oneShots.RemoveAt(index);
                continue;
            }
            Place(shot);
        }
        if (_shakeLeft > 0) _shakeLeft = Math.Max(0, _shakeLeft - realDelta);
        if (!_shakeEnabled || _shakeLeft <= 0) return Vector3.Zero;
        return _shakeDirection * (float)(_shakeLeft / ShakeSeconds);
    }

    public void Dispose()
    {
        _cache.RequireMainThread();
        if (_disposed) return;
        _disposed = true;
        foreach (var light in _lights)
            if (GodotObject.IsInstanceValid(light.Node)) light.Node.Free();
        foreach (var node in _persistent)
            if (GodotObject.IsInstanceValid(node.Node)) node.Node.Free();
        foreach (var shot in _oneShots)
            if (GodotObject.IsInstanceValid(shot.Node)) shot.Node.Free();
        _lights.Clear();
        _persistent.Clear();
        _oneShots.Clear();
        if (GodotObject.IsInstanceValid(Root)) Root.Free();
        foreach (var look in _looks)
        {
            look.Material.Dispose();
            look.Mesh.Dispose();
        }
        _shader.Dispose();
    }

    SharedLook Look(Appearance appearance, Mesh mesh)
    {
        var material = new ShaderMaterial { Shader = _shader };
        try
        {
            material.SetShaderParameter("appearance", (int)appearance);
            return new(mesh, material);
        }
        catch
        {
            material.Dispose();
            mesh.Dispose();
            throw;
        }
    }

    void Align(ISceneFrame? previous, ISceneFrame frame)
    {
        if (previous is null || previous.Update.Frame.Session != frame.Update.Frame.Session ||
            previous.Update.Frame.Space != frame.Update.Frame.Space)
        {
            ClearOneShots();
            _played.Clear();
            return;
        }
        var shift = frame.Update.Base is not null ? ToGodot(frame.Update.ShiftExistingRoots)
            : new Vector3((float)(previous.Origin.X - frame.Origin.X), (float)(previous.Origin.Y - frame.Origin.Y),
                (float)(previous.Origin.Z - frame.Origin.Z));
        if (shift == Vector3.Zero) return;
        foreach (var shot in _oneShots)
        {
            shot.P0 += shift;
            shot.Target += shift;
            Place(shot);
        }
    }

    void ClearOneShots()
    {
        foreach (var shot in _oneShots)
            if (GodotObject.IsInstanceValid(shot.Node)) shot.Node.Free();
        _oneShots.Clear();
    }

    void Sync(IReadOnlyDictionary<SceneObjectId, SceneDisplay.DisplayObject> objects)
    {
        var lights = objects.Values.SelectMany(item => item.Source.Lights.Select(light => (item, light))).ToArray();
        var effects = DesiredEffects(objects);
        Retain(_lights, lights.Select(item => (item.item.Source.Id, item.light.Seed)),
            (live, pair) => live.Object == pair.Id && live.Seed == pair.Seed,
            live => live.Node);
        Retain(_persistent, effects.Select(item => (item.Item.Source.Id, item.Effect.Kind, item.Effect.Seed)),
            (live, pair) => live.Object == pair.Id && live.Kind == pair.Kind && live.Seed == pair.Seed,
            live => live.Node);
        foreach (var (item, light) in lights)
        {
            var live = _lights.FirstOrDefault(entry => entry.Object == item.Source.Id && entry.Seed == light.Seed &&
                GodotObject.IsInstanceValid(entry.Node));
            if (live is null)
            {
                var node = new OmniLight3D { Name = "SceneLight", ShadowEnabled = false };
                item.Root.AddChild(node, forceReadableName: true);
                live = new LiveLight { Node = node, Object = item.Source.Id, Seed = light.Seed };
                _lights.Add(live);
            }
            else if (live.Node.GetParent() != item.Root) live.Node.Reparent(item.Root);
            live.Damage = light.Damage;
            live.Node.LightColor = Html(light.Color, Colors.White);
            live.Node.OmniRange = (float)light.RangeCells;
            live.Node.Position = Vector3.Zero;
            UpdateLight(live);
        }
        foreach (var (item, effect) in effects)
        {
            var live = _persistent.FirstOrDefault(entry => entry.Object == item.Source.Id && entry.Kind == effect.Kind &&
                entry.Seed == effect.Seed && GodotObject.IsInstanceValid(entry.Node));
            var appearance = effect.Kind switch
            {
                PersistentEffectKind.Fire => Appearance.Fire,
                PersistentEffectKind.Sparks => Appearance.Sparks,
                _ => Appearance.Smoke
            };
            if (live is null)
            {
                var node = Instance(appearance, effect.Kind.ToString(), effect.Strength, effect.Seed, 0, 0, PersistentTint(effect.Kind));
                node.Position = ToGodot(effect.LocalPosition);
                item.Root.AddChild(node, forceReadableName: true);
                _persistent.Add(new PersistentNode
                {
                    Node = node, Object = item.Source.Id, Kind = effect.Kind, Seed = effect.Seed
                });
            }
            else
            {
                if (live.Node.GetParent() != item.Root) live.Node.Reparent(item.Root);
                live.Node.Position = ToGodot(effect.LocalPosition);
                live.Node.SetInstanceShaderParameter("strength", effect.Strength);
            }
        }
    }

    (SceneDisplay.DisplayObject Item, PersistentSceneEffect Effect)[] DesiredEffects(
        IReadOnlyDictionary<SceneObjectId, SceneDisplay.DisplayObject> objects)
    {
        var listed = objects.Values.SelectMany(item => item.Source.Effects.Select(effect => (Item: item, Effect: effect)))
            .Where(item => item.Effect.Kind is PersistentEffectKind.Smoke or PersistentEffectKind.Fire or PersistentEffectKind.Sparks)
            .ToArray();
        var smoke = listed.Where(item => item.Effect.Kind == PersistentEffectKind.Smoke)
            .OrderByDescending(item => item.Effect.Strength).ThenBy(item => item.Item.Source.Id.Value).ThenBy(item => item.Effect.Seed)
            .Take(SmokeCap);
        return [.. smoke, .. listed.Where(item => item.Effect.Kind != PersistentEffectKind.Smoke)];
    }

    static void Retain<TLive, TKey>(List<TLive> live, IEnumerable<TKey> desired, Func<TLive, TKey, bool> same, Func<TLive, Node> node)
    {
        var keep = new HashSet<TLive>();
        foreach (var key in desired)
        {
            var match = live.FirstOrDefault(item => !keep.Contains(item) && GodotObject.IsInstanceValid(node(item)) && same(item, key));
            if (match is not null) keep.Add(match);
        }
        for (var index = live.Count - 1; index >= 0; index--)
            if (!keep.Contains(live[index]))
            {
                if (GodotObject.IsInstanceValid(node(live[index]))) node(live[index]).Free();
                live.RemoveAt(index);
            }
    }

    void Play(SceneEffectCue cue)
    {
        if (!_played.Add(cue.Id.Value)) return;
        var age = _simulationSeconds - cue.StartedAtSimulationSeconds;
        var from = ToGodot(cue.From);
        var color = cue.Color is { } paint && Color.HtmlIsValid(paint.Value) ? Color.FromHtml(paint.Value) : (Color?)null;
        switch (cue.Kind)
        {
            case SceneEffectKind.Shot:
                if (cue.To is { } to && age < ShotSeconds)
                    AddShot(from, ToGodot(to), cue.StartedAtSimulationSeconds, color ?? new Color(1, 0.95f, 0.8f));
                break;
            case SceneEffectKind.Impact:
                if (age < ImpactSeconds && TakeFlash())
                    AddBurst(Appearance.Flash, "Flash", from, cue, ImpactSeconds, 0.55f, color ?? new Color(1, 0.95f, 0.8f));
                Shake(cue);
                break;
            case SceneEffectKind.Fragments:
                AddFragments(cue, from, color ?? FragmentColor, FragmentBurst, age);
                break;
            case SceneEffectKind.Destruction:
                if (age < FireballSeconds && TakeFlash())
                    AddBurst(Appearance.Flash, "Fireball", from, cue, FireballSeconds, 2.2f, color ?? new Color(1, 0.55f, 0.2f));
                if (age < DestructionSmokeSeconds)
                    AddBurst(Appearance.Smoke, "Smoke", from, cue, DestructionSmokeSeconds, 1.4f, new Color(0.45f, 0.43f, 0.4f));
                AddFragments(cue, from, color ?? FragmentColor, DestructionBurst, age);
                Shake(cue);
                break;
            case SceneEffectKind.RailwayDamage:
                AddFragments(cue, from, color ?? FragmentColor, RailwayBurst, age);
                break;
        }
    }

    void AddFragments(SceneEffectCue cue, Vector3 from, Color color, int count, double age)
    {
        if (age >= FragmentSeconds) return;
        for (var index = 0; index < count; index++)
        {
            var size = 0.25f + (float)(Mix(cue.Id.Value, (ulong)index) % 1000) / 1000f * 0.25f;
            var node = Instance(Appearance.Fragment, "Fragment", 1, cue.Id.Value + (ulong)index,
                cue.StartedAtSimulationSeconds, (float)FragmentSeconds, color);
            node.Scale = Vector3.One * size;
            var shot = new OneShot
            {
                Node = node, Started = cue.StartedAtSimulationSeconds, Lifetime = FragmentSeconds,
                P0 = from, Velocity = Velocity(cue.Id.Value, index), Falls = true
            };
            _oneShots.Add(shot);
            Root.AddChild(node, forceReadableName: true);
            Place(shot);
        }
    }

    void AddShot(Vector3 from, Vector3 to, double started, Color color)
    {
        var node = Instance(Appearance.Shot, "Shot", 1, 0, started, (float)ShotSeconds, color);
        var shot = new OneShot
        {
            Node = node, Started = started, Lifetime = ShotSeconds, P0 = from, Target = to, Shot = true
        };
        _oneShots.Add(shot);
        Root.AddChild(node, forceReadableName: true);
        Place(shot);
    }

    void AddBurst(Appearance appearance, string name, Vector3 at, SceneEffectCue cue, double lifetime, float scale, Color color)
    {
        var node = Instance(appearance, name, 1, cue.Id.Value, cue.StartedAtSimulationSeconds, (float)lifetime, color);
        node.Scale = Vector3.One * scale;
        var shot = new OneShot
        {
            Node = node, Started = cue.StartedAtSimulationSeconds, Lifetime = lifetime, P0 = at
        };
        _oneShots.Add(shot);
        Root.AddChild(node, forceReadableName: true);
        Place(shot);
    }

    MeshInstance3D Instance(Appearance appearance, string name, float strength, ulong seed, double started, float lifetime, Color tint)
    {
        var look = _looks[(int)appearance];
        var node = new MeshInstance3D
        {
            Name = name, Mesh = look.Mesh, MaterialOverride = look.Material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
        };
        node.SetInstanceShaderParameter("strength", strength);
        node.SetInstanceShaderParameter("seed", (seed & 0xFFFFFF) / (float)0xFFFFFF);
        node.SetInstanceShaderParameter("start_seconds", (float)started);
        node.SetInstanceShaderParameter("lifetime", lifetime);
        node.SetInstanceShaderParameter("tint", tint);
        return node;
    }

    void Place(OneShot shot)
    {
        if (shot.Shot)
        {
            var delta = shot.Target - shot.P0;
            var length = delta.Length();
            shot.Node.Position = (shot.P0 + shot.Target) * 0.5f;
            shot.Node.Basis = length > 0.001f ? Basis.LookingAt(delta / length, Vector3.Up) : Basis.Identity;
            shot.Node.Scale = new Vector3(0.05f, 0.05f, Math.Max(length, 0.05f));
            return;
        }
        shot.Node.Position = shot.Falls ? FragmentPosition(shot.P0, shot.Velocity, Age(shot.Started)) : shot.P0;
    }

    void UpdateLight(LiveLight light)
    {
        if (light.Damage <= 0)
        {
            light.Node.LightEnergy = 1;
            light.Bright = true;
            return;
        }
        var desired = DesiredBright(light.Seed, _simulationSeconds);
        if (desired && !light.Bright && Allow(light.Edges)) light.Bright = true;
        else if (!desired) light.Bright = false;
        light.Node.LightEnergy = light.Bright ? 1 : 0;
    }

    bool TakeFlash() => Allow(_flashes);

    bool Allow(List<double> times)
    {
        times.RemoveAll(time => _realSeconds - time >= 1);
        if (times.Count >= 3) return false;
        times.Add(_realSeconds);
        return true;
    }

    void Shake(SceneEffectCue cue)
    {
        if (!_shakeEnabled || cue.Subject is not WorldObjectId.Instance { Id: InstanceId.Vehicle vehicle } ||
            !_followed.Contains(vehicle.Id)) return;
        var angle = (Mix(cue.Id.Value, 3) % 10_000) / 10_000f * Mathf.Tau;
        _shakeDirection = new Vector3(Mathf.Cos(angle), 0.35f, Mathf.Sin(angle)).Normalized() * ShakeDistance;
        _shakeLeft = ShakeSeconds;
    }

    void WriteTime()
    {
        foreach (var look in _looks) look.Material.SetShaderParameter("simulation_seconds", _simulationSeconds);
    }

    double Age(double started) => Math.Max(0, _simulationSeconds - started);

    static bool DesiredBright(ulong seed, double simulationSeconds)
    {
        var step = 0.1 + (seed & 7) * 0.01;
        return ((long)Math.Floor(simulationSeconds / step) & 1) == 0;
    }

    static Vector3 FragmentPosition(Vector3 origin, Vector3 velocity, double age)
    {
        var time = (float)age;
        return origin + velocity * time + new Vector3(0, (float)(-0.5 * Gravity) * time * time, 0);
    }

    static Vector3 Velocity(ulong id, int index)
    {
        var yaw = (Mix(id, (ulong)index) % 10_000) / 10_000f * Mathf.Tau;
        var horizontal = 2.5f + (Mix(id, (ulong)(index + 100)) % 1000) / 1000f * 3f;
        var up = 3.5f + (Mix(id, (ulong)(index + 200)) % 1000) / 1000f * 3f;
        return new Vector3(Mathf.Cos(yaw) * horizontal, up, Mathf.Sin(yaw) * horizontal);
    }

    static ulong Mix(ulong left, ulong right)
    {
        var value = left ^ right * 0x9E3779B97F4A7C15UL;
        value ^= value >> 30;
        value *= 0xBF58476D1CE4E5B9UL;
        value ^= value >> 27;
        return value;
    }

    static readonly Color FragmentColor = Color.FromHtml("#9d9a93");

    static Color PersistentTint(PersistentEffectKind kind) => kind switch
    {
        PersistentEffectKind.Fire => new Color(1, 0.45f, 0.12f),
        PersistentEffectKind.Sparks => new Color(1, 0.78f, 0.35f),
        _ => new Color(0.55f, 0.55f, 0.55f)
    };

    static Color Html(PaintColor color, Color fallback) =>
        Color.HtmlIsValid(color.Value) ? Color.FromHtml(color.Value) : fallback;

    static Vector3 ToGodot(System.Numerics.Vector3 value) => new(value.X, value.Y, value.Z);
}
