using System.Collections.Immutable;
using System.Numerics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes.Tests;

[TestClass]
public sealed class TerrainCollisionTests
{
    [TestMethod]
    [DataRow("tie-x", 0)]
    [DataRow("tie-z", 0)]
    [DataRow("negative", 0)]
    [DataRow("uneven-bounds", 0)]
    [DataRow("row", 0)]
    [DataRow("column", 0)]
    [DataRow("diagonal", 0)]
    [DataRow("random", 17)]
    [DataRow("random", 29)]
    [DataRow("random", 640)]
    [DataRow("single", 0)]
    [DataRow("empty", 0)]
    public void PatchMatchesBruteForceAtEverySample(string input, int seed)
    {
        Dictionary<GridColumn, double> source = input switch
        {
            "tie-x" => new() { [new(-4, 0)] = 11, [new(0, -4)] = 22 },
            "tie-z" => new() { [new(-5, -6)] = 11, [new(-5, 2)] = 22 },
            "negative" => new() { [new(-17, -3)] = -5.25, [new(-9, -19)] = 7.5, [new(-2, -8)] = 3 },
            "uneven-bounds" => new() { [new(2, 5)] = 11, [new(15, 8)] = 22, [new(6, 15)] = 33 },
            "row" => new() { [new(-7, 3)] = 11, [new(9, 3)] = 22, [new(18, 3)] = 33 },
            "column" => new() { [new(3, -7)] = 11, [new(3, 9)] = 22, [new(3, 18)] = 33 },
            "diagonal" => Enumerable.Range(-12, 25).SelectMany(x => Enumerable.Range(x - 1, 3)
                .Select(z => new KeyValuePair<GridColumn, double>(new(x, z), x * 100 + z))).ToDictionary(),
            "random" => RandomHeights(seed),
            "single" => new() { [new(-3, 7)] = -5.25 },
            "empty" => [],
            _ => throw new ArgumentOutOfRangeException(nameof(input))
        };
        var heights = source.ToImmutableDictionary();
        var partOrigin = new ScenePoint(10_000_000_000.125, -23, -10_000_000_000.375);
        var frameOrigin = new ScenePoint(10_000_000_003.5, -100.125, -10_000_000_002.75);
        var expected = BrutePatch(new(7), partOrigin, frameOrigin, heights);
        var actual = TerrainCollision.Patch(new(7), partOrigin, frameOrigin, heights);
        if (expected is null)
        {
            Assert.IsNull(actual);
            return;
        }
        Assert.IsNotNull(actual);
        Assert.AreEqual(expected.Id, actual.Id);
        Assert.AreEqual(expected.Width, actual.Width);
        Assert.AreEqual(expected.Depth, actual.Depth);
        Assert.AreEqual(expected.SpacingCells, actual.SpacingCells);
        Assert.AreEqual(expected.Origin, actual.Origin);
        CollectionAssert.AreEqual(expected.Heights.ToArray(), actual.Heights.ToArray());
    }

    [TestMethod]
    public void CacheReusesSamplesAndRebasesFromOriginalDoubleCoordinates()
    {
        var heights = new Dictionary<GridColumn, double>
        { [new(-4, -4)] = -123.25, [new(8, 4)] = 7.5 }.ToImmutableDictionary();
        var partOrigin = new ScenePoint(10_000_000_000.1, 9, -10_000_000_000.3);
        var initialOrigin = new ScenePoint(10_000_000_000.2, 100_000_000.123456, -10_000_000_000.4);
        var shiftedOrigin = new ScenePoint(10_000_002_048.7, 100_001_024.987654, -10_000_004_096.9);
        var cache = new TerrainCollision.PatchCache();
        ulong nextId = 0;
        SceneObjectId Id() => new(++nextId);
        var first = cache.Get("terrain", Id, partOrigin, initialOrigin, heights)!;
        Assert.AreSame(first, cache.Get("terrain", Id, partOrigin, initialOrigin, heights));
        foreach (var origin in new[] { shiftedOrigin, initialOrigin, shiftedOrigin, initialOrigin })
        {
            var rebased = cache.Get("terrain", Id, partOrigin, origin, heights)!;
            Assert.AreEqual(first.Id, rebased.Id);
            Assert.AreEqual(first.Heights, rebased.Heights);
            Assert.AreEqual(TerrainCollision.Patch(first.Id, partOrigin, origin, heights)!.Origin, rebased.Origin);
            Assert.AreSame(rebased, cache.Get("terrain", Id, partOrigin, origin, heights));
        }
        var changed = heights.SetItem(new(8, 4), 19.75);
        var updated = cache.Get("terrain", Id, partOrigin, initialOrigin, changed)!;
        Assert.AreNotEqual(first.Heights, updated.Heights);
        CollectionAssert.AreEqual(TerrainCollision.Patch(updated.Id, partOrigin, initialOrigin, changed)!.Heights.ToArray(),
            updated.Heights.ToArray());
        Assert.AreSame(updated, cache.Get("terrain", Id, partOrigin, initialOrigin, changed));
    }

    private static Dictionary<GridColumn, double> RandomHeights(int seed)
    {
        var random = new Random(seed);
        var heights = new Dictionary<GridColumn, double>();
        for (var index = 0; index < 80; index++)
            heights[new(random.Next(-19, 22), random.Next(-23, 18))] = index * 0.25;
        return heights;
    }

    private static TerrainCollisionPatch? BrutePatch(SceneObjectId id, ScenePoint partOrigin, ScenePoint frameOrigin,
        ImmutableDictionary<GridColumn, double> heights)
    {
        if (heights.Count == 0) return null;
        var minX = heights.Keys.Min(column => column.X);
        var minZ = heights.Keys.Min(column => column.Z);
        var width = (heights.Keys.Max(column => column.X) - minX) / 4 + 1;
        var depth = (heights.Keys.Max(column => column.Z) - minZ) / 4 + 1;
        var originHeight = BruteHeight(heights, minX, minZ);
        var samples = ImmutableArray.CreateBuilder<float>(width * depth);
        for (var z = 0; z < depth; z++)
            for (var x = 0; x < width; x++)
                samples.Add((float)(BruteHeight(heights, minX + x * 4, minZ + z * 4) - originHeight));
        return new(id, new Vector3((float)(partOrigin.X + minX - frameOrigin.X),
            (float)(originHeight - frameOrigin.Y), (float)(partOrigin.Z + minZ - frameOrigin.Z)),
            width, depth, 4, samples.DrainToImmutable());
    }

    private static double BruteHeight(ImmutableDictionary<GridColumn, double> heights, int x, int z)
    {
        if (heights.TryGetValue(new(x, z), out var known)) return known;
        var best = 0d;
        var bestDistance = long.MaxValue;
        var bestX = int.MaxValue;
        var bestZ = int.MaxValue;
        foreach (var (column, height) in heights)
        {
            var dx = (long)column.X - x;
            var dz = (long)column.Z - z;
            var distance = dx * dx + dz * dz;
            if (distance < bestDistance || distance == bestDistance && (column.X < bestX || column.X == bestX && column.Z < bestZ))
            {
                bestDistance = distance;
                best = height;
                bestX = column.X;
                bestZ = column.Z;
            }
        }
        return best;
    }
}
