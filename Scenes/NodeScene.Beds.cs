using Train.Content.Contracts;

namespace Train.Scenes;

internal sealed partial class NodeScene
{
    internal static void RasterizeBed(Dictionary<GridColumn, RailBed> bed, Func<double, RailwaySample> sample,
        double length, CancellationToken token = default)
    {
        for (var arc = 0.0; arc <= length; arc = Math.Min(length, arc + 0.5))
        {
            token.ThrowIfCancellationRequested();
            var point = sample(arc);
            var half = RailwayCoordinates.BedBaseHalfWidth + RailwayCoordinates.BedBlendWidth;
            var pendingColumn = default(GridColumn);
            var pendingDistance = double.PositiveInfinity;
            for (var offset = -half; offset <= half; offset += 0.5)
            {
                var at = RailwayCoordinates.Add(point.Position, point.Right * (float)offset);
                var column = new GridColumn((int)Math.Floor(at.X), (int)Math.Floor(at.Z));
                var distance = Math.Abs(offset);
                if (column == pendingColumn) pendingDistance = Math.Min(pendingDistance, distance);
                else
                {
                    if (pendingDistance < double.PositiveInfinity)
                        PutBed(bed, pendingColumn, pendingDistance, point.Position.Y);
                    pendingColumn = column;
                    pendingDistance = distance;
                }
            }
            PutBed(bed, pendingColumn, pendingDistance, point.Position.Y);
            if (arc == length) break;
        }
    }

    private static void PutBed(Dictionary<GridColumn, RailBed> bed, GridColumn column, double distance, double height)
    {
        if (!bed.TryGetValue(column, out var old) || distance < old.Distance) bed[column] = new(distance, height);
    }
}
