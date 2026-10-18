using System.Numerics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes.Tests;

[TestClass]
public sealed class ConstructionFogTests
{
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
