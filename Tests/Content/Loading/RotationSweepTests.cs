using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.Designs;
using C = Train.Content.Contracts;

namespace Train.Content.Tests.Loading;

[TestClass]
public sealed class RotationSweepTests
{
    [TestMethod]
    public void Non_right_angle_interval_sweeps_side_cells_but_only_touches_diagonal_cells()
    {
        C.GridPosition[] expected = [new(-1, 0, 0), new(0, -1, 0), new(0, 0, 0), new(0, 1, 0), new(1, 0, 0)];
        CollectionAssert.AreEqual(expected, Sweep(new(0, 0, 0), new(0, 0, 0), C.RotationAxis.Z, 10, 20));
    }

    [TestMethod]
    [DataRow(10.0)]
    [DataRow(0.0001)]
    public void Short_positive_arc_has_asymmetric_exact_occupancy(double maximum)
    {
        C.GridPosition[] expected = [new(0, 0, 0), new(0, 1, 0), new(1, 0, 0), new(1, 1, 0), new(2, 0, 0)];
        CollectionAssert.AreEqual(expected, Sweep(new(1, 0, 0), new(0, 0, 0), C.RotationAxis.Z, 0, maximum));
    }

    [TestMethod]
    public void Full_turn_excludes_cells_touching_only_the_outer_corner_circle()
    {
        var expected = (from x in Enumerable.Range(-2, 5)
                        from y in Enumerable.Range(-2, 5)
                        where Math.Abs(x) + Math.Abs(y) <= 2
                        select new C.GridPosition(x, y, 0)).ToArray();
        CollectionAssert.AreEqual(expected, Sweep(new(1, 0, 0), new(0, 0, 0), C.RotationAxis.Z, -180, 180));
    }

    [TestMethod]
    [DataRow(C.RotationAxis.X)]
    [DataRow(C.RotationAxis.Y)]
    [DataRow(C.RotationAxis.Z)]
    public void Off_origin_pivot_preserves_axis_layer_and_right_handed_direction(C.RotationAxis axis)
    {
        var pivot = new C.GridPosition(5, -3, 7);
        C.GridPosition Map(int u, int v) => axis switch
        {
            C.RotationAxis.X => new(8, pivot.Y + u, pivot.Z + v),
            C.RotationAxis.Y => new(pivot.X + v, 2, pivot.Z + u),
            _ => new(pivot.X + u, pivot.Y + v, -4)
        };
        var expected = new[] { Map(0, 0), Map(0, 1), Map(1, 0), Map(1, 1), Map(2, 0) }
            .OrderBy(p => p.X).ThenBy(p => p.Y).ThenBy(p => p.Z).ToArray();
        CollectionAssert.AreEqual(expected, Sweep(Map(1, 0), pivot, axis, 0, 10));
    }

    [TestMethod]
    public void Declared_interval_away_from_zero_still_includes_original_cells_once()
    {
        var cell = new C.GridPosition(3, 0, 0);
        var rotation = new C.RotationAppearance(C.RotationAxis.Z, new(0, 0, 0), 170, 180, 1);
        var actual = RotationSweep.Calculate([cell, cell], rotation);
        Assert.Contains(cell, actual);
        Assert.Contains(new C.GridPosition(-3, 0, 0), actual);
        Assert.DoesNotContain(new C.GridPosition(2, 0, 0), actual);
        Assert.AreEqual(actual.Length, actual.Distinct().Count());
        CollectionAssert.AreEqual(actual.ToArray(), RotationSweep.Calculate([cell], rotation).ToArray());
    }

    private static C.GridPosition[] Sweep(C.GridPosition cell, C.GridPosition pivot, C.RotationAxis axis,
        double minimum, double maximum) => RotationSweep.Calculate([cell],
            new(axis, pivot, minimum, maximum, 1)).ToArray();
}
