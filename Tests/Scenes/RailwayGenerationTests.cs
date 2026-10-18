using System.Collections.Immutable;
using System.Numerics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes.Tests;

[TestClass]
public sealed class RailwayGenerationTests
{
    [TestMethod]
    public void HighDetourProductionSegmentMapsGeneratedTerrainHeightLocationAndPickConsistently()
    {
        var snapshot = RailwayFixtures.Snapshot(585, 28.224, relief: 0, tracks: 1, trackLimit: 3);
        var path = new RailwayPath(snapshot, RailwayFixtures.Edge);
        var segment = RailwayTerrain.Build(snapshot, path, path.SegmentCount - 1, CancellationToken.None);
        var frame = new SceneFrame(new(new(Guid.NewGuid()), new(1), new(1), 1), segment.Origin,
            0, SceneReadiness.Ready, segment.Geometry.Objects, segment.Geometry.Picking,
            [], segment.Geometry.Diagnostics, railway: path, segments: [segment]);
        var begin = segment.Index * RailwayCoordinates.SegmentLengthCells;
        var cell = segment.Geometry.Picking.Single().Grid.Cells.First(cell =>
        {
            var arc = ((WorldLocation.Edge)((PickSubject.Terrain)cell.Subject!).Location).DistanceMeters * RailwayCoordinates.CellsPerMeter;
            var center = RailwayCoordinates.Add(segment.Origin, new Vector3(cell.At.X + .5f, 0, cell.At.Z + .5f));
            var sample = path.Sample(arc);
            var lateral = (center.X - sample.Position.X) * sample.Right.X +
                (center.Z - sample.Position.Z) * sample.Right.Z;
            return arc > begin + 1 && arc < path.LengthCells - 2 && Math.Abs(lateral) > 25 &&
                cell.At.Y + 1 + segment.Origin.Y == segment.Heights[new(cell.At.X, cell.At.Z)];
        });
        var point = new Vector3(cell.At.X + .5f, cell.At.Y + 1, cell.At.Z + .5f);
        var expected = ((PickSubject.Terrain)cell.Subject!).Location;
        Assert.AreEqual((double)point.Y, ((Lookup<double>.Found)frame.TerrainHeight(point.X, point.Z)).Value);
        var located = (WorldLocation.Edge)((Lookup<WorldLocation>.Found)frame.Locate(point)).Value;
        Assert.AreEqual(((WorldLocation.Edge)expected).DistanceMeters, located.DistanceMeters, 1e-6);
        var hit = (ScenePick.Hit)frame.Pick(new(point + Vector3.UnitY * 10, -Vector3.UnitY, 20));
        Assert.AreEqual(expected, ((PickSubject.Terrain)hit.Value.Subject).Location);
        Assert.AreEqual(point.Y, hit.Value.Point.Y, 1e-5);
        Assert.IsTrue(Math.Abs(path.Sample(located.DistanceMeters * RailwayCoordinates.CellsPerMeter).Forward.X) > .01,
            "This production segment must exercise a bend, rather than only the endpoint straight.");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ProductionTerrainPickAtLoadedSegmentBoundaryIsNotHiddenByUnloadedSegmentFog(bool diagonal)
    {
        var segmentLength = RailwayCoordinates.SegmentLengthCells;
        var boundaryArc = 2 * segmentLength;
        var lengthMeters = (boundaryArc + 2) / RailwayCoordinates.CellsPerMeter;
        var snapshot = RailwayFixtures.Snapshot(lengthMeters, lengthMeters, relief: 0, tracks: 0);
        if (diagonal)
        {
            var end = snapshot.Core.World.Nodes[1] with { Position = new(lengthMeters * .6, lengthMeters * .8) };
            snapshot = RailwayFixtures.ReplaceCore(snapshot, node: end);
        }
        using var release = new ManualResetEventSlim();
        await using var vehicles = new SceneVehicles();
        var scene = RailwayFixtures.Controlled(snapshot, vehicles, (input, path, index, token) =>
        {
            if (index != 2) release.Wait(token);
            return index == 2 ? RailwayTerrain.Build(input, path, index, token) :
                RailwayFixtures.EmptySegment(input, path, index, token);
        });
        var target = new WorldSceneTarget.Location(new WorldLocation.Edge(RailwayFixtures.Edge, boundaryArc / RailwayCoordinates.CellsPerMeter));
        var request = new SceneFrameRequest(1, null, SceneDebugLayer.None);
        var stamp = new SceneFrameStamp(new(Guid.NewGuid()), new(1), new(1), 1);
        try
        {
            await SceneFixtures.AwaitPendingWork(scene.PendingWork);
            scene.Present(snapshot, null, target, request, stamp, null);
            // Only the target segment may finish before the assertion, regardless of the concurrency limit.
            await Task.WhenAny(scene.PendingWork);
            var frame = scene.Present(snapshot, null, target, request, stamp with { Revision = 2 }, null);
            Assert.AreEqual(SceneReadiness.Partial, frame.Readiness);
            var loaded = RailwayCoordinates.Relative(Point(boundaryArc + 1), frame.Origin);
            var hit = frame.Pick(new(loaded, -Vector3.UnitY, 30));
            Assert.IsInstanceOfType<ScenePick.Hit>(hit,
                "Fog for unloaded segment 1 must not cover segment 2's generated terrain.");
            Assert.IsInstanceOfType<PickSubject.Terrain>(((ScenePick.Hit)hit).Value.Subject);
            var boundary = RailwayCoordinates.Relative(Point(boundaryArc), frame.Origin);
            Assert.IsInstanceOfType<ScenePick.Hit>(frame.Pick(new(boundary, -Vector3.UnitY, 30)),
                "An unloaded segment excludes the exact start plane of its loaded neighbor.");
            var unloaded = RailwayCoordinates.Relative(Point(boundaryArc - .5), frame.Origin);
            Assert.IsInstanceOfType<ScenePick.NotReady>(frame.Pick(new(unloaded, -Vector3.UnitY, 30)));
        }
        finally { release.Set(); await scene.DisposeAsync(); }
        // 斜向射线的横向偏移在首帧根空间中可精确表示，避免单精度取整把边界点移入前段。
        ScenePoint Point(double forward) => diagonal ? new(21.25 * .8 + forward * .6, 10, -21.25 * .6 + forward * .8) :
            new(20.5, 10, forward);
    }

    [TestMethod]
    public void UnloadedCurveOuterExtremeRemainsFoggedBetweenBoundingSamples()
    {
        var path = new RailwayPath(RailwayFixtures.Snapshot(600, 28.224, relief: 0, tracks: 3, trackLimit: 3), RailwayFixtures.Edge);
        var begin = path.LengthCells * .32;
        var width = RailwayCoordinates.FogHalfWidth(path.Edge.Tracks);
        var outerSample = OuterExtreme(path, begin, path.LengthCells, width - .01);
        var outer = RailwayCoordinates.Relative(outerSample.Position, new(0, 0, 0)) with { Y = 10 };
        var frame = new SceneFrame(new(new(Guid.NewGuid()), new(1), new(1), 1), new(0, 0, 0),
            0, SceneReadiness.Partial, [], [], [new(path.Bounds(begin, path.LengthCells, new(0, 0, 0), width), new(begin, path.LengthCells, width))], [],
            railway: path);
        Assert.IsInstanceOfType<ScenePick.NotReady>(frame.Pick(new(outer, -Vector3.UnitY, 30)));
    }

    [TestMethod]
    public async Task CurvedGapFogExcludesAttachedColumnsAndReusesTheUnchangedFootprint()
    {
        var snapshot = RailwayFixtures.Snapshot(600, 28.224, relief: 0, tracks: 3, trackLimit: 3);
        var path = new RailwayPath(snapshot, RailwayFixtures.Edge);
        var columns = Enumerable.Range(256, 128).SelectMany(arc => new[] { -2d, 0, 2 }.Select(offset =>
        {
            var point = path.Sample(arc, offset).Position;
            return new GridColumn((int)Math.Floor(point.X), (int)Math.Floor(point.Z));
        })).Distinct().ToImmutableDictionary(column => column, _ => 0d);
        using var release = new ManualResetEventSlim();
        await using var vehicles = new SceneVehicles();
        await using var scene = RailwayFixtures.Controlled(snapshot, vehicles, (input, railway, index, token) =>
        {
            if (index != 2) release.Wait(token);
            var part = RailwayFixtures.EmptySegment(input, railway, index, token);
            return index == 2 ? part with { Origin = default, Heights = columns } : part;
        });
        var target = new WorldSceneTarget.Location(new WorldLocation.Edge(RailwayFixtures.Edge, 128));
        var request = new SceneFrameRequest(1, null, SceneDebugLayer.None);
        var stamp = new SceneFrameStamp(new(Guid.NewGuid()), new(1), new(1), 1);
        try
        {
            scene.Present(snapshot, null, target, request, stamp, null);
            await Task.WhenAny(scene.PendingWork);
            var frame = scene.Present(snapshot, null, target, request, stamp with { Revision = 2 }, null);
            var fog = frame.Update.Environment.FoggedRegions;
            foreach (var column in columns.Keys)
            {
                var x = column.X - frame.Origin.X;
                var z = column.Z - frame.Origin.Z;
                var overlap = fog.FirstOrDefault(box => box.Minimum.X < x + 1 - 1e-4 && box.Maximum.X > x + 1e-4 &&
                    box.Minimum.Z < z + 1 - 1e-4 && box.Maximum.Z > z + 1e-4);
                Assert.IsNull(overlap, $"Fog must exclude column {column} at origin {frame.Origin}; overlap {overlap}.");
            }
            var gap = RailwayCoordinates.Relative(path.Sample(192).Position, frame.Origin);
            Assert.IsTrue(fog.Any(box => gap.X >= box.Minimum.X && gap.X < box.Maximum.X &&
                gap.Z >= box.Minimum.Z && gap.Z < box.Maximum.Z), "The unloaded curve must remain fogged.");
            var unchanged = scene.Present(snapshot, null, target, request, stamp with { Revision = 3 }, frame);
            Assert.AreSame(fog[0], unchanged.Update.Environment.FoggedRegions[0],
                "An unchanged attached set and origin must reuse fog boxes.");
        }
        finally { release.Set(); }
    }

    [TestMethod]
    public void HorizontalRayThroughUnloadedCurveCannotPickKnownCubeBehindIt()
    {
        var path = new RailwayPath(RailwayFixtures.Snapshot(600, 28.224, relief: 0, tracks: 3, trackLimit: 3), RailwayFixtures.Edge);
        var begin = path.LengthCells * .32;
        var width = RailwayCoordinates.FogHalfWidth(path.Edge.Tracks);
        var outer = OuterExtreme(path, begin, path.LengthCells, width);
        var side = Math.Sign(outer.Position.X);
        var geometry = SceneFixtures.Build(SceneFixtures.Projection([SceneFixtures.Cell(new(0, 0, 20))]));
        var start = new Vector3((float)(outer.Position.X + side * 20), .5f, (float)outer.Position.Z);
        var ray = new SceneRay(start, Vector3.Normalize(new Vector3(.5f, .5f, 20.5f) - start), 1000);
        Assert.IsInstanceOfType<ScenePick.Hit>(SceneFixtures.Frame(geometry).Pick(ray),
            "The known cube must be reachable by this ray before adding the intervening fog.");
        var frame = new SceneFrame(new(new(Guid.NewGuid()), new(1), new(1), 1), new(0, 0, 0),
            0, SceneReadiness.Partial, geometry.Objects, geometry.Picking,
            [new(path.Bounds(begin, path.LengthCells, new(0, 0, 0), width), new(begin, path.LengthCells, width))], [],
            railway: path);
        Assert.IsInstanceOfType<ScenePick.NotReady>(frame.Pick(ray),
            "The ray crosses the unloaded middle bend before reaching the cube near the loaded start.");
    }
    private static RailwaySample OuterExtreme(RailwayPath path, double begin, double end, double width) =>
        Enumerable.Range(0, 2001).SelectMany(index => new[]
        {
            path.Sample(begin + (end - begin) * index / 2000, width),
            path.Sample(begin + (end - begin) * index / 2000, -width)
        }).MaxBy(sample => Math.Abs(sample.Position.X));

}
