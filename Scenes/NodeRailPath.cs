using System.Numerics;
using Train.Scenes.Contracts;

namespace Train.Scenes;

internal sealed class NodeRailPath(double length, Func<double, RailwaySample> sample)
{
    internal double Length { get; } = length;
    internal RailwaySample Sample(double arc) => sample(arc);

    internal NodeRailPath Slice(double begin, double end) => new(end - begin, arc => Sample(begin + arc));
    internal NodeRailPath Reverse() => new(Length, arc =>
    {
        var value = Sample(Length - arc);
        return value with { Forward = -value.Forward, Right = -value.Right };
    });

    internal static NodeRailPath Straight(ScenePoint start, Vector3 forward, double length) =>
        new(length, arc => new(RailwayCoordinates.Add(start, forward * (float)arc), forward,
            Vector3.Normalize(Vector3.Cross(Vector3.UnitY, forward))));

    internal static NodeRailPath From(NodeLayout.Track track) => new(track.Length, track.Sample);

    internal static NodeRailPath Join(params NodeRailPath[] paths)
    {
        var parts = paths.Where(path => path.Length > 1e-8).ToArray();
        if (parts.Length == 0) return paths[0];
        return new(parts.Sum(path => path.Length), arc =>
        {
            foreach (var part in parts)
            {
                if (arc <= part.Length) return part.Sample(arc);
                arc -= part.Length;
            }
            return parts[^1].Sample(parts[^1].Length + arc);
        });
    }
}
