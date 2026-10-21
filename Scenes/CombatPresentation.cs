using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes;

internal sealed class PresentationState
{
    private const int LineSideCells = 16;
    private const int LineStaggerCells = 8;
    private const int BesideCells = 12;
    private const double PounceSeconds = 0.4;
    private readonly List<Noted> incoming = [];
    private readonly List<HeldCue> pending = [];
    private readonly Dictionary<(InstanceId Instance, string Path), TurretAim> turrets = [];
    private readonly Dictionary<CreatureId, (double StartedAt, HitTarget Target)> pounces = [];
    private readonly Dictionary<WorldObjectId, (ScenePoint Position, Quaternion Rotation)> poses = [];
    private readonly Dictionary<CreatureId, Vector3> creatures = [];
    private ScenePoint frameOrigin;
    private readonly Dictionary<CreatureAppearanceProjection, (ImmutableArray<SceneObject> Objects, ImmutableDictionary<SceneObjectId, string> Keys)> templates = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<CreatureId, (CreatureAppearanceProjection Appearance, ImmutableArray<SceneObject> Objects)> bodies = [];
    private ulong nextEffect;

    internal void Clear()
    {
        incoming.Clear();
        pending.Clear();
        turrets.Clear();
        pounces.Clear();
        poses.Clear();
        creatures.Clear();
        bodies.Clear();
        templates.Clear();
    }

    internal void Note(SceneWorldSnapshot before, SceneWorldSnapshot snapshot, ImmutableArray<EventEnvelope> events, WorldLocation? here)
    {
        foreach (var item in events)
        {
            if (!InSpace(item.Event, here)) continue;
            var seconds = item.Position.CompletedSteps * SimulationTime.StepSeconds;
            incoming.Add(new(item, StructureAims(before, snapshot, item.Event)));
            if (item.Event is CoreEvent.CombatHits hits)
                foreach (var hit in hits.Hits)
                {
                    if (hit.Source is AttackSource.Weapon weapon)
                        NoteTurret(snapshot, weapon, hit.Target, seconds);
                    if (hit.Source is AttackSource.Creature creature)
                        pounces[creature.Individual.Creature] = (seconds, hit.Target);
                }
        }
    }

    internal void Acknowledge(SceneFrame? displayed, SceneRequestId request, SceneSpaceId space)
    {
        if (displayed is null || displayed.Update.Frame.Request != request || displayed.Update.Frame.Space != space) return;
        var seen = displayed.Update.Effects.Select(item => item.Id).ToHashSet();
        pending.RemoveAll(item => seen.Contains(item.Id));
    }

    internal void ApplyTurrets(ImmutableArray<SceneObject>.Builder objects, InstanceView instance, string prefix,
        Func<string, SceneObjectId?> findId, double seconds, SceneWorldSnapshot snapshot, ScenePoint origin)
    {
        frameOrigin = origin;
        foreach (var aim in turrets.Values.Where(item => item.Instance == instance.Id).ToArray())
        {
            if (findId(prefix + aim.Path) is not { } id)
            {
                turrets.Remove((aim.Instance, aim.Path));
                continue;
            }
            var index = -1;
            for (var cursor = 0; cursor < objects.Count; cursor++)
                if (objects[cursor].Id == id) { index = cursor; break; }
            if (index < 0)
            {
                turrets.Remove((aim.Instance, aim.Path));
                continue;
            }
            if (!aim.Resolved) Resolve(aim, objects, id, snapshot);
            if (!aim.Resolved) continue;
            objects[index] = objects[index] with { Pose = aim.Pose(objects[index].Pose, seconds) };
        }
    }

    internal void AddCreatures(ImmutableArray<SceneObject>.Builder objects, SceneWorldSnapshot snapshot, WorldLocation here,
        ScenePoint origin, RailwayPath? path, NodeLayout? layout, double seconds, Func<string, SceneObjectId> objectId,
        Func<double, double, double>? ground = null)
    {
        frameOrigin = origin;
        creatures.Clear();
        var alive = new HashSet<CreatureId>();
        var used = new HashSet<CreatureAppearanceProjection>(ReferenceEqualityComparer.Instance);
        var dead = Dead(incoming.Select(item => item.Envelope.Event));
        var combatGroups = CombatGroups(snapshot, here);
        var lanes = new Dictionary<int, double>();
        if (here is not WorldLocation.Edge || path is not null)
        {
            foreach (var group in Groups(snapshot, here))
            {
                if (snapshot.Core.FindCreatureGroup(group) is not Lookup<CreatureGroupView>.Found found) continue;
                var combat = combatGroups.Contains(group);
                foreach (var creature in found.Value.Creatures)
                {
                    var appearance = snapshot.Content.FindCreature(creature.Definition) is ContentLookup<CreatureAppearanceProjection>.Found located
                        ? located.Value : null;
                    var occupied = appearance?.Scene.OccupiedBounds;
                    var minZ = occupied?.Minimum.Z ?? 0;
                    var endZ = occupied is null ? LineStaggerCells : occupied.Maximum.Z + 1;
                    var side = Side(snapshot.Core.World.WorldSeed, combat ? CombatId(snapshot, here) : 0, creature.Id.Value);
                    var along = lanes.TryGetValue(side, out var tail) ? tail - minZ : 0d;
                    lanes[side] = along + endZ;
                    var placed = Place(here, origin, path, layout, found.Value, along, side, combat, ground);
                    creatures[creature.Id] = placed.Position;
                    if (dead.Contains((group, creature.Id)) || appearance is null) continue;
                    alive.Add(creature.Id);
                    used.Add(appearance);
                    var pose = new ScenePose(placed.Position + PounceOffset(snapshot, objects, creature.Id, placed, seconds) + new Vector3(0, Bob(seconds), 0),
                        placed.Rotation);
                    var body = Body(snapshot, appearance, group, creature.Id, objectId);
                    var ratio = (double)creature.Health / creature.MaximumHealth;
                    var lightDamage = (float)Math.Max(0, (StructureDamage.FireRatio - ratio) / StructureDamage.FireRatio);
                    if (lightDamage > 0)
                        body = body.Select(item => item.Lights.IsEmpty ? item : item with
                        {
                            Lights = item.Lights.Select(light => light with { Damage = lightDamage }).ToImmutableArray()
                        }).ToImmutableArray();
                    objects.AddRange(SceneGeometry.Place(new(body, [], []), pose).Objects);
                }
            }
        }
        foreach (var id in bodies.Keys.Where(id => !alive.Contains(id)).ToArray()) bodies.Remove(id);
        foreach (var appearance in templates.Keys.Where(item => !used.Contains(item)).ToArray()) templates.Remove(appearance);
    }

    internal ImmutableArray<SceneEffectCue> Finish(ImmutableArray<SceneObject>.Builder objects, SceneWorldSnapshot snapshot,
        WorldLocation? here, ScenePoint origin, RailwayPath? path)
    {
        frameOrigin = origin;
        var composed = Compose(objects);
        foreach (var noted in incoming)
        {
            var seconds = noted.Envelope.Position.CompletedSteps * SimulationTime.StepSeconds;
            switch (noted.Envelope.Event)
            {
                case CoreEvent.CombatHits hits:
                    foreach (var hit in hits.Hits)
                        AddHit(snapshot, composed.Roots, noted, hit, seconds);
                    break;
                case CoreEvent.CombatDestruction destruction:
                    foreach (var unit in destruction.Units)
                        AddUnit(snapshot, unit, seconds, path);
                    foreach (var part in destruction.Parts)
                        AddPart(snapshot, composed.Roots, part, seconds, path);
                    break;
                case CoreEvent.RailBroken broken when path is not null && here is WorldLocation.Edge edge && broken.Edge == edge.Id:
                    Cue(SceneEffectKind.RailwayDamage, path.Sample(broken.Break.DistanceMeters * RailwayCoordinates.CellsPerMeter).Position,
                        null, null, null, null, seconds);
                    break;
            }
        }
        incoming.Clear();
        poses.Clear();
        foreach (var (subject, pose) in composed.Roots)
            poses[subject] = (World(pose.Position, origin), pose.Rotation);
        return Emit(origin);
    }

    private void AddHit(SceneWorldSnapshot snapshot, Dictionary<WorldObjectId, ScenePose> roots, Noted noted, CombatHit hit, double seconds)
    {
        var from = Source(snapshot, roots, hit.Source);
        var to = TargetPoint(snapshot, roots, hit.Target);
        var subject = HitSubject(hit.Target);
        var (color, collision) = Struck(snapshot, noted, hit.Target);
        Cue(SceneEffectKind.Shot, from, to, color, collision, subject, seconds);
        Cue(SceneEffectKind.Impact, to, null, color, collision, subject, seconds);
        Cue(SceneEffectKind.Fragments, to, null, color, collision, subject, seconds);
    }

    private void AddUnit(SceneWorldSnapshot snapshot, DestroyedUnit unit, double seconds, RailwayPath? path)
    {
        switch (unit)
        {
            case DestroyedUnit.Instance instance:
                var subject = new WorldObjectId.Instance(instance.Value.Instance);
                Cue(SceneEffectKind.Destruction, Point(subject) ?? Map(instance.Value.Location, path), null, null, null, subject, seconds);
                break;
            case DestroyedUnit.Creature creature:
                var body = new WorldObjectId.Creature(creature.Value.Creature);
                var position = creatures.TryGetValue(creature.Value.Creature, out var placed) ? World(placed, frameOrigin) :
                    Point(body) ?? Map(creature.Value.Location, path);
                var (color, collision) = CreatureFragments(snapshot, creature.Value.Definition);
                Cue(SceneEffectKind.Fragments, position, null, color, collision, body, seconds);
                break;
        }
    }

    private void AddPart(SceneWorldSnapshot snapshot, Dictionary<WorldObjectId, ScenePose> roots, DestroyedPart part,
        double seconds, RailwayPath? path)
    {
        var subject = new WorldObjectId.Instance(part.Instance.Instance);
        var at = CellCenter(snapshot, roots, part.Instance.Instance, part.Instance.Design, part.Part) is { } center
            ? World(center, frameOrigin) : Point(subject) ?? Map(part.Instance.Location, path);
        var (color, collision) = PartSurface(snapshot, part.Instance.Design, part.Part);
        Cue(SceneEffectKind.Fragments, at, null, color, collision, subject, seconds);
    }

    private void Cue(SceneEffectKind kind, Vector3? from, Vector3? to, PaintColor? color, VoxelCollision? collision,
        WorldObjectId? subject, double seconds) =>
        Cue(kind, from is { } start ? World(start, frameOrigin) : null, to is { } end ? World(end, frameOrigin) : null,
            color, collision, subject, seconds);

    private void Cue(SceneEffectKind kind, ScenePoint? from, ScenePoint? to, PaintColor? color, VoxelCollision? collision,
        WorldObjectId? subject, double seconds)
    {
        if (from is not { } start) return;
        if (kind == SceneEffectKind.Shot && to is null) return;
        pending.Add(new(new(++nextEffect), kind, start, to, color, collision, subject, seconds));
    }

    private ImmutableArray<SceneEffectCue> Emit(ScenePoint origin) => pending.Select(item => new SceneEffectCue(item.Id, item.Kind,
        Frame(item.From, origin), item.To is { } to ? Frame(to, origin) : null, item.Color, item.Collision, item.Subject,
        item.StartedAt)).ToImmutableArray();

    private Vector3? Source(SceneWorldSnapshot snapshot, Dictionary<WorldObjectId, ScenePose> roots, AttackSource source) => source switch
    {
        AttackSource.Weapon weapon => CellCenter(snapshot, roots, weapon.Instance.Instance, weapon.Instance.Design, weapon.WeaponPart),
        AttackSource.Creature creature => CreatureFrame(creature.Individual.Creature),
        _ => null
    };

    private Vector3? TargetPoint(SceneWorldSnapshot snapshot, Dictionary<WorldObjectId, ScenePose> roots, HitTarget target) => target switch
    {
        HitTarget.Part part => CellCenter(snapshot, roots, part.Instance.Instance, part.Instance.Design, part.HitPart),
        HitTarget.Creature creature => CreatureFrame(creature.Individual.Creature),
        HitTarget.Structure structure => StructureFrame(roots, structure),
        _ => null
    };

    private Vector3? StructureFrame(Dictionary<WorldObjectId, ScenePose> roots, HitTarget.Structure structure)
    {
        if (Aim(structure) is not { } aim) return null;
        return Offset(roots, new WorldObjectId.Instance(structure.Instance.Instance), aim.Local);
    }

    private Vector3? CellCenter(SceneWorldSnapshot snapshot, Dictionary<WorldObjectId, ScenePose> roots,
        InstanceId instance, DesignHash design, PartId part)
    {
        var subject = new WorldObjectId.Instance(instance);
        if (snapshot.Content.FindDesign(design) is ContentLookup<DesignEvaluation>.Found found && found.Value is not DesignEvaluation.Failed)
        {
            var voxel = DesignGeometry.Projection(found.Value).Voxels.Where(item => item.Number == part)
                .OrderBy(item => item.At.X).ThenBy(item => item.At.Y).ThenBy(item => item.At.Z).FirstOrDefault();
            if (voxel is not null)
                return Offset(roots, subject, new(voxel.At.X + .5f, voxel.At.Y + .5f, voxel.At.Z + .5f));
        }
        return Offset(roots, subject, Vector3.Zero);
    }

    private Vector3? Offset(Dictionary<WorldObjectId, ScenePose> roots, WorldObjectId subject, Vector3 local)
    {
        if (roots.TryGetValue(subject, out var pose))
            return pose.Position + Vector3.Transform(local, pose.Rotation);
        if (!poses.TryGetValue(subject, out var world)) return null;
        return Frame(world.Position, frameOrigin) + Vector3.Transform(local, world.Rotation);
    }

    private Vector3? CreatureFrame(CreatureId creature)
    {
        if (creatures.TryGetValue(creature, out var placed)) return placed;
        return poses.TryGetValue(new WorldObjectId.Creature(creature), out var world) ? Frame(world.Position, frameOrigin) : null;
    }

    private ScenePoint? Point(WorldObjectId subject) =>
        poses.TryGetValue(subject, out var world) ? world.Position : null;

    private (PaintColor? Color, VoxelCollision? Collision) Struck(SceneWorldSnapshot snapshot, Noted noted, HitTarget target) => target switch
    {
        HitTarget.Part part => PartSurface(snapshot, part.Instance.Design, part.HitPart),
        HitTarget.Structure structure => StructureColor(snapshot, noted, structure),
        HitTarget.Creature creature => CreatureFragments(snapshot, creature.Individual.Definition),
        _ => (null, null)
    };

    private (PaintColor? Color, VoxelCollision? Collision) StructureColor(SceneWorldSnapshot snapshot, Noted noted, HitTarget.Structure structure)
    {
        StructureAim? aim = null;
        foreach (var item in noted.Structures)
            if (item.Target.Equals(structure)) aim = item;
        if (aim is not { } found || snapshot.Content.FindDesign(structure.Instance.Design) is not ContentLookup<DesignEvaluation>.Found design ||
            design.Value is DesignEvaluation.Failed) return (null, null);
        var voxel = DesignGeometry.Projection(design.Value).Voxels.FirstOrDefault(item =>
            DesignGeometry.Key(item.ObjectPath) == found.Path && item.LocalAt == found.At);
        return voxel is null ? (null, null) : Surface(snapshot, voxel);
    }

    private (PaintColor? Color, VoxelCollision? Collision) PartSurface(SceneWorldSnapshot snapshot, DesignHash design, PartId part)
    {
        if (snapshot.Content.FindDesign(design) is not ContentLookup<DesignEvaluation>.Found found || found.Value is DesignEvaluation.Failed)
            return (null, null);
        var voxel = DesignGeometry.Projection(found.Value).Voxels.FirstOrDefault(item => item.Number == part);
        return voxel is null ? (null, null) : Surface(snapshot, voxel);
    }

    private static (PaintColor? Color, VoxelCollision? Collision) Surface(SceneWorldSnapshot snapshot, SceneVoxelProjection voxel)
    {
        if (snapshot.Content.FindVoxelAppearance(voxel.Voxel) is not ContentLookup<VoxelAppearanceProjection>.Found found)
            return (voxel.Paint, null);
        return (voxel.Paint, found.Value.Collision);
    }

    private static (PaintColor? Color, VoxelCollision? Collision) CreatureFragments(SceneWorldSnapshot snapshot, CreatureDefinitionId definition)
    {
        if (snapshot.Content.FindCreature(definition) is not ContentLookup<CreatureAppearanceProjection>.Found found) return (null, null);
        var voxel = found.Value.Scene.Voxels.FirstOrDefault();
        return voxel is null ? (null, null) : Surface(snapshot, voxel);
    }

    private static ScenePoint? Map(WorldLocation location, RailwayPath? path)
    {
        if (path is not null && location is WorldLocation.Edge edge && edge.Id == path.Edge.Id)
            return path.Sample(edge.DistanceMeters * RailwayCoordinates.CellsPerMeter).Position;
        return null;
    }

    private static ScenePoint World(Vector3 frame, ScenePoint origin) =>
        new(origin.X + frame.X, origin.Y + frame.Y, origin.Z + frame.Z);

    private static Vector3 Frame(ScenePoint point, ScenePoint origin) =>
        new((float)(point.X - origin.X), (float)(point.Y - origin.Y), (float)(point.Z - origin.Z));

    private StructureAim? Aim(HitTarget.Structure structure)
    {
        StructureAim? aim = null;
        foreach (var noted in incoming)
            foreach (var item in noted.Structures)
                if (item.Target.Equals(structure)) aim = item;
        return aim;
    }

    private static ImmutableArray<StructureAim> StructureAims(SceneWorldSnapshot? before, SceneWorldSnapshot after, CoreEvent item)
    {
        if (item is not CoreEvent.CombatHits hits) return [];
        var builder = ImmutableArray.CreateBuilder<StructureAim>();
        foreach (var hit in hits.Hits)
            if (hit.Target is HitTarget.Structure structure && Choose(before, after, structure) is { } aim)
                builder.Add(aim);
        return builder.ToImmutable();
    }

    private static StructureAim? Choose(SceneWorldSnapshot? before, SceneWorldSnapshot after, HitTarget.Structure structure)
    {
        var current = Find(after, structure.Instance.Instance);
        var content = after.Content;
        if (current is null)
        {
            current = Find(before, structure.Instance.Instance);
            content = before?.Content;
        }
        if (current is null || content is null ||
            content.FindDesign(current.Design) is not ContentLookup<DesignEvaluation>.Found design ||
            design.Value is DesignEvaluation.Failed) return null;
        var projection = DesignGeometry.Projection(design.Value);
        var report = StructureDamage.Assess(current, projection);
        var oldRemoved = Find(before, structure.Instance.Instance) is { } previousInstance
            ? StructureDamage.Assess(previousInstance, projection).Removed : [];
        var fresh = new List<SceneVoxelProjection>();
        foreach (var cell in report.Removed)
        {
            if (oldRemoved.Contains(cell)) continue;
            if (Voxel(projection, cell) is { } voxel) fresh.Add(voxel);
        }
        SceneVoxelProjection? chosen;
        Vector3 local;
        if (fresh.Count > 0)
        {
            var sum = Vector3.Zero;
            foreach (var voxel in fresh) sum += new Vector3(voxel.At.X + .5f, voxel.At.Y + .5f, voxel.At.Z + .5f);
            local = sum / fresh.Count;
            chosen = fresh[0];
        }
        else
        {
            chosen = null;
            foreach (var cell in report.Order)
            {
                if (report.Removed.Contains(cell)) continue;
                chosen = Voxel(projection, cell);
                break;
            }
            if (chosen is null) return null;
            local = new(chosen.At.X + .5f, chosen.At.Y + .5f, chosen.At.Z + .5f);
        }
        return new(structure, local, DesignGeometry.Key(chosen.ObjectPath), chosen.LocalAt);
    }

    private static SceneVoxelProjection? Voxel(DesignSceneProjection projection, StructureDamage.CellRef cell)
    {
        foreach (var voxel in projection.Voxels)
            if (DesignGeometry.Key(voxel.ObjectPath) == cell.Path && voxel.LocalAt == cell.At) return voxel;
        return null;
    }

    private static InstanceView? Find(SceneWorldSnapshot? snapshot, InstanceId id) =>
        snapshot?.Core.FindInstance(id) is Lookup<InstanceView>.Found found ? found.Value : null;

    private void NoteTurret(SceneWorldSnapshot snapshot, AttackSource.Weapon weapon, HitTarget target, double seconds)
    {
        if (snapshot.Content.FindDesign(weapon.Instance.Design) is not ContentLookup<DesignEvaluation>.Found found ||
            found.Value is DesignEvaluation.Failed) return;
        var projection = DesignGeometry.Projection(found.Value);
        string? path = null;
        RotationAppearance? rotation = null;
        foreach (var nested in projection.NestedObjects)
            if (nested.Details is NestedObjectDetails.FunctionalComposite composite && composite.Number == weapon.WeaponPart &&
                composite.Rotation is { } spin)
            { path = DesignGeometry.Key(nested.NestedItemIndices); rotation = spin; break; }
        if (rotation is null)
        {
            var voxel = projection.Voxels.FirstOrDefault(item => item.Number == weapon.WeaponPart);
            var ancestry = voxel?.ObjectPath ?? [];
            while (ancestry.Length > 0 && rotation is null)
            {
                var ancestor = DesignGeometry.Key(ancestry);
                var nested = projection.NestedObjects.FirstOrDefault(item => DesignGeometry.Key(item.NestedItemIndices) == ancestor);
                rotation = nested?.Details switch
                {
                    NestedObjectDetails.Assembly assembly => assembly.Rotation,
                    NestedObjectDetails.FunctionalComposite composite => composite.Rotation,
                    _ => null
                };
                if (rotation is not null) path = ancestor;
                ancestry = ancestry.RemoveAt(ancestry.Length - 1);
            }
        }
        if (path is null || rotation is null) return;
        var key = (weapon.Instance.Instance, path);
        var start = turrets.TryGetValue(key, out var existing) ? existing.Angle(seconds) : 0;
        turrets[key] = new(weapon.Instance.Instance, path, rotation, seconds, start, target, false, start);
    }

    private void Resolve(TurretAim aim, ImmutableArray<SceneObject>.Builder objects, SceneObjectId turret, SceneWorldSnapshot snapshot)
    {
        var posed = Compose(objects);
        var pose = posed.All[turret];
        if (TargetPoint(snapshot, posed.Roots, aim.Target) is not { } target) return;
        var local = Vector3.Transform(target - pose.Position, Quaternion.Inverse(pose.Rotation));
        var degrees = aim.Appearance.Axis switch
        {
            RotationAxis.X => Math.Atan2(-local.Y, local.Z),
            RotationAxis.Z => Math.Atan2(local.Y, local.X),
            _ => Math.Atan2(local.X, local.Z)
        } * 180 / Math.PI;
        aim.TargetDegrees = Math.Clamp(degrees, aim.Appearance.MinimumDegrees, aim.Appearance.MaximumDegrees);
        aim.Resolved = true;
    }

    private static HashSet<CreatureGroupId> CombatGroups(SceneWorldSnapshot snapshot, WorldLocation here)
    {
        var groups = new HashSet<CreatureGroupId>();
        foreach (var combat in snapshot.Core.World.Combats)
        {
            if (!SamePlace(combat.Location, here) || snapshot.Core.FindCombat(combat.Id) is not Lookup<CombatView>.Found found) continue;
            foreach (var participant in found.Value.Participants)
                if (participant.Unit is CombatUnitId.CreatureGroup group) groups.Add(group.Id);
        }
        return groups;
    }

    private static IEnumerable<CreatureGroupId> Groups(SceneWorldSnapshot snapshot, WorldLocation here)
    {
        var listed = new HashSet<CreatureGroupId>();
        foreach (var item in snapshot.Core.World.CreatureGroups)
            if (SamePlace(item.Location, here)) listed.Add(item.Id);
        foreach (var group in CombatGroups(snapshot, here)) listed.Add(group);
        return listed;
    }

    private static HashSet<(CreatureGroupId Group, CreatureId Creature)> Dead(IEnumerable<CoreEvent> events)
    {
        var dead = new HashSet<(CreatureGroupId, CreatureId)>();
        foreach (var item in events)
            if (item is CoreEvent.CombatDestruction destruction)
                foreach (var unit in destruction.Units)
                    if (unit is DestroyedUnit.Creature creature)
                        dead.Add((creature.Value.Group, creature.Value.Creature));
        return dead;
    }

    private static (Vector3 Position, Vector3 Forward, Quaternion Rotation) Place(WorldLocation here,
        ScenePoint origin, RailwayPath? path, NodeLayout? layout, CreatureGroupView group, double along, int side,
        bool combat, Func<double, double, double>? ground)
    {
        if (here is WorldLocation.Edge && path is not null)
        {
            var distance = group.Location is WorldLocation.Edge edge ? edge.DistanceMeters : 0;
            var sample = path.Sample(distance * RailwayCoordinates.CellsPerMeter);
            var lateral = combat ? LineSideCells : BesideCells;
            var relative = RailwayCoordinates.Relative(sample.Position, origin);
            return (relative + sample.Right * (side * lateral) + sample.Forward * (float)along, sample.Forward,
                RailwayCoordinates.Rotation(sample.Forward));
        }
        var outward = !combat || layout is null ? side * BesideCells :
            side < 0 ? layout.Bounds.Minimum.X - LineSideCells : layout.Bounds.Maximum.X + LineSideCells;
        var alongZ = (combat && layout is not null ? layout.Bounds.Minimum.Y : 0) + along;
        var height = ground?.Invoke(outward, alongZ) ?? 0;
        return (new((float)(outward - origin.X), (float)(height - origin.Y), (float)(alongZ - origin.Z)), Vector3.UnitX, Quaternion.Identity);
    }

    private Vector3 PounceOffset(SceneWorldSnapshot snapshot, ImmutableArray<SceneObject>.Builder objects,
        CreatureId creature, (Vector3 Position, Vector3 Forward, Quaternion Rotation) placed, double seconds)
    {
        if (!pounces.TryGetValue(creature, out var pounce)) return Vector3.Zero;
        var elapsed = seconds - pounce.StartedAt;
        if (elapsed < 0 || elapsed > PounceSeconds) return Vector3.Zero;
        var amount = (float)Math.Sin(elapsed / PounceSeconds * Math.PI);
        var direction = placed.Forward;
        if (TargetPoint(snapshot, Compose(objects).Roots, pounce.Target) is { } point)
        {
            var toward = new Vector3(point.X - placed.Position.X, 0, point.Z - placed.Position.Z);
            if (toward.LengthSquared() > 1e-4f) direction = Vector3.Normalize(toward);
        }
        return direction * amount;
    }

    private static float Bob(double seconds) => (float)(Math.Sin(seconds * Math.Tau) * 0.2);

    private static ulong CombatId(SceneWorldSnapshot snapshot, WorldLocation here)
    {
        foreach (var combat in snapshot.Core.World.Combats)
            if (SamePlace(combat.Location, here)) return combat.Id.Value;
        return 0;
    }

    private static int Side(ulong worldSeed, ulong combat, ulong creature)
    {
        Span<byte> identity = stackalloc byte[24];
        BinaryPrimitives.WriteUInt64LittleEndian(identity, worldSeed);
        BinaryPrimitives.WriteUInt64LittleEndian(identity[8..], combat);
        BinaryPrimitives.WriteUInt64LittleEndian(identity[16..], creature);
        return (SHA256.HashData(identity)[0] & 1) == 0 ? -1 : 1;
    }

    private static WorldObjectId? HitSubject(HitTarget target) => target switch
    {
        HitTarget.Structure structure => new WorldObjectId.Instance(structure.Instance.Instance),
        HitTarget.Part part => new WorldObjectId.Instance(part.Instance.Instance),
        HitTarget.Creature creature => new WorldObjectId.Creature(creature.Individual.Creature),
        _ => null
    };

    private static bool InSpace(CoreEvent item, WorldLocation? here) => (item, here) switch
    {
        (CoreEvent.CombatHits hits, WorldLocation.Edge edge) => hits.Location is WorldLocation.Edge at && at.Id == edge.Id,
        (CoreEvent.CombatHits hits, WorldLocation.Node node) => hits.Location is WorldLocation.Node at && at.Id == node.Id,
        (CoreEvent.CombatDestruction destruction, WorldLocation.Edge edge) => destruction.Location is WorldLocation.Edge at && at.Id == edge.Id,
        (CoreEvent.CombatDestruction destruction, WorldLocation.Node node) => destruction.Location is WorldLocation.Node at && at.Id == node.Id,
        (CoreEvent.RailBroken broken, WorldLocation.Edge edge) => broken.Edge == edge.Id,
        _ => false
    };

    private static bool SamePlace(WorldLocation location, WorldLocation here) => (location, here) switch
    {
        (WorldLocation.Edge left, WorldLocation.Edge right) => left.Id == right.Id,
        (WorldLocation.Node left, WorldLocation.Node right) => left.Id == right.Id,
        _ => false
    };

    private readonly record struct PlacedPoses(Dictionary<SceneObjectId, ScenePose> All, Dictionary<WorldObjectId, ScenePose> Roots);

    private static PlacedPoses Compose(IReadOnlyList<SceneObject> objects)
    {
        var byId = objects.ToDictionary(item => item.Id);
        var all = new Dictionary<SceneObjectId, ScenePose>();
        var roots = new Dictionary<WorldObjectId, ScenePose>();
        ScenePose Pose(SceneObjectId id)
        {
            if (all.TryGetValue(id, out var pose)) return pose;
            var item = byId[id];
            pose = item.Parent is { } parent && byId.ContainsKey(parent) ? SceneGeometry.Compose(Pose(parent), item.Pose) : item.Pose;
            all[id] = pose;
            return pose;
        }
        foreach (var item in objects)
        {
            var pose = Pose(item.Id);
            if (item.Subject is not null && item.Parent is null) roots[item.Subject] = pose;
        }
        return new(all, roots);
    }

    private ImmutableArray<SceneObject> Body(SceneWorldSnapshot snapshot, CreatureAppearanceProjection appearance,
        CreatureGroupId group, CreatureId creature, Func<string, SceneObjectId> objectId)
    {
        if (!templates.TryGetValue(appearance, out var template))
        {
            var local = new Dictionary<string, SceneObjectId>();
            ulong next = 0;
            var built = DesignGeometry.Build(snapshot.Content, appearance.Scene, [], new WorldObjectId.Creature(creature),
                new(Vector3.Zero, Quaternion.Identity), key => local.TryGetValue(key, out var id) ? id : local[key] = new(++next));
            templates[appearance] = template = (built.Objects, local.ToImmutableDictionary(item => item.Value, item => item.Key));
        }
        if (bodies.TryGetValue(creature, out var cached) && ReferenceEquals(cached.Appearance, appearance)) return cached.Objects;
        var map = new Dictionary<SceneObjectId, SceneObjectId>();
        foreach (var (old, key) in template.Keys)
            map[old] = objectId("creature:" + group.Value + ":" + creature.Value + ":" + key);
        var subject = new WorldObjectId.Creature(creature);
        var remapped = template.Objects.Select(item => item with
        {
            Id = map[item.Id],
            Parent = item.Parent is { } parent ? map[parent] : null,
            Subject = subject,
            Lights = item.Lights.IsEmpty ? item.Lights : item.Lights.Select(light => light with
            {
                Seed = BinaryPrimitives.ReadUInt64LittleEndian(SHA256.HashData(
                    Encoding.UTF8.GetBytes(creature.Value.ToString(CultureInfo.InvariantCulture) + ":" + template.Keys[item.Id])))
            }).ToImmutableArray()
        }).ToImmutableArray();
        bodies[creature] = (appearance, remapped);
        return remapped;
    }

    private sealed class TurretAim(InstanceId instance, string path, RotationAppearance appearance, double startedAt,
        double startDegrees, HitTarget target, bool resolved, double targetDegrees)
    {
        internal InstanceId Instance { get; } = instance;
        internal string Path { get; } = path;
        internal RotationAppearance Appearance { get; } = appearance;
        internal double StartedAt { get; } = startedAt;
        internal double StartDegrees { get; } = startDegrees;
        internal HitTarget Target { get; } = target;
        internal bool Resolved { get; set; } = resolved;
        internal double TargetDegrees { get; set; } = targetDegrees;
        internal double Angle(double seconds)
        {
            var elapsed = Math.Max(0, seconds - StartedAt);
            var span = TargetDegrees - StartDegrees;
            var moved = Math.CopySign(Math.Min(Math.Abs(span), Appearance.SpeedDegreesPerSecond * elapsed), span);
            return Math.Clamp(StartDegrees + moved, Appearance.MinimumDegrees, Appearance.MaximumDegrees);
        }
        internal ScenePose Pose(ScenePose installed, double seconds)
        {
            var extra = Quaternion.CreateFromAxisAngle(Appearance.Axis switch
            {
                RotationAxis.X => Vector3.UnitX,
                RotationAxis.Z => Vector3.UnitZ,
                _ => Vector3.UnitY
            }, (float)(Angle(seconds) * Math.PI / 180));
            var pivot = new Vector3(Appearance.Pivot.X + .5f, Appearance.Pivot.Y + .5f, Appearance.Pivot.Z + .5f);
            return new(installed.Position + Vector3.Transform(pivot - Vector3.Transform(pivot, extra), installed.Rotation),
                Quaternion.Normalize(installed.Rotation * extra));
        }
    }

    private sealed record Noted(EventEnvelope Envelope, ImmutableArray<StructureAim> Structures);

    private readonly record struct StructureAim(HitTarget Target, Vector3 Local, string Path, GridPosition At);

    private sealed record HeldCue(SceneEffectId Id, SceneEffectKind Kind, ScenePoint From, ScenePoint? To,
        PaintColor? Color, VoxelCollision? Collision, WorldObjectId? Subject, double StartedAt);
}
