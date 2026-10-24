using Train.Content.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes;

internal sealed class TerrainHeightCap
{
    private readonly double[] fine;
    private readonly double[] coarse;
    private readonly int coarseX, coarseZ, coarseWidth, coarseDepth;
    internal int GridOriginX { get; }
    internal int GridOriginZ { get; }
    internal int Width { get; }
    internal int Depth { get; }
    internal long ArrayBytes => ((long)fine.Length + coarse.Length) * sizeof(double);

    private TerrainHeightCap(ScenePanBounds bounds)
    {
        GridOriginX = (int)Math.Floor(bounds.Minimum.X / 4.0) * 4;
        GridOriginZ = (int)Math.Floor(bounds.Minimum.Y / 4.0) * 4;
        Width = Math.Max(1, (int)Math.Ceiling(bounds.Maximum.X / 4.0) - GridOriginX / 4);
        Depth = Math.Max(1, (int)Math.Ceiling(bounds.Maximum.Y / 4.0) - GridOriginZ / 4);
        coarseX = (int)Math.Floor(GridOriginX / 32.0) * 32;
        coarseZ = (int)Math.Floor(GridOriginZ / 32.0) * 32;
        coarseWidth = (int)Math.Ceiling((GridOriginX + Width * 4) / 32.0) - coarseX / 32;
        coarseDepth = (int)Math.Ceiling((GridOriginZ + Depth * 4) / 32.0) - coarseZ / 32;
        fine = new double[checked(Width * Depth)];
        coarse = new double[checked(coarseWidth * coarseDepth)];
        Array.Fill(fine, double.PositiveInfinity);
        Array.Fill(coarse, double.PositiveInfinity);
    }

    internal double Minimum(int vertexX, int vertexZ, int spacing)
    {
        if (spacing != 4 && spacing != 32) throw new ArgumentOutOfRangeException(nameof(spacing));
        if (vertexX + (double)spacing <= GridOriginX || vertexX - (double)spacing >= GridOriginX + Width * 4.0 ||
            vertexZ + (double)spacing <= GridOriginZ || vertexZ - (double)spacing >= GridOriginZ + Depth * 4.0)
            return double.PositiveInfinity;
        var values = spacing == 4 ? fine : coarse;
        var originX = spacing == 4 ? GridOriginX : coarseX;
        var originZ = spacing == 4 ? GridOriginZ : coarseZ;
        var width = spacing == 4 ? Width : coarseWidth;
        var depth = spacing == 4 ? Depth : coarseDepth;
        var x = (int)Math.Floor((vertexX - (double)originX) / spacing);
        var z = (int)Math.Floor((vertexZ - (double)originZ) / spacing);
        var result = double.PositiveInfinity;
        for (var dz = -1; dz <= 0; dz++)
        for (var dx = -1; dx <= 0; dx++)
            if (x + dx >= 0 && x + dx < width && z + dz >= 0 && z + dz < depth)
                result = Math.Min(result, values[(z + dz) * width + x + dx]);
        return result;
    }

    internal static Builder Begin(ScenePanBounds bounds, Func<GridColumn, double?> columnTop,
        Func<int, int, double, double, double>? finishCell = null) => new(bounds, columnTop, finishCell);

    internal sealed class Builder
    {
        internal static Builder FromCompleted(TerrainHeightCap cap) => new(cap);
        private Builder(TerrainHeightCap cap) { this.cap = cap; Complete = true; }
        private readonly TerrainHeightCap cap;
        private Func<GridColumn, double?>? columnTop;
        private Func<int, int, double, double, double>? finishCell;
        private long rawCursor;
        private int fineCursor;
        private double globalMinimum = double.PositiveInfinity;
        internal bool Complete { get; private set; }
        internal TerrainHeightCap Result => Complete ? cap : throw new InvalidOperationException("Terrain height cap is incomplete.");
        internal int GridOriginX => cap.GridOriginX;
        internal int GridOriginZ => cap.GridOriginZ;
        internal int Width => cap.Width;
        internal int Depth => cap.Depth;

        internal Builder(ScenePanBounds bounds, Func<GridColumn, double?> columnTop,
            Func<int, int, double, double, double>? finishCell)
        {
            cap = new(bounds);
            this.columnTop = columnTop;
            this.finishCell = finishCell;
        }

        internal int Advance(int maxOperations, CancellationToken token = default)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxOperations);
            var operations = 0;
            var rawWidth = cap.Width * 4;
            var rawCount = (long)rawWidth * cap.Depth * 4;
            while (!Complete && operations < maxOperations)
            {
                token.ThrowIfCancellationRequested();
                if (rawCursor < rawCount)
                {
                    var x = (int)(rawCursor % rawWidth);
                    var z = (int)(rawCursor / rawWidth);
                    var top = columnTop!(new(cap.GridOriginX + x, cap.GridOriginZ + z));
                    if (top is { } value)
                    {
                        var index = z / 4 * cap.Width + x / 4;
                        cap.fine[index] = Math.Min(cap.fine[index], value);
                        globalMinimum = Math.Min(globalMinimum, value);
                    }
                    rawCursor++;
                }
                else
                {
                    var x = cap.GridOriginX + fineCursor % cap.Width * 4;
                    var z = cap.GridOriginZ + fineCursor / cap.Width * 4;
                    var value = cap.fine[fineCursor];
                    if (finishCell is not null) value = finishCell(x, z, value, globalMinimum);
                    cap.fine[fineCursor] = value;
                    var coarseIndex = (z - cap.coarseZ) / 32 * cap.coarseWidth + (x - cap.coarseX) / 32;
                    cap.coarse[coarseIndex] = Math.Min(cap.coarse[coarseIndex], value);
                    fineCursor++;
                    Complete = fineCursor == cap.fine.Length;
                    if (Complete) { columnTop = null; finishCell = null; }
                }
                operations++;
            }
            return operations;
        }
    }
}
