using C = Train.Content.Contracts;
using K = Train.Core.Contracts;

namespace Train.Content.Designs;

internal static class PlotRules
{
    internal static bool IsStation(K.PlotSize size) => size is K.PlotSize.SmallStation or K.PlotSize.LargeStation;

    internal static bool Fits(K.PlotSize building, K.PlotSize plot) =>
        IsStation(building) == IsStation(plot) && building <= plot;

    internal static C.GridBounds Envelope(K.PlotSize size)
    {
        var (width, depth, height) = size switch
        {
            K.PlotSize.Small => (48, 48, 48), K.PlotSize.Medium => (96, 96, 80),
            K.PlotSize.Large => (192, 192, 128), K.PlotSize.Giant => (384, 384, 256),
            K.PlotSize.SmallStation => (192, 112, 48), K.PlotSize.LargeStation => (256, 208, 64),
            _ => throw new ArgumentOutOfRangeException(nameof(size))
        };
        return new(new(-width / 2, 0, -depth / 2), new(width - width / 2 - 1, height - 1, depth - depth / 2 - 1));
    }

    internal static (long MinX, long MaxX, long MinZ, long MaxZ) Footprint(C.SettlementPlotLayout plot)
    {
        var bounds = Envelope(plot.Size);
        var a = bounds.Minimum;
        var b = bounds.Maximum;
        // Rotate the inclusive integer bounds: negation preserves the one-cell offset of even sizes.
        var (minX, maxX, minZ, maxZ) = plot.Orientation switch
        {
            C.VoxelOrientation.PxPy => (a.X, b.X, a.Z, b.Z),
            C.VoxelOrientation.PzPy => (-b.Z, -a.Z, a.X, b.X),
            C.VoxelOrientation.NxPy => (-b.X, -a.X, -b.Z, -a.Z),
            C.VoxelOrientation.NzPy => (a.Z, b.Z, -b.X, -a.X),
            _ => throw new ArgumentOutOfRangeException(nameof(plot))
        };
        return ((long)plot.At.X + minX, (long)plot.At.X + maxX,
            (long)plot.At.Z + minZ, (long)plot.At.Z + maxZ);
    }
}
