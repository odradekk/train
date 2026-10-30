using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes.Tests;

[TestClass]
public sealed class NodeHeightsTests
{
    [TestMethod]
    public void Published_terrain_columns_cannot_be_changed_through_a_mutable_dictionary_interface()
    {
        var snapshot = NodeFixtures.Snapshot(platforms: 0, trains: []);
        var prepared = NodeScene.Prepare(snapshot, NodeLayout.Build(snapshot, NodeFixtures.NodeId));
        var part = prepared.BuildChunk(new(0, 0));
        var column = part.Heights.Keys.First();
        var height = part.Heights[column];
        var frame = NodeHeights.Empty.WithPart((1, 0, 0, "terrain"), part);

        Assert.ThrowsExactly<NotSupportedException>(() =>
            ((IDictionary<GridColumn, double>)part.Heights)[column] = height + 1);
        Assert.AreEqual(height, frame.At(column.X + .5, column.Z + .5));
    }

    [TestMethod]
    public void Indexed_columns_preserve_part_order_origins_and_older_snapshots_after_changes()
    {
        var geometry = WorldSceneSession.BuildGeometry(_ => new([], [], []));
        NodeScene.Part Part(double origin, params (int X, int Z, double Height)[] columns) =>
            new("", geometry, columns.ToImmutableDictionary(column => new GridColumn(column.X, column.Z), column => column.Height), new(origin, 0, origin));
        var boundary = Part(-.5, (-32, -32, 7), (32, 32, 9));
        var terrain = Part(0, (-33, -33, 3), (31, 31, 5), (32, 32, 6));
        var first = NodeHeights.Empty.WithPart((1, 0, 0, "terrain"), terrain)
            .WithPart((0, 0, 0, "boundary"), boundary);
        foreach (var x in new[] { -33.01, -32.5, -32.01, -32, 31.49, 31.5, 31.99, 32, 32.49, 32.5, 33, 1e100 })
        {
            double? expected = null;
            foreach (var part in new[] { boundary, terrain })
            {
                var local = Math.Floor(x - part.Origin.X);
                if (local >= int.MinValue && local <= int.MaxValue && part.Heights.TryGetValue(new((int)local, (int)local), out var height))
                { expected = height; break; }
            }
            Assert.AreEqual(expected, first.At(x, x), $"Column at {x} must match the ordered full scan.");
        }
        Assert.AreSame(first, first.WithPart((0, 0, 0, "boundary"), boundary));
        var removed = first.WithPart((0, 0, 0, "boundary"), null);
        Assert.AreEqual(5d, removed.At(31.75, 31.75));
        Assert.AreEqual(9d, first.At(31.75, 31.75), "An older frame must retain its original height data.");
        var replaced = removed.WithPart((1, 0, 0, "terrain"), Part(0, (64, 64, 11)));
        Assert.IsNull(replaced.At(31.75, 31.75));
        Assert.AreEqual(11d, replaced.At(64.5, 64.5));
        Assert.AreEqual(5d, removed.At(31.75, 31.75));
        var asymmetric = replaced.WithPart((1, 0, 0, "terrain"), Part(0, (64, 65, 13)));
        Assert.AreEqual(13d, asymmetric.At(64.5, 65.5));
        Assert.IsNull(asymmetric.At(65.5, 64.5));
        Assert.IsNull(asymmetric.WithPart((1, 0, 0, "terrain"), Part(0)).At(64.5, 65.5));
    }
}
