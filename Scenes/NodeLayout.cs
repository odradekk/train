using System.Collections.Immutable;
using System.Numerics;
using System.Text.Json;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes;

internal sealed class NodeLayout
{
    // #372 starts single-vehicle trains of 52, 64 and 104 cells; leave 12 cells at each end.
    internal const int PlatformLength = 128;
    internal const int PlatformWidth = 8;
    internal const int PlatformClearance = 16;
    internal const int StationTrackSpacing = 40;
    internal const int GroundMargin = 32;
    internal const int ApproachStraight = 16;
    internal const int QueueGap = 4;
    internal sealed record Entry(EdgeId Edge, ScenePoint Point, Vector3 Outward, int TrackLimit);
    internal NodeId Node { get; }
    internal int Platforms { get; }
    internal double HalfSize { get; }
    internal string Inputs { get; }
    internal ImmutableArray<Entry> Entries { get; }
    private readonly ImmutableDictionary<(EdgeId Edge, double Offset, int Station), Track> connections;

    private NodeLayout(NodeId node, int platforms, double halfSize, string inputs,
        ImmutableArray<Entry> entries, ImmutableDictionary<(EdgeId, double, int), Track> connections)
    {
        Node = node; Platforms = platforms; HalfSize = halfSize; Inputs = inputs;
        Entries = entries; this.connections = connections;
    }

    internal static string InputKey(SceneWorldSnapshot snapshot, NodeId id)
    {
        var world = snapshot.Core.World;
        var node = world.Nodes.Single(item => item.Id == id);
        var adjacent = world.Edges.Where(edge => edge.Start == id || edge.End == id).OrderBy(edge => edge.Id.Value)
            .Select(edge => new object[] { edge.Id, edge.TrackLimit,
                world.Nodes.Single(other => other.Id == (edge.Start == id ? edge.End : edge.Start)).Position }).ToArray();
        var rules = snapshot.Content.FindRailwayAppearance(world.RailwayRule);
        return JsonSerializer.Serialize(new object[] { id, node.Position, node.Platforms, adjacent,
            rules is ContentLookup<RailwayAppearanceProjection>.Found found ? found.Value.MinimumCurveRadiusMeters : throw new RailwayBuildException("The railway appearance is missing.") });
    }

    internal static NodeLayout Build(SceneWorldSnapshot snapshot, NodeId id, CancellationToken cancellation = default)
    {
        var world = snapshot.Core.World;
        var node = world.Nodes.Single(item => item.Id == id);
        var rules = snapshot.Content.FindRailwayAppearance(world.RailwayRule) is ContentLookup<RailwayAppearanceProjection>.Found found
            ? found.Value : throw new RailwayBuildException("The railway appearance is missing.");
        var radius = rules.MinimumCurveRadiusMeters * RailwayCoordinates.CellsPerMeter;
        if (!double.IsFinite(radius) || radius <= 0) throw new RailwayBuildException("The railway curve radius is invalid.");
        var directions = world.Edges.Where(edge => edge.Start == id || edge.End == id).OrderBy(edge => edge.Id.Value).Select(edge =>
        {
            var other = world.Nodes.Single(item => item.Id == (edge.Start == id ? edge.End : edge.Start));
            var bearing = new Vector3((float)(other.Position.X - node.Position.X), 0, (float)(other.Position.Y - node.Position.Y));
            if (!SceneGeometry.Finite(bearing) || bearing.LengthSquared() == 0) throw new RailwayBuildException("The node entry bearing is invalid.");
            var direction = Vector3.Normalize(bearing);
            return (edge.Id, edge.TrackLimit, Direction: direction);
        }).ToArray();
        var platforms = node.Platforms;
        var halfLength = platforms > 0 ? PlatformLength / 2.0 : 0;
        var width = platforms > 0 ? StationTrackSpacing * (platforms - 1) / 2.0 + PlatformClearance + PlatformWidth : 0;
        var corridor = RailwayCoordinates.BedBaseHalfWidth + (directions.Length == 0 ? 0 :
            directions.Max(item => RailwayCoordinates.OutermostTrackOffset(item.TrackLimit)));
        var halfSize = Math.Ceiling(Math.Max(halfLength + GroundMargin + (directions.Length > 0 ? ApproachStraight : 0),
            Math.Max(width + GroundMargin, directions.Length > 0 ? radius * 2 + corridor + GroundMargin : 0)) / VoxelGrid.ChunkSize) * VoxelGrid.ChunkSize;
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            var entries = directions.Select(item => new Entry(item.Id,
                new(item.Direction.X * halfSize, 0, item.Direction.Z * halfSize), item.Direction, item.TrackLimit)).ToImmutableArray();
            var paths = ImmutableDictionary.CreateBuilder<(EdgeId, double, int), Track>();
            var valid = true;
            foreach (var entry in entries)
            {
                var right = Vector3.Cross(Vector3.UnitY, -entry.Outward);
                var side = entry.Outward.Z >= 0 ? 1 : -1;
                // Every possible lane offset is reserved, including odd/even track-count changes.
                for (var lane = 1 - entry.TrackLimit; lane < entry.TrackLimit && valid; lane++)
                for (var station = 0; station < Math.Max(1, platforms); station++)
                {
                    var offset = lane * RailwayCoordinates.TrackSpacing / 2;
                    var start = RailwayCoordinates.Add(entry.Point, right * (float)offset - entry.Outward * ApproachStraight);
                    var end = new ScenePoint(platforms > 0 ? StationX(station, platforms) : 0, 0, side * (halfLength + PlatformClearance));
                    Track? path = null;
                    foreach (var startHandle in new[] { .5, .75, 1.0, .25 })
                    {
                        foreach (var endHandle in new[] { .5, .25, .75, 1.0 })
                        {
                            var endLimit = Math.Sqrt(halfSize * halfSize - end.X * end.X) - Math.Abs(end.Z);
                            var candidate = new Track(start, -entry.Outward, end, -side * Vector3.UnitZ,
                                halfSize * startHandle, Math.Min(halfSize * endHandle, endLimit));
                            if (candidate.Fits(radius, platforms)) { path = candidate; break; }
                        }
                        if (path is not null) break;
                    }
                    if (path is null) { valid = false; break; }
                    paths.Add((entry.Edge, offset, station), path);
                }
                if (!valid) break;
            }
            if (valid) return new(id, platforms, halfSize, InputKey(snapshot, id), entries, paths.ToImmutable());
            halfSize = Math.Ceiling(halfSize * 1.25 / VoxelGrid.ChunkSize) * VoxelGrid.ChunkSize;
        }
    }

    internal static double StationX(int index, int count) => StationTrackSpacing * (index - (count - 1) / 2.0);
    internal Entry EntryFor(EdgeId edge) => Entries.Single(item => item.Edge == edge);
    internal Track Connection(EdgeId edge, double offset, int station) => connections[(edge, offset, station)];
    internal ScenePoint EntryPoint(EdgeId edge) => EntryFor(edge).Point;
    internal ScenePanBounds PanBounds(ScenePoint origin)
    {
        var minimum = new Vector2((float)-HalfSize - 1);
        var maximum = new Vector2((float)HalfSize + 1);
        foreach (var entry in Entries)
        {
            var right = Vector3.Cross(Vector3.UnitY, entry.Outward);
            var width = RailwayCoordinates.TerrainHalfWidth(entry.TrackLimit) + 2;
            var extent = new Vector2(Math.Abs(right.X) * (float)width + 2, Math.Abs(right.Z) * (float)width + 2);
            var at = new Vector2((float)entry.Point.X, (float)entry.Point.Z);
            minimum = Vector2.Min(minimum, at - extent); maximum = Vector2.Max(maximum, at + extent);
        }
        var shift = new Vector2((float)origin.X, (float)origin.Z);
        return new(minimum - shift, maximum - shift);
    }

    internal sealed class Track
    {
        private readonly Vector3 a, b, c, d;
        private double[]? distances;
        internal double Length => Distances[^1];
        internal Track(ScenePoint start, Vector3 startForward, ScenePoint end, Vector3 endForward, double startHandle, double endHandle)
        {
            a = RailwayCoordinates.Relative(start, default); d = RailwayCoordinates.Relative(end, default);
            b = a + startForward * (float)startHandle; c = d - endForward * (float)endHandle;
        }
        private double[] Distances
        {
            get
            {
                if (Volatile.Read(ref distances) is { } existing) return existing;
                var steps = Math.Max(32, (int)Math.Ceiling((Vector3.Distance(a, b) + Vector3.Distance(b, c) + Vector3.Distance(c, d)) * 2));
                var result = new double[steps + 1];
                var previous = a;
                for (var i = 1; i <= steps; i++)
                {
                    var point = Point((double)i / steps);
                    result[i] = result[i - 1] + Vector3.Distance(previous, point); previous = point;
                }
                return Interlocked.CompareExchange(ref distances, result, null) ?? result;
            }
        }
        private Vector3 Point(double t)
        {
            var s = 1 - t;
            return a * (float)(s * s * s) + b * (float)(3 * s * s * t) + c * (float)(3 * s * t * t) + d * (float)(t * t * t);
        }
        private Vector3 Tangent(double t) => (b - a) * (float)(3 * (1 - t) * (1 - t)) +
            (c - b) * (float)(6 * (1 - t) * t) + (d - c) * (float)(3 * t * t);
        internal bool Fits(double radius, int platforms)
        {
            for (var i = 0; i <= 1024; i++)
            {
                var t = i / 1024.0;
                var tangent = Tangent(t);
                var second = (c - 2 * b + a) * (float)(6 * (1 - t)) + (d - 2 * c + b) * (float)(6 * t);
                var speed = tangent.Length();
                if (speed <= 0 || Vector3.Cross(tangent, second).Length() * radius * 1.05 > speed * speed * speed) return false;
                var point = Point(t);
                for (var station = 0; station < platforms; station++)
                {
                    var left = StationX(station, platforms) + PlatformClearance;
                    var dx = Math.Max(left - point.X, Math.Max(0, point.X - left - PlatformWidth));
                    var dz = Math.Max(0, Math.Abs(point.Z) - PlatformLength / 2.0);
                    if (dx * dx + dz * dz < PlatformClearance * PlatformClearance - 0.001) return false;
                }
            }
            return true;
        }
        internal RailwaySample Sample(double arc)
        {
            var distances = Distances;
            var bounded = Math.Clamp(arc, 0, Length);
            var index = Array.BinarySearch(distances, bounded);
            var t = index >= 0 ? (double)index / (distances.Length - 1) :
                ((~index - 1) + (bounded - distances[~index - 1]) / (distances[~index] - distances[~index - 1])) / (distances.Length - 1);
            var forward = Vector3.Normalize(Tangent(t));
            var point = Point(t) + forward * (float)(arc - bounded);
            return new(new(point.X, point.Y, point.Z), forward, Vector3.Cross(Vector3.UnitY, forward));
        }
    }
}
