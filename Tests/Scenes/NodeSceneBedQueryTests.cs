using System.Numerics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes.Tests;

[TestClass]
public sealed class NodeSceneBedQueryTests
{
    [TestMethod]
    [DataRow(32, 32, false)]
    [DataRow(-32, -32, false)]
    [DataRow(-32, 32, true)]
    [DataRow(32, -32, true)]
    public void DiagonalAndCurvedOverlapsKeepTheFirstHeightAtEqualDistance(int x, int z, bool curved)
    {
        foreach (var height in new[] { 8d, 12d })
        {
            RailwaySample Sample(double arc, double y)
            {
                var angle = curved ? arc / 12 : Math.PI / 4;
                var forward = new Vector3((float)Math.Sin(angle), 0, (float)Math.Cos(angle));
                return new(new(x + 16.25 + Math.Sin(angle) * arc, y, z + 16.25 + Math.Cos(angle) * arc),
                    forward, Vector3.Cross(Vector3.UnitY, forward));
            }
            var length = curved ? 16.75 : 3.75;
            (Func<double, RailwaySample> Sample, double Length)[] routes =
                [(arc => Sample(arc, height), length), (arc => Sample(arc, 20 - height), length)];
            var actual = Arrange(routes);
            var expected = Reference(routes);
            var first = Arrange([(arc => Sample(arc, height), length)]);
            Assert.AreEqual(first.ChunkInputs(new(x, z)), actual.ChunkInputs(new(x, z)));
            NodeFixtures.AssertPartEqual(first.BuildChunk(new(x, z)), actual.BuildChunk(new(x, z)));
            NodeFixtures.AssertPartEqual(expected.BuildChunk(new(x, z)), actual.BuildChunk(new(x, z)));
            AssertHeight(actual, expected.Bed, new(x + 16, z + 16));
            Assert.AreEqual(height - 1, actual.BuildChunk(new(x, z)).Heights[new(x + 16, z + 16)]);
        }
    }

    [TestMethod]
    [DataRow(32, 32)]
    [DataRow(-32, -32)]
    [DataRow(-32, 32)]
    [DataRow(32, -32)]
    public void NonIntegralEndpointContributesAcrossTheChunkBoundary(int x, int z)
    {
        RailwaySample Sample(double arc) => new(new(x + 16.25, 8, z + VoxelGrid.ChunkSize - .75 + arc),
            Vector3.UnitZ, Vector3.UnitX);
        var actual = Arrange([(Sample, .75)]);
        var beforeEndpoint = Arrange([(Sample, .5)]);
        var from = new GridColumn(x, z + VoxelGrid.ChunkSize);
        var column = new GridColumn(x + 16, z + VoxelGrid.ChunkSize);
        Assert.AreNotEqual(7d, beforeEndpoint.BuildChunk(from).Heights[column]);
        Assert.AreEqual(7d, actual.BuildChunk(from).Heights[column]);
        NodeFixtures.AssertPartEqual(Reference([(Sample, .75)]).BuildChunk(from), actual.BuildChunk(from));
    }

    [TestMethod]
    [DataRow(32, 32, false)]
    [DataRow(-32, -32, false)]
    [DataRow(-32, 32, true)]
    [DataRow(32, -32, true)]
    public void BedRaisedColumnMeetsItsLowerNeighborAcrossTheChunkBoundary(int x, int z, bool alongX)
    {
        var size = VoxelGrid.ChunkSize;
        var high = alongX ? new GridColumn(x + size - 1, z + 16) : new GridColumn(x + 16, z + size - 1);
        var low = alongX ? new GridColumn(high.X + 1, high.Z) : new GridColumn(high.X, high.Z + 1);
        // A short route parallel to the seam owns only the high side; the next arc endpoint stays in that column.
        RailwaySample Sample(double arc) => new(new(high.X + .25, 80, high.Z + .25),
            alongX ? Vector3.UnitX : Vector3.UnitZ, alongX ? Vector3.UnitZ : Vector3.UnitX);
        var prepared = Arrange([(Sample, .25)]);
        var first = prepared.BuildChunk(new(x, z));
        var second = prepared.BuildChunk(alongX ? new(x + size, z) : new(x, z + size));
        var top = (int)first.Heights[high] - 1;
        var lower = (int)second.Heights[low] - 1;
        Assert.IsTrue(top - lower > RailwayCoordinates.TerrainDepthCells, "The bed must force a skirt deeper than ordinary terrain.");
        var ys = first.Geometry.Geometry.Picking.SelectMany(pick => pick.Grid.Cells)
            .Where(cell => cell.At.X == high.X && cell.At.Z == high.Z).Select(cell => cell.At.Y).ToHashSet();
        for (var y = lower + 1; y <= top; y++) Assert.IsTrue(ys.Contains(y), $"Missing skirt cell at {high}, Y={y}.");
        var reference = Reference([(Sample, .25)]);
        AssertHeight(prepared, reference.Bed, high);
        AssertHeight(prepared, reference.Bed, low);
        NodeFixtures.AssertPartEqual(reference.BuildChunk(new(x, z)), first);
        NodeFixtures.AssertPartEqual(reference.BuildChunk(alongX ? new(x + size, z) : new(x, z + size)), second);
    }

    [TestMethod]
    public void GlobalBoundarySkirtsReachGroundFromTheCompleteSerialBed()
    {
        var terrain = RailwayFixtures.TerrainAppearance with { LocalHeightNoise = [new(4, 40)] };
        var snapshot = NodeFixtures.Snapshot(trains: [], tracks: 1, content: NodeFixtures.Content(
            terrain: terrain, rules: RailwayFixtures.Rules with { MinimumCurveRadiusMeters = 1 }));
        var node = snapshot.Core.World.Nodes.Single(item => item.Id == NodeFixtures.NodeId);
        snapshot = RailwayFixtures.ReplaceCore(snapshot, node with { Terrain = node.Terrain with { Relief = 1 } }, worldSeed: 4);
        node = snapshot.Core.World.Nodes.Single(item => item.Id == NodeFixtures.NodeId);
        var layout = NodeLayout.Build(snapshot, NodeFixtures.NodeId);
        var prepared = NodeScene.Prepare(snapshot, layout);
        var bed = new Dictionary<GridColumn, NodeScene.RailBed>();
        foreach (var entry in layout.Entries)
        foreach (var route in layout.EntryRoutes(entry, snapshot.Core.World.Edges.Single(edge => edge.Id == entry.Edge).Tracks))
            RasterizeReference(bed, route.Sample, route.Length);
        var anyTracks = snapshot.Core.World.Edges.Any(edge => (edge.Start == layout.Node || edge.End == layout.Node) && edge.Tracks > 0);
        foreach (var route in layout.StationRoutes(anyTracks)) RasterizeReference(bed, route.Sample, route.Length);
        foreach (var route in anyTracks ? layout.TurnRoutes() : []) RasterizeReference(bed, route.Sample, route.Length);
        foreach (var line in layout.Parking)
            RasterizeReference(bed, arc => new(RailwayCoordinates.Add(line.Start, line.Forward * (float)arc),
                line.Forward, Vector3.Cross(Vector3.UnitY, line.Forward)), line.Length);
        var extended = 0;
        var bedChangedBottom = 0;
        foreach (var (edge, boundary) in prepared.Global.Boundaries)
        {
            var part = prepared.Global.Parts[NodePartKeys.Boundary(edge)];
            var original = boundary.Grid.Cells.GroupBy(cell => new GridColumn(cell.At.X, cell.At.Z))
                .ToDictionary(group => group.Key, group => group.Min(cell => cell.At.Y));
            var actual = part.Geometry.Geometry.Picking.SelectMany(pick => pick.Grid.Cells)
                .GroupBy(cell => new GridColumn(cell.At.X, cell.At.Z))
                .ToDictionary(group => group.Key, group => group.Select(cell => cell.At.Y).ToHashSet());
            var ox = (int)Math.Round(boundary.Origin.X);
            var oy = (int)Math.Round(boundary.Origin.Y);
            var oz = (int)Math.Round(boundary.Origin.Z);
            foreach (var local in part.Heights.Keys)
            {
                var at = new GridColumn(local.X + ox, local.Z + oz);
                GridColumn[] neighbors = [new(at.X + 1, at.Z), new(at.X - 1, at.Z), new(at.X, at.Z + 1), new(at.X, at.Z - 1)];
                var surfaces = new List<int>();
                var unmodifiedSurfaces = new List<int>();
                foreach (var neighbor in neighbors)
                {
                    if (part.Heights.ContainsKey(new(neighbor.X - ox, neighbor.Z - oz))) continue;
                    if (prepared.BoundarySurfaces.TryGetValue(neighbor, out var owned))
                    {
                        surfaces.Add((int)Math.Floor(owned));
                        unmodifiedSurfaces.Add((int)Math.Floor(owned));
                    }
                    else if (neighbor.X >= layout.Bounds.Minimum.X && neighbor.X < layout.Bounds.Maximum.X &&
                        neighbor.Z >= layout.Bounds.Minimum.Y && neighbor.Z < layout.Bounds.Maximum.Y)
                    {
                        var point = new ScenePoint(neighbor.X + .5, 0, neighbor.Z + .5);
                        surfaces.Add((int)Math.Floor(TerrainSurface.NodeHeight(snapshot, node, layout, point, bed.GetValueOrDefault(neighbor))));
                        unmodifiedSurfaces.Add((int)Math.Floor(TerrainSurface.NodeHeight(snapshot, node, layout, point, null)));
                    }
                }
                if (surfaces.Count == 0) continue;
                var bottom = Math.Min(original[local], surfaces.Min() + 1 - oy);
                Assert.AreEqual(bottom, actual[local].Min(), $"Wrong global boundary skirt bottom at {at}.");
                for (var y = bottom; y < original[local]; y++)
                    Assert.IsTrue(actual[local].Contains(y), $"Global boundary skirt misses {at}, Y={y + oy}.");
                if (bottom < original[local]) extended++;
                if (bottom != Math.Min(original[local], unmodifiedSurfaces.Min() + 1 - oy)) bedChangedBottom++;
            }
        }
        Assert.IsTrue(extended > 0, "The fixture must extend an original boundary column.");
        Assert.IsTrue(bedChangedBottom > 0, "At least one skirt bottom must depend on the complete bed.");
    }

    private static void AssertHeight(NodeScene.Preparation prepared, Dictionary<GridColumn, NodeScene.RailBed> bed, GridColumn column)
    {
        var node = prepared.Snapshot.Core.World.Nodes.Single(item => item.Id == prepared.Global.Layout.Node);
        var point = new ScenePoint(column.X + .5, 0, column.Z + .5);
        var expected = TerrainSurface.NodeHeight(prepared.Snapshot, node, prepared.Global.Layout, point,
            bed.GetValueOrDefault(column), prepared.Terrain);
        Assert.AreEqual(expected, new NodeTerrainCaps(prepared).Height(point));
    }

    private static NodeScene.Preparation Arrange((Func<double, RailwaySample> Sample, double Length)[] routes) =>
        Empty() with { Beds = new(routes) };

    private static ReferenceChunk Reference((Func<double, RailwaySample> Sample, double Length)[] routes)
    {
        var bed = new Dictionary<GridColumn, NodeScene.RailBed>();
        foreach (var route in routes) RasterizeReference(bed, route.Sample, route.Length);
        return new(Empty(), bed);
    }

    private sealed record ReferenceChunk(NodeScene.Preparation Data, Dictionary<GridColumn, NodeScene.RailBed> Bed)
    {
        internal NodeScene.Part BuildChunk(GridColumn from) => Data.BuildChunk(from, LocalBed(from));

        private Dictionary<GridColumn, NodeScene.RailBed> LocalBed(GridColumn from) => Bed
            .Where(item => item.Key.X >= from.X - 1 && item.Key.X <= from.X + VoxelGrid.ChunkSize &&
                item.Key.Z >= from.Z - 1 && item.Key.Z <= from.Z + VoxelGrid.ChunkSize)
            .ToDictionary();
    }
    private static NodeScene.Preparation Empty()
    {
        var layout = NodeFixtures.Layout;
        layout = layout with { SceneModel = layout.SceneModel with { Scene = SceneFixtures.Projection(
            [SceneFixtures.Cell(new(-64, 0, -64)), SceneFixtures.Cell(new(96, 0, 96))]) } };
        var snapshot = NodeFixtures.EmptyNodeSnapshot(layout, []);
        return NodeScene.Prepare(snapshot, NodeLayout.Build(snapshot, NodeFixtures.NodeId)) with { Beds = new([]) };
    }

    // Full serial rasterization from baseline 30029b5f; intentionally independent of task query bounds.
    private static void RasterizeReference(Dictionary<GridColumn, NodeScene.RailBed> bed,
        Func<double, RailwaySample> sample, double length)
    {
        for (var arc = 0.0; arc <= length; arc = Math.Min(length, arc + 0.5))
        {
            var point = sample(arc);
            var half = RailwayCoordinates.BedBaseHalfWidth + RailwayCoordinates.BedBlendWidth;
            var pendingColumn = default(GridColumn);
            var pendingDistance = double.PositiveInfinity;
            for (var offset = -half; offset <= half; offset += 0.5)
            {
                var at = RailwayCoordinates.Add(point.Position, point.Right * (float)offset);
                var column = new GridColumn((int)Math.Floor(at.X), (int)Math.Floor(at.Z));
                var distance = Math.Abs(offset);
                if (column == pendingColumn) pendingDistance = Math.Min(pendingDistance, distance);
                else
                {
                    if (pendingDistance < double.PositiveInfinity) Put(pendingColumn, pendingDistance, point.Position.Y);
                    pendingColumn = column;
                    pendingDistance = distance;
                }
            }
            Put(pendingColumn, pendingDistance, point.Position.Y);
            if (arc == length) break;
        }
        void Put(GridColumn column, double distance, double height)
        {
            if (!bed.TryGetValue(column, out var old) || distance < old.Distance) bed[column] = new(distance, height);
        }
    }
}
