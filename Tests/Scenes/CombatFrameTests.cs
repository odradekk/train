using System.Collections.Immutable;
using System.Numerics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using Train.Scenes.Contracts;

namespace Train.Scenes.Tests;

[TestClass]
public sealed class CombatFrameTests
{
    private static readonly SceneFrameRequest Request = new(1, null, SceneDebugLayer.None);

    [TestMethod]
    public void SameIdentityDesignAndDurabilityRemoveTheSameCellsAndRepairRestoresFromTheEnd()
    {
        var projection = Line(8);
        var ruined = RailwayFixtures.Instance() with { Structure = new(0, 100) };
        var first = StructureDamage.Assess(ruined, projection);
        var second = StructureDamage.Assess(ruined, projection);
        CollectionAssert.AreEqual(first.Order.ToArray(), second.Order.ToArray());
        Assert.IsTrue(first.Removed.SetEquals(second.Removed));
        var halfway = StructureDamage.Assess(ruined with { Structure = new(50, 100) }, projection);
        var ruinedPrefix = first.Order.Where(first.Removed.Contains).ToArray();
        var repairedPrefix = first.Order.Where(halfway.Removed.Contains).ToArray();
        CollectionAssert.AreEqual(ruinedPrefix.Take(repairedPrefix.Length).ToArray(), repairedPrefix);
        Assert.IsTrue(halfway.Removed.Count < first.Removed.Count);
    }

    [TestMethod]
    public void RemovalStartsOnExposedCellsAndGrowsEveryCraterTogether()
    {
        var cells = new List<SceneVoxelProjection>();
        for (var x = 0; x < 3; x++)
            for (var y = 0; y < 3; y++)
                for (var z = 0; z < 3; z++)
                    cells.Add(SceneFixtures.Cell(new(x, y, z), 1));
        var order = StructureDamage.Assess(RailwayFixtures.Instance() with { Structure = new(0, 100) },
            SceneFixtures.Projection(cells.ToImmutableArray())).Order;
        var first = order[0].At;
        Assert.IsTrue(first.X is 0 or 2 || first.Y is 0 or 2 || first.Z is 0 or 2);
        var interior = order.Select((cell, index) => (cell.At, index)).Single(item => item.At == new GridPosition(1, 1, 1)).index;
        Assert.IsTrue(interior > 0);
    }

    [TestMethod]
    public void AdjacentDamageUsesRootCoordinates()
    {
        var nested = SceneFixtures.Cell(new(0, 0, 0), 1, path: [0]) with { LocalAt = new(4, 4, 4) };
        var neighbor = SceneFixtures.Cell(new(1, 0, 0), 1);
        var far = SceneFixtures.Cell(new(8, 0, 0), 1);
        var projection = SceneFixtures.Projection([nested, neighbor, far]);
        StructureDamage.Report? report = null;
        for (ulong id = 1; id < 40 && report is null; id++)
        {
            var candidate = StructureDamage.Assess(RailwayFixtures.Instance(id) with { Structure = new(0, 100) }, projection);
            if (candidate.Removed.Contains(new("0", nested.LocalAt))) report = candidate;
        }
        Assert.IsNotNull(report);
        Assert.AreEqual(1, report!.Damage[new("", neighbor.LocalAt)]);
    }

    [TestMethod]
    public async Task TerrainPatchesFillMissingColumnsAndStayCached()
    {
        var heights = ImmutableDictionary.CreateRange(new Dictionary<GridColumn, double>
        {
            [new(0, 0)] = 3,
            [new(8, 4)] = 7
        });
        var sparse = TerrainCollision.Patch(new(1), default, default, heights)!;
        Assert.AreEqual(3, sparse.Width);
        Assert.AreEqual(2, sparse.Depth);
        Assert.AreEqual(sparse.Width * sparse.Depth, sparse.Heights.Length);
        Assert.AreEqual(3, sparse.Heights[0] + sparse.Origin.Y, 1e-4);
        Assert.AreEqual(7, sparse.Heights[5] + sparse.Origin.Y, 1e-4);
        Assert.AreEqual(3, sparse.Heights[1] + sparse.Origin.Y, 1e-4);

        var content = RailwayFixtures.Content(design: RailwayFixtures.Vehicle());
        await using var session = Open(content, RailwayFixtures.Instance());
        session.RequestView(new WorldSceneTarget.Location(RailwayFixtures.Location));
        var line = await RailwayFixtures.Settled(session, Request);
        Assert.IsNotEmpty(line.Update.Environment.TerrainCollision);
        var repeated = Present(session, line);
        Assert.AreSame(line.Update.Environment.TerrainCollision[0], repeated.Update.Environment.TerrainCollision[0]);

        var nodeSnapshot = NodeFixtures.Snapshot(platforms: 0, trains: [], instances: []);
        var layout = NodeLayout.Build(nodeSnapshot, NodeFixtures.NodeId);
        var prepared = NodeScene.Prepare(nodeSnapshot, layout, null, default);
        var from = new GridColumn((int)layout.Bounds.Minimum.X, (int)layout.Bounds.Minimum.Y);
        var chunk = prepared.BuildChunk(from);
        Assert.IsTrue(chunk.Heights.Count > 0);
        var nodePatch = TerrainCollision.Patch(new(2), chunk.Origin, default, chunk.Heights)!;
        Assert.AreEqual(nodePatch.Width * nodePatch.Depth, nodePatch.Heights.Length);
        var minX = chunk.Heights.Keys.Min(column => column.X);
        var minZ = chunk.Heights.Keys.Min(column => column.Z);
        var sample = chunk.Heights.Keys.First(column => (column.X - minX) % 4 == 0 && (column.Z - minZ) % 4 == 0);
        var index = (sample.Z - minZ) / 4 * nodePatch.Width + (sample.X - minX) / 4;
        Assert.AreEqual(chunk.Heights[sample], nodePatch.Heights[index] + nodePatch.Origin.Y, 1e-3);
    }

    [TestMethod]
    public void HitDamageDoesNotChooseRemovedCells()
    {
        var projection = Line(8);
        var body = RailwayFixtures.Instance() with { Structure = new(25, 100) };
        var ignored = new CombatHit(new AttackSource.Weapon(new(body.Id, body.Design, RailwayFixtures.Location), new(1)),
            new HitTarget.Structure(new(body.Id, body.Design, RailwayFixtures.Location)), 999);
        var other = ignored with { Damage = 1 };
        Assert.AreNotEqual(ignored.Damage, other.Damage);
        var removed = StructureDamage.Assess(body, projection).Removed;
        Assert.IsTrue(removed.SetEquals(StructureDamage.Assess(body, projection).Removed));
    }

    [TestMethod]
    public void StructureVertexDamageIsBinaryAndPartDamageIsContinuous()
    {
        var structure = SceneFixtures.Cell(new(0, 0, 0), 1);
        var neighbor = SceneFixtures.Cell(new(1, 0, 0), 1);
        var part = SceneFixtures.Cell(new(3, 0, 0), 9);
        var projection = SceneFixtures.Projection([structure, neighbor, SceneFixtures.Cell(new(2, 0, 0), 1), part]) with
        {
            Elements = [
                Element(structure, false), Element(neighbor, false), Element(SceneFixtures.Cell(new(2, 0, 0), 1), false),
                Element(part, true)]
        };
        var body = RailwayFixtures.Instance() with
        {
            Structure = new(0, 100),
            Parts = [new(new(9), new(40, 100), false, false)]
        };
        var report = StructureDamage.Assess(body, projection);
        var built = WorldSceneSession.BuildObject(SceneFixtures.Content(SceneFixtures.Evaluation(projection)), body, SceneFixtures.Identity);
        var vertices = Vertices(built.Geometry.Geometry);
        Assert.IsTrue(report.Scorched);
        Assert.AreEqual(1, built.Geometry.Geometry.Objects[0].Visuals.OfType<SceneVisual.Geometry>().First().Surface.Damage, 1e-5);
        Assert.IsTrue(vertices.Any(vertex => vertex.Damage == 0));
        Assert.IsTrue(vertices.Any(vertex => vertex.Damage == 1));
        Assert.IsTrue(vertices.Any(vertex => Math.Abs(vertex.Damage - 0.6f) < 1e-5));
        Assert.IsFalse(vertices.Any(vertex => vertex.Damage is > 0 and < 1 && Math.Abs(vertex.Damage - 0.6f) > 1e-5));
    }

    [TestMethod]
    public void UnequalDamageDoesNotMergeOrInterpolate()
    {
        var left = new SceneCell(new(0, 0, 0), new(VoxelOrientation.PxPy, SceneFixtures.Cube, null, null, null, []));
        var right = new SceneCell(new(1, 0, 0), new(VoxelOrientation.PxPy, SceneFixtures.Cube, null, null, null, []));
        var grid = new VoxelGrid([left, right]);
        var plain = DesignGeometry.Meshes(grid, false).OfType<SceneVisual.Geometry>().SelectMany(item => item.Mesh.Vertices).Count();
        var split = DesignGeometry.Meshes(grid, false, cellDamage: new Dictionary<GridPosition, float> { [left.At] = 1 })
            .OfType<SceneVisual.Geometry>().SelectMany(item => item.Mesh.Vertices).ToArray();
        Assert.IsTrue(split.Length > plain);
        Assert.IsTrue(split.Any(vertex => vertex.Damage == 0));
        Assert.IsTrue(split.Any(vertex => vertex.Damage == 1));
        Assert.IsFalse(split.Any(vertex => vertex.Damage is > 0 and < 1));
    }

    [TestMethod]
    public void LightsAndPersistentEffectsFollowTheDurabilityThresholds()
    {
        var cell = SceneFixtures.Cell(new(0, 0, 0), 1);
        var part = SceneFixtures.Cell(new(2, 0, 0), 9);
        var projection = SceneFixtures.Projection([cell, SceneFixtures.Cell(new(1, 0, 0), 1), part]) with
        {
            Elements = [Element(cell, false), Element(SceneFixtures.Cell(new(1, 0, 0), 1), false), Element(part, true)]
        };
        var appearance = SceneFixtures.Cube with { Behaviors = new(false, new(new("lamp"), 3)) };
        var content = SceneFixtures.Content(SceneFixtures.Evaluation(projection), appearance: appearance);
        var smoking = WorldSceneSession.BuildObject(content, RailwayFixtures.Instance() with
        {
            Structure = new(40, 100),
            Parts = [new(new(9), new(50, 100), false, false)]
        }, SceneFixtures.Identity);
        var effects = smoking.Geometry.Geometry.Objects.SelectMany(item => item.Effects).ToArray();
        Assert.IsTrue(effects.Any(item => item.Kind == PersistentEffectKind.Smoke));
        Assert.IsFalse(effects.Any(item => item.Kind is PersistentEffectKind.Fire or PersistentEffectKind.Leak or PersistentEffectKind.FacilitySmoke));
        Assert.AreEqual(PersistentEffectKind.Sparks, effects.Single(item => item.Kind == PersistentEffectKind.Sparks).Kind);
        var burning = StructureDamage.Assess(RailwayFixtures.Instance() with { Structure = new(0, 100) }, projection, content);
        Assert.IsTrue(burning.Effects.Any(item => item.Effect.Kind == PersistentEffectKind.Fire));
        Assert.AreEqual(1, burning.Lights[0].Light.Damage, 1e-5);
        var warm = StructureDamage.Assess(RailwayFixtures.Instance() with
        {
            Structure = new(50, 100),
            Parts = [new(new(9), new(25, 100), false, false)]
        }, projection, content);
        Assert.AreEqual(0, warm.Lights.Single(item => item.At == cell.At).Light.Damage);
        Assert.AreEqual(0.75f, warm.Lights.Single(item => item.At == part.LocalAt).Light.Damage, 1e-5);
        var destroyed = StructureDamage.Assess(RailwayFixtures.Instance() with
        {
            Structure = new(100, 100),
            Parts = [new(new(9), new(0, 100), false, true)]
        }, projection, content);
        Assert.IsFalse(destroyed.Lights.Any(item => item.At == part.LocalAt));
        Assert.IsFalse(destroyed.Effects.Any(item => item.Effect.Kind == PersistentEffectKind.Sparks));
    }

    [TestMethod]
    public async Task TwoFreshSessionsBuildTheSameVerticesForTheSameDurability()
    {
        var evaluation = RailwayFixtures.Vehicle(cells: Enumerable.Range(0, 6).Select(i => new GridPosition(i, 0, 0)).ToImmutableArray());
        var content = RailwayFixtures.Content(design: evaluation);
        var instance = RailwayFixtures.Instance() with { Structure = new(0, 100) };
        var first = await Frame(content, instance);
        var second = await Frame(content, instance);
        CollectionAssert.AreEqual(Mesh(first, instance).ToArray(), Mesh(second, instance).ToArray());
    }

    [TestMethod]
    public async Task DurabilityChangeWithoutEventsUpdatesTheMeshAndEmitsNoCue()
    {
        var evaluation = RailwayFixtures.Vehicle(cells: Enumerable.Range(0, 6).Select(i => new GridPosition(i, 0, 0)).ToImmutableArray());
        var content = RailwayFixtures.Content(design: evaluation);
        var healthy = RailwayFixtures.Instance();
        await using var session = Open(content, healthy);
        session.RequestView(new WorldSceneTarget.Location(RailwayFixtures.Location));
        var before = await RailwayFixtures.Settled(session, Request);
        session.Observe(Snapshot(content, healthy with { Structure = new(0, 100) }, steps: 1), []);
        var after = await RailwayFixtures.Settled(session, Request, before);
        Assert.IsEmpty(after.Update.Effects);
        CollectionAssert.AreNotEqual(Mesh(before, healthy).ToArray(), Mesh(after, healthy).ToArray());
    }

    [TestMethod]
    public async Task CueIdsStayStableUntilTheDisplayedFrameAdvances()
    {
        var (content, instance, evaluation) = Turret();
        await using var session = Open(content, instance);
        session.RequestView(new WorldSceneTarget.Location(RailwayFixtures.Location));
        var settled = await RailwayFixtures.Settled(session, Request);
        var hit = Snapshot(content, instance, steps: 1);
        session.Observe(hit, [Envelope(hit, new CoreEvent.CombatHits(new(1), RailwayFixtures.Location, [Hit(instance)]))]);
        var first = Present(session, settled);
        var retry = Present(session);
        CollectionAssert.AreEqual(first.Update.Effects.Select(item => item.Id).ToArray(), retry.Update.Effects.Select(item => item.Id).ToArray());
        Assert.IsTrue(first.Update.Effects.Any(item => item.Kind == SceneEffectKind.Shot));
        Assert.IsEmpty(Present(session, first).Update.Effects);
    }

    [TestMethod]
    public async Task EventsFromAnotherLocationAreNotReplayed()
    {
        var content = RailwayFixtures.Content(design: RailwayFixtures.Vehicle());
        var instance = RailwayFixtures.Instance();
        await using var session = Open(content, instance);
        session.RequestView(new WorldSceneTarget.Location(RailwayFixtures.Location));
        await RailwayFixtures.Settled(session, Request);
        var elsewhere = Snapshot(content, instance, steps: 1);
        session.Observe(elsewhere, [Envelope(elsewhere, new CoreEvent.CombatHits(new(1), new WorldLocation.Edge(new(99), 0),
            [Hit(instance)]))]);
        Assert.IsEmpty(Present(session).Update.Effects);
        session.Observe(Snapshot(content, instance, steps: 2), []);
        Assert.IsEmpty(Present(session).Update.Effects);
    }

    [TestMethod]
    public async Task SameStepDeletedInstanceStillEmitsDestruction()
    {
        var content = RailwayFixtures.Content(design: RailwayFixtures.Vehicle());
        var instance = RailwayFixtures.Instance();
        await using var session = Open(content, instance);
        session.RequestView(new WorldSceneTarget.Location(RailwayFixtures.Location));
        var settled = await RailwayFixtures.Settled(session, Request);
        var trainless = Snapshot(content, instance, steps: 1, vehicles: []);
        var gone = new SceneWorldSnapshot(new MissingInstance(trainless.Core, instance.Id), content);
        session.Observe(gone, [Envelope(gone, new CoreEvent.CombatDestruction(new(1), RailwayFixtures.Location, [],
            [new DestroyedUnit.Instance(new(instance.Id, instance.Design, RailwayFixtures.Location))]))]);
        var frame = Present(session, settled);
        Assert.AreEqual(SceneEffectKind.Destruction, frame.Update.Effects.Single().Kind);
        Assert.AreEqual(new WorldObjectId.Instance(instance.Id), frame.Update.Effects.Single().Subject);
    }

    [TestMethod]
    public async Task RailBreakEmitsRailwayDamageAndRepairEmitsNothing()
    {
        var content = RailwayFixtures.Content(design: RailwayFixtures.Vehicle());
        var instance = RailwayFixtures.Instance();
        await using var session = Open(content, instance);
        session.RequestView(new WorldSceneTarget.Location(RailwayFixtures.Location));
        await RailwayFixtures.Settled(session, Request);
        var broken = RailwayFixtures.ReplaceCore(Snapshot(content, instance, steps: 1),
            edge: Snapshot(content, instance).Core.World.Edges[0] with { Breaks = [new(new(4), 1, 1)] });
        session.Observe(broken, [Envelope(broken, new CoreEvent.RailBroken(RailwayFixtures.Edge, broken.Core.World.Edges[0].Breaks[0]))]);
        var brokenFrame = Present(session);
        Assert.AreEqual(SceneEffectKind.RailwayDamage, brokenFrame.Update.Effects.Single().Kind);
        var repaired = Snapshot(content, instance, steps: 2);
        session.Observe(repaired, [Envelope(repaired, new CoreEvent.RailRepaired(RailwayFixtures.Edge, new(4), 1))]);
        Assert.IsEmpty(Present(session, brokenFrame).Update.Effects);
    }

    [TestMethod]
    public async Task TheSameSimulationSecondsKeepTurretAndCreaturePosesAcrossPresentCounts()
    {
        var (content, instance, evaluation) = Turret();
        var creature = new CreatureDefinitionId("test:beast");
        content = RailwayFixtures.Content(design: evaluation, creatures: new Dictionary<CreatureDefinitionId, ContentLookup<CreatureAppearanceProjection>>
        {
            [creature] = new ContentLookup<CreatureAppearanceProjection>.Found(new(creature,
                SceneFixtures.Projection([SceneFixtures.Cell(new(0, 0, 0))]), new("Beast", "")))
        });
        var group = new CreatureGroupView(new(7), null, RailwayFixtures.Location, null, CreatureActivity.Guarding, 0,
            [new(new(3), creature, 10, 10)]);
        var once = await Pose(content, instance, group, false);
        var twice = await Pose(content, instance, group, true);
        Assert.AreEqual(once.Turret, twice.Turret);
        Assert.AreEqual(once.Creature, twice.Creature);
        Assert.AreNotEqual(Quaternion.Identity, once.Turret.Rotation);
    }

    [TestMethod]
    public void TurretAimUsesTheLocalFrameWhenTheHostIsRotated()
    {
        var turned = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2);
        var hosted = Aim(RotationAxis.Y, new(10, 0, 0), new(Vector3.Zero, turned));
        Assert.IsTrue(Vector3.Dot(hosted.Forward, hosted.Toward) > 0.99f);
        var dipped = Aim(RotationAxis.X, new(0, -8, 4), new(Vector3.Zero, Quaternion.Identity));
        Assert.IsTrue(Vector3.Dot(dipped.Forward, dipped.Toward) > 0.99f);
        var lookups = 0;
        var (content, instance, _) = Turret();
        var snapshot = Snapshot(content, instance, steps: 1);
        var state = new PresentationState();
        state.Note(snapshot, [Envelope(snapshot, new CoreEvent.CombatHits(new(1), RailwayFixtures.Location, [Hit(instance)]))], RailwayFixtures.Location);
        var objects = ImmutableArray.CreateBuilder<SceneObject>();
        state.ApplyTurrets(objects, instance, "vehicle:" + instance.Id + ":", _ => { lookups++; return null; }, 1, snapshot);
        state.ApplyTurrets(objects, instance, "vehicle:" + instance.Id + ":", _ => { lookups++; return null; }, 1, snapshot);
        Assert.AreEqual(1, lookups);
    }

    private static (Vector3 Forward, Vector3 Toward) Aim(RotationAxis axis, GridPosition target, ScenePose host)
    {
        var rotation = new RotationAppearance(axis, new(0, 0, 0), -180, 180, 3600);
        var cells = ImmutableArray.Create(SceneFixtures.Cell(new(0, 0, 0), 9, path: [0]), SceneFixtures.Cell(target, 1));
        var nested = ImmutableArray.Create(new NestedObjectProjection([0], new(0, 0, 0), VoxelOrientation.PxPy,
            new NestedObjectDetails.FunctionalComposite(new("test:turret"), new(9), rotation)));
        var evaluation = RailwayFixtures.Vehicle() with { Scene = SceneFixtures.Projection(cells, nested) };
        var content = RailwayFixtures.Content(design: evaluation);
        var instance = RailwayFixtures.Instance();
        var snapshot = Snapshot(content, instance, steps: 1);
        var state = new PresentationState();
        state.Note(snapshot, [Envelope(snapshot, new CoreEvent.CombatHits(new(1), RailwayFixtures.Location,
            [new CombatHit(new AttackSource.Weapon(new(instance.Id, instance.Design, RailwayFixtures.Location), new(9)),
                new HitTarget.Part(new(instance.Id, instance.Design, RailwayFixtures.Location), new(1)), 1)]))],
            RailwayFixtures.Location);
        var root = new SceneObjectId(1);
        var turret = new SceneObjectId(2);
        var objects = ImmutableArray.CreateBuilder<SceneObject>();
        objects.Add(new(root, null, host, new WorldObjectId.Instance(instance.Id), [], [], []));
        objects.Add(new(turret, root, new(Vector3.Zero, Quaternion.Identity), null, [], [], []));
        var key = "vehicle:" + instance.Id + ":0";
        state.ApplyTurrets(objects, instance, "vehicle:" + instance.Id + ":", asked => asked == key ? turret : null, 1, snapshot);
        var aimed = objects.Single(item => item.Id == turret).Pose;
        var worldRotation = Quaternion.Normalize(host.Rotation * aimed.Rotation);
        var worldPosition = host.Position + Vector3.Transform(aimed.Position, host.Rotation);
        var forward = Vector3.Transform(axis == RotationAxis.Z ? Vector3.UnitX : Vector3.UnitZ, worldRotation);
        var cell = Vector3.Transform(new Vector3(target.X + .5f, target.Y + .5f, target.Z + .5f), host.Rotation) + host.Position;
        return (Vector3.Normalize(forward), Vector3.Normalize(cell - worldPosition));
    }

    private static async Task<(ScenePose Turret, Vector3 Creature)> Pose(IContentView content, InstanceView instance,
        CreatureGroupView group, bool extra)
    {
        await using var session = Open(content, instance, group);
        session.RequestView(new WorldSceneTarget.Location(RailwayFixtures.Location));
        var settled = await RailwayFixtures.Settled(session, Request);
        var armed = Snapshot(content, instance, steps: 1, group: group);
        session.Observe(armed, [Envelope(armed, new CoreEvent.CombatHits(new(1), RailwayFixtures.Location,
            [new CombatHit(new AttackSource.Weapon(new(instance.Id, instance.Design, RailwayFixtures.Location), new(9)),
                new HitTarget.Part(new(instance.Id, instance.Design, RailwayFixtures.Location), new(1)), 1)]))]);
        var later = Snapshot(content, instance, steps: 2, group: group);
        session.Observe(later, []);
        if (extra) Present(session);
        var frame = Present(session, settled, new SceneFrameRequest(1, null, SceneDebugLayer.None));
        var root = frame.Objects.Single(item => item.Parent is null && item.Subject == new WorldObjectId.Instance(instance.Id));
        var turret = frame.Objects.Single(item => item.Parent == root.Id && item.Visuals.Length > 0);
        var creature = frame.Objects.Single(item => item.Subject is WorldObjectId.Creature).Pose.Position;
        return (turret.Pose, creature);
    }

    [TestMethod]
    public async Task PausedSimulationTimeKeepsCreatureAndTurretPoses()
    {
        var (_, instance, evaluation) = Turret();
        var creature = new CreatureDefinitionId("test:beast");
        var content = RailwayFixtures.Content(design: evaluation, creatures:
            new Dictionary<CreatureDefinitionId, ContentLookup<CreatureAppearanceProjection>>
            {
                [creature] = new ContentLookup<CreatureAppearanceProjection>.Found(new(creature,
                    SceneFixtures.Projection([SceneFixtures.Cell(new(0, 0, 0))]), new("Beast", "")))
            });
        var group = new CreatureGroupView(new(7), null, RailwayFixtures.Location, null, CreatureActivity.Guarding, 0,
            [new(new(3), creature, 10, 10)]);
        var first = await Pose(content, instance, group, false);
        var second = await Pose(content, instance, group, false);
        Assert.AreEqual(first.Turret, second.Turret);
        Assert.AreEqual(first.Creature, second.Creature);
    }

    [TestMethod]
    public async Task CreatureAppearanceComesFromThePairedContentViewAndDeathUsesTheEventDefinition()
    {
        var creature = new CreatureDefinitionId("test:beast");
        var other = new CreatureDefinitionId("test:other");
        var content = RailwayFixtures.Content(design: RailwayFixtures.Vehicle(), creatures: new Dictionary<CreatureDefinitionId, ContentLookup<CreatureAppearanceProjection>>
        {
            [creature] = new ContentLookup<CreatureAppearanceProjection>.Found(new(creature,
                SceneFixtures.Projection([SceneFixtures.Cell(new(0, 0, 0)) with { Paint = new("red") }]), new("Beast", ""))),
            [other] = new ContentLookup<CreatureAppearanceProjection>.Found(new(other,
                SceneFixtures.Projection([SceneFixtures.Cell(new(0, 0, 0)) with { Paint = new("blue") }]), new("Other", "")))
        });
        var group = new CreatureGroupView(new(7), null, RailwayFixtures.Location, null, CreatureActivity.Guarding, 0,
            [new(new(3), creature, 10, 10)]);
        var instance = RailwayFixtures.Instance();
        await using var session = Open(content, instance, group);
        session.RequestView(new WorldSceneTarget.Location(RailwayFixtures.Location));
        var settled = await RailwayFixtures.Settled(session, Request);
        Assert.IsTrue(settled.Objects.Any(item => item.Subject == new WorldObjectId.Creature(new(3))));
        var dead = Snapshot(content, instance, steps: 1, group: group with { Creatures = [] });
        session.Observe(dead, [Envelope(dead, new CoreEvent.CombatDestruction(new(1), RailwayFixtures.Location, [],
            [new DestroyedUnit.Creature(new(group.Id, new(3), other, RailwayFixtures.Location))]))]);
        var frame = Present(session, settled);
        Assert.IsFalse(frame.Objects.Any(item => item.Subject == new WorldObjectId.Creature(new(3))));
        Assert.AreEqual(new PaintColor("blue"), frame.Update.Effects.Single(item => item.Kind == SceneEffectKind.Fragments).Color);
    }

    [TestMethod]
    public async Task PresentationOnlyContentUpdateRebuildsTheCreature()
    {
        var creature = new CreatureDefinitionId("test:beast");
        CreatureAppearanceProjection Appearance(string paint) => new(creature,
            SceneFixtures.Projection([SceneFixtures.Cell(new(0, 0, 0)) with { Paint = new(paint) }]), new("Beast", ""));
        var content = RailwayFixtures.Content(design: RailwayFixtures.Vehicle(), creatures: new Dictionary<CreatureDefinitionId, ContentLookup<CreatureAppearanceProjection>>
        { [creature] = new ContentLookup<CreatureAppearanceProjection>.Found(Appearance("red")) });
        var group = new CreatureGroupView(new(7), null, RailwayFixtures.Location, null, CreatureActivity.Guarding, 0,
            [new(new(3), creature, 10, 10)]);
        var instance = RailwayFixtures.Instance();
        await using var session = Open(content, instance, group);
        session.RequestView(new WorldSceneTarget.Location(RailwayFixtures.Location));
        var before = await RailwayFixtures.Settled(session, Request);
        var again = Present(session, before);
        Assert.AreSame(CreatureMesh(before), CreatureMesh(again));
        var updated = RailwayFixtures.Content(design: RailwayFixtures.Vehicle(), creatures:
            new Dictionary<CreatureDefinitionId, ContentLookup<CreatureAppearanceProjection>>
            { [creature] = new ContentLookup<CreatureAppearanceProjection>.Found(Appearance("green")) });
        session.Observe(Snapshot(updated, instance, steps: 1, group: group), []);
        var after = Present(session, before);
        var beforePaint = Paint(before);
        var afterPaint = Paint(after);
        Assert.AreEqual(new PaintColor("red"), beforePaint);
        Assert.AreEqual(new PaintColor("green"), afterPaint);
        Assert.AreNotSame(CreatureMesh(before), CreatureMesh(after));
    }

    private static SceneMesh CreatureMesh(SceneFrame frame) => frame.Objects.Where(item => item.Subject is WorldObjectId.Creature)
        .SelectMany(item => item.Visuals.OfType<SceneVisual.Geometry>()).Select(item => item.Mesh).First();

    private static PaintColor? Paint(SceneFrame frame) => frame.Objects.Where(item => item.Subject is WorldObjectId.Creature)
        .SelectMany(item => item.Visuals.OfType<SceneVisual.Geometry>()).Select(item => ((SceneMaterial.Voxel)item.Mesh.Material).Paint)
        .First();

    private static (FixedContentView Content, InstanceView Instance, DesignEvaluation Evaluation) Turret()
    {
        var rotation = new RotationAppearance(RotationAxis.Y, new(0, 0, 0), -180, 180, 3600);
        var cells = ImmutableArray.Create(SceneFixtures.Cell(new(0, 0, 0), 9, path: [0]), SceneFixtures.Cell(new(4, 0, 0), 1));
        var nested = ImmutableArray.Create(new NestedObjectProjection([0], new(0, 0, 0), VoxelOrientation.PxPy,
            new NestedObjectDetails.FunctionalComposite(new("test:turret"), new(9), rotation)));
        var projection = SceneFixtures.Projection(cells, nested);
        var evaluation = RailwayFixtures.Vehicle() with { Scene = projection };
        return (RailwayFixtures.Content(design: evaluation), RailwayFixtures.Instance(), evaluation);
    }

    private static DesignElementProjection Element(SceneVoxelProjection cell, bool functional) =>
        new(cell.Number, cell.Element, new ComputedValue<ImmutableArray<GridPosition>>.Known([cell.At]), functional, false);

    private static DesignSceneProjection Line(int count) => SceneFixtures.Projection(
        Enumerable.Range(0, count).Select(index => SceneFixtures.Cell(new(index, 0, 0), 1)).ToImmutableArray());

    private static IEnumerable<SceneVertex> Vertices(RenderedDesign geometry) => geometry.Objects.SelectMany(item =>
        item.Visuals.OfType<SceneVisual.Geometry>()).SelectMany(item => item.Mesh.Vertices);

    private static IEnumerable<SceneVertex> Mesh(SceneFrame frame, InstanceView instance) => frame.Objects
        .Where(item => item.Subject == new WorldObjectId.Instance(instance.Id))
        .SelectMany(item => item.Visuals.OfType<SceneVisual.Geometry>()).SelectMany(item => item.Mesh.Vertices);

    private static async Task<SceneFrame> Frame(IContentView content, InstanceView instance)
    {
        await using var session = Open(content, instance);
        session.RequestView(new WorldSceneTarget.Location(RailwayFixtures.Location));
        return await RailwayFixtures.Settled(session, Request);
    }

    private static WorldSceneSession Open(IContentView content, InstanceView instance, CreatureGroupView? group = null) =>
        new(new(Guid.NewGuid()), Snapshot(content, instance, group: group), new(), new());

    private static SceneWorldSnapshot Snapshot(IContentView content, InstanceView instance, ulong steps = 0,
        ImmutableArray<VehicleId>? vehicles = null, CreatureGroupView? group = null) 
    {
        var train = RailwayFixtures.Train(0, vehicles ?? [new(1)]);
        var snapshot = RailwayFixtures.Snapshot(4, 4, 0, steps: steps, content: content, trains: [train], instances: [instance]);
        if (group is null) return snapshot;
        return snapshot with
        {
            Core = new CreatureCore(snapshot.Core, group)
        };
    }

    private static SceneFrame Present(WorldSceneSession session, ISceneFrame? displayed = null, SceneFrameRequest? request = null) =>
        (SceneFrame)((SceneResult<ISceneFrame>.Success)session.Present(request ?? Request, displayed)).Value;

    private static EventEnvelope Envelope(SceneWorldSnapshot snapshot, CoreEvent item) => new(snapshot.Core.Position, 0, item);

    private static CombatHit Hit(InstanceView instance) => new(
        new AttackSource.Weapon(new(instance.Id, instance.Design, RailwayFixtures.Location), new(9)),
        new HitTarget.Structure(new(instance.Id, instance.Design, RailwayFixtures.Location)), 4);

    private sealed class MissingInstance(ICoreReadView inner, InstanceId missing) : ICoreReadView
    {
        public Lookup<InstanceView> FindInstance(InstanceId id) => id == missing ? new Lookup<InstanceView>.Missing() : inner.FindInstance(id);
        public SimulationPosition Position => inner.Position;
        public CoreRevision Revision => inner.Revision;
        public CoreInputId InputIdentity => inner.InputIdentity;
        public CoreFault? Fault => inner.Fault;
        public WorldSummary World => inner.World;
        public EconomyView Economy => inner.Economy;
        public Lookup<NodeView> FindNode(NodeId id) => inner.FindNode(id);
        public Lookup<EdgeView> FindEdge(EdgeId id) => inner.FindEdge(id);
        public Lookup<LocationView> FindLocation(WorldLocation location) => inner.FindLocation(location);
        public Lookup<TrainView> FindTrain(TrainId id) => inner.FindTrain(id);
        public Lookup<SettlementView> FindSettlement(SettlementId id) => inner.FindSettlement(id);
        public Lookup<InventoryView> FindInventory(InventoryId id) => inner.FindInventory(id);
        public Lookup<LeaseView> FindLease(LeaseId id) => inner.FindLease(id);
        public Lookup<FacilityView> FindFacility(FacilityKey key) => inner.FindFacility(key);
        public Lookup<JobView> FindJob(JobId id) => inner.FindJob(id);
        public Lookup<CombatView> FindCombat(CombatId id) => inner.FindCombat(id);
        public Lookup<CreatureGroupView> FindCreatureGroup(CreatureGroupId id) => inner.FindCreatureGroup(id);
        public Lookup<CreatureView> FindCreature(CreatureId id) => inner.FindCreature(id);
        public Lookup<LairView> FindLair(LairId id) => inner.FindLair(id);
        public CoreResult<TradeQuote> QuoteTrade(TradeQuoteRequest request) => inner.QuoteTrade(request);
        public CoreResult<VehicleTradeQuote> QuoteVehicle(VehicleTradeQuoteRequest request) => inner.QuoteVehicle(request);
        public CoreResult<LeaseQuote> QuoteLease(LeaseRequest request) => inner.QuoteLease(request);
        public CoreResult<JobQuote> GetJobQuote(JobRequest request, JobFunding funding) => inner.GetJobQuote(request, funding);
        public CoreResult<SettlementFoundingPreview> PreviewSettlement(NodeId node) => inner.PreviewSettlement(node);
    }

    private sealed class CreatureCore(ICoreReadView inner, CreatureGroupView group) : ICoreReadView
    {
        public WorldSummary World => inner.World with
        {
            CreatureGroups = [new(group.Id, group.Location, group.Activity)]
        };
        public Lookup<CreatureGroupView> FindCreatureGroup(CreatureGroupId id) =>
            id == group.Id ? new Lookup<CreatureGroupView>.Found(group) : inner.FindCreatureGroup(id);
        public SimulationPosition Position => inner.Position;
        public CoreRevision Revision => inner.Revision;
        public CoreInputId InputIdentity => inner.InputIdentity;
        public CoreFault? Fault => inner.Fault;
        public EconomyView Economy => inner.Economy;
        public Lookup<NodeView> FindNode(NodeId id) => inner.FindNode(id);
        public Lookup<EdgeView> FindEdge(EdgeId id) => inner.FindEdge(id);
        public Lookup<LocationView> FindLocation(WorldLocation location) => inner.FindLocation(location);
        public Lookup<TrainView> FindTrain(TrainId id) => inner.FindTrain(id);
        public Lookup<InstanceView> FindInstance(InstanceId id) => inner.FindInstance(id);
        public Lookup<SettlementView> FindSettlement(SettlementId id) => inner.FindSettlement(id);
        public Lookup<InventoryView> FindInventory(InventoryId id) => inner.FindInventory(id);
        public Lookup<LeaseView> FindLease(LeaseId id) => inner.FindLease(id);
        public Lookup<FacilityView> FindFacility(FacilityKey key) => inner.FindFacility(key);
        public Lookup<JobView> FindJob(JobId id) => inner.FindJob(id);
        public Lookup<CombatView> FindCombat(CombatId id) => inner.FindCombat(id);
        public Lookup<CreatureView> FindCreature(CreatureId id) => inner.FindCreature(id);
        public Lookup<LairView> FindLair(LairId id) => inner.FindLair(id);
        public CoreResult<TradeQuote> QuoteTrade(TradeQuoteRequest request) => inner.QuoteTrade(request);
        public CoreResult<VehicleTradeQuote> QuoteVehicle(VehicleTradeQuoteRequest request) => inner.QuoteVehicle(request);
        public CoreResult<LeaseQuote> QuoteLease(LeaseRequest request) => inner.QuoteLease(request);
        public CoreResult<JobQuote> GetJobQuote(JobRequest request, JobFunding funding) => inner.GetJobQuote(request, funding);
        public CoreResult<SettlementFoundingPreview> PreviewSettlement(NodeId node) => inner.PreviewSettlement(node);
    }
}
