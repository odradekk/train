using System.Collections.Immutable;
using System.Numerics;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes;

internal sealed class NodeCorridor
{
    internal ImmutableHashSet<GridColumn> Chunks { get; }

    internal NodeCorridor(NodeLayout layout, NodeView dimensions, TrainPosition.Node position,
        double lengthCells, ImmutableArray<NodeLayout.Stop> platforms)
    {
        var stops = position.PlatformNumber is { } number
            ? platforms.Skip(number - 1).Take(1)
            : platforms.Append(layout.Bypass);
        var exits = position.NextEdge is { } edge
            ? layout.Entries.Where(entry => entry.Edge == edge)
            : layout.Entries.Where(entry => layout.EntryTracks.GetValueOrDefault(entry.Edge) > 0);
        var routes = ImmutableArray.CreateBuilder<ScenePoint[]>();
        foreach (var stop in stops.Distinct())
        foreach (var exit in exits.Cast<NodeLayout.Entry?>().DefaultIfEmpty())
        foreach (var track in position.NextTrack is { } selected ? [selected] :
            Enumerable.Range(1, Math.Max(1, exit is null ? 0 : layout.EntryTracks.GetValueOrDefault(exit.Edge))))
        {
            var route = position with { NextEdge = exit?.Edge, NextTrack = exit is null ? null : track };
            var inbound = new NodeTrainPath(layout, dimensions, route with { Phase = NodeTravelPhase.Approaching }, stop, lengthCells, null);
            var outbound = new NodeTrainPath(layout, dimensions, route with { Phase = NodeTravelPhase.Departing }, stop, lengthCells, null);
            var begin = Math.Clamp(inbound.Arc(position.DistanceMeters), 0, inbound.Length);
            var points = new List<ScenePoint>();
            if (position.Phase != NodeTravelPhase.Departing && begin < inbound.StopArc)
                points.AddRange(NodeScene.SamplePath(inbound, begin, inbound.StopArc));
            points.AddRange(NodeScene.SamplePath(outbound, Math.Max(begin, outbound.StopArc), outbound.Length));
            routes.Add(points.ToArray());
        }
        var chunks = ImmutableHashSet.CreateBuilder<GridColumn>();
        foreach (var points in routes)
        for (var i = 0; i < points.Length; i++)
        {
            ScenePoint[] segment = [points[Math.Max(0, i - 1)], points[i]];
            foreach (var from in new NodeRange(points[i], NodeStreaming.NearCells + NodeStreaming.PathSampleCells).Chunks(layout))
                if (!chunks.Contains(from) && NodeStreaming.PathPriority(new(new(from.X, -128, from.Z),
                    new(from.X + VoxelGrid.ChunkSize, 128, from.Z + VoxelGrid.ChunkSize)), segment).Distance <=
                    NodeStreaming.NearCells * NodeStreaming.NearCells) chunks.Add(from);
        }
        Chunks = chunks.ToImmutable();
    }

    internal bool Intersects(SceneBounds bounds)
    {
        var size = VoxelGrid.ChunkSize;
        for (var z = (int)Math.Floor(bounds.Minimum.Z / size) * size; z < bounds.Maximum.Z; z += size)
        for (var x = (int)Math.Floor(bounds.Minimum.X / size) * size; x < bounds.Maximum.X; x += size)
            if (Chunks.Contains(new(x, z))) return true;
        return false;
    }

    internal bool Intersects(SceneBounds bounds, ScenePose pose)
    {
        var minimum = new Vector3(float.MaxValue);
        var maximum = new Vector3(float.MinValue);
        foreach (var x in new[] { bounds.Minimum.X, bounds.Maximum.X })
        foreach (var y in new[] { bounds.Minimum.Y, bounds.Maximum.Y })
        foreach (var z in new[] { bounds.Minimum.Z, bounds.Maximum.Z })
        {
            var point = pose.Position + Vector3.Transform(new(x, y, z), pose.Rotation);
            minimum = Vector3.Min(minimum, point);
            maximum = Vector3.Max(maximum, point);
        }
        return Intersects(new(minimum, maximum));
    }
}
