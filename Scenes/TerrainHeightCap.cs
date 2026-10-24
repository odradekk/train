using Train.Content.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes;

internal sealed class TerrainHeightCap
{
    private readonly double[] fine;
    private readonly double[] coarse;
    private readonly int coarseX, coarseZ, coarseWidth, coarseDepth;
    private int GridOriginX { get; }
    private int GridOriginZ { get; }
    private int Width { get; }
    private int Depth { get; }

    private TerrainHeightCap(ScenePanBounds bounds)
    {
        GridOriginX = (int)Math.Floor(bounds.Minimum.X / (double)TerrainTile.NearSpacingCells) * TerrainTile.NearSpacingCells;
        GridOriginZ = (int)Math.Floor(bounds.Minimum.Y / (double)TerrainTile.NearSpacingCells) * TerrainTile.NearSpacingCells;
        Width = Math.Max(1, (int)Math.Ceiling(bounds.Maximum.X / (double)TerrainTile.NearSpacingCells) - GridOriginX / TerrainTile.NearSpacingCells);
        Depth = Math.Max(1, (int)Math.Ceiling(bounds.Maximum.Y / (double)TerrainTile.NearSpacingCells) - GridOriginZ / TerrainTile.NearSpacingCells);
        coarseX = (int)Math.Floor(GridOriginX / (double)TerrainTile.FarSpacingCells) * TerrainTile.FarSpacingCells;
        coarseZ = (int)Math.Floor(GridOriginZ / (double)TerrainTile.FarSpacingCells) * TerrainTile.FarSpacingCells;
        coarseWidth = (int)Math.Ceiling((GridOriginX + Width * TerrainTile.NearSpacingCells) / (double)TerrainTile.FarSpacingCells) - coarseX / TerrainTile.FarSpacingCells;
        coarseDepth = (int)Math.Ceiling((GridOriginZ + Depth * TerrainTile.NearSpacingCells) / (double)TerrainTile.FarSpacingCells) - coarseZ / TerrainTile.FarSpacingCells;
        fine = new double[checked(Width * Depth)];
        coarse = new double[checked(coarseWidth * coarseDepth)];
        Array.Fill(fine, double.PositiveInfinity);
        Array.Fill(coarse, double.PositiveInfinity);
    }

    internal double Minimum(int vertexX, int vertexZ, int spacing)
    {
        if (vertexX + (double)spacing <= GridOriginX || vertexX - (double)spacing >= GridOriginX + Width * (double)TerrainTile.NearSpacingCells ||
            vertexZ + (double)spacing <= GridOriginZ || vertexZ - (double)spacing >= GridOriginZ + Depth * (double)TerrainTile.NearSpacingCells)
            return double.PositiveInfinity;
        var values = spacing == TerrainTile.NearSpacingCells ? fine : coarse;
        var originX = spacing == TerrainTile.NearSpacingCells ? GridOriginX : coarseX;
        var originZ = spacing == TerrainTile.NearSpacingCells ? GridOriginZ : coarseZ;
        var width = spacing == TerrainTile.NearSpacingCells ? Width : coarseWidth;
        var depth = spacing == TerrainTile.NearSpacingCells ? Depth : coarseDepth;
        var x = (int)Math.Floor((vertexX - (double)originX) / spacing);
        var z = (int)Math.Floor((vertexZ - (double)originZ) / spacing);
        var result = double.PositiveInfinity;
        for (var dz = -1; dz <= 0; dz++)
        for (var dx = -1; dx <= 0; dx++)
            if (x + dx >= 0 && x + dx < width && z + dz >= 0 && z + dz < depth)
                result = Math.Min(result, values[(z + dz) * width + x + dx]);
        return result;
    }

    internal static Builder CachedPart<TKey>(Dictionary<TKey, Builder> parts, TKey key, string partKey,
        Func<string, TerrainHeightCap?>? completed, Func<Builder> begin) where TKey : notnull
    {
        if (parts.TryGetValue(key, out var existing)) return existing;
        var work = completed?.Invoke(partKey) is { } cap ? Builder.FromCompleted(cap) : begin();
        parts.Add(key, work);
        return work;
    }

    internal static Builder Begin(ScenePanBounds bounds, Func<GridColumn, double?> columnTop,
        Func<int, int, double, double, double>? finishCell = null) => new(bounds, columnTop, finishCell);

    internal sealed class Builder
    {
        private IEnumerator<int>? work;
        private TerrainHeightCap? result;
        internal bool Complete => result is not null;
        internal TerrainHeightCap Result => result ?? throw new InvalidOperationException("Terrain height cap is incomplete.");
        internal static Builder FromCompleted(TerrainHeightCap cap) => new(cap);
        private Builder(TerrainHeightCap cap) => result = cap;

        internal Builder(ScenePanBounds bounds, Func<GridColumn, double?> columnTop,
            Func<int, int, double, double, double>? finishCell) =>
            work = Generate(new(bounds), columnTop, finishCell).GetEnumerator();

        internal int Advance(int maxOperations, CancellationToken token = default)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxOperations);
            var operations = 0;
            while (!Complete && operations < maxOperations)
            {
                token.ThrowIfCancellationRequested();
                work!.MoveNext();
                operations++;
            }
            if (Complete) { work?.Dispose(); work = null; }
            return operations;
        }

        private IEnumerable<int> Generate(TerrainHeightCap cap, Func<GridColumn, double?> columnTop,
            Func<int, int, double, double, double>? finishCell)
        {
            var globalMinimum = double.PositiveInfinity;
            var width = cap.Width * TerrainTile.NearSpacingCells;
            var depth = cap.Depth * TerrainTile.NearSpacingCells;
            for (var z = 0; z < depth; z++)
            for (var x = 0; x < width; x++)
            {
                if (columnTop(new(cap.GridOriginX + x, cap.GridOriginZ + z)) is { } top)
                {
                    var index = z / TerrainTile.NearSpacingCells * cap.Width + x / TerrainTile.NearSpacingCells;
                    cap.fine[index] = Math.Min(cap.fine[index], top);
                    globalMinimum = Math.Min(globalMinimum, top);
                }
                yield return 1;
            }
            // Finish receives every fine cell exactly once, in world row-major order.
            for (var index = 0; index < cap.fine.Length; index++)
            {
                var x = cap.GridOriginX + index % cap.Width * TerrainTile.NearSpacingCells;
                var z = cap.GridOriginZ + index / cap.Width * TerrainTile.NearSpacingCells;
                var value = cap.fine[index];
                if (finishCell is not null) value = finishCell(x, z, value, globalMinimum);
                cap.fine[index] = value;
                var coarseIndex = (z - cap.coarseZ) / TerrainTile.FarSpacingCells * cap.coarseWidth + (x - cap.coarseX) / TerrainTile.FarSpacingCells;
                cap.coarse[coarseIndex] = Math.Min(cap.coarse[coarseIndex], value);
                if (index == cap.fine.Length - 1) result = cap;
                yield return 1;
            }
        }
    }
}
