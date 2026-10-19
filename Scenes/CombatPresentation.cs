using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Numerics;
using System.Security.Cryptography;
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
    private readonly List<(double Seconds, CoreEvent Event)> incoming = [];
    private readonly List<SceneEffectCue> pending = [];
    private readonly Dictionary<(InstanceId Instance, string Path), TurretAim> turrets = [];
    private readonly Dictionary<CreatureId, double> pounces = [];
    private readonly Dictionary<InstanceId, ImmutableHashSet<StructureDamage.CellRef>> removed = [];
    private readonly List<InstanceView> seen = [];
    private Dictionary<WorldObjectId, ScenePose> poses = [];
    private readonly Dictionary<CreatureId, Vector3> creatures = [];
    private readonly Dictionary<CreatureAppearanceProjection, (ImmutableArray<SceneObject> Objects, ImmutableDictionary<SceneObjectId, string> Keys)> templates = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<CreatureId, (CreatureAppearanceProjection Appearance, ImmutableArray<SceneObject> Objects)> bodies = [];
    private ulong nextEffect;

    internal void Clear()
    {
        incoming.Clear();
        pending.Clear();
        turrets.Clear();
        pounces.Clear();
        removed.Clear();
        seen.Clear();
        poses.Clear();
        creatures.Clear();
        bodies.Clear();
    }

    internal void Note(SceneWorldSnapshot snapshot, ImmutableArray<EventEnvelope> events, WorldLocation? here)
    {
        var seconds = snapshot.Core.Position.CompletedSteps * SimulationTime.StepSeconds;
        foreach (var item in events)
        {
            if (!InSpace(item.Event, here)) continue;
            incoming.Add((seconds, item.Event));
            if (item.Event is CoreEvent.CombatHits hits)
                foreach (var hit in hits.Hits)
                {
                    if (hit.Source is AttackSource.Weapon weapon)
                        NoteTurret(snapshot, weapon, hit.Target, seconds);
                    if (hit.Source is AttackSource.Creature creature)
                        pounces[creature.Individual.Creature] = seconds;
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
        Func<string, SceneObjectId?> findId, double seconds, SceneWorldSnapshot snapshot)
    {
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
        ScenePoint origin, RailwayPath? path, NodeLayout? layout, double seconds, Func<string, SceneObjectId> objectId)
    {
        creatures.Clear();
        var dead = Dead(incoming.Select(item => item.Event));
        var combatGroups = CombatGroups(snapshot, here);
        var index = 0;
        if (here is WorldLocation.Edge && path is null) return;
        foreach (var group in Groups(snapshot, here))
        {
            if (snapshot.Core.FindCreatureGroup(group) is not Lookup<CreatureGroupView>.Found found) continue;
            var combat = combatGroups.Contains(group);
            foreach (var creature in found.Value.Creatures)
            {
                var placed = Place(snapshot, here, origin, path, layout, found.Value, creature, index, combat);
                index++;
                creatures[creature.Id] = placed.Position;
                if (dead.Contains((group, creature.Id))) continue;
                if (snapshot.Content.FindCreature(creature.Definition) is not ContentLookup<CreatureAppearanceProjection>.Found appearance)
                    continue;
                var pose = new ScenePose(placed.Position + placed.Forward * (float)Pounce(creature.Id, seconds) + new Vector3(0, Bob(seconds), 0),
                    placed.Rotation);
                var body = Body(snapshot, appearance.Value, group, creature.Id, objectId);
                objects.AddRange(SceneGeometry.Place(new(body, [], []), pose).Objects);
            }
        }
    }

    internal ImmutableArray<SceneEffectCue> Finish(ImmutableArray<SceneObject>.Builder objects, SceneWorldSnapshot snapshot,
        WorldLocation? here, ScenePoint origin, RailwayPath? path)
    {
        var composed = Compose(objects);
        foreach (var (seconds, item) in incoming)
        {
            switch (item)
            {
                case CoreEvent.CombatHits hits:
                    foreach (var hit in hits.Hits)
                        AddHit(snapshot, composed, hit, seconds);
                    break;
                case CoreEvent.CombatDestruction destruction:
                    foreach (var unit in destruction.Units)
                        AddUnit(snapshot, unit, seconds, path, origin);
                    foreach (var part in destruction.Parts)
                        AddPart(snapshot, composed, part, seconds, path, origin);
                    break;
                case CoreEvent.RailBroken broken when path is not null && here is WorldLocation.Edge edge && broken.Edge == edge.Id:
                    var sample = path.Sample(broken.Break.DistanceMeters * RailwayCoordinates.CellsPerMeter);
                    Cue(SceneEffectKind.RailwayDamage, RailwayCoordinates.Relative(sample.Position, origin), null, null, null, null, seconds);
                    break;
            }
        }
        incoming.Clear();
        poses = composed;
        Remember(snapshot);
        return pending.ToImmutableArray();
    }

    private void AddHit(SceneWorldSnapshot snapshot, Dictionary<WorldObjectId, ScenePose> composed, CombatHit hit, double seconds)
    {
        var from = Source(snapshot, composed, hit.Source) ?? TargetPoint(snapshot, composed, hit.Target) ?? Vector3.Zero;
        var to = TargetPoint(snapshot, composed, hit.Target) ?? from;
        var subject = HitSubject(hit.Target);
        var (color, collision) = Struck(snapshot, hit.Target);
        Cue(SceneEffectKind.Shot, from, to, color, collision, subject, seconds);
        Cue(SceneEffectKind.Impact, to, null, color, collision, subject, seconds);
        Cue(SceneEffectKind.Fragments, to, null, color, collision, subject, seconds);
    }

    private void AddUnit(SceneWorldSnapshot snapshot, DestroyedUnit unit, double seconds, RailwayPath? path, ScenePoint origin)
    {
        switch (unit)
        {
            case DestroyedUnit.Instance instance:
                var subject = new WorldObjectId.Instance(instance.Value.Instance);
                var at = poses.TryGetValue(subject, out var pose) ? pose.Position : Map(instance.Value.Location, path, origin);
                Cue(SceneEffectKind.Destruction, at, null, null, null, subject, seconds);
                break;
            case DestroyedUnit.Creature creature:
                var body = new WorldObjectId.Creature(creature.Value.Creature);
                var position = creatures.TryGetValue(creature.Value.Creature, out var placed) ? placed :
                    poses.TryGetValue(body, out var old) ? old.Position : Map(creature.Value.Location, path, origin);
                var (color, collision) = CreatureFragments(snapshot, creature.Value.Definition);
                Cue(SceneEffectKind.Fragments, position, null, color, collision, body, seconds);
                break;
        }
    }

    private void AddPart(SceneWorldSnapshot snapshot, Dictionary<WorldObjectId, ScenePose> composed, DestroyedPart part,
        double seconds, RailwayPath? path, ScenePoint origin)
    {
        var subject = new WorldObjectId.Instance(part.Instance.Instance);
        var at = CellCenter(snapshot, composed, part.Instance.Instance, part.Instance.Design, part.Part) ??
            (poses.TryGetValue(subject, out var pose) ? pose.Position : Map(part.Instance.Location, path, origin));
        var (color, collision) = PartSurface(snapshot, part.Instance.Design, part.Part);
        Cue(SceneEffectKind.Fragments, at, null, color, collision, subject, seconds);
    }

    private void Cue(SceneEffectKind kind, Vector3 from, Vector3? to, PaintColor? color, VoxelCollision? collision,
        WorldObjectId? subject, double seconds) =>
        pending.Add(new(new(++nextEffect), kind, from, to, color, collision, subject, seconds));

    private Vector3? Source(SceneWorldSnapshot snapshot, Dictionary<WorldObjectId, ScenePose> composed, AttackSource source) => source switch
    {
        AttackSource.Weapon weapon => CellCenter(snapshot, composed, weapon.Instance.Instance, weapon.Instance.Design, weapon.WeaponPart),
        AttackSource.Creature creature => creatures.TryGetValue(creature.Individual.Creature, out var placed) ? placed :
            poses.TryGetValue(new WorldObjectId.Creature(creature.Individual.Creature), out var pose) ? pose.Position : null,
        _ => null
    };

    private Vector3? TargetPoint(SceneWorldSnapshot snapshot, Dictionary<WorldObjectId, ScenePose> composed, HitTarget target) => target switch
    {
        HitTarget.Part part => CellCenter(snapshot, composed, part.Instance.Instance, part.Instance.Design, part.HitPart),
        HitTarget.Creature creature => creatures.TryGetValue(creature.Individual.Creature, out var placed) ? placed :
            poses.TryGetValue(new WorldObjectId.Creature(creature.Individual.Creature), out var pose) ? pose.Position : null,
        HitTarget.Structure structure => StructureCenter(snapshot, composed, structure.Instance),
        _ => null
    };

    private Vector3? StructureCenter(SceneWorldSnapshot snapshot, Dictionary<WorldObjectId, ScenePose> composed, InstancePresentationRef instance)
    {
        var subject = new WorldObjectId.Instance(instance.Instance);
        var root = poses.TryGetValue(subject, out var previous) ? previous.Position :
            composed.TryGetValue(subject, out var current) ? current.Position : (Vector3?)null;
        if (Known(instance.Instance) is not { } found ||
            snapshot.Content.FindDesign(found.Design) is not ContentLookup<DesignEvaluation>.Found design ||
            design.Value is DesignEvaluation.Failed || !composed.TryGetValue(subject, out var pose))
            return root;
        var report = StructureDamage.Assess(found, DesignGeometry.Projection(design.Value));
        if (!removed.TryGetValue(instance.Instance, out var before)) return pose.Position;
        var fresh = report.Removed.Where(cell => !before.Contains(cell)).ToArray();
        if (fresh.Length == 0) return pose.Position;
        var sum = Vector3.Zero;
        foreach (var cell in fresh)
        {
            var voxel = DesignGeometry.Projection(design.Value).Voxels.First(item =>
                new StructureDamage.CellRef(DesignGeometry.Key(item.ObjectPath), item.LocalAt) == cell);
            sum += pose.Position + Vector3.Transform(new(voxel.At.X + .5f, voxel.At.Y + .5f, voxel.At.Z + .5f), pose.Rotation);
        }
        return sum / fresh.Length;
    }

    private Vector3? CellCenter(SceneWorldSnapshot snapshot, Dictionary<WorldObjectId, ScenePose> composed,
        InstanceId instance, DesignHash design, PartId part)
    {
        var subject = new WorldObjectId.Instance(instance);
        if (!composed.TryGetValue(subject, out var pose) && !poses.TryGetValue(subject, out pose)) return null;
        if (snapshot.Content.FindDesign(design) is not ContentLookup<DesignEvaluation>.Found found || found.Value is DesignEvaluation.Failed)
            return pose.Position;
        var voxel = DesignGeometry.Projection(found.Value).Voxels.Where(item => item.Number == part)
            .OrderBy(item => item.At.X).ThenBy(item => item.At.Y).ThenBy(item => item.At.Z).FirstOrDefault();
        return voxel is null ? pose.Position :
            pose.Position + Vector3.Transform(new(voxel.At.X + .5f, voxel.At.Y + .5f, voxel.At.Z + .5f), pose.Rotation);
    }

    private (PaintColor? Color, VoxelCollision? Collision) Struck(SceneWorldSnapshot snapshot, HitTarget target) => target switch
    {
        HitTarget.Part part => PartSurface(snapshot, part.Instance.Design, part.HitPart),
        HitTarget.Structure structure => StructureSurface(snapshot, structure.Instance),
        HitTarget.Creature creature => CreatureFragments(snapshot, creature.Individual.Definition),
        _ => (null, null)
    };

    private (PaintColor? Color, VoxelCollision? Collision) StructureSurface(SceneWorldSnapshot snapshot, InstancePresentationRef instance)
    {
        if (Known(instance.Instance) is not { } found ||
            snapshot.Content.FindDesign(found.Design) is not ContentLookup<DesignEvaluation>.Found design ||
            design.Value is DesignEvaluation.Failed) return (null, null);
        var report = StructureDamage.Assess(found, DesignGeometry.Projection(design.Value));
        var before = removed.GetValueOrDefault(instance.Instance) ?? [];
        var voxel = DesignGeometry.Projection(design.Value).Voxels.FirstOrDefault(item =>
            report.Removed.Contains(new(DesignGeometry.Key(item.ObjectPath), item.LocalAt)) &&
            !before.Contains(new(DesignGeometry.Key(item.ObjectPath), item.LocalAt)));
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

    private static Vector3 Map(WorldLocation location, RailwayPath? path, ScenePoint origin)
    {
        if (path is not null && location is WorldLocation.Edge edge && edge.Id == path.Edge.Id)
            return RailwayCoordinates.Relative(path.Sample(edge.DistanceMeters * RailwayCoordinates.CellsPerMeter).Position, origin);
        return Vector3.Zero;
    }

    internal void Seen(InstanceView instance) => seen.Add(instance);

    private InstanceView? Known(InstanceId id)
    {
        for (var index = seen.Count - 1; index >= 0; index--)
            if (seen[index].Id == id) return seen[index];
        return null;
    }

    private void Remember(SceneWorldSnapshot snapshot)
    {
        foreach (var instance in seen)
        {
            if (snapshot.Content.FindDesign(instance.Design) is not ContentLookup<DesignEvaluation>.Found design ||
                design.Value is DesignEvaluation.Failed) continue;
            removed[instance.Id] = StructureDamage.Assess(instance, DesignGeometry.Projection(design.Value)).Removed;
        }
        seen.Clear();
    }

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
        var pose = ComposeAll(objects)[turret];
        var target = TargetPoint(snapshot, Compose(objects), aim.Target) ?? pose.Position + Vector3.UnitZ;
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

    private (Vector3 Position, Vector3 Forward, Quaternion Rotation) Place(SceneWorldSnapshot snapshot, WorldLocation here,
        ScenePoint origin, RailwayPath? path, NodeLayout? layout, CreatureGroupView group, CreatureView creature, int index, bool combat)
    {
        var side = Side(snapshot.Core.World.WorldSeed, combat ? CombatId(snapshot, here) : 0, creature.Id.Value);
        if (here is WorldLocation.Edge && path is not null)
        {
            var distance = group.Location is WorldLocation.Edge edge ? edge.DistanceMeters : 0;
            var sample = path.Sample(distance * RailwayCoordinates.CellsPerMeter);
            var lateral = combat ? LineSideCells : BesideCells;
            var along = combat ? index * LineStaggerCells : 0;
            var relative = RailwayCoordinates.Relative(sample.Position, origin);
            return (relative + sample.Right * (side * lateral) + sample.Forward * along, sample.Forward,
                RailwayCoordinates.Rotation(sample.Forward));
        }
        var outward = !combat || layout is null ? side * BesideCells :
            side < 0 ? layout.Bounds.Minimum.X - LineSideCells : layout.Bounds.Maximum.X + LineSideCells;
        var alongZ = combat && layout is not null ? layout.Bounds.Minimum.Y + index * LineStaggerCells : 0;
        return (new((float)(outward - origin.X), 0, (float)(alongZ - origin.Z)), Vector3.UnitX, Quaternion.Identity);
    }

    private double Pounce(CreatureId creature, double seconds)
    {
        if (!pounces.TryGetValue(creature, out var started)) return 0;
        var elapsed = seconds - started;
        return elapsed < 0 || elapsed > PounceSeconds ? 0 : Math.Sin(elapsed / PounceSeconds * Math.PI);
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

    private static Dictionary<SceneObjectId, ScenePose> ComposeAll(IReadOnlyList<SceneObject> objects)
    {
        var byId = objects.ToDictionary(item => item.Id);
        var composed = new Dictionary<SceneObjectId, ScenePose>();
        ScenePose Pose(SceneObjectId id)
        {
            if (composed.TryGetValue(id, out var pose)) return pose;
            var item = byId[id];
            pose = item.Parent is { } parent && byId.ContainsKey(parent) ? SceneGeometry.Compose(Pose(parent), item.Pose) : item.Pose;
            composed[id] = pose;
            return pose;
        }
        foreach (var item in objects) Pose(item.Id);
        return composed;
    }

    private static Dictionary<WorldObjectId, ScenePose> Compose(IReadOnlyList<SceneObject> objects)
    {
        var byId = objects.ToDictionary(item => item.Id);
        var composed = new Dictionary<SceneObjectId, ScenePose>();
        var roots = new Dictionary<WorldObjectId, ScenePose>();
        ScenePose Pose(SceneObjectId id)
        {
            if (composed.TryGetValue(id, out var pose)) return pose;
            var item = byId[id];
            pose = item.Parent is { } parent && byId.ContainsKey(parent) ? SceneGeometry.Compose(Pose(parent), item.Pose) : item.Pose;
            composed[id] = pose;
            return pose;
        }
        foreach (var item in objects)
        {
            var pose = Pose(item.Id);
            if (item.Subject is not null && item.Parent is null) roots[item.Subject] = pose;
        }
        return roots;
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
            Subject = subject
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
}
