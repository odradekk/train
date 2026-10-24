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
    public async Task HitDamageDoesNotChooseRemovedCells()
    {
        var light = await DamagedMesh(1);
        var heavy = await DamagedMesh(999);
        Assert.IsTrue(light.Any(vertex => vertex.Damage > 0));
        Assert.IsTrue(light.SequenceEqual(heavy));
    }

    private static async Task<SceneVertex[]> DamagedMesh(int damage)
    {
        var body = RailwayFixtures.Instance() with { Structure = new(25, 100) };
        var content = RailwayFixtures.Content(design: RailwayFixtures.Vehicle() with { Scene = Line(8) });
        await using var session = Open(content, body);
        session.RequestView(new WorldSceneTarget.Location(RailwayFixtures.Location));
        var settled = await RailwayFixtures.Settled(session, Request);
        var snapshot = Snapshot(content, body, steps: 1);
        var hit = new CombatHit(new AttackSource.Weapon(new(body.Id, body.Design, RailwayFixtures.Location), new(1)),
            new HitTarget.Structure(new(body.Id, body.Design, RailwayFixtures.Location)), damage);
        session.Observe(snapshot, [Envelope(snapshot, new CoreEvent.CombatHits(new(1), RailwayFixtures.Location, [hit]))]);
        return Mesh(Present(session, settled), body).ToArray();
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
    public void OnlyDamagedPropulsionAndPowerPartsLeakAtTheirLowestCellInLocalCoordinates()
    {
        var high = SceneFixtures.Cell(new(-4, 3, 0), 1);
        var low = SceneFixtures.Cell(new(2, -1, 5), 1, path: [0, 2]) with { LocalAt = new(7, 8, 9) };
        var tied = SceneFixtures.Cell(new(3, -1, 0), 1);
        var power = SceneFixtures.Cell(new(0, 0, 0), 2);
        var control = SceneFixtures.Cell(new(1, 0, 0), 3);
        var projection = SceneFixtures.Projection([high, tied, low, power, control],
            [new([0], new(-5, -9, -4), VoxelOrientation.PxPy, new NestedObjectDetails.Assembly(null)),
                new([0, 2], default, VoxelOrientation.PxPy, new NestedObjectDetails.Assembly(null))]) with
        { Elements = [Element(high, true), Element(power, true), Element(control, true)] };
        var evaluation = NodeFixtures.StationEvaluation();
        var basis = evaluation.Core!.Design.Parts[0];
        evaluation = evaluation with { Scene = projection, Core = evaluation.Core with { Design = evaluation.Core.Design with
        {
            Parts = [basis with { Id = new(1), Capabilities = [new PartCapability.Propulsion(1, 1)] },
                basis with { Id = new(2), Capabilities = [new PartCapability.Power(1)] },
                basis with { Id = new(3), Capabilities = [new PartCapability.Control()] }]
        } } };
        var body = RailwayFixtures.Instance() with { Parts = [new(new(1), new(25, 100), false, false),
            new(new(2), new(50, 100), false, false), new(new(3), new(50, 100), false, false)] };
        var content = SceneFixtures.Content(evaluation, body.Design);
        var report = StructureDamage.Assess(body, projection, content);
        var leaks = report.Effects.Where(item => item.Effect.Kind == PersistentEffectKind.Leak).ToArray();
        Assert.AreEqual(2, leaks.Length);
        Assert.AreEqual("0/2", leaks[0].Path);
        Assert.AreEqual(new Vector3(7.5f, 8, 9.5f), leaks[0].Effect.LocalPosition);
        Assert.AreEqual(.75f, leaks[0].Effect.Strength);
        Assert.AreEqual("", leaks[1].Path);
        Assert.AreEqual(new Vector3(.5f, 0, .5f), leaks[1].Effect.LocalPosition);
        Assert.AreEqual(3, report.Effects.Count(item => item.Effect.Kind == PersistentEffectKind.Sparks));
        CollectionAssert.AreEqual(leaks, StructureDamage.Assess(body, projection, content).Effects
            .Where(item => item.Effect.Kind == PersistentEffectKind.Leak).ToArray());
        var rotated = projection with { Voxels = projection.Voxels.SetItem(2, low with { At = new(-13, -2, 5) }),
            NestedObjects = projection.NestedObjects.SetItem(1,
            projection.NestedObjects[1] with { Orientation = VoxelOrientation.PyNx }) };
        var rotatedLeak = StructureDamage.Assess(body, rotated, content).Effects.First(item => item.Effect.Kind == PersistentEffectKind.Leak);
        Assert.IsTrue(Vector3.Distance(new Vector3(7, 8.5f, 9.5f), rotatedLeak.Effect.LocalPosition) < 1e-5);
        var repaired = body with { Parts = body.Parts.Select(part => part with { Durability = new(100, 100) }).ToImmutableArray() };
        Assert.IsFalse(StructureDamage.Assess(repaired, projection, content).Effects.Any(item => item.Effect.Kind == PersistentEffectKind.Leak));
        var destroyed = body with { Parts = body.Parts.Select(part => part with { Destroyed = true }).ToImmutableArray() };
        Assert.IsFalse(StructureDamage.Assess(destroyed, projection, content).Effects.Any(item => item.Effect.Kind == PersistentEffectKind.Leak));
    }

    [TestMethod]
    public void LeakPrefersAnyClearBottomThenTheFirstExposedSideAndStopsWhenEnclosed()
    {
        var low = SceneFixtures.Cell(new(0, 1, 0), 1);
        var high = SceneFixtures.Cell(new(2, 2, 0), 1);
        var floor = SceneFixtures.Cell(new(0, 0, 0), 2);
        var body = RailwayFixtures.Instance() with { Parts = [new(new(1), new(40, 100), false, false)] };
        var evaluation = NodeFixtures.StationEvaluation();
        evaluation = evaluation with { Core = evaluation.Core! with { Design = evaluation.Core.Design with
        { Parts = [evaluation.Core.Design.Parts[0] with { Id = new(1), Capabilities = [new PartCapability.Power(1)] }] } } };
        StructureDamage.Report Assess(params SceneVoxelProjection[] cells)
        {
            var projection = SceneFixtures.Projection(cells.ToImmutableArray()) with { Elements = [Element(low, true)] };
            return StructureDamage.Assess(body, projection, SceneFixtures.Content(evaluation with { Scene = projection }, body.Design));
        }
        Assert.AreEqual(new Vector3(2.5f, 2, .5f), Assess(high, floor, low).Effects
            .Single(item => item.Effect.Kind == PersistentEffectKind.Leak).Effect.LocalPosition);
        var blocked = new[] { high, floor, low, SceneFixtures.Cell(new(2, 1, 0), 2) };
        var side = Assess(blocked).Effects.Single(item => item.Effect.Kind == PersistentEffectKind.Leak);
        Assert.AreEqual(new Vector3(0, 1.5f, .5f), side.Effect.LocalPosition);
        Assert.AreEqual(.6f, side.Effect.Strength);
        var enclosed = blocked.Concat(new[] { low, high }.SelectMany(cell => new[]
        {
            cell.At with { X = cell.At.X - 1 }, cell.At with { X = cell.At.X + 1 },
            cell.At with { Z = cell.At.Z - 1 }, cell.At with { Z = cell.At.Z + 1 },
            cell.At with { Y = cell.At.Y + 1 }
        }).Select(at => SceneFixtures.Cell(at, 2))).ToArray();
        var sealedReport = Assess(enclosed);
        Assert.IsFalse(sealedReport.Effects.Any(item => item.Effect.Kind == PersistentEffectKind.Leak));
        Assert.AreEqual(1, sealedReport.Effects.Count(item => item.Effect.Kind == PersistentEffectKind.Sparks));
    }

    [TestMethod]
    public void RemovedStructuralFloorDoesNotBlockTheLeak()
    {
        var power = SceneFixtures.Cell(new(0, 1, 0), 1);
        var cells = Enumerable.Range(0, 5).Select(x => SceneFixtures.Cell(new(x, 0, 0), 2)).Append(power).ToImmutableArray();
        var body = RailwayFixtures.Instance() with { Structure = new(0, 100), Parts = [new(new(1), new(50, 100), false, false)] };
        var evaluation = NodeFixtures.StationEvaluation();
        evaluation = evaluation with { Core = evaluation.Core! with { Design = evaluation.Core.Design with
        { Parts = [evaluation.Core.Design.Parts[0] with { Id = new(1), Capabilities = [new PartCapability.Power(1)] }] } } };
        var projection = SceneFixtures.Projection(cells) with { Elements = [Element(power, true)] };
        var content = SceneFixtures.Content(evaluation with { Scene = projection }, body.Design);
        var removed = StructureDamage.Assess(body, projection, content).Removed.First().At;
        power = power with { At = removed with { Y = 1 }, LocalAt = removed with { Y = 1 } };
        projection = projection with { Voxels = cells.SetItem(cells.Length - 1, power) };
        var report = StructureDamage.Assess(body, projection, content);
        Assert.IsTrue(report.Removed.Any(cell => cell.At == removed));
        Assert.AreEqual(new Vector3(removed.X + .5f, 1, removed.Z + .5f), report.Effects
            .Single(item => item.Effect.Kind == PersistentEffectKind.Leak).Effect.LocalPosition);
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
    public async Task PartialFrameEmitsAnObservedHit()
    {
        var (content, instance, _) = Turret();
        var snapshot = RailwayFixtures.Snapshot(800, 600, relief: 0, steps: 1, content: content,
            trains: [RailwayFixtures.Train(128)], instances: [instance]);
        var state = new PresentationState();
        state.Note(snapshot, snapshot, [Envelope(snapshot, new CoreEvent.CombatHits(new(1), RailwayFixtures.Location, [Hit(instance)]))],
            RailwayFixtures.Location);
        using var release = new ManualResetEventSlim();
        await using var vehicles = new SceneVehicles();
        await using var scene = RailwayFixtures.Controlled(snapshot, vehicles, (input, path, index, token) =>
        {
            if (index != 2) release.Wait(token);
            return RailwayFixtures.EmptySegment(input, path, index, token);
        }, presentation: state);
        var target = new WorldSceneTarget.Location(new WorldLocation.Edge(RailwayFixtures.Edge, 128));
        var stamp = new SceneFrameStamp(new(Guid.NewGuid()), new(1), new(1), 1);
        try
        {
            var revision = 1ul;
            var frame = scene.Present(snapshot, null, target, Request, stamp, null);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (frame.Readiness == SceneReadiness.Loading && watch.Elapsed < TimeSpan.FromSeconds(5))
            {
                var pending = scene.PendingWork;
                if (pending.Length > 0) await Task.WhenAny(pending);
                else await Task.Delay(10);
                frame = scene.Present(snapshot, null, target, Request, stamp with { Revision = ++revision }, null);
            }
            Assert.AreEqual(SceneReadiness.Partial, frame.Readiness);
            Assert.IsTrue(frame.Update.Effects.Any(item => item.Kind == SceneEffectKind.Shot));
        }
        finally { release.Set(); }
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
        state.Note(snapshot, snapshot, [Envelope(snapshot, new CoreEvent.CombatHits(new(1), RailwayFixtures.Location, [Hit(instance)]))], RailwayFixtures.Location);
        var objects = ImmutableArray.CreateBuilder<SceneObject>();
        state.ApplyTurrets(objects, instance, "vehicle:" + instance.Id + ":", _ => { lookups++; return null; }, 1, snapshot, default);
        state.ApplyTurrets(objects, instance, "vehicle:" + instance.Id + ":", _ => { lookups++; return null; }, 1, snapshot, default);
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
        state.Note(snapshot, snapshot, [Envelope(snapshot, new CoreEvent.CombatHits(new(1), RailwayFixtures.Location,
            [new CombatHit(new AttackSource.Weapon(new(instance.Id, instance.Design, RailwayFixtures.Location), new(9)),
                new HitTarget.Part(new(instance.Id, instance.Design, RailwayFixtures.Location), new(1)), 1)]))],
            RailwayFixtures.Location);
        var root = new SceneObjectId(1);
        var turret = new SceneObjectId(2);
        var objects = ImmutableArray.CreateBuilder<SceneObject>();
        objects.Add(new(root, null, host, new WorldObjectId.Instance(instance.Id), [], [], []));
        objects.Add(new(turret, root, new(Vector3.Zero, Quaternion.Identity), null, [], [], []));
        var key = "vehicle:" + instance.Id + ":0";
        state.ApplyTurrets(objects, instance, "vehicle:" + instance.Id + ":", asked => asked == key ? turret : null, 1, snapshot, default);
        var aimed = objects.Single(item => item.Id == turret).Pose;
        var worldRotation = Quaternion.Normalize(host.Rotation * aimed.Rotation);
        var worldPosition = host.Position + Vector3.Transform(aimed.Position, host.Rotation);
        var forward = Vector3.Transform(axis == RotationAxis.Z ? Vector3.UnitX : Vector3.UnitZ, worldRotation);
        var cell = Vector3.Transform(new Vector3(target.X + .5f, target.Y + .5f, target.Z + .5f), host.Rotation) + host.Position;
        return (Vector3.Normalize(forward), Vector3.Normalize(cell - worldPosition));
    }

    private static (FixedContentView Content, InstanceView Instance, CreatureGroupView Group) Beast()
    {
        var (_, instance, evaluation) = Turret();
        var creature = new CreatureDefinitionId("test:beast");
        var content = RailwayFixtures.Content(design: evaluation, creatures: new Dictionary<CreatureDefinitionId, ContentLookup<CreatureAppearanceProjection>>
        {
            [creature] = new ContentLookup<CreatureAppearanceProjection>.Found(new(creature,
                SceneFixtures.Projection([SceneFixtures.Cell(new(0, 0, 0))]), new("Beast", "")))
        });
        var group = new CreatureGroupView(new(7), null, RailwayFixtures.Location, null, CreatureActivity.Guarding, 0,
            [new(new(3), creature, 10, 10)]);
        return (content, instance, group);
    }

    private static (ScenePose Turret, Vector3 Creature) Poses(SceneFrame frame, InstanceView instance)
    {
        var root = frame.Objects.Single(item => item.Parent is null && item.Subject == new WorldObjectId.Instance(instance.Id));
        var turret = frame.Objects.Single(item => item.Parent == root.Id && item.Visuals.Length > 0);
        var creature = frame.Objects.Single(item => item.Subject is WorldObjectId.Creature).Pose.Position;
        return (turret.Pose, creature);
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
        var (content, instance, group) = Beast();
        await using var session = Open(content, instance, group);
        session.RequestView(new WorldSceneTarget.Location(RailwayFixtures.Location));
        var settled = await RailwayFixtures.Settled(session, Request);
        var armed = Snapshot(content, instance, steps: 1, group: group);
        session.Observe(armed, [Envelope(armed, new CoreEvent.CombatHits(new(1), RailwayFixtures.Location,
            [new CombatHit(new AttackSource.Weapon(new(instance.Id, instance.Design, RailwayFixtures.Location), new(9)),
                new HitTarget.Part(new(instance.Id, instance.Design, RailwayFixtures.Location), new(1)), 1)]))]);
        session.Observe(Snapshot(content, instance, steps: 2, group: group), []);
        var held = new SceneFrameRequest(0, null, SceneDebugLayer.None);
        var first = Poses(Present(session, settled, held), instance);
        var second = Poses(Present(session, settled, held), instance);
        Assert.AreEqual(first.Turret, second.Turret);
        Assert.AreEqual(first.Creature, second.Creature);
        var advanced = Poses(Present(session, settled, Request), instance);
        Assert.AreNotEqual(first.Turret, advanced.Turret);
        Assert.AreNotEqual(first.Creature, advanced.Creature);
    }

    [TestMethod]
    public async Task SameStepDeathLeavesTheCreatureInTheQueryAndEmitsOnlyFragments()
    {
        var (_, instance, evaluation) = Turret();
        var creature = new CreatureDefinitionId("test:beast");
        var other = new CreatureDefinitionId("test:other");
        var content = RailwayFixtures.Content(design: evaluation, creatures: new Dictionary<CreatureDefinitionId, ContentLookup<CreatureAppearanceProjection>>
        {
            [creature] = new ContentLookup<CreatureAppearanceProjection>.Found(new(creature,
                SceneFixtures.Projection([SceneFixtures.Cell(new(0, 0, 0)) with { Paint = new("red") }]), new("Beast", ""))),
            [other] = new ContentLookup<CreatureAppearanceProjection>.Found(new(other,
                SceneFixtures.Projection([SceneFixtures.Cell(new(0, 0, 0)) with { Paint = new("blue") }]), new("Other", "")))
        });
        var group = new CreatureGroupView(new(7), null, RailwayFixtures.Location, null, CreatureActivity.Guarding, 0,
            [new(new(3), creature, 10, 10)]);
        await using var session = Open(content, instance, group);
        session.RequestView(new WorldSceneTarget.Location(RailwayFixtures.Location));
        var settled = await RailwayFixtures.Settled(session, Request);
        var dead = Snapshot(content, instance, steps: 1, group: group);
        session.Observe(dead, [Envelope(dead, new CoreEvent.CombatDestruction(new(1), RailwayFixtures.Location, [],
            [new DestroyedUnit.Creature(new(group.Id, new(3), other, RailwayFixtures.Location))]))]);
        var frame = Present(session, settled);
        Assert.IsFalse(frame.Objects.Any(item => item.Subject is WorldObjectId.Creature));
        Assert.AreEqual(1, frame.Update.Effects.Length);
        Assert.AreEqual(SceneEffectKind.Fragments, frame.Update.Effects[0].Kind);
        Assert.AreEqual(new PaintColor("blue"), frame.Update.Effects[0].Color);
    }

    [TestMethod]
    public async Task WeaponHitsOutsideTheFocusRangeKeepTheirShotEndpoints()
    {
        var parked = RailwayFixtures.Instance(7, train: null) with { Location = NodeFixtures.Location };
        var member = RailwayFixtures.Instance(1, train: new TrainId(1)) with { Location = NodeFixtures.Location };
        var train = NodeFixtures.Train(1, vehicles: [new(1)]);
        var combat = new CombatView(new(1), NodeFixtures.Location, 1, []);
        var snapshot = NodeFixtures.Snapshot(platforms: 2, trains: [train], instances: [member, parked],
            settlement: NodeFixtures.Settlement() with { ParkedVehicles = [new(7)] }, combats: [combat]);
        await using var session = new WorldSceneSession(new(Guid.NewGuid()), snapshot, new(), new());
        session.RequestView(new WorldSceneTarget.Combat(combat.Id));
        var open = await RailwayFixtures.Settled(session, Request);
        var parkedPose = ((Lookup<ScenePose>.Found)open.FindPose(new WorldObjectId.Instance(parked.Id))).Value;
        var near = new SceneCameraSample(open, parkedPose.Position, 6656);
        var hits = new CombatHit[]
        {
            new(new AttackSource.Weapon(new(parked.Id, parked.Design, NodeFixtures.Location), new(1)),
                new HitTarget.Structure(new(member.Id, member.Design, NodeFixtures.Location)), 4),
            new(new AttackSource.Weapon(new(member.Id, member.Design, NodeFixtures.Location), new(1)),
                new HitTarget.Structure(new(parked.Id, parked.Design, NodeFixtures.Location)), 4)
        }.ToImmutableArray();
        var attacked = NodeFixtures.Snapshot(platforms: 2, trains: [train], instances: [member, parked], steps: 1,
            settlement: NodeFixtures.Settlement() with { ParkedVehicles = [new(7)] }, combats: [combat]);
        session.Observe(attacked, [Envelope(attacked, new CoreEvent.CombatHits(new(1), NodeFixtures.Location, hits))]);
        var nearFrame = Present(session, open, new(1, near, SceneDebugLayer.None));
        var nearShots = Shots(nearFrame);
        Assert.AreEqual(2, nearShots.Length);
        var cleared = Present(session, nearFrame, new(1, near, SceneDebugLayer.None));
        Assert.IsEmpty(Shots(cleared));

        // 焦点移到保留范围外：建筑和停放车不再合成，且上一帧位姿已不再包含停放车。
        var far = new SceneCameraSample(cleared, parkedPose.Position + new Vector3(0, 0, 400), 6656);
        var farFrame = Present(session, cleared, new(1, far, SceneDebugLayer.None));
        Assert.IsInstanceOfType<Lookup<ScenePose>.Missing>(farFrame.FindPose(new WorldObjectId.Instance(parked.Id)));
        Assert.IsInstanceOfType<Lookup<ScenePose>.Found>(farFrame.FindPose(new WorldObjectId.Instance(member.Id)));
        Assert.IsEmpty(Shots(farFrame));
        var farAttacked = NodeFixtures.Snapshot(platforms: 2, trains: [train], instances: [member, parked], steps: 3,
            settlement: NodeFixtures.Settlement() with { ParkedVehicles = [new(7)] }, combats: [combat]);
        session.Observe(farAttacked, [Envelope(farAttacked, new CoreEvent.CombatHits(new(1), NodeFixtures.Location, hits))]);
        var hitFrame = Present(session, farFrame, new(1, far, SceneDebugLayer.None));
        Assert.IsInstanceOfType<Lookup<ScenePose>.Missing>(hitFrame.FindPose(new WorldObjectId.Instance(parked.Id)));
        var farShots = Shots(hitFrame);
        Assert.AreEqual(2, farShots.Length);
        foreach (var subject in new WorldObjectId[] { new WorldObjectId.Instance(member.Id), new WorldObjectId.Instance(parked.Id) })
        {
            var before = nearShots.Single(item => item.Subject == subject);
            var after = farShots.Single(item => item.Subject == subject);
            AssertPoint(before, nearFrame, after, hitFrame);
        }
    }

    private static ImmutableArray<SceneEffectCue> Shots(ISceneFrame frame) =>
        frame.Update.Effects.Where(item => item.Kind == SceneEffectKind.Shot).ToImmutableArray();

    private static void AssertPoint(SceneEffectCue expected, ISceneFrame expectedFrame,
        SceneEffectCue actual, ISceneFrame actualFrame)
    {
        var before = ToWorld(expected.From, expectedFrame);
        var after = ToWorld(actual.From, actualFrame);
        Assert.AreEqual(before.X, after.X, 1e-3, "Shot.From.X");
        Assert.AreEqual(before.Y, after.Y, 1e-3, "Shot.From.Y");
        Assert.AreEqual(before.Z, after.Z, 1e-3, "Shot.From.Z");
        var expectedTo = ToWorld(expected.To!.Value, expectedFrame);
        var actualTo = ToWorld(actual.To!.Value, actualFrame);
        Assert.AreEqual(expectedTo.X, actualTo.X, 1e-3, "Shot.To.X");
        Assert.AreEqual(expectedTo.Y, actualTo.Y, 1e-3, "Shot.To.Y");
        Assert.AreEqual(expectedTo.Z, actualTo.Z, 1e-3, "Shot.To.Z");
    }

    private static Vector3 ToWorld(Vector3 point, ISceneFrame frame) =>
        new((float)(point.X + frame.Origin.X), (float)(point.Y + frame.Origin.Y), (float)(point.Z + frame.Origin.Z));

    [TestMethod]
    public async Task ChangingViewDoesNotReplayCues()
    {
        var content = RailwayFixtures.Content(design: RailwayFixtures.Vehicle());
        var instance = RailwayFixtures.Instance();
        await using var session = Open(content, instance);
        session.RequestView(new WorldSceneTarget.Location(RailwayFixtures.Location));
        await RailwayFixtures.Settled(session, Request);
        var armed = Snapshot(content, instance, steps: 1);
        session.Observe(armed, [Envelope(armed, new CoreEvent.CombatHits(new(1), RailwayFixtures.Location, [Hit(instance)]))]);
        session.RequestView(new WorldSceneTarget.Location(new WorldLocation.Node(new(2))));
        Assert.IsEmpty(Present(session).Update.Effects);
        session.RequestView(new WorldSceneTarget.Location(RailwayFixtures.Location));
        var returned = await RailwayFixtures.Settled(session, new(1, null, SceneDebugLayer.None));
        Assert.IsEmpty(returned.Update.Effects);

        await using var followed = Open(content, instance);
        followed.RequestView(new WorldSceneTarget.Follow(new(1), new(1)));
        await RailwayFixtures.Settled(followed, Request);
        var onEdge = Snapshot(content, instance, steps: 1);
        followed.Observe(onEdge, [Envelope(onEdge, new CoreEvent.CombatHits(new(1), RailwayFixtures.Location, [Hit(instance)]))]);
        var parked = RailwayFixtures.Train(0) with
        {
            Position = new TrainPosition.Node(new(1), null, null, null, null, NodeTravelPhase.Docked, 0, null)
        };
        followed.Observe(RailwayFixtures.Snapshot(4, 4, 0, steps: 2, content: content, trains: [parked], instances: [instance]), []);
        Assert.IsEmpty(Present(followed).Update.Effects);
    }

    [TestMethod]
    public async Task CuesObservedWhileLoadingAppearOnceTheSceneIsReady()
    {
        var content = RailwayFixtures.Content(design: RailwayFixtures.Vehicle());
        var instance = RailwayFixtures.Instance();
        await using var session = Open(content, instance);
        session.RequestView(new WorldSceneTarget.Location(RailwayFixtures.Location));
        var armed = Snapshot(content, instance, steps: 1);
        session.Observe(armed, [Envelope(armed, new CoreEvent.CombatHits(new(1), RailwayFixtures.Location, [Hit(instance)]))]);
        var frame = Present(session);
        while (frame.Readiness != SceneReadiness.Ready)
        {
            Assert.IsEmpty(frame.Update.Effects);
            await SceneFixtures.AwaitPendingWork(session.PendingNodeBuilds.Concat(session.PendingRailwayBuilds)
                .Concat(session.PendingObjectBuilds).ToArray());
            frame = Present(session);
        }
        Assert.IsTrue(frame.Update.Effects.Any(item => item.Kind == SceneEffectKind.Shot));
    }

    [TestMethod]
    public async Task CreatureEyeDamageFollowsIndividualHealthWithoutRebuildingAppearanceGeometry()
    {
        var creature = new CreatureDefinitionId("test:beast");
        var eye = new VoxelDefinitionId("test:eye");
        var content = RailwayFixtures.Content(design: RailwayFixtures.Vehicle(), creatures: new Dictionary<CreatureDefinitionId, ContentLookup<CreatureAppearanceProjection>>
        {
            [creature] = new ContentLookup<CreatureAppearanceProjection>.Found(new(creature,
                SceneFixtures.Projection([SceneFixtures.Cell(new(0, 0, 0)) with { Voxel = eye }]), new("Beast", "")))
        }, voxels: new Dictionary<VoxelDefinitionId, ContentLookup<VoxelAppearanceProjection>>
        { [eye] = new ContentLookup<VoxelAppearanceProjection>.Found(SceneFixtures.Cube with
            { Definition = eye, Behaviors = new(false, new(new("#ffffff"), 4)) }) });
        var group = new CreatureGroupView(new(7), null, RailwayFixtures.Location, null, CreatureActivity.Guarding, 0,
            [new(new(3), creature, 100, 100), new(new(4), creature, 25, 100)]);
        var instance = RailwayFixtures.Instance();
        await using var session = Open(content, instance, group);
        session.RequestView(new WorldSceneTarget.Location(RailwayFixtures.Location));
        var settled = await RailwayFixtures.Settled(session, Request);
        SceneLight Eye(SceneFrame frame, ulong id) => frame.Objects.Single(item =>
            item.Subject == new WorldObjectId.Creature(new(id)) && !item.Lights.IsEmpty).Lights.Single();
        SceneMesh Mesh(SceneFrame frame, ulong id) => frame.Objects.Where(item =>
            item.Subject == new WorldObjectId.Creature(new(id))).SelectMany(item => item.Visuals)
            .OfType<SceneVisual.Geometry>().Single().Mesh;
        Assert.AreEqual(0, Eye(settled, 3).Damage);
        Assert.AreEqual(0, Eye(settled, 4).Damage);
        Assert.AreNotEqual(Eye(settled, 3).Seed, Eye(settled, 4).Seed);
        Assert.AreSame(Mesh(settled, 3), Mesh(settled, 4));
        var hurt = group with { Creatures = [new(new(3), creature, 10, 100), new(new(4), creature, 50, 100)] };
        session.Observe(Snapshot(content, instance, steps: 1, group: hurt), []);
        var damaged = Present(session, settled);
        Assert.AreEqual(.6f, Eye(damaged, 3).Damage, 1e-5);
        Assert.AreEqual(0, Eye(damaged, 4).Damage);
        Assert.AreEqual(Eye(settled, 3).Seed, Eye(damaged, 3).Seed);
        Assert.AreSame(Mesh(settled, 3), Mesh(damaged, 3));
        var healed = group with { Creatures = [new(new(3), creature, 100, 100), new(new(4), creature, 0, 100)] };
        var dead = Snapshot(content, instance, steps: 2, group: healed);
        session.Observe(dead, [Envelope(dead, new CoreEvent.CombatDestruction(new(1), RailwayFixtures.Location, [],
            [new DestroyedUnit.Creature(new(group.Id, new(4), creature, RailwayFixtures.Location))]))]);
        var final = Present(session, damaged);
        Assert.AreEqual(0, Eye(final, 3).Damage);
        Assert.IsFalse(final.Objects.Any(item => item.Subject == new WorldObjectId.Creature(new(4))));
        Assert.AreSame(Mesh(settled, 3), Mesh(final, 3));
        await using var reopened = Open(content, instance, group);
        reopened.RequestView(new WorldSceneTarget.Location(RailwayFixtures.Location));
        var cold = await RailwayFixtures.Settled(reopened, Request);
        Assert.AreEqual(Eye(settled, 3).Seed, Eye(cold, 3).Seed);
    }

    [TestMethod]
    public async Task CreatureAppearanceComesFromThePairedContentViewAndDeathUsesTheEventDefinition()
    {
        var creature = new CreatureDefinitionId("test:beast");
        var other = new CreatureDefinitionId("test:other");
        var eye = new VoxelDefinitionId("test:eye");
        var content = RailwayFixtures.Content(design: RailwayFixtures.Vehicle(), creatures: new Dictionary<CreatureDefinitionId, ContentLookup<CreatureAppearanceProjection>>
        {
            [creature] = new ContentLookup<CreatureAppearanceProjection>.Found(new(creature,
                SceneFixtures.Projection([SceneFixtures.Cell(new(0, 0, 0)) with { Paint = new("red") },
                    SceneFixtures.Cell(new(-1, 1, 0)) with { Voxel = eye },
                    SceneFixtures.Cell(new(1, 1, 0)) with { Voxel = eye }]), new("Beast", ""))),
            [other] = new ContentLookup<CreatureAppearanceProjection>.Found(new(other,
                SceneFixtures.Projection([SceneFixtures.Cell(new(0, 0, 0)) with { Paint = new("blue") }]), new("Other", "")))
        }, voxels: new Dictionary<VoxelDefinitionId, ContentLookup<VoxelAppearanceProjection>>
        { [eye] = new ContentLookup<VoxelAppearanceProjection>.Found(SceneFixtures.Cube with
            { Definition = eye, Behaviors = new(false, new(new("#ffffff"), 4)) }) });
        var group = new CreatureGroupView(new(7), null, RailwayFixtures.Location, null, CreatureActivity.Guarding, 0,
            [new(new(3), creature, 10, 10)]);
        var instance = RailwayFixtures.Instance();
        await using var session = Open(content, instance, group);
        session.RequestView(new WorldSceneTarget.Location(RailwayFixtures.Location));
        var settled = await RailwayFixtures.Settled(session, Request);
        Assert.IsTrue(settled.Objects.Any(item => item.Subject == new WorldObjectId.Creature(new(3))));
        var eyes = settled.Objects.Where(item => item.Subject == new WorldObjectId.Creature(new(3)) && !item.Lights.IsEmpty).ToArray();
        Assert.AreEqual(2, eyes.Length);
        var body = settled.Objects.Single(item => item.Subject == new WorldObjectId.Creature(new(3)) && item.Parent is null);
        Assert.IsTrue(eyes.All(item => item.Parent == body.Id && item.Lights.Single().Damage == 0));
        Assert.AreEqual(eyes.Length, eyes.Select(item => item.Lights.Single().Seed).Distinct().Count(),
            "同一生物不同格子的眼灯不能共用闪烁种子。");
        CollectionAssert.AreEquivalent(new[] { new Vector3(-.5f, 1.5f, .5f), new Vector3(1.5f, 1.5f, .5f) },
            eyes.Select(item => item.Pose.Position).ToArray());
        var dead = Snapshot(content, instance, steps: 1, group: group with { Creatures = [] });
        session.Observe(dead, [Envelope(dead, new CoreEvent.CombatDestruction(new(1), RailwayFixtures.Location, [],
            [new DestroyedUnit.Creature(new(group.Id, new(3), other, RailwayFixtures.Location))]))]);
        var frame = Present(session, settled);
        Assert.IsFalse(frame.Objects.Any(item => item.Subject == new WorldObjectId.Creature(new(3))));
        Assert.AreEqual(new PaintColor("blue"), frame.Update.Effects.Single(item => item.Kind == SceneEffectKind.Fragments).Color);
    }

    [TestMethod]
    public async Task CreaturesStaggerAndPounceTowardTheTarget()
    {
        var creature = new CreatureDefinitionId("test:beast");
        var content = RailwayFixtures.Content(design: RailwayFixtures.Vehicle(), creatures: new Dictionary<CreatureDefinitionId, ContentLookup<CreatureAppearanceProjection>>
        {
            [creature] = new ContentLookup<CreatureAppearanceProjection>.Found(new(creature,
                SceneFixtures.Projection([SceneFixtures.Cell(new(0, 0, 0))]), new("Beast", "")))
        });
        var group = new CreatureGroupView(new(7), null, RailwayFixtures.Location, null, CreatureActivity.Guarding, 0,
            [new(new(3), creature, 10, 10), new(new(4), creature, 10, 10)]);
        var instance = RailwayFixtures.Instance();
        await using var session = Open(content, instance, group);
        session.RequestView(new WorldSceneTarget.Location(RailwayFixtures.Location));
        var settled = await RailwayFixtures.Settled(session, Request);
        var first = CreatureAt(settled, 3);
        var second = CreatureAt(settled, 4);
        var separated = Vector3.Distance(first, second);
        Assert.IsTrue(Math.Abs(separated - 8) < 0.05 || Math.Abs(separated - 24) < 0.05);
        var individual = new CreaturePresentationRef(group.Id, new(3), creature, RailwayFixtures.Location);
        var armed = Snapshot(content, instance, steps: 1, group: group);
        session.Observe(armed, [Envelope(armed, new CoreEvent.CombatHits(new(1), RailwayFixtures.Location,
            [new CombatHit(new AttackSource.Creature(individual),
                new HitTarget.Part(new(instance.Id, instance.Design, RailwayFixtures.Location), new(1)), 1)]))]);
        session.Observe(Snapshot(content, instance, steps: 2, group: group), []);
        var frame = Present(session, settled);
        var moved = CreatureAt(frame, 3);
        var vehicle = frame.Objects.Single(item => item.Parent is null && item.Subject == new WorldObjectId.Instance(instance.Id)).Pose.Position;
        var delta = new Vector3(moved.X - first.X, 0, moved.Z - first.Z);
        var toward = new Vector3(vehicle.X - first.X, 0, vehicle.Z - first.Z);
        Assert.IsTrue(delta.Length() > 0.05f);
        Assert.IsTrue(Vector3.Dot(Vector3.Normalize(delta), Vector3.Normalize(toward)) > 0.9f);
    }

    [TestMethod]
    public async Task ADozenCreaturesDoNotOverlapOnAnEdge()
    {
        var (content, group) = Dozen(RailwayFixtures.Location, views =>
            RailwayFixtures.Content(design: RailwayFixtures.Vehicle(), creatures: views));
        AssertSeparated(await EdgeFrame(content, group, false), 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12);
        AssertSeparated(await EdgeFrame(content, group, true), 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12);
    }

    [TestMethod]
    public async Task ADozenCreaturesDoNotOverlapAtANode()
    {
        var (content, group) = Dozen(NodeFixtures.Location, views => NodeFixtures.Content(creatures: views));
        AssertSeparated(await NodeFrame(content, group, false), 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12);
        AssertSeparated(await NodeFrame(content, group, true), 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12);
    }

    [TestMethod]
    public async Task ADeadMemberKeepsItsPlaceBetweenTheLiving()
    {
        var large = new CreatureDefinitionId("test:large");
        var content = RailwayFixtures.Content(design: RailwayFixtures.Vehicle(), creatures: new Dictionary<CreatureDefinitionId, ContentLookup<CreatureAppearanceProjection>>
        { [large] = new ContentLookup<CreatureAppearanceProjection>.Found(Footprint(large, -5, 4, -7, 6)) });
        // 种子 3、非战斗：1、2、4 都在正侧，2 夹在 1 和 4 之间。
        var group = new CreatureGroupView(new(7), null, RailwayFixtures.Location, null, CreatureActivity.Guarding, 0,
            [new(new(1), large, 10, 10), new(new(2), large, 10, 10), new(new(4), large, 10, 10)]);
        var instance = RailwayFixtures.Instance();
        await using var session = Open(content, instance, group);
        session.RequestView(new WorldSceneTarget.Location(RailwayFixtures.Location));
        var settled = await RailwayFixtures.Settled(session, Request);
        var snapshot = Snapshot(content, instance, group: group);
        session.Observe(snapshot, [Envelope(snapshot, new CoreEvent.CombatDestruction(new(1), RailwayFixtures.Location, [],
            [new DestroyedUnit.Creature(new(group.Id, new(2), large, RailwayFixtures.Location))]))]);
        var frame = Present(session, settled);
        AssertSeparated(frame, [1, 4]);
        Assert.IsFalse(frame.Objects.Any(item => item.Subject is WorldObjectId.Creature creature && creature.Id.Value == 2));
        var fragments = frame.Update.Effects.Single(item => item.Kind == SceneEffectKind.Fragments);
        Assert.AreEqual(new WorldObjectId.Creature(new(2)), fragments.Subject);
        var point = fragments.From;
        Assert.IsFalse(Inside(CreatureMeshBounds(frame, 1), point));
        Assert.IsFalse(Inside(CreatureMeshBounds(frame, 4), point));
    }

    [TestMethod]
    public async Task TwoGroupsAtOneEdgeDoNotOverlap()
    {
        var large = new CreatureDefinitionId("test:large");
        var content = RailwayFixtures.Content(design: RailwayFixtures.Vehicle(), creatures: new Dictionary<CreatureDefinitionId, ContentLookup<CreatureAppearanceProjection>>
        { [large] = new ContentLookup<CreatureAppearanceProjection>.Found(Footprint(large, -5, 4, -7, 6)) });
        CreatureGroupView Line(ulong id, ulong first) => new(new(id), null, RailwayFixtures.Location, null, CreatureActivity.Guarding, 0,
            Enumerable.Range(0, 4).Select(offset => new CreatureView(new(first + (ulong)offset), large, 10, 10)).ToImmutableArray());
        var groups = new[] { Line(7, 1), Line(8, 5) };
        var snapshot = RailwayFixtures.Snapshot(4, 4, 0, content: content, trains: [RailwayFixtures.Train(0, [new(1)])],
            instances: [RailwayFixtures.Instance()], groups: groups);
        await using var session = new WorldSceneSession(new(Guid.NewGuid()), snapshot, new(), new());
        session.RequestView(new WorldSceneTarget.Location(RailwayFixtures.Location));
        AssertSeparated(await RailwayFixtures.Settled(session, Request), [1, 2, 3, 4, 5, 6, 7, 8]);
    }

    [TestMethod]
    public async Task HitsAndPounceStayOnTheTargetedCreature()
    {
        var large = new CreatureDefinitionId("test:large");
        var content = RailwayFixtures.Content(design: RailwayFixtures.Vehicle(), creatures: new Dictionary<CreatureDefinitionId, ContentLookup<CreatureAppearanceProjection>>
        { [large] = new ContentLookup<CreatureAppearanceProjection>.Found(Footprint(large, -5, 4, -7, 6)) });
        var group = new CreatureGroupView(new(7), null, RailwayFixtures.Location, null, CreatureActivity.Guarding, 0,
            [new(new(1), large, 10, 10), new(new(2), large, 10, 10)]);
        var instance = RailwayFixtures.Instance();
        await using var session = Open(content, instance, group);
        session.RequestView(new WorldSceneTarget.Location(RailwayFixtures.Location));
        var settled = await RailwayFixtures.Settled(session, Request);
        var attacker = new CreaturePresentationRef(group.Id, new(1), large, RailwayFixtures.Location);
        var target = new CreaturePresentationRef(group.Id, new(2), large, RailwayFixtures.Location);
        var armed = Snapshot(content, instance, steps: 1, group: group);
        session.Observe(armed, [Envelope(armed, new CoreEvent.CombatHits(new(1), RailwayFixtures.Location,
            [new CombatHit(new AttackSource.Creature(attacker), new HitTarget.Creature(target), 1)]))]);
        session.Observe(Snapshot(content, instance, steps: 2, group: group), []);
        var frame = Present(session, settled);
        var effects = frame.Update.Effects.Where(item => item.Kind is SceneEffectKind.Shot or SceneEffectKind.Impact).ToArray();
        Assert.IsTrue(effects.Any(item => item.Kind == SceneEffectKind.Shot));
        Assert.IsTrue(effects.Any(item => item.Kind == SceneEffectKind.Impact));
        var roots = new[] { (Id: 1ul, Position: CreatureAt(frame, 1)), (Id: 2ul, Position: CreatureAt(frame, 2)) };
        foreach (var effect in effects)
        {
            Assert.AreEqual(new WorldObjectId.Creature(new(2)), effect.Subject);
            var point = effect.Kind == SceneEffectKind.Shot ? effect.To!.Value : effect.From;
            Assert.AreEqual(2ul, roots.MinBy(item => Vector3.Distance(item.Position, point)).Id);
        }
        var before = CreatureAt(settled, 1);
        var delta = new Vector3(roots[0].Position.X - before.X, 0, roots[0].Position.Z - before.Z);
        Assert.IsTrue(delta.Length() > 0.05f);
        var toward = Vector3.Normalize(new Vector3(roots[1].Position.X - before.X, 0, roots[1].Position.Z - before.Z));
        Assert.IsTrue(Vector3.Dot(Vector3.Normalize(delta), toward) > 0.9f);
    }

    [TestMethod]
    public void NodeCreatureHeightUsesALoadedColumn()
    {
        var snapshot = NodeFixtures.Snapshot(platforms: 0, trains: [], instances: []);
        var layout = NodeLayout.Build(snapshot, NodeFixtures.NodeId);
        var prepared = NodeScene.Prepare(snapshot, layout, null, default);
        var chunk = prepared.BuildChunk(new((int)layout.Bounds.Minimum.X, (int)layout.Bounds.Minimum.Y));
        var column = chunk.Heights.First();
        var x = chunk.Origin.X + column.Key.X + 0.5;
        var z = chunk.Origin.Z + column.Key.Z + 0.5;
        Assert.AreEqual(column.Value, NodeScene.GroundHeight([chunk], x, z), 1e-6);
        Assert.AreEqual(0, NodeScene.GroundHeight([chunk], x + 10000, z));
    }

    private static Vector3 CreatureAt(SceneFrame frame, ulong id) =>
        frame.Objects.Single(item => item.Parent is null && item.Subject == new WorldObjectId.Creature(new(id))).Pose.Position;

    private static (FixedContentView Content, CreatureGroupView Group) Dozen(WorldLocation location,
        Func<Dictionary<CreatureDefinitionId, ContentLookup<CreatureAppearanceProjection>>, FixedContentView> content)
    {
        var small = new CreatureDefinitionId("test:small");
        var large = new CreatureDefinitionId("test:large");
        var views = new Dictionary<CreatureDefinitionId, ContentLookup<CreatureAppearanceProjection>>
        {
            [small] = new ContentLookup<CreatureAppearanceProjection>.Found(Footprint(small, -3, 2, -3, 2)),
            [large] = new ContentLookup<CreatureAppearanceProjection>.Found(Footprint(large, -5, 4, -7, 6))
        };
        // 种子 3、战斗 0 和 1 下 9 与 10 同侧，且 9 更短、紧排在 10 前；固定 8 格或只加前一只长度都会相交。
        var members = Enumerable.Range(1, 12).Select(number =>
            new CreatureView(new((ulong)number), number == 9 ? small : large, 10, 10)).ToImmutableArray();
        return (content(views), new(new(7), null, location, null, CreatureActivity.Guarding, 0, members));
    }

    private static CreatureAppearanceProjection Footprint(CreatureDefinitionId id, int minX, int maxX, int minZ, int maxZ) =>
        new(id, SceneFixtures.Projection([
            SceneFixtures.Cell(new(minX, 0, minZ)),
            SceneFixtures.Cell(new(maxX, 0, maxZ), 2)]) with
        {
            OccupiedBounds = new(new(minX, 0, minZ), new(maxX, 0, maxZ))
        }, new(id.Value, ""));

    private static async Task<SceneFrame> EdgeFrame(FixedContentView content, CreatureGroupView group, bool combat)
    {
        var snapshot = RailwayFixtures.Snapshot(4, 4, 0, content: content, trains: [RailwayFixtures.Train(0, [new(1)])],
            instances: [RailwayFixtures.Instance()], groups: [group],
            combats: combat ? [new CombatView(new(1), group.Location, 1, [new(new CombatUnitId.CreatureGroup(group.Id), false, 0, null)])] : null);
        await using var session = new WorldSceneSession(new(Guid.NewGuid()), snapshot, new(), new());
        session.RequestView(new WorldSceneTarget.Location(RailwayFixtures.Location));
        return await RailwayFixtures.Settled(session, Request);
    }

    private static async Task<SceneFrame> NodeFrame(FixedContentView content, CreatureGroupView group, bool combat)
    {
        var snapshot = NodeFixtures.Snapshot(platforms: 0, trains: [], instances: [], content: content, groups: [group],
            combats: combat ? [new CombatView(new(1), group.Location, 1, [new(new CombatUnitId.CreatureGroup(group.Id), false, 0, null)])] : null);
        await using var session = new WorldSceneSession(new(Guid.NewGuid()), snapshot, new(), new(), RailwayFixtures.EmptyNode);
        session.RequestView(new WorldSceneTarget.Location(NodeFixtures.Location));
        return await RailwayFixtures.Settled(session, Request);
    }

    private static void AssertSeparated(SceneFrame frame, params ulong[] ids)
    {
        var bounds = ids.Select(id => CreatureMeshBounds(frame, id)).ToArray();
        for (var left = 0; left < bounds.Length; left++)
            for (var right = left + 1; right < bounds.Length; right++)
            {
                var gapX = Math.Max(bounds[left].Min.X - bounds[right].Max.X, bounds[right].Min.X - bounds[left].Max.X);
                var gapY = Math.Max(bounds[left].Min.Y - bounds[right].Max.Y, bounds[right].Min.Y - bounds[left].Max.Y);
                var gapZ = Math.Max(bounds[left].Min.Z - bounds[right].Max.Z, bounds[right].Min.Z - bounds[left].Max.Z);
                Assert.IsTrue(gapX >= -0.001f || gapY >= -0.001f || gapZ >= -0.001f,
                    $"Creatures {ids[left]} and {ids[right]} overlap.");
            }
    }

    private static (Vector3 Min, Vector3 Max) CreatureMeshBounds(SceneFrame frame, ulong id)
    {
        var root = frame.Objects.Single(item => item.Parent is null && item.Subject == new WorldObjectId.Creature(new(id)));
        var min = new Vector3(float.PositiveInfinity);
        var max = new Vector3(float.NegativeInfinity);
        foreach (var vertex in root.Visuals.OfType<SceneVisual.Geometry>().SelectMany(item => item.Mesh.Vertices))
        {
            var point = root.Pose.Position + Vector3.Transform(vertex.Position, root.Pose.Rotation);
            min = Vector3.Min(min, point);
            max = Vector3.Max(max, point);
        }
        return (min, max);
    }

    private static bool Inside((Vector3 Min, Vector3 Max) box, Vector3 point) =>
        point.X > box.Min.X && point.X < box.Max.X &&
        point.Y > box.Min.Y && point.Y < box.Max.Y &&
        point.Z > box.Min.Z && point.Z < box.Max.Z;

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
        return RailwayFixtures.Snapshot(4, 4, 0, steps: steps, content: content, trains: [train], instances: [instance],
            groups: group is null ? null : [group]);
    }

    private static SceneFrame Present(WorldSceneSession session, ISceneFrame? displayed = null, SceneFrameRequest? request = null)
    {
        var result = session.Present(request ?? Request, displayed);
        Assert.IsInstanceOfType<SceneResult<ISceneFrame>.Success>(result, result.ToString());
        return (SceneFrame)((SceneResult<ISceneFrame>.Success)result).Value;
    }

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

}
