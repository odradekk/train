using System.Collections.Immutable;
using System.Numerics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using Train.Scenes.Contracts;

namespace Train.Scenes.Tests;

[TestClass]
public sealed class RailwayGenerationTests
{
    [TestMethod]
    public void HighDetourProductionSegmentMapsGeneratedTerrainHeightLocationAndPickConsistently()
    {
        var snapshot = RailwayFixtures.Snapshot(600, 28.224, relief: 0, tracks: 1, trackLimit: 3);
        var path = new RailwayPath(snapshot, RailwayFixtures.Edge);
        var segment = RailwayTerrain.Build(snapshot, path, path.SegmentCount - 1, CancellationToken.None);
        var frame = new SceneFrame(new(new(Guid.NewGuid()), new(1), new(1), 1), segment.Origin,
            0, SceneReadiness.Ready, segment.Geometry.Objects, segment.Geometry.Picking,
            new([], []), segment.Geometry.Diagnostics, railway: path, segments: [segment]);
        var cell = segment.Geometry.Picking.Single().Grid.Cells.First(cell =>
        {
            var arc = ((WorldLocation.Edge)((PickSubject.Terrain)cell.Subject!).Location).DistanceMeters * 2;
            var center = RailwayCoordinates.Add(segment.Origin, new Vector3(cell.At.X + .5f, 0, cell.At.Z + .5f));
            var sample = path.Sample(arc);
            var lateral = (center.X - sample.Position.X) * sample.Right.X +
                (center.Z - sample.Position.Z) * sample.Right.Z;
            return arc > 1160 && arc < 1190 && Math.Abs(lateral) > 25 &&
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
        Assert.IsTrue(Math.Abs(path.Sample(1170).Forward.X) > .1,
            "This production segment must exercise a bend, rather than only the endpoint straight.");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ProductionTerrainPickAtLoadedSegmentBoundaryIsNotHiddenByUnloadedSegmentFog(bool diagonal)
    {
        var snapshot = RailwayFixtures.Snapshot(600, 600, relief: 0);
        if (diagonal)
        {
            var nodes = snapshot.Core.World.Nodes;
            var end = nodes[1] with { Position = new(360, 480) };
            snapshot = snapshot with { Core = new FixedCoreReadView
            {
                Position = snapshot.Core.Position,
                World = snapshot.Core.World with { Nodes = [nodes[0], end] },
                Edges = new Dictionary<EdgeId, Lookup<EdgeView>> { [RailwayFixtures.Edge] = snapshot.Core.FindEdge(RailwayFixtures.Edge) },
                Nodes = new Dictionary<NodeId, Lookup<NodeView>>
                {
                    [nodes[0].Id] = snapshot.Core.FindNode(nodes[0].Id),
                    [end.Id] = new Lookup<NodeView>.Found(new(end, [RailwayFixtures.Edge]))
                }
            } };
        }
        ulong nextId = 0;
        await using var scene = new RailwayScene(snapshot, RailwayFixtures.Edge, new(), _ => new(++nextId),
            (input, path, index, token) => index == 2 ? RailwayTerrain.Build(input, path, index, token) :
                new(index, path.Sample(index * 128).Position, new([], [], []),
                    ImmutableDictionary<GridColumn, double>.Empty,
                    RailwayDependencies.Capture(input, path, index, [], [], [], [], false, false), []));
        var target = new WorldSceneTarget.Location(new WorldLocation.Edge(RailwayFixtures.Edge, 256));
        var request = new SceneFrameRequest(1, null, SceneDebugLayer.None);
        var session = new SceneSessionId(Guid.NewGuid());
        ulong revision = 0;
        SceneFrame frame;
        do
        {
            await Task.WhenAll(scene.PendingWork);
            frame = scene.Present(snapshot, null, target, request, new(session, new(1), new(1), ++revision), null);
        } while (scene.PendingWork.Length > 0);
        Assert.AreEqual(SceneReadiness.Ready, frame.Readiness);
        var loaded = RailwayCoordinates.Relative(Point(280.5), frame.Origin);
        var hit = frame.Pick(new(loaded, -Vector3.UnitY, 30));
        Assert.IsInstanceOfType<ScenePick.Hit>(hit,
            "Fog for the preceding unloaded segments must not cover segment 2's generated terrain.");
        Assert.IsInstanceOfType<PickSubject.Terrain>(((ScenePick.Hit)hit).Value.Subject);
        var unloaded = RailwayCoordinates.Relative(Point(100.5), frame.Origin);
        Assert.IsInstanceOfType<ScenePick.NotReady>(frame.Pick(new(unloaded, -Vector3.UnitY, 30)));
        ScenePoint Point(double forward) => diagonal ? new(20.5 * .8 + forward * .6, 10, -20.5 * .6 + forward * .8) :
            new(20.5, 10, forward);
    }

    [TestMethod]
    public void UnloadedCurveOuterExtremeRemainsFoggedBetweenBoundingSamples()
    {
        var path = new RailwayPath(RailwayFixtures.Snapshot(600, 28.224, relief: 0, tracks: 3, trackLimit: 3), RailwayFixtures.Edge);
        const double radius = 80;
        const double width = 91;
        var transverse = (1200 - 2 * Math.PI * radius) / 2;
        var side = Math.Sign(path.Sample(100).Position.X);
        // The middle semicircle's outer extreme is at arc 600, between the old 51-cell samples.
        var outer = new Vector3((float)(side * (transverse + 2 * radius + width - .01)), 10, 160);
        var frame = new SceneFrame(new(new(Guid.NewGuid()), new(1), new(1), 1), new(0, 0, 0),
            0, SceneReadiness.Partial, [], [], new([path.Bounds(384, 1200, new(0, 0, 0), width)], []), [],
            railway: path, railwayFog: [(384, 1200)]);
        Assert.IsInstanceOfType<ScenePick.NotReady>(frame.Pick(new(outer, -Vector3.UnitY, 30)));
    }

    [TestMethod]
    public void HorizontalRayThroughUnloadedCurveCannotPickKnownCubeBehindIt()
    {
        var path = new RailwayPath(RailwayFixtures.Snapshot(600, 28.224, relief: 0, tracks: 3, trackLimit: 3), RailwayFixtures.Edge);
        var transverse = (1200 - 2 * Math.PI * 80) / 2;
        var side = Math.Sign(path.Sample(100).Position.X);
        var geometry = SceneFixtures.Build(SceneFixtures.Projection([SceneFixtures.Cell(new(0, 0, 20))]));
        var start = new Vector3((float)(side * (transverse + 160 + 91 + 20)), .5f, 160);
        var ray = new SceneRay(start, Vector3.Normalize(new Vector3(.5f, .5f, 20.5f) - start), 1000);
        Assert.IsInstanceOfType<ScenePick.Hit>(SceneFixtures.Frame(geometry).Pick(ray),
            "The known cube must be reachable by this ray before adding the intervening fog.");
        var frame = new SceneFrame(new(new(Guid.NewGuid()), new(1), new(1), 1), new(0, 0, 0),
            0, SceneReadiness.Partial, geometry.Objects, geometry.Picking,
            new([path.Bounds(384, 1200, new(0, 0, 0), 91)], []), [],
            railway: path, railwayFog: [(384, 1200)]);
        Assert.IsInstanceOfType<ScenePick.NotReady>(frame.Pick(ray),
            "The ray crosses the unloaded middle bend before reaching the cube near the loaded start.");
    }
}
