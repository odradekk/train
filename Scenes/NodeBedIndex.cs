using Train.Content.Contracts;

namespace Train.Scenes;

internal sealed class NodeBedIndex
{
    private sealed record Block(RailwaySample[] Samples, int Left, int Back, int Right, int Front);
    private readonly Block[] blocks;
    internal bool HasBeds => blocks.Length != 0;

    internal NodeBedIndex(IEnumerable<(Func<double, RailwaySample> Sample, double Length)> routes,
        CancellationToken token = default)
    {
        var built = new List<Block>();
        foreach (var route in routes)
        {
            var samples = new List<RailwaySample>(32);
            var left = int.MaxValue; var back = int.MaxValue;
            var right = int.MinValue; var front = int.MinValue;
            for (var arc = 0.0; arc <= route.Length; arc = Math.Min(route.Length, arc + .5))
            {
                token.ThrowIfCancellationRequested();
                var sample = route.Sample(arc);
                samples.Add(sample);
                var half = RailwayCoordinates.BedBaseHalfWidth + RailwayCoordinates.BedBlendWidth;
                Include(-half); Include(half);
                if (samples.Count == 32 || arc == route.Length)
                {
                    built.Add(new(samples.ToArray(), left, back, right, front));
                    samples.Clear();
                    left = back = int.MaxValue; right = front = int.MinValue;
                }
                if (arc == route.Length) break;

                void Include(double offset)
                {
                    var at = RailwayCoordinates.Add(sample.Position, sample.Right * (float)offset);
                    var x = (int)Math.Floor(at.X); var z = (int)Math.Floor(at.Z);
                    left = Math.Min(left, x); right = Math.Max(right, x);
                    back = Math.Min(back, z); front = Math.Max(front, z);
                }
            }
        }
        blocks = built.ToArray();
    }

    internal Dictionary<GridColumn, NodeScene.RailBed> ForChunk(GridColumn from, CancellationToken token = default) =>
        Query(from.X - 1, from.Z - 1, from.X + VoxelGrid.ChunkSize, from.Z + VoxelGrid.ChunkSize, null, token);

    internal Dictionary<GridColumn, NodeScene.RailBed> ForColumns(HashSet<GridColumn> columns, CancellationToken token = default)
    {
        if (columns.Count == 0) return [];
        return Query(columns.Min(column => column.X), columns.Min(column => column.Z),
            columns.Max(column => column.X), columns.Max(column => column.Z), columns, token);
    }

    internal NodeScene.RailBed? At(GridColumn column) =>
        Query(column.X, column.Z, column.X, column.Z, null, default).GetValueOrDefault(column);

    private static bool Intersects(Block block, HashSet<GridColumn> columns)
    {
        foreach (var column in columns)
            if (column.X >= block.Left && column.X <= block.Right && column.Z >= block.Back && column.Z <= block.Front)
                return true;
        return false;
    }

    private Dictionary<GridColumn, NodeScene.RailBed> Query(int left, int back, int right, int front,
        HashSet<GridColumn>? columns, CancellationToken token)
    {
        var bed = new Dictionary<GridColumn, NodeScene.RailBed>();
        foreach (var block in blocks)
        {
            token.ThrowIfCancellationRequested();
            if (block.Left > right || block.Right < left || block.Back > front || block.Front < back) continue;
            if (columns is not null && !Intersects(block, columns)) continue;
            foreach (var point in block.Samples)
            {
                token.ThrowIfCancellationRequested();
                var half = RailwayCoordinates.BedBaseHalfWidth + RailwayCoordinates.BedBlendWidth;
                var pending = default(GridColumn);
                var distance = double.PositiveInfinity;
                for (var offset = -half; offset <= half; offset += .5)
                {
                    var at = RailwayCoordinates.Add(point.Position, point.Right * (float)offset);
                    var column = new GridColumn((int)Math.Floor(at.X), (int)Math.Floor(at.Z));
                    if (column == pending) distance = Math.Min(distance, Math.Abs(offset));
                    else
                    {
                        if (distance < double.PositiveInfinity) Put(pending, distance, point.Position.Y);
                        pending = column; distance = Math.Abs(offset);
                    }
                }
                Put(pending, distance, point.Position.Y);
            }
        }
        return bed;

        void Put(GridColumn column, double distance, double height)
        {
            if (column.X < left || column.X > right || column.Z < back || column.Z > front ||
                columns is not null && !columns.Contains(column)) return;
            // The original route, arc and offset order decides equal-distance heights.
            if (!bed.TryGetValue(column, out var old) || distance < old.Distance) bed[column] = new(distance, height);
        }
    }
}
