using System.Collections.Immutable;
using System.Globalization;
using Train.Content.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes;

internal sealed class RailwayStreamingCoverage(RailwayPath path, SceneStreamingSceneId scene)
{
    private readonly object gate = new();
    private int cachedSegment = -1;
    private ScenePoint cachedOrigin;
    private Dictionary<GridColumn, bool>? columns;
    internal static string SegmentKey(int index) => index.ToString(CultureInfo.InvariantCulture);
    internal RailwayPath Path => path;
    internal ISceneStreamingCoverage At(ScenePoint origin) => new Snapshot(this, origin);

    private sealed class Snapshot(RailwayStreamingCoverage coverage, ScenePoint origin) : ISceneStreamingCoverage
    {
        public ImmutableArray<SceneStreamingPartId> FindTerrainParts(double x, double z) =>
            coverage.Find(x + origin.X, z + origin.Z);
    }

    private ImmutableArray<SceneStreamingPartId> Find(double x, double z)
    {
        if (!double.IsFinite(x) || !double.IsFinite(z)) throw new ArgumentException("Coordinates must be finite.");
        var wx = Math.Floor(x); var wz = Math.Floor(z);
        var point = new ScenePoint(wx + .5, 0, wz + .5);
        var arc = path.Locate(point);
        var nearest = path.Sample(arc);
        var width = RailwayCoordinates.TerrainHalfWidth(path.Edge.Tracks);
        var lateral = (point.X - nearest.Position.X) * nearest.Right.X + (point.Z - nearest.Position.Z) * nearest.Right.Z;
        if (Math.Abs(lateral) > width) return [];
        var index = Math.Min(path.SegmentCount - 1, (int)(arc / RailwayCoordinates.SegmentLengthCells));
        lock (gate)
        {
            // Share one exact segment footprint across frame snapshots, without retaining the whole route.
            if (cachedSegment != index)
            {
                var anchor = path.Sample(index * RailwayCoordinates.SegmentLengthCells).Position;
                cachedOrigin = new(Math.Floor(anchor.X / VoxelGrid.ChunkSize) * VoxelGrid.ChunkSize,
                    0, Math.Floor(anchor.Z / VoxelGrid.ChunkSize) * VoxelGrid.ChunkSize);
                columns = RailwayTerrain.SampleColumns(path, index, cachedOrigin, static (_, _, _, _) => true);
                cachedSegment = index;
            }
            var cx = wx - cachedOrigin.X; var cz = wz - cachedOrigin.Z;
            if (cx < int.MinValue || cx >= int.MaxValue || cz < int.MinValue || cz >= int.MaxValue) return [];
            var column = new GridColumn((int)cx, (int)cz);
            return columns!.ContainsKey(column)
                ? [new(scene, SceneStreamingPartKind.RailwaySegment, SegmentKey(index))] : [];
        }
    }
}
