using System.Collections.Immutable;
using System.Numerics;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes;

internal static class SceneDebugLines
{
    private static readonly Vector4 ChunkColor = new(1, .8f, 0, 1);
    private static readonly Vector4 SegmentColor = new(0, 1, 1, 1);
    private static readonly Vector4 CenterlineColor = new(1, 0, 1, 1);
    private static readonly Vector4 OriginXColor = new(1, 0, 0, 1);
    private static readonly Vector4 OriginYColor = new(0, 1, 0, 1);
    private static readonly Vector4 OriginZColor = new(0, 0, 1, 1);
    private static readonly Vector4 PickRayColor = new(1, 1, 1, 1);

    internal static ImmutableArray<SceneDebugLine> Generate(SceneDebugLayer layers,
        ImmutableArray<PickGrid> picking, ScenePoint origin, RailwayPath? railway,
        ImmutableArray<RailwaySegment> segments, NodeLayout? node,
        ImmutableDictionary<EdgeId, int>? nodeTracks, SceneFrameStamp stamp, ScenePickRaySample? pickRay)
    {
        var lines = ImmutableArray.CreateBuilder<SceneDebugLine>();
        if ((layers & SceneDebugLayer.Chunks) != 0)
            foreach (var grid in picking)
            foreach (var chunk in grid.Grid.Chunks)
            {
                var size = VoxelGrid.ChunkSize;
                var minimum = new Vector3(chunk.X * size, chunk.Y * size, chunk.Z * size);
                for (var x = 0; x <= size; x += size)
                for (var y = 0; y <= size; y += size)
                for (var z = 0; z <= size; z += size)
                {
                    var from = minimum + new Vector3(x, y, z);
                    if (x == 0) Add(from, from + new Vector3(size, 0, 0));
                    if (y == 0) Add(from, from + new Vector3(0, size, 0));
                    if (z == 0) Add(from, from + new Vector3(0, 0, size));
                }

                void Add(Vector3 from, Vector3 to) => lines.Add(new(
                    grid.Pose.Position + Vector3.Transform(from, grid.Pose.Rotation),
                    grid.Pose.Position + Vector3.Transform(to, grid.Pose.Rotation), ChunkColor));
            }
        if (railway is not null)
            foreach (var segment in segments)
            {
                var begin = segment.Index * RailwayCoordinates.SegmentLengthCells;
                var end = Math.Min(begin + RailwayCoordinates.SegmentLengthCells, railway.LengthCells);
                if ((layers & SceneDebugLayer.RailwaySegments) != 0)
                {
                    var width = (float)RailwayCoordinates.TerrainHalfWidth(railway.Edge.TrackLimit);
                    foreach (var arc in new[] { begin, end })
                    {
                        var at = railway.Sample(arc);
                        lines.Add(new(RailwayCoordinates.Relative(RailwayCoordinates.Add(at.Position, -at.Right * width), origin),
                            RailwayCoordinates.Relative(RailwayCoordinates.Add(at.Position, at.Right * width), origin), SegmentColor));
                    }
                }
                if ((layers & SceneDebugLayer.Centerline) != 0) Polyline(arc => railway.Sample(arc), begin, end);
            }
        if ((layers & SceneDebugLayer.Origin) != 0)
        {
            lines.Add(new(Vector3.Zero, Vector3.UnitX * VoxelGrid.ChunkSize, OriginXColor));
            lines.Add(new(Vector3.Zero, Vector3.UnitY * VoxelGrid.ChunkSize, OriginYColor));
            lines.Add(new(Vector3.Zero, Vector3.UnitZ * VoxelGrid.ChunkSize, OriginZColor));
        }
        if ((layers & SceneDebugLayer.Centerline) != 0 && node is not null)
        {
            foreach (var entry in node.Entries)
                foreach (var route in node.EntryRoutes(entry, nodeTracks![entry.Edge]))
                    Polyline(route.Sample, 0, route.Length);
            var anyTracks = nodeTracks!.Values.Any(count => count > 0);
            foreach (var route in node.StationRoutes(anyTracks)
                .Concat(anyTracks ? node.TurnRoutes() : []))
                Polyline(route.Sample, 0, route.Length);
        }
        if ((layers & SceneDebugLayer.PickRay) != 0 && pickRay is { } sample &&
            sample.Frame.Update.Frame.Session == stamp.Session && sample.Frame.Update.Frame.Request == stamp.Request &&
            sample.Frame.Update.Frame.Space == stamp.Space)
        {
            var from = RailwayCoordinates.Relative(RailwayCoordinates.Add(sample.Frame.Origin, sample.Ray.Origin), origin);
            lines.Add(new(from, from + sample.Ray.Direction * (float)sample.Ray.MaximumDistanceCells, PickRayColor));
        }
        return lines.ToImmutable();

        void Polyline(Func<double, RailwaySample> sample, double begin, double end)
        {
            var from = RailwayCoordinates.Relative(sample(begin).Position, origin);
            for (var arc = begin; arc < end;)
            {
                arc = Math.Min(arc + 1, end);
                var to = RailwayCoordinates.Relative(sample(arc).Position, origin);
                lines.Add(new(from, to, CenterlineColor));
                from = to;
            }
        }
    }
}
