using System.Collections.Immutable;
using Godot;
using Train.App.Assets;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Rendering;

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
    const double CollapseDustSeconds = 1.2;
    const double ShakeSeconds = 0.25;
    const float ShakeDistance = 0.15f;
    const double FlickerBucket = 0.4;
    const int FragmentBurst = 6;
    const int DestructionBurst = 18;
    const int RailwayBurst = 4;

    enum Appearance { Smoke, Fire, Sparks, Shot, Flash, Fragment, Leak, Dust }

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
    }

    sealed class OneShot
    {
        internal MeshInstance3D Node { get; init; } = null!;
        internal double Started { get; init; }
        internal double Lifetime { get; init; }
        internal Vector3 P0 { get; set; }
        internal Vector3 Target { get; set; }
        internal Vector3 Velocity { get; init; }
        internal Vector3 Landing { get; set; }
        internal bool Shot { get; init; }
        internal bool Falls { get; init; }
        internal bool TestGround { get; init; }
        internal bool Landed { get; set; }
    }

    readonly AssetResourceCache _cache;
    readonly Shader _shader;
    readonly SharedLook[] _looks;
    readonly Dictionary<string, Node3D> _groups = [];
    readonly Dictionary<(ulong Object, PersistentEffectKind Kind, ulong Seed), PersistentNode> _persistent = [];
    readonly Dictionary<(ulong Object, ulong Seed), LiveLight> _lights = [];
    readonly List<OneShot> _oneShots = [];
    readonly HashSet<ulong> _played = [];
    readonly List<double> _flashes = [];
    ImmutableArray<VehicleId> _followed = [];
    ImmutableArray<TerrainCollisionPatch> _patches = [];
    Vector3? _camera;
    double _simulationSeconds;
    double _realSeconds;
    double _flickerSeconds;
    double _previousSimulation;
    double _timeOrigin;
    double _shakeLeft;
    Vector3 _shakeDirection;
    bool _disposed;

    internal Node3D Root { get; } = new() { Name = "CombatEffects" };
    internal double FlickerSeconds => _flickerSeconds;

    internal CombatEffects(AssetResourceCache cache)
    {
        cache.RequireMainThread();
        _cache = cache;
        _shader = ResourceLoader.Load<Shader>("res://Rendering/combat_effect.gdshader", cacheMode: ResourceLoader.CacheMode.Ignore)
            ?? throw new InvalidOperationException("战斗特效着色器无法加载。");
        _looks =
        [
            Look(Appearance.Smoke, Quads(4)),
            Look(Appearance.Fire, Quads(3)),
            Look(Appearance.Sparks, Quads(6)),
            Look(Appearance.Shot, new BoxMesh { Size = Vector3.One }),
            Look(Appearance.Flash, new SphereMesh { Radius = 0.5f, Height = 1, RadialSegments = 8, Rings = 4 }),
            Look(Appearance.Fragment, new BoxMesh { Size = Vector3.One }),
            Look(Appearance.Leak, Quads(5)),
            Look(Appearance.Dust, Quads(6))
        ];
    }

    internal void SetFollowedTrain(ImmutableArray<VehicleId> vehicles) => _followed = vehicles;

    internal void Apply(ISceneFrame? previous, ISceneFrame frame,
        IReadOnlyDictionary<SceneObjectId, SceneDisplay.DisplayObject> objects)
    {
        _cache.RequireMainThread();
        _simulationSeconds = frame.SimulationSeconds;
        _patches = frame.Update.Environment.TerrainCollision;
        Align(previous, frame);
        Sync(objects);
        foreach (var cue in frame.Update.Effects) Play(cue);
        _played.Clear();
        foreach (var cue in frame.Update.Effects) _played.Add(cue.Id.Value);
        WriteTime();
    }

    internal Vector3 Advance(ISceneFrame frame, double realDelta, Vector3? camera = null)
    {
        _cache.RequireMainThread();
        var real = Math.Max(0, realDelta);
        _realSeconds += real;
        var simDelta = Math.Max(0, frame.SimulationSeconds - _previousSimulation);
        _previousSimulation = frame.SimulationSeconds;
        _flickerSeconds += Math.Min(simDelta, real);
        _camera = camera;
        _simulationSeconds = frame.SimulationSeconds;
        WriteTime();
        foreach (var light in _lights.Values) UpdateLight(light);
        for (var index = _oneShots.Count - 1; index >= 0; index--)
        {
            var shot = _oneShots[index];
            if (Age(shot.Started) >= shot.Lifetime)
            {
                shot.Node.Free();
                _oneShots.RemoveAt(index);
                continue;
            }
            Place(shot);
        }
        if (_shakeLeft > 0) _shakeLeft = Math.Max(0, _shakeLeft - real);
        if (_shakeLeft <= 0) return Vector3.Zero;
        return _shakeDirection * (float)(_shakeLeft / ShakeSeconds);
    }

    public void Dispose()
    {
        _cache.RequireMainThread();
        if (_disposed) return;
        _disposed = true;
        foreach (var light in _lights.Values) light.Node.Free();
        foreach (var node in _persistent.Values) node.Node.Free();
        foreach (var shot in _oneShots) shot.Node.Free();
        _lights.Clear();
        _persistent.Clear();
        _oneShots.Clear();
        Root.Free();
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
        material.SetShaderParameter("appearance", (int)appearance);
        return new(mesh, material);
    }

    void Align(ISceneFrame? previous, ISceneFrame frame)
    {
        if (previous is null || previous.Update.Frame.Session != frame.Update.Frame.Session ||
            previous.Update.Frame.Space != frame.Update.Frame.Space)
        {
            ClearOneShots();
            _played.Clear();
            _timeOrigin = frame.SimulationSeconds;
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
            shot.Landing += shift;
            Place(shot);
        }
    }

    void ClearOneShots()
    {
        foreach (var shot in _oneShots) shot.Node.Free();
        _oneShots.Clear();
    }

    void Sync(IReadOnlyDictionary<SceneObjectId, SceneDisplay.DisplayObject> objects)
    {
        var lights = new Dictionary<(ulong, ulong), (SceneDisplay.DisplayObject Item, SceneLight Light)>();
        foreach (var item in objects.Values)
            foreach (var light in item.Source.Lights)
                lights[(item.Source.Id.Value, light.Seed)] = (item, light);
        foreach (var key in _lights.Keys.ToArray())
            if (!lights.ContainsKey(key))
            {
                _lights[key].Node.Free();
                _lights.Remove(key);
            }
        foreach (var (key, (item, light)) in lights)
        {
            if (!_lights.TryGetValue(key, out var live))
            {
                var node = new OmniLight3D { Name = "SceneLight", ShadowEnabled = false };
                item.Root.AddChild(node);
                live = new LiveLight { Node = node, Object = item.Source.Id, Seed = light.Seed };
                _lights[key] = live;
            }
            live.Damage = light.Damage;
            live.Node.LightColor = Html(light.Color, Colors.White);
            live.Node.OmniRange = (float)light.RangeCells;
            live.Node.Position = Vector3.Zero;
            UpdateLight(live);
        }

        var effects = DesiredEffects(objects);
        var desired = new Dictionary<(ulong, PersistentEffectKind, ulong), (SceneDisplay.DisplayObject Item, PersistentSceneEffect Effect)>();
        foreach (var (item, effect) in effects)
            desired[(item.Source.Id.Value, effect.Kind, effect.Seed)] = (item, effect);
        foreach (var key in _persistent.Keys.ToArray())
            if (!desired.ContainsKey(key))
            {
                _persistent[key].Node.Free();
                _persistent.Remove(key);
            }
        foreach (var (key, (item, effect)) in desired)
        {
            if (!_persistent.TryGetValue(key, out var live))
            {
                var appearance = effect.Kind switch
                {
                    PersistentEffectKind.Fire => Appearance.Fire,
                    PersistentEffectKind.Sparks => Appearance.Sparks,
                    PersistentEffectKind.Leak => Appearance.Leak,
                    _ => Appearance.Smoke
                };
                var node = Instance(appearance, effect.Kind.ToString(), effect.Strength, effect.Seed,
                    effect.Kind is PersistentEffectKind.FacilitySmoke or PersistentEffectKind.Leak ? 0 : _simulationSeconds,
                    0, PersistentTint(effect.Kind));
                node.Position = ToGodot(effect.LocalPosition);
                item.Root.AddChild(node);
                _persistent[key] = new PersistentNode
                {
                    Node = node, Object = item.Source.Id, Kind = effect.Kind, Seed = effect.Seed
                };
            }
            else
            {
                live.Node.Position = ToGodot(effect.LocalPosition);
                live.Node.SetInstanceShaderParameter("strength", effect.Strength);
            }
        }
    }

    (SceneDisplay.DisplayObject Item, PersistentSceneEffect Effect)[] DesiredEffects(
        IReadOnlyDictionary<SceneObjectId, SceneDisplay.DisplayObject> objects)
    {
        var listed = objects.Values.SelectMany(item => item.Source.Effects.Select(effect => (Item: item, Effect: effect)))
            .Where(item => item.Effect.Kind is PersistentEffectKind.Smoke or PersistentEffectKind.Fire or
                PersistentEffectKind.Sparks or PersistentEffectKind.Leak or PersistentEffectKind.FacilitySmoke)
            .ToArray();
        var smoke = listed.Where(item => item.Effect.Kind is PersistentEffectKind.Smoke or PersistentEffectKind.FacilitySmoke)
            .OrderByDescending(item => item.Effect.Strength).ThenBy(item => item.Item.Source.Id.Value).ThenBy(item => item.Effect.Seed)
            .Take(SmokeCap);
        return [.. smoke, .. listed.Where(item => item.Effect.Kind is not PersistentEffectKind.Smoke and not PersistentEffectKind.FacilitySmoke)];
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
                if (cue.Subject is WorldObjectId.Instance { Id: InstanceId.Building })
                {
                    var ground = new Vector3(from.X, GroundHeight(from, _patches) ?? from.Y, from.Z);
                    if (age < CollapseDustSeconds)
                        AddBurst(Appearance.Dust, "Dust", ground + Vector3.Up * .15f, cue,
                            CollapseDustSeconds, 1.4f, new Color(.45f, .43f, .4f));
                    AddFragments(cue, ground, color ?? FragmentColor, DestructionBurst, age, collapse: true);
                    break;
                }
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

    void AddFragments(SceneEffectCue cue, Vector3 from, Color color, int count, double age, bool collapse = false)
    {
        if (age >= FragmentSeconds) return;
        for (var index = 0; index < count; index++)
        {
            var size = 0.25f + (float)(Mix(cue.Id.Value, (ulong)index) % 1000) / 1000f * 0.25f;
            var node = Instance(Appearance.Fragment, "Fragment", 1, cue.Id.Value + (ulong)index,
                cue.StartedAtSimulationSeconds, (float)FragmentSeconds, color);
            node.Scale = Vector3.One * size;
            var velocity = Velocity(cue.Id.Value, index);
            var position = from;
            if (collapse)
            {
                position += new Vector3(velocity.X * .2f, 1 + Mathf.Abs(velocity.Y) * .2f, velocity.Z * .2f);
                velocity = new Vector3(velocity.X * .08f, -2, velocity.Z * .08f);
            }
            var shot = new OneShot
            {
                Node = node, Started = cue.StartedAtSimulationSeconds, Lifetime = FragmentSeconds,
                P0 = position, Velocity = velocity, Falls = true,
                TestGround = cue.FragmentCollision is VoxelCollision.FullBox or VoxelCollision.LocalBox
            };
            _oneShots.Add(shot);
            Group(collapse ? "Collapse" : "Fragment").AddChild(node);
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
        Group("Shot").AddChild(node);
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
        Group(name).AddChild(node);
        Place(shot);
    }

    Node3D Group(string name)
    {
        if (_groups.TryGetValue(name, out var node)) return node;
        node = new Node3D { Name = name };
        Root.AddChild(node);
        _groups[name] = node;
        return node;
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
        node.SetInstanceShaderParameter("start_seconds", Relative(started));
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
            var midpoint = (shot.P0 + shot.Target) * 0.5f;
            var thickness = ShotThickness(shot.P0, shot.Target);
            shot.Node.Position = midpoint;
            shot.Node.Basis = length > 0.001f ? Basis.LookingAt(delta / length, Vector3.Up) : Basis.Identity;
            shot.Node.Scale = new Vector3(thickness, thickness, Math.Max(length, thickness));
            return;
        }
        if (!shot.Falls)
        {
            shot.Node.Position = shot.P0;
            return;
        }
        if (shot.Landed)
        {
            shot.Node.Position = shot.Landing;
            return;
        }
        var position = FragmentPosition(shot.P0, shot.Velocity, Age(shot.Started));
        if (shot.TestGround && GroundHeight(position, _patches) is { } height && position.Y <= height)
        {
            shot.Landed = true;
            shot.Landing = new Vector3(position.X, height, position.Z);
            shot.Node.Position = shot.Landing;
            return;
        }
        shot.Node.Position = position;
    }

    float ShotThickness(Vector3 from, Vector3 to)
    {
        const float minimum = 0.2f;
        if (_camera is not { } camera) return minimum;
        var height = DisplayServer.WindowGetSize().Y;
        if (height <= 0) height = 1080;
        var nearest = Geometry3D.GetClosestPointToSegment(camera, from, to);
        var distance = Mathf.Max(1f, nearest.DistanceTo(camera));
        var pixel = 2f * Mathf.Tan(Mathf.DegToRad(30f)) * distance / height;
        return Mathf.Max(minimum, pixel * 4f);
    }

    void UpdateLight(LiveLight light) => light.Node.LightEnergy = Lit(light.Seed, light.Damage, _flickerSeconds) ? 1 : 0;

    static bool Lit(ulong seed, float damage, double flicker)
    {
        if (damage <= 0) return true;
        var bucket = (long)Math.Floor(flicker / FlickerBucket);
        if (bucket < 0) bucket = 0;
        var roll = (Mix(seed, (ulong)bucket) % 1000) / 1000.0;
        return roll >= damage * 0.5;
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
        if (cue.Subject is not WorldObjectId.Instance { Id: InstanceId.Vehicle vehicle } ||
            !_followed.Contains(vehicle.Id)) return;
        var angle = (Mix(cue.Id.Value, 3) % 10_000) / 10_000f * Mathf.Tau;
        _shakeDirection = new Vector3(Mathf.Cos(angle), 0.35f, Mathf.Sin(angle)).Normalized() * ShakeDistance;
        _shakeLeft = ShakeSeconds;
    }

    void WriteTime()
    {
        var seconds = Relative(_simulationSeconds);
        var flicker = (float)_flickerSeconds;
        foreach (var look in _looks)
        {
            look.Material.SetShaderParameter("simulation_seconds", seconds);
            look.Material.SetShaderParameter("flicker_seconds", flicker);
        }
    }

    float Relative(double seconds) => (float)(seconds - _timeOrigin);

    double Age(double started) => Math.Max(0, _simulationSeconds - started);

    internal static float? GroundHeight(Vector3 point, ImmutableArray<TerrainCollisionPatch> patches)
    {
        foreach (var patch in patches)
        {
            var x = (point.X - patch.Origin.X) / patch.SpacingCells;
            var z = (point.Z - patch.Origin.Z) / patch.SpacingCells;
            if (x < 0 || z < 0 || x > patch.Width - 1 || z > patch.Depth - 1) continue;
            return Sample(patch, x, z);
        }
        return null;
    }

    static float Sample(TerrainCollisionPatch patch, double x, double z)
    {
        var x0 = Math.Min((int)Math.Floor(x), patch.Width - 1);
        var z0 = Math.Min((int)Math.Floor(z), patch.Depth - 1);
        var x1 = Math.Min(x0 + 1, patch.Width - 1);
        var z1 = Math.Min(z0 + 1, patch.Depth - 1);
        var tx = x1 == x0 ? 0 : x - x0;
        var tz = z1 == z0 ? 0 : z - z0;
        float At(int ix, int iz) => patch.Heights[iz * patch.Width + ix];
        var a = At(x0, z0) + (float)tx * (At(x1, z0) - At(x0, z0));
        var b = At(x0, z1) + (float)tx * (At(x1, z1) - At(x0, z1));
        return a + (float)tz * (b - a);
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

    static ArrayMesh Quads(int count)
    {
        var vertices = new Vector3[count * 4];
        var normals = new Vector3[count * 4];
        var uvs = new Vector2[count * 4];
        var uv2 = new Vector2[count * 4];
        var indices = new int[count * 6];
        for (var i = 0; i < count; i++)
        {
            var vertex = i * 4;
            vertices[vertex] = new(-0.5f, -0.5f, 0);
            vertices[vertex + 1] = new(0.5f, -0.5f, 0);
            vertices[vertex + 2] = new(0.5f, 0.5f, 0);
            vertices[vertex + 3] = new(-0.5f, 0.5f, 0);
            uvs[vertex] = new(0, 1);
            uvs[vertex + 1] = new(1, 1);
            uvs[vertex + 2] = new(1, 0);
            uvs[vertex + 3] = new(0, 0);
            for (var corner = 0; corner < 4; corner++)
            {
                normals[vertex + corner] = Vector3.Back;
                uv2[vertex + corner] = new(i, count);
            }
            var index = i * 6;
            indices[index] = vertex;
            indices[index + 1] = vertex + 1;
            indices[index + 2] = vertex + 2;
            indices[index + 3] = vertex;
            indices[index + 4] = vertex + 2;
            indices[index + 5] = vertex + 3;
        }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.Normal] = normals;
        arrays[(int)Mesh.ArrayType.TexUV] = uvs;
        arrays[(int)Mesh.ArrayType.TexUV2] = uv2;
        arrays[(int)Mesh.ArrayType.Index] = indices;
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    static readonly Color FragmentColor = Color.FromHtml("#9d9a93");

    static Color PersistentTint(PersistentEffectKind kind) => kind switch
    {
        PersistentEffectKind.Fire => new Color(1, 0.45f, 0.12f),
        PersistentEffectKind.Sparks => new Color(1, 0.78f, 0.35f),
        PersistentEffectKind.Leak => new Color(.15f, .12f, .09f),
        PersistentEffectKind.FacilitySmoke => new Color(.5f, .52f, .54f),
        _ => new Color(0.55f, 0.55f, 0.55f)
    };

    static Color Html(PaintColor color, Color fallback) =>
        Color.HtmlIsValid(color.Value) ? Color.FromHtml(color.Value) : fallback;

    static Vector3 ToGodot(System.Numerics.Vector3 value) => new(value.X, value.Y, value.Z);
}
