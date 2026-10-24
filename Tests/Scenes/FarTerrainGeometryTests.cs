using System.Numerics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes.Tests;

[TestClass]
public sealed class FarTerrainGeometryTests
{
    // 带外连续性用例仅覆盖显式80m最小曲线半径与1–3轨；更小半径的合法输入不具有此普遍保证。
    [TestMethod]
    [DataRow(600.0, 28.224, 1)]
    [DataRow(600.0, 28.224, 2)]
    [DataRow(600.0, 28.224, 3)]
    [DataRow(6000.0, 1.0, 1)]
    [DataRow(6000.0, 1.0, 2)]
    [DataRow(6000.0, 1.0, 3)]
    public void FarSurfaceJoinsBothSidesOfTheVoxelBand(double length, double direct, int tracks)
    {
        var (path, surface, appearance) = Surface(length, direct, tracks);
        var width = RailwayCoordinates.TerrainHalfWidth(tracks);
        for (var i = 0; i <= 64; i++)
        foreach (var side in new[] { -1, 1 })
        {
            var arc = path.LengthCells * i / 64;
            var boundary = path.Sample(arc, side * width).Position;
            Assert.AreEqual(surface.Height(arc, boundary, appearance), surface.FarHeight(boundary), 1e-5,
                $"Band join at arc {arc}, side {side}");
            AssertContinuous(surface, boundary);
        }
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    public void EndpointCapsJoinTheBandAndReachUnmodifiedGround(int tracks)
    {
        var (path, surface, appearance) = Surface(6000, 1, tracks);
        var width = RailwayCoordinates.TerrainHalfWidth(tracks);
        foreach (var arc in new[] { 0.0, path.LengthCells })
        {
            var sample = path.Sample(arc);
            var direction = arc == 0 ? -1 : 1;
            for (var step = 0; step <= 16; step++)
            {
                var angle = Math.PI * step / 16;
                var dx = sample.Right.X * Math.Cos(angle) + direction * sample.Forward.X * Math.Sin(angle);
                var dz = sample.Right.Z * Math.Cos(angle) + direction * sample.Forward.Z * Math.Sin(angle);
                var boundary = new ScenePoint(sample.Position.X + dx * width, 0, sample.Position.Z + dz * width);
                var lateral = (boundary.X - sample.Position.X) * sample.Right.X +
                    (boundary.Z - sample.Position.Z) * sample.Right.Z;
                Assert.AreEqual(surface.ColumnHeight(arc, boundary, lateral, appearance, out _, out _),
                    surface.FarHeight(boundary), 1e-5);
                AssertContinuous(surface, boundary);
                var outside = new ScenePoint(sample.Position.X + dx * (width + 32.001), 0,
                    sample.Position.Z + dz * (width + 32.001));
                Assert.AreEqual(Ground(path, appearance, outside), surface.FarHeight(outside), 1e-5);
                AssertContinuous(surface, outside);
            }
        }
    }

    [TestMethod]
    public void FarGroundDoesNotRetainTheHeightOfTheNearestRailwayBranch()
    {
        var (path, surface, appearance) = Surface(6000, 1, 3);
        Assert.IsTrue(Math.Abs(path.Sample(path.LengthCells / 2).Position.Y) > 1,
            "The fixture must distinguish rail-relative terrain from raw terrain.");
        foreach (var point in new[] { new ScenePoint(-100000, 0, -100000), new ScenePoint(100000, 0, 100000) })
            Assert.AreEqual(Ground(path, appearance, point), surface.FarHeight(point), 1e-9);

        // A circle centre is equidistant from a bend's many arc positions, which have different rail heights.
        // Derive it from three sampled points, independently of the alignment's internal pieces.
        ScenePoint? equalDistance = null;
        for (var i = 1; i < 99 && equalDistance is null; i++)
        {
            var a = path.Sample(path.LengthCells * (i - 1) / 100).Position;
            var b = path.Sample(path.LengthCells * i / 100).Position;
            var c = path.Sample(path.LengthCells * (i + 1) / 100).Position;
            var determinant = 2 * (a.X * (b.Z - c.Z) + b.X * (c.Z - a.Z) + c.X * (a.Z - b.Z));
            if (Math.Abs(determinant) < 1e-4) continue;
            var aa = a.X * a.X + a.Z * a.Z;
            var bb = b.X * b.X + b.Z * b.Z;
            var cc = c.X * c.X + c.Z * c.Z;
            var centre = new ScenePoint((aa * (b.Z - c.Z) + bb * (c.Z - a.Z) + cc * (a.Z - b.Z)) / determinant, 0,
                (aa * (c.X - b.X) + bb * (a.X - c.X) + cc * (b.X - a.X)) / determinant);
            var radius = HorizontalDistance(centre, b);
            if (radius < 150 || radius > 170) continue;
            var nearest = Enumerable.Range(0, 2001).Min(step =>
                HorizontalDistance(centre, path.Sample(path.LengthCells * step / 2000).Position));
            if (nearest > RailwayCoordinates.TerrainHalfWidth(3) + 34) equalDistance = centre;
        }
        Assert.IsNotNull(equalDistance, "The long S fixture must contain a distant bend centre.");
        foreach (var dx in new[] { -0.01, 0, 0.01 })
        foreach (var dz in new[] { -0.01, 0, 0.01 })
        {
            var point = new ScenePoint(equalDistance.Value.X + dx, 0, equalDistance.Value.Z + dz);
            Assert.AreEqual(Ground(path, appearance, point), surface.FarHeight(point), 1e-7);
            AssertContinuous(surface, point);
        }
    }

    [TestMethod]
    public void AdjacentTilesShareWorldPositionsAndHeightsAlongTheirWholeEdge()
    {
        static double Height(ScenePoint point) => 3 * Math.Sin(point.Z / 7) + point.X / 16;
        var left = Tile(new(-64, 12, 32), 8, Height);
        var right = Tile(new(0, 12, 32), 8, Height);
        var leftEdge = left.Mesh.Vertices.Where(vertex => vertex.Position.X == 64)
            .Select(vertex => World(vertex.Position, left.Origin)).ToHashSet();
        var rightEdge = right.Mesh.Vertices.Where(vertex => vertex.Position.X == 0)
            .Select(vertex => World(vertex.Position, right.Origin)).ToHashSet();
        Assert.IsTrue(leftEdge.Count > 8);
        Assert.IsTrue(leftEdge.SetEquals(rightEdge), "Top edges and skirts must meet in world coordinates.");
        for (var z = 0.0; z <= 64; z += 0.25)
            Assert.AreEqual(left.Origin.Y + left.Height(64, z)!.Value,
                right.Origin.Y + right.Height(0, z)!.Value, 1e-7);
    }

    [TestMethod]
    public void TileTrianglesHaveValidWindingNormalsAndSkirtsOnEverySide()
    {
        var tile = Tile(new(128, 17, -64), 8, point => 5 * Math.Sin(point.X / 13) + 7 * Math.Cos(point.Z / 11));
        var mesh = tile.Mesh;
        foreach (var vertex in mesh.Vertices)
        {
            Assert.IsTrue(float.IsFinite(vertex.Normal.X) && float.IsFinite(vertex.Normal.Y) && float.IsFinite(vertex.Normal.Z));
            Assert.AreEqual(1f, vertex.Normal.Length(), 1e-5f);
        }
        foreach (var indices in mesh.TriangleIndices.Chunk(3))
        {
            Assert.AreEqual(3, indices.Length);
            Assert.IsTrue(indices.All(index => index >= 0 && index < mesh.Vertices.Length));
            var a = mesh.Vertices[indices[0]];
            var b = mesh.Vertices[indices[1]];
            var c = mesh.Vertices[indices[2]];
            var normal = Vector3.Cross(b.Position - a.Position, c.Position - a.Position);
            Assert.IsTrue(normal.LengthSquared() > 1e-8, "No triangle may collapse.");
            Assert.IsTrue(Vector3.Dot(normal, a.Normal + b.Normal + c.Normal) > 0,
                "Counter-clockwise winding must agree with the published normals.");
            if (Math.Abs(normal.Y) < 1e-6)
                Assert.IsTrue(Vector3.Dot(normal, (a.Position + b.Position + c.Position) / 3 - new Vector3(32, 0, 32)) > 0,
                    "Skirt triangles must face out from the tile.");
            else
                Assert.IsTrue(normal.Y > 0, "Surface triangles must face upward.");
        }
        foreach (var side in new[] { 0, 1, 2, 3 })
        {
            var edge = mesh.Vertices.Where(vertex => side switch
            {
                0 => vertex.Position.X == 0,
                1 => vertex.Position.X == 64,
                2 => vertex.Position.Z == 0,
                _ => vertex.Position.Z == 64
            }).GroupBy(vertex => (vertex.Position.X, vertex.Position.Z)).ToArray();
            Assert.IsTrue(edge.Length >= 9);
            Assert.IsTrue(edge.All(column => column.Max(vertex => vertex.Position.Y) - column.Min(vertex => vertex.Position.Y) >= 6 - 1e-5),
                $"Side {side} must have a skirt at least six cells deep.");
        }
    }

    [TestMethod]
    [DataRow(-1)]
    [DataRow(1)]
    public void SkirtsCoverHeightDifferencesBetweenCoarseAndFineEdges(int direction)
    {
        double Height(ScenePoint point) => direction * 40 * (1 - Math.Cos(Math.PI * point.Z / 16));
        (GridColumn Column, double Top)[] loweredColumns = [(new(63, 1), -120.1), (new(64, 2), -100.25)];
        foreach (var columns in new[] { Array.Empty<(GridColumn Column, double Top)>(), loweredColumns })
        {
            var coarse = Tile(new(0, 0, 0), 32, Height, columns);
            var fine = Tile(new(64, 0, 0), 4, Height, columns.Reverse());
            if (columns.Length > 0)
            {
                Assert.IsTrue(coarse.Height(64, 0)!.Value <= -120.1);
                Assert.IsTrue(fine.Height(0, 0)!.Value <= -120.1);
            }
            for (var z = 0.0; z <= 64; z += .5)
            {
                var coarseTop = coarse.Height(64, z)!.Value;
                var fineTop = fine.Height(0, z)!.Value;
                Assert.IsTrue(Math.Max(EdgeBottom(coarse, 64, z), EdgeBottom(fine, 0, z)) <= Math.Min(coarseTop, fineTop) + 1e-5,
                    $"Skirts leave an open height interval at z={z}: coarse {coarseTop}, fine {fineTop}, limits={columns.Length}");
            }
        }
    }

    [TestMethod]
    [DataRow(4)]
    [DataRow(32)]
    public void StaticVoxelTopsBoundWholeColumnFootprintsAndLeaveDistantSurfaceUnchanged(int spacing)
    {
        static double Height(ScenePoint point) => 25 + point.X / 8 + point.Z / 16;
        var origin = new ScenePoint(0, 100, 0);
        (GridColumn Column, double Top)[] columns =
        [
            (new(1, 1), -20.1), (new(5, 2), 3.25),
            (new(1, 5), -4.75), (new(31, 31), -8.1),
            (new(32, 31), -37.15), (new(63, 65), -50.3)
        ];
        var tile = Tile(origin, spacing, Height, columns, sizeCells: 128);
        var reordered = Tile(origin, spacing, Height, columns.Reverse(), sizeCells: 128);
        var original = Tile(origin, spacing, Height, sizeCells: 128);
        CollectionAssert.AreEqual(tile.Mesh.Vertices.Select(vertex => vertex.Position).ToArray(),
            reordered.Mesh.Vertices.Select(vertex => vertex.Position).ToArray());
        foreach (var (column, top) in columns)
        foreach (var offset in new Vector2[] { new(0, 0), new(1, 0), new(0, 1), new(1, 1), new(.5f, .5f) })
        {
            var x = column.X + offset.X;
            var z = column.Z + offset.Y;
            Assert.IsTrue(original.Height(x, z)!.Value + origin.Y > top,
                "The original surface must actually exceed the stepped voxel top.");
            Assert.IsTrue(tile.Height(x, z)!.Value + origin.Y <= top,
                $"The remote surface exceeds voxel {column} at ({x}, {z}); spacing={spacing}, top={top}");
        }
        // 四顶点保守下压会影响相邻三角形；这里选择不受这些顶点影响的非L0区域。
        for (var z = 104.0; z <= 128; z += 4)
        for (var x = 104.0; x <= 128; x += 4)
            Assert.AreEqual(original.Height(x, z)!.Value, tile.Height(x, z)!.Value, 1e-12);
    }

    [TestMethod]
    [DataRow(4)]
    [DataRow(32)]
    public void StaticColumnLimitsKeepAdjacentEdgesIdenticalWithSeparateMarginsAndInputOrders(int spacing)
    {
        static double Height(ScenePoint point) => 40 + point.X / 16 + point.Z / 32;
        var allColumns = Enumerable.Range(-40, 144).SelectMany(z => Enumerable.Range(60, 8)
            .Select(x => (Column: new GridColumn(x, z), Top: x < 64 ? -20.1 : z < 32 ? -70.25 : -45.5)))
            .Concat([(Column: new GridColumn(20, 10), Top: -90.1),
                (Column: new GridColumn(104, 10), Top: -100.25)]).ToArray();
        var leftOrigin = new ScenePoint(0, 0, 0);
        var rightOrigin = new ScenePoint(64, 0, 0);
        var leftColumns = InMargin(leftOrigin).Reverse();
        var rightColumns = InMargin(rightOrigin).OrderBy(item => (item.Column.X * 37 ^ item.Column.Z * 101) & 255);
        var left = Tile(leftOrigin, spacing, Height, leftColumns);
        var right = Tile(rightOrigin, spacing, Height, rightColumns);
        var leftEdge = left.Mesh.Vertices.Where(vertex => vertex.Position.X == 64)
            .Select(vertex => World(vertex.Position, left.Origin)).ToHashSet();
        var rightEdge = right.Mesh.Vertices.Where(vertex => vertex.Position.X == 0)
            .Select(vertex => World(vertex.Position, right.Origin)).ToHashSet();
        Assert.IsTrue(leftEdge.SetEquals(rightEdge), "Static limits must preserve both the shared surface and skirt edge.");
        for (var z = 0.0; z <= 64; z += .5)
            Assert.AreEqual(left.Height(64, z)!.Value, right.Height(0, z)!.Value, 1e-12);

        IEnumerable<(GridColumn Column, double Top)> InMargin(ScenePoint origin) => allColumns.Where(item =>
            item.Column.X >= origin.X - TerrainTile.ColumnMarginCells && item.Column.X < origin.X + 64 + TerrainTile.ColumnMarginCells &&
            item.Column.Z >= origin.Z - TerrainTile.ColumnMarginCells && item.Column.Z < origin.Z + 64 + TerrainTile.ColumnMarginCells);
    }

    [TestMethod]
    public void ChangingTheLocalVerticalOriginPreservesTheWorldGridAndSurface()
    {
        static double Height(ScenePoint point) => (point.X - 100000000) / 8 + (point.Z + 100000000) / 16;
        var first = Tile(new(100000000, 0, -100000000), 8, Height);
        var moved = Tile(first.Origin with { Y = 256 }, 8, Height);
        CollectionAssert.AreEqual(first.Mesh.TriangleIndices.ToArray(), moved.Mesh.TriangleIndices.ToArray());
        CollectionAssert.AreEqual(first.Mesh.Vertices.Select(vertex => World(vertex.Position, first.Origin)).ToArray(),
            moved.Mesh.Vertices.Select(vertex => World(vertex.Position, moved.Origin)).ToArray());
        foreach (var x in new[] { 0.0, 0.75, 25.5, 64 })
        foreach (var z in new[] { 0.0, 3.25, 59.125, 64 })
            Assert.AreEqual(first.Height(x, z)!.Value + first.Origin.Y,
                moved.Height(x, z)!.Value + moved.Origin.Y, 1e-7);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void HeightQueriesInterpolateThePublishedTriangles(bool saddle)
    {
        var tile = Tile(new(0, 11, 0), 16, point => saddle ? point.X * point.Z / 64 : 2 * point.X - point.Z / 2 + 3);
        var checkedTriangles = 0;
        foreach (var indices in tile.Mesh.TriangleIndices.Chunk(3))
        {
            var a = tile.Mesh.Vertices[indices[0]].Position;
            var b = tile.Mesh.Vertices[indices[1]].Position;
            var c = tile.Mesh.Vertices[indices[2]].Position;
            if (Vector3.Cross(b - a, c - a).Y <= 0) continue;
            foreach (var weights in new[] { new Vector3(.2f, .3f, .5f), new Vector3(.6f, .25f, .15f),
                new Vector3(0, .4999975f, .5000025f) })
            {
                var x = weights.X * (double)a.X + weights.Y * (double)b.X + weights.Z * (double)c.X;
                var z = weights.X * (double)a.Z + weights.Y * (double)b.Z + weights.Z * (double)c.Z;
                var y = weights.X * (double)a.Y + weights.Y * (double)b.Y + weights.Z * (double)c.Y;
                var queried = tile.Height(x, z)!.Value;
                Assert.AreEqual(y, queried, 1e-5);
                Assert.IsTrue(queried >= Math.Min(a.Y, Math.Min(b.Y, c.Y)) &&
                    queried <= Math.Max(a.Y, Math.Max(b.Y, c.Y)));
            }
            checkedTriangles++;
        }
        Assert.IsTrue(checkedTriangles >= 32);
        Assert.IsNull(tile.Height(-0.001, 32));
        Assert.IsNull(tile.Height(32, 64.001));
    }

    [TestMethod]
    public void CapFinalizationIncludesEmptyCellsInWorldRowOrderBeforeCoarseReduction()
    {
        var seen = new List<(int X, int Z, double Raw)>();
        var builder = TerrainHeightCap.Begin(new(new(-4, -4), new(4, 4)),
            column => column == new GridColumn(0, 0) ? -9 : null,
            (x, z, raw, global) =>
            {
                Assert.AreEqual(-9.0, global);
                seen.Add((x, z, raw));
                return Math.Min(raw, -12);
            });
        while (!builder.Complete) builder.Advance(1);
        CollectionAssert.AreEqual(new[] { (-4, -4), (0, -4), (-4, 0), (0, 0) },
            seen.Select(cell => (cell.X, cell.Z)).ToArray());
        Assert.AreEqual(3, seen.Count(cell => double.IsPositiveInfinity(cell.Raw)));
        Assert.AreEqual(-12.0, builder.Result.Minimum(0, 0, 4));
        Assert.AreEqual(-12.0, builder.Result.Minimum(0, 0, 32));
        Assert.AreEqual(8L * sizeof(double), builder.Result.ArrayBytes);
    }

    [TestMethod]
    public void CapReductionAndIncrementalBudgetsAreIndependentOfStepSize()
    {
        var bounds = new ScenePanBounds(new(-8, -8), new(40, 40));
        static double? Top(GridColumn column) => column.X == 3 && column.Z == 2 ? -91 :
            column.X == 27 && column.Z == 26 ? -37 : null;
        var first = TerrainHeightCap.Begin(bounds, Top);
        Assert.ThrowsExactly<InvalidOperationException>(() => _ = first.Result);
        while (!first.Complete) Assert.IsTrue(first.Advance(7) is > 0 and <= 7);
        var second = TerrainHeightCap.Begin(bounds, Top);
        while (!second.Complete) Assert.IsTrue(second.Advance(103) is > 0 and <= 103);
        foreach (var spacing in new[] { 4, 32 })
        for (var z = -32; z <= 64; z += spacing)
        for (var x = -32; x <= 64; x += spacing)
            Assert.AreEqual(first.Result.Minimum(x, z, spacing), second.Result.Minimum(x, z, spacing));
        for (var z = -32; z <= 64; z += 32)
        for (var x = -32; x <= 64; x += 32)
        {
            var fineMinimum = double.PositiveInfinity;
            for (var dz = -28; dz <= 28; dz += 4)
            for (var dx = -28; dx <= 28; dx += 4)
                fineMinimum = Math.Min(fineMinimum, first.Result.Minimum(x + dx, z + dz, 4));
            Assert.AreEqual(fineMinimum, first.Result.Minimum(x, z, 32));
        }
        Assert.AreEqual(-91.0, first.Result.Minimum(0, 0, 4));
        Assert.AreEqual(-91.0, first.Result.Minimum(4, 4, 4));
        Assert.AreEqual(double.PositiveInfinity, first.Result.Minimum(12, 12, 4));
        Assert.AreEqual(-91.0, first.Result.Minimum(32, 32, 32));
        Assert.AreEqual(-37.0, first.Result.Minimum(28, 28, 4));
        Assert.AreEqual(0, first.Advance(7));

        var material = new SceneMaterial.BuiltIn(BuiltInMaterial.RailSteel);
        var small = TerrainTile.Begin(new(0, 7, 0), 64, 4, point => point.X / 8, material,
            lowerBounds: [first.Result]);
        Assert.ThrowsExactly<InvalidOperationException>(() => _ = small.Result);
        while (!small.Complete) Assert.IsTrue(small.Advance(3) is > 0 and <= 3);
        var large = TerrainTile.Build(new(0, 7, 0), 64, 4, point => point.X / 8, material,
            lowerBounds: [second.Result]);
        CollectionAssert.AreEqual(large.Mesh.Vertices.ToArray(), small.Result.Mesh.Vertices.ToArray());
        CollectionAssert.AreEqual(large.Mesh.TriangleIndices.ToArray(), small.Result.Mesh.TriangleIndices.ToArray());
    }

    private static (RailwayPath Path, TerrainSurface.Railway Surface, TerrainAppearanceProjection Appearance)
        Surface(double length, double direct, int tracks)
    {
        var appearance = RailwayFixtures.TerrainAppearance with { LocalHeightNoise = [new(13, 2), new(73, .4)] };
        var content = RailwayFixtures.Content(terrain: appearance,
            rules: RailwayFixtures.Rules with { MinimumCurveRadiusMeters = 80 });
        var path = new RailwayPath(RailwayFixtures.Snapshot(length, direct, relief: 3, tracks: tracks, content: content), RailwayFixtures.Edge);
        return (path, new(path, _ => appearance), appearance);
    }

    private static TerrainTile Tile(ScenePoint origin, int spacing, Func<ScenePoint, double> height,
        IEnumerable<(GridColumn Column, double Top)>? columns = null, int sizeCells = 64)
    {
        IReadOnlyList<TerrainHeightCap>? caps = null;
        if (columns is not null)
        {
            var tops = columns.ToDictionary(item => item.Column, item => item.Top);
            if (tops.Count > 0)
            {
                var bounds = new ScenePanBounds(new(tops.Keys.Min(c => c.X), tops.Keys.Min(c => c.Z)),
                    new(tops.Keys.Max(c => c.X) + 1, tops.Keys.Max(c => c.Z) + 1));
                var builder = TerrainHeightCap.Begin(bounds, column => tops.TryGetValue(column, out var top) ? top : null);
                while (!builder.Complete) builder.Advance(1024);
                caps = [builder.Result];
            }
        }
        return TerrainTile.Build(origin, sizeCells, spacing, height,
            new SceneMaterial.BuiltIn(BuiltInMaterial.RailSteel), lowerBounds: caps);
    }

    private static double Ground(RailwayPath path, TerrainAppearanceProjection appearance, ScenePoint point) =>
        TerrainSurface.Ground(path.Seed, path.Edge.Id.Value, path.Edge.Terrain, appearance, point);

    private static void AssertContinuous(TerrainSurface.Railway surface, ScenePoint point)
    {
        const double epsilon = 1e-5;
        var centre = surface.FarHeight(point);
        foreach (var direction in new[] { new Vector2(1, 0), new Vector2(0, 1) })
        foreach (var sign in new[] { -1, 1 })
        {
            var nearby = new ScenePoint(point.X + sign * epsilon * direction.X, 0, point.Z + sign * epsilon * direction.Y);
            Assert.AreEqual(centre, surface.FarHeight(nearby), .001,
                $"Height jumps at {point}, towards {nearby}");
        }
    }

    private static ScenePoint World(Vector3 point, ScenePoint origin) =>
        new(origin.X + point.X, origin.Y + point.Y, origin.Z + point.Z);

    private static double EdgeBottom(TerrainTile tile, int x, double z)
    {
        var edge = tile.Mesh.Vertices.Where(vertex => vertex.Position.X == x)
            .GroupBy(vertex => vertex.Position.Z)
            .Select(column => (Z: (double)column.Key, Y: (double)column.Min(vertex => vertex.Position.Y)))
            .OrderBy(column => column.Z).ToArray();
        var first = edge.Last(column => column.Z <= z);
        var last = edge.First(column => column.Z >= z);
        return first.Z == last.Z ? first.Y : first.Y + (last.Y - first.Y) * (z - first.Z) / (last.Z - first.Z);
    }

    private static double HorizontalDistance(ScenePoint a, ScenePoint b) =>
        Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Z - b.Z, 2));
}
