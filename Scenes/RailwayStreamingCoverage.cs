using System.Collections.Immutable;
using System.Globalization;
using Train.Scenes.Contracts;

namespace Train.Scenes;

internal sealed class RailwayStreamingCoverage(RailwayPath path, ScenePoint origin, SceneStreamingSceneId scene) : ISceneStreamingCoverage
{
    public ImmutableArray<SceneStreamingPartId> FindTerrainParts(double x, double z)
    {
        if (!double.IsFinite(x) || !double.IsFinite(z)) throw new ArgumentException("Coordinates must be finite.");
        var result = ImmutableArray.CreateBuilder<SceneStreamingPartId>();
        var point = new ScenePoint(x + origin.X, 0, z + origin.Z);
        for (var index = 0; index < path.SegmentCount; index++)
        {
            var begin = index * RailwayCoordinates.SegmentLengthCells;
            var end = Math.Min(path.LengthCells, begin + RailwayCoordinates.SegmentLengthCells);
            if (path.ContainsArcRange(point, new(begin, end, RailwayCoordinates.TerrainHalfWidth(path.Edge.Tracks))))
                result.Add(new(scene, SceneStreamingPartKind.RailwaySegment, index.ToString(CultureInfo.InvariantCulture)));
        }
        return result.ToImmutable();
    }
}
