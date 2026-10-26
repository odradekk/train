using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes.Tests;

[TestClass]
public sealed class TerrainCapSourceTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void RailwayPartBoundsProductionFlattenedColumns(bool river)
    {
        var hash = new DesignHash("cap-flatten");
        var model = SceneFixtures.Evaluation(SceneFixtures.Projection([SceneFixtures.Cell(new(0, 0, 0))]) with
        { Design = hash, RootFlatten = new(new(-2, -2), new(2, 2), -3) }) with { Design = hash };
        var terrain = RailwayFixtures.TerrainAppearance with
        {
            LocalHeightNoise = [new(8, 1)],
            Decorations = [new(hash, 1)],
            DecorationTargetPerSquareMeter = .03,
            RailwayModels = [new(1, hash, hash, hash)]
        };
        var length = river ? 20 : 8;
        var snapshot = RailwayFixtures.Snapshot(length, length, relief: 1,
            content: RailwayFixtures.Content(terrain: terrain, design: model));
        if (river)
        {
            var edge = snapshot.Core.World.Edges.Single();
            snapshot = RailwayFixtures.ReplaceCore(snapshot,
                edge: edge with { Terrain = edge.Terrain with { FeatureIds = ["river"] } });
        }
        var path = new RailwayPath(snapshot, RailwayFixtures.Edge);
        var segment = RailwayTerrain.Build(snapshot, path, 0, CancellationToken.None);
        var unflattened = RailwayTerrain.Build(snapshot with
        {
            Content = RailwayFixtures.Content(terrain: terrain with
            { Decorations = [], RailwayModels = [] })
        }, path, 0, CancellationToken.None);
        Assert.IsTrue(segment.Heights.Any(pair => pair.Value < unflattened.Heights[pair.Key]),
            "The fixture must actually lower production L0 terrain.");
        if (river) Assert.IsTrue(RailwayTerrain.HeightFlattens(snapshot, path, 0).Length > 0,
            "The river fixture must place a fixed model with RootFlatten.");
        var source = new RailwayTerrainCaps(snapshot, path);
        var work = source.Part(0);
        var area = path.Bounds(0, Math.Min(path.LengthCells, RailwayCoordinates.SegmentLengthCells), default,
            RailwayCoordinates.TerrainHalfWidth(path.Edge.Tracks) + RailwayCoordinates.CellFootprintMargin);
        Complete(work, new(new(area.Minimum.X, area.Minimum.Z), new(area.Maximum.X, area.Maximum.Z)));
        AssertCapBoundsColumns(work.Result, segment.Heights.Select(pair =>
            (X: pair.Key.X + segment.Origin.X, Z: pair.Key.Z + segment.Origin.Z, Top: pair.Value)));
        AssertCapBoundsColumns(source.Part(0).Result, segment.Heights.Select(pair =>
            (X: pair.Key.X + segment.Origin.X, Z: pair.Key.Z + segment.Origin.Z, Top: pair.Value)));
        var bounds = new ScenePanBounds(
            new((float)segment.Heights.Keys.Min(column => column.X + segment.Origin.X),
                (float)segment.Heights.Keys.Min(column => column.Z + segment.Origin.Z)),
            new((float)segment.Heights.Keys.Max(column => column.X + segment.Origin.X + 1),
                (float)segment.Heights.Keys.Max(column => column.Z + segment.Origin.Z + 1)));
        var requested = source.Parts(bounds).Single().Work;
        Assert.IsTrue(requested.Complete, "An already completed part request must not require more work.");
        AssertCapBoundsColumns(requested.Result, segment.Heights.Select(pair =>
            (X: pair.Key.X + segment.Origin.X, Z: pair.Key.Z + segment.Origin.Z, Top: pair.Value)));
        Assert.AreEqual(0, work.Advance(7));
    }

    [TestMethod]
    public void NodeChunkAndBoundaryReusePreparationAndBoundProductionColumns()
    {
        var snapshot = RailwayFixtures.Snapshot(20, 20, relief: 0);
        var prepared = NodeScene.Prepare(snapshot, NodeLayout.Build(snapshot, NodeFixtures.NodeId));
        var source = new NodeTerrainCaps(prepared);
        var bounds = prepared.Global.Layout.Bounds;
        var center = (bounds.Minimum + bounds.Maximum) / 2;
        var chunkFrom = new GridColumn((int)Math.Floor(center.X / VoxelGrid.ChunkSize) * VoxelGrid.ChunkSize,
            (int)Math.Floor(center.Y / VoxelGrid.ChunkSize) * VoxelGrid.ChunkSize);
        var boundaryKey = prepared.Global.Parts.First(item => NodePartKeys.IsBoundary(item.Key) && !item.Value.Heights.IsEmpty).Key;
        var boundary = (Key: boundaryKey, Work: source.Boundary(boundaryKey));
        var chunk = (Key: NodeScene.ChunkKey(chunkFrom), Work: source.Chunk(chunkFrom));
        foreach (var (key, work) in new[] { chunk, boundary })
        {
            var same = NodePartKeys.IsBoundary(key) ? source.Boundary(key) : source.Parts(new(
                new(chunkFrom.X, chunkFrom.Z), new(chunkFrom.X + VoxelGrid.ChunkSize, chunkFrom.Z + VoxelGrid.ChunkSize)))
                .Single(item => item.Key == key).Work;
            NodeScene.Part part;
            if (NodePartKeys.IsBoundary(key))
            {
                part = prepared.Global.Parts[key];
            }
            else
            {
                part = prepared.BuildChunk(chunkFrom);
            }
            Assert.IsTrue(part.Heights.Count > 0);
            var area = NodePartKeys.IsBoundary(key)
                ? new ScenePanBounds(
                    new((float)part.Heights.Keys.Min(column => column.X + part.Origin.X),
                        (float)part.Heights.Keys.Min(column => column.Z + part.Origin.Z)),
                    new((float)part.Heights.Keys.Max(column => column.X + part.Origin.X + 1),
                        (float)part.Heights.Keys.Max(column => column.Z + part.Origin.Z + 1)))
                : new ScenePanBounds(new(chunkFrom.X, chunkFrom.Z),
                    new(chunkFrom.X + VoxelGrid.ChunkSize, chunkFrom.Z + VoxelGrid.ChunkSize));
            Complete(work, area);
            AssertCapBoundsColumns(work.Result, part.Heights.Select(pair =>
                (X: pair.Key.X + part.Origin.X, Z: pair.Key.Z + part.Origin.Z, Top: pair.Value + part.Origin.Y)));
            Assert.IsTrue(same.Complete);
            AssertCapBoundsColumns(same.Result, part.Heights.Select(pair =>
                (X: pair.Key.X + part.Origin.X, Z: pair.Key.Z + part.Origin.Z, Top: pair.Value + part.Origin.Y)));
            Assert.AreEqual(0, same.Advance(7));
        }
    }

    private static void Complete(TerrainHeightCap.Builder work, ScenePanBounds bounds)
    {
        var spacing = TerrainTile.NearSpacingCells;
        var width = (long)(Math.Ceiling(bounds.Maximum.X / spacing) - Math.Floor(bounds.Minimum.X / spacing));
        var depth = (long)(Math.Ceiling(bounds.Maximum.Y / spacing) - Math.Floor(bounds.Minimum.Y / spacing));
        var remaining = Math.Max(1, width) * Math.Max(1, depth) * (spacing * spacing + 1);
        while (!work.Complete && remaining > 0)
        {
            var used = work.Advance((int)Math.Min(37, remaining));
            Assert.IsTrue(used is > 0 and <= 37);
            remaining -= used;
        }
        Assert.IsTrue(work.Complete, "The cap must finish within one visit per column and reduction cell.");
    }

    private static void AssertCapBoundsColumns(TerrainHeightCap cap,
        IEnumerable<(double X, double Z, double Top)> columns)
    {
        foreach (var spacing in new[] { TerrainTile.NearSpacingCells, TerrainTile.FarSpacingCells })
        foreach (var (x, z, top) in columns)
        foreach (var dx in new[] { 0, spacing })
        foreach (var dz in new[] { 0, spacing })
        {
            var vx = (int)Math.Floor(x / spacing) * spacing + dx;
            var vz = (int)Math.Floor(z / spacing) * spacing + dz;
            Assert.IsTrue(cap.Minimum(vx, vz, spacing) <= top,
                $"LOD spacing {spacing} must bound L0 column ({x}, {z}) top {top} at supporting vertex ({vx}, {vz}).");
        }
    }
}
