using System.Collections.Immutable;
using System.Numerics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes.Tests;

[TestClass]
public sealed class NodeOverviewTests
{
    [TestMethod]
    public void Overview_contains_every_template_plot_with_size_and_perimeter_corners()
    {
        var sizes = new[] { (PlotSize.Small, 48, 48), (PlotSize.Medium, 96, 96),
            (PlotSize.Large, 192, 192), (PlotSize.Giant, 384, 384),
            (PlotSize.SmallStation, 192, 112), (PlotSize.LargeStation, 256, 208) };
        var plots = sizes.Select((size, index) => new SettlementPlotLayout(index + 1, size.Item1,
            new(index * 512, 17, 200), index % 2 == 0 ? VoxelOrientation.PxPy : VoxelOrientation.PzPy)).ToImmutableArray();
        var snapshot = NodeFixtures.Snapshot(platforms: 0, trains: [],
            content: NodeFixtures.Content(layout: NodeFixtures.Layout with { Plots = plots }));
        var overview = Frame(NodeLayout.Build(snapshot, NodeFixtures.NodeId), snapshot).NodeOverview!;
        CollectionAssert.AreEquivalent(plots.Select(plot => plot.Number).ToArray(),
            overview.Plots.Select(plot => plot.TemplatePlotNumber).ToArray());
        Assert.IsTrue(overview.Lair.IsEmpty);
        foreach (var (size, index) in sizes.Select((size, index) => (size, index)))
        {
            var source = plots[index];
            var actual = overview.Plots.Single(plot => plot.TemplatePlotNumber == source.Number);
            Assert.AreEqual(source.Size, actual.Size);
            Vector2[] local = [new(-size.Item2 / 2, -size.Item3 / 2), new(size.Item2 / 2, -size.Item3 / 2),
                new(size.Item2 / 2, size.Item3 / 2), new(-size.Item2 / 2, size.Item3 / 2)];
            var expected = local.Select(point => index % 2 == 0
                ? new Vector2(source.At.X + point.X, source.At.Z + point.Y)
                : new Vector2(source.At.X + 1 - point.Y, source.At.Z + point.X)).ToArray();
            Assert.AreEqual(4, actual.Corners.Length);
            Assert.AreEqual(4, actual.Corners.Distinct().Count());
            for (var corner = 0; corner < 4; corner++)
            {
                Assert.IsTrue(expected.Any(point => Vector2.Distance(point, actual.Corners[corner]) < .001f));
                var edge = Vector2.Distance(actual.Corners[corner], actual.Corners[(corner + 1) % 4]);
                Assert.IsTrue(Math.Abs(edge - size.Item2) < .001f || Math.Abs(edge - size.Item3) < .001f);
            }
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Overview_tracks_match_every_centerline_segment_in_the_same_frame(bool emptyNode)
    {
        var snapshot = emptyNode ? RailwayFixtures.Snapshot() : NodeFixtures.Snapshot(tracks: 2);
        var layout = NodeLayout.Build(snapshot, NodeFixtures.NodeId);
        var frame = Frame(layout, snapshot, new(123, 7, -456), SceneDebugLayer.Centerline);
        var overview = frame.NodeOverview!;
        if (emptyNode) Assert.IsTrue(overview.Plots.IsEmpty);
        else Assert.IsFalse(overview.Plots.IsEmpty);
        Assert.IsTrue(overview.Lair.IsEmpty);
        Assert.IsFalse(overview.Tracks.IsEmpty);
        var segments = overview.Tracks.SelectMany(track => track.Zip(track.Skip(1))).ToArray();
        Assert.AreEqual(frame.DebugLines.Length, segments.Length);
        for (var index = 0; index < segments.Length; index++)
        {
            var line = frame.DebugLines[index];
            Assert.AreEqual(new Vector2(line.From.X, line.From.Z), segments[index].First);
            Assert.AreEqual(new Vector2(line.To.X, line.To.Z), segments[index].Second);
        }
    }

    [TestMethod]
    public void Same_layout_and_origin_reuse_overview_and_origin_changes_preserve_old_frames()
    {
        var snapshot = NodeFixtures.Snapshot();
        var layout = NodeLayout.Build(snapshot, NodeFixtures.NodeId);
        var before = Frame(layout, snapshot);
        var overview = before.NodeOverview!;
        var originalCorner = overview.Plots[0].Corners[0];
        var originalTrack = overview.Tracks[0][0];
        Assert.AreSame(overview, Frame(layout, snapshot).NodeOverview);
        var origin = new ScenePoint(256, 41, -512);
        var after = Frame(layout, snapshot, origin);
        var moved = after.NodeOverview!;
        Assert.AreNotSame(overview, moved);
        Assert.AreSame(moved, Frame(layout, snapshot, origin).NodeOverview);
        var shift = new Vector2((float)origin.X, (float)origin.Z);
        Assert.AreEqual(before.NodePanBounds!.Minimum - shift, after.NodePanBounds!.Minimum);
        for (var plot = 0; plot < overview.Plots.Length; plot++)
        for (var corner = 0; corner < overview.Plots[plot].Corners.Length; corner++)
            Assert.IsTrue(Vector2.Distance(overview.Plots[plot].Corners[corner] - shift, moved.Plots[plot].Corners[corner]) < .001f);
        for (var track = 0; track < overview.Tracks.Length; track++)
        for (var point = 0; point < overview.Tracks[track].Length; point++)
            Assert.IsTrue(Vector2.Distance(overview.Tracks[track][point] - shift, moved.Tracks[track][point]) < .001f);
        Assert.AreSame(overview, before.NodeOverview);
        Assert.IsNotNull(before.NodeOverview);
        Assert.AreEqual(originalCorner, before.NodeOverview.Plots[0].Corners[0]);
        Assert.AreEqual(originalTrack, before.NodeOverview.Tracks[0][0]);
        Assert.IsTrue(before.NodeOverview.Lair.IsEmpty);
        Assert.IsTrue(moved.Lair.IsEmpty);
    }

    [TestMethod]
    public void Lair_outline_contains_placed_cells_and_stays_inside_pan_bounds()
    {
        var model = NodeLairTests.Model("test:lair-line", [new(0, 2, 0), new(1, 2, 0), new(2, 2, 0)],
            new(new(0, 0), new(2, 0), 5));
        var snapshot = NodeLairTests.World(NodeLairTests.Found(model));
        var built = NodeLairTests.Build(snapshot);
        var cells = NodeLairTests.LairCells(built).Select(cell =>
        {
            var parts = cell.Split(',');
            return (X: int.Parse(parts[0]), Z: int.Parse(parts[2]));
        }).ToArray();
        Assert.IsTrue(cells.Length > 0);
        var minX = cells.Min(cell => cell.X);
        var maxX = cells.Max(cell => cell.X) + 1;
        var minZ = cells.Min(cell => cell.Z);
        var maxZ = cells.Max(cell => cell.Z) + 1;
        var origin = new ScenePoint(256, 41, -512);
        var first = Frame(built.Layout, snapshot);
        Assert.AreSame(first.NodeOverview, Frame(built.Layout, snapshot).NodeOverview);
        var kept = first.NodeOverview!.Lair.ToArray();
        var shifted = Frame(built.Layout, snapshot, origin);
        Assert.AreSame(shifted.NodeOverview, Frame(built.Layout, snapshot, origin).NodeOverview);
        Assert.AreNotSame(first.NodeOverview, shifted.NodeOverview);
        CollectionAssert.AreEqual(kept, first.NodeOverview.Lair.ToArray());
        AssertOutline(first, cells, minX, maxX, minZ, maxZ, default);
        AssertOutline(shifted, cells, minX, maxX, minZ, maxZ, origin);
    }

    [TestMethod]
    public void Missing_lair_content_leaves_the_overview_outline_empty()
    {
        var snapshot = NodeLairTests.World(new ContentLookup<LairAppearanceProjection>.NotFound());
        var overview = Frame(NodeLayout.Build(snapshot, NodeFixtures.NodeId), snapshot).NodeOverview!;
        Assert.IsTrue(overview.Plots.IsEmpty);
        Assert.IsTrue(overview.Lair.IsEmpty);
        Assert.IsFalse(overview.Tracks.IsEmpty);
    }

    private static void AssertOutline(SceneFrame frame, (int X, int Z)[] cells, int minX, int maxX, int minZ, int maxZ,
        ScenePoint origin)
    {
        var overview = frame.NodeOverview!;
        var bounds = frame.NodePanBounds!;
        Assert.IsTrue(overview.Plots.IsEmpty);
        Assert.IsFalse(overview.Tracks.IsEmpty);
        var shift = new Vector2((float)origin.X, (float)origin.Z);
        Vector2[] expected = [new Vector2(minX, minZ) - shift, new Vector2(maxX, minZ) - shift,
            new Vector2(maxX, maxZ) - shift, new Vector2(minX, maxZ) - shift];
        Assert.AreEqual(expected.Length, overview.Lair.Length);
        for (var corner = 0; corner < expected.Length; corner++)
            Assert.IsTrue(Vector2.Distance(expected[corner], overview.Lair[corner]) < .001f);
        foreach (var corner in overview.Lair)
        {
            Assert.IsTrue(corner.X >= bounds.Minimum.X - .001f && corner.X <= bounds.Maximum.X + .001f);
            Assert.IsTrue(corner.Y >= bounds.Minimum.Y - .001f && corner.Y <= bounds.Maximum.Y + .001f);
        }
        foreach (var cell in cells)
        {
            var point = new Vector2(cell.X + .5f, cell.Z + .5f) - shift;
            Assert.IsTrue(point.X > overview.Lair.Min(corner => corner.X) && point.X < overview.Lair.Max(corner => corner.X));
            Assert.IsTrue(point.Y > overview.Lair.Min(corner => corner.Y) && point.Y < overview.Lair.Max(corner => corner.Y));
        }
    }

    private static SceneFrame Frame(NodeLayout layout, SceneWorldSnapshot snapshot, ScenePoint origin = default,
        SceneDebugLayer layers = SceneDebugLayer.None) => new(new(new(Guid.Empty), new(1), new(1), 1), origin,
            0, SceneReadiness.Partial, [], [], [], [], node: layout, debugLayers: layers,
            nodeTracks: snapshot.Core.World.Edges.Where(edge => edge.Start == layout.Node || edge.End == layout.Node)
                .ToImmutableDictionary(edge => edge.Id, edge => edge.Tracks));
}
