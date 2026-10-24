using System.Numerics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes.Tests;

[TestClass]
public sealed class ConstructionFogTests
{
    [TestMethod]
    [DataRow(224, 256)]
    [DataRow(256, 224)]
    public async Task Refreshing_layout_bounds_updates_terrain_fog_without_moving_the_camera(int before, int after)
    {
        SceneWorldSnapshot Snapshot(int extent)
        {
            var template = NodeFixtures.Layout with { Plots = [] };
            template = template with { SceneModel = template.SceneModel with
            { Scene = SceneFixtures.Projection([]) with { OccupiedBounds = new(new(-extent, 0, -extent), new(extent - 1, 0, extent - 1)) } } };
            return NodeFixtures.Snapshot(platforms: 0, trains: [], content: NodeFixtures.Content(layout: template,
                rules: RailwayFixtures.Rules with { MinimumCurveRadiusMeters = 1 }));
        }
        var initial = Snapshot(before);
        var changed = Snapshot(after);
        var edge = changed.Core.World.Edges[0];
        changed = RailwayFixtures.ReplaceCore(changed, edge: edge with { Tracks = 0 });
        await using var vehicles = new SceneVehicles();
        await using var scene = new NodeScene(initial, NodeFixtures.NodeId, new(), SceneFixtures.NextId(), vehicles, new(), _ => null,
            buildChunk: (_, _, _, token) => Wait(token), buildRail: (_, _, _, token) => Wait(token));
        await scene.BuildWork!;
        scene.Pump(allowBuild: false);
        var oldBounds = scene.Layout!.Bounds;
        var oldWindow = new NodeRange(default, NodeStreaming.RetainCells).Window(scene.Layout);
        var first = scene.Present(initial, new(1, null, SceneDebugLayer.None), NodeFixtures.NextStamp(scene), null, default);
        var pending = scene.PendingWork.ToHashSet();
        scene.Pump(changed, allowBuild: false);
        await Task.WhenAll(scene.PendingWork.Where(task => !pending.Contains(task)));
        var next = scene.Present(changed, new(1, null, SceneDebugLayer.None), NodeFixtures.NextStamp(scene), first, default);
        var newBounds = scene.Layout!.Bounds;
        Assert.AreNotEqual(oldBounds, newBounds, "The production refresh must adopt a different terrain boundary.");
        Assert.AreEqual(oldWindow, new NodeRange(default, NodeStreaming.RetainCells).Window(scene.Layout),
            "Both layouts must extend beyond the unchanged terrain fog window.");
        var probe = new ScenePoint((oldBounds.Maximum.X + newBounds.Maximum.X) / 2, 0, 0);
        Assert.AreEqual(before > after, first.Update.Environment.FoggedRegions.Any(box => Contains(box, probe)));
        Assert.AreEqual(after > before, next.Update.Environment.FoggedRegions.Any(box => Contains(box, probe)),
            "Terrain fog must follow the adopted layout boundary even when no terrain rows or camera inputs changed.");

        static NodeScene.Part Wait(CancellationToken token)
        {
            token.WaitHandle.WaitOne();
            token.ThrowIfCancellationRequested();
            throw new InvalidOperationException();
        }
    }

    [TestMethod]
    [DataRow(384d, 512d)]
    [DataRow(384d, 384.2d)]
    public void AttachedRailFootprintIncludesSleepersBeyondBothArcEnds(double begin, double end)
    {
        RailwaySample Sample(double arc) => new(new(0, 0, arc), Vector3.UnitZ, Vector3.UnitX);
        var segment = new NodeScene.RailSegment("rail", Sample, begin, end,
            new(new(-11, -11, (float)begin - 11), new(11, 11, (float)end + 11)), "inputs");
        SceneBounds[] missing = [new(new(-20, -10, (float)begin - 2), new(20, 10, (float)begin)),
            new(new(-20, -10, (float)end), new(20, 10, (float)end + 2))];
        var fog = ConstructionFog.Clip(missing, segment.Footprint);
        var vertices = segment.Build().Geometry.Geometry.Objects.SelectMany(item => item.Visuals)
            .OfType<SceneVisual.Geometry>().SelectMany(visual => visual.Mesh.Vertices)
            .Where(vertex => vertex.Position.Z < begin || vertex.Position.Z > end).ToArray();
        Assert.IsTrue(vertices.Length > 0, "The actual mesh must extend beyond the arc range.");
        foreach (var vertex in vertices)
            Assert.IsFalse(fog.Any(box => Contains(box, new(vertex.Position.X, vertex.Position.Y, vertex.Position.Z))),
                "Pending neighboring segments must not fog the attached mesh's sleeper overhang.");
        Assert.IsTrue(fog.Any(box => Contains(box, new(0, 0, begin - 1.5))), "The genuinely unbuilt track remains fogged.");
    }

    [TestMethod]
    public void OverlappingMissingBoundsAreUnitedBeforeClippingAndMergedAcrossRows()
    {
        SceneBounds[] missing = [new(new(.25f, -3, .25f), new(6.25f, 7, 10.25f)),
            new(new(4.25f, -3, .25f), new(10.25f, 7, 10.25f))];
        var built = Enumerable.Range(1, 9).Select(z =>
            new SceneBounds(new(4, 0, z), new(6, 1, z + 1))).ToArray();
        var clipped = ConstructionFog.Clip(missing, built);
        Assert.HasCount(4, clipped);
        Assert.AreEqual(100 - 18, clipped.Sum(box =>
            (box.Maximum.X - box.Minimum.X) * (box.Maximum.Z - box.Minimum.Z)));
        Assert.IsTrue(clipped.All(box => box.Minimum.Y == -3 && box.Maximum.Y == 7));
        CollectionAssert.AreEqual(clipped.ToArray(), ConstructionFog.Clip(missing.Reverse(), built.Reverse()).ToArray());
    }

    [TestMethod]
    public void ColumnsMergeRegardlessOfInputOrder()
    {
        GridColumn[] columns = [new(1, 1), new(0, 0), new(1, 0), new(0, 1), new(0, 0)];
        var boxes = ConstructionFog.Columns(columns);
        Assert.HasCount(1, boxes);
        Assert.AreEqual(new SceneBounds(new(0, -128, 0), new(2, 128, 2)), boxes[0]);
        CollectionAssert.AreEqual(boxes.ToArray(), ConstructionFog.Columns(columns.Reverse()).ToArray());
    }

    [TestMethod]
    public void CurvedBuiltBandClearsNeighboringMissingBoundsAndPreservesMissingCenter()
    {
        RailwaySample Sample(double arc)
        {
            var angle = arc / 20;
            return new(new(20 * Math.Sin(angle), 0, 20 * (1 - Math.Cos(angle))),
                new((float)Math.Cos(angle), 0, (float)Math.Sin(angle)),
                new((float)-Math.Sin(angle), 0, (float)Math.Cos(angle)));
        }
        var built = ConstructionFog.Band(Sample, 0, 20, 2, -2, 2);
        var missing = new SceneBounds(new(-3, -10, -3), new(25, 10, 25));
        var clipped = ConstructionFog.Clip([missing], built);
        Assert.IsTrue(clipped.Length > 0);
        foreach (var box in clipped)
        {
            Assert.AreEqual(missing.Minimum.Y, box.Minimum.Y);
            Assert.AreEqual(missing.Maximum.Y, box.Maximum.Y);
            foreach (var occupied in built)
                Assert.IsFalse(box.Minimum.X < occupied.Maximum.X && box.Maximum.X > occupied.Minimum.X &&
                    box.Minimum.Z < occupied.Maximum.Z && box.Maximum.Z > occupied.Minimum.Z);
        }
        Assert.IsTrue(clipped.Any(box => Contains(box, Sample(25).Position)));
        Assert.IsFalse(clipped.Any(box => Contains(box, Sample(10).Position)));
        foreach (var box in built)
        {
            Assert.AreEqual(Math.Floor(box.Minimum.X), box.Minimum.X);
            Assert.AreEqual(Math.Floor(box.Maximum.Z), box.Maximum.Z);
        }
        CollectionAssert.AreEqual(built.ToArray(), ConstructionFog.Band(Sample, 0, 20, 2, -2, 2).ToArray());
    }

    [TestMethod]
    public void DiagonalSweepCoversCellsBetweenSamplesAtGridCorner()
    {
        var direction = Vector3.Normalize(new Vector3(1, 0, 1));
        RailwaySample Sample(double arc) => new(new(.1 + direction.X * arc, 0, .1 + direction.Z * arc),
            direction, new(-direction.Z, 0, direction.X));
        var boxes = ConstructionFog.Band(Sample, 0, 2, .05, -1, 1);
        Assert.IsTrue(boxes.Any(box => Contains(box, new(.8, 0, .8))));
        Assert.IsTrue(boxes.Any(box => Contains(box, new(1.2, 0, 1.2))));
        Assert.IsTrue(boxes.Any(box => box.Minimum.X <= 0 && box.Maximum.X >= 1 &&
            box.Minimum.Z <= 1 && box.Maximum.Z >= 2));
    }

    [TestMethod]
    public void StraightBandMergesRowsAndDoesNotExtendEndCaps()
    {
        RailwaySample Sample(double arc) => new(new(0, 0, arc), Vector3.UnitZ, Vector3.UnitX);
        var boxes = ConstructionFog.Band(Sample, 0, 20, 2, -1, 1);
        Assert.HasCount(1, boxes);
        Assert.AreEqual(new SceneBounds(new(-2, -1, 0), new(2, 1, 20)), boxes[0]);
    }

    private static bool Contains(SceneBounds box, ScenePoint point) => point.X > box.Minimum.X &&
        point.X < box.Maximum.X && point.Z > box.Minimum.Z && point.Z < box.Maximum.Z;
}
