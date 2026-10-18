using System.Collections.Immutable;
using System.Numerics;
using Train.Scenes.Contracts;

namespace Train.Scenes;

internal sealed partial class NodeLayout
{
    private SceneNodeOverview? overview;
    private ScenePoint overviewOrigin;

    internal SceneNodeOverview Overview(ScenePoint origin)
    {
        if (overview is not null && overviewOrigin == origin) return overview;
        var plots = Template is null ? [] : Template.Plots.Select(plot =>
        {
            var corners = NodeSettlement.Corners(plot);
            return new SceneOverviewPlot(plot.Number, plot.Size,
                new[] { corners[0], corners[1], corners[3], corners[2] }
                    .Select(point => new Vector2((float)(point.X - origin.X), (float)(point.Z - origin.Z)))
                    .ToImmutableArray());
        }).ToImmutableArray();
        var anyTracks = EntryTracks.Values.Any(count => count > 0);
        var routes = Entries.SelectMany(entry => EntryRoutes(entry, EntryTracks[entry.Edge]))
            .Concat(StationRoutes(anyTracks)).Concat(anyTracks ? TurnRoutes() : []);
        var tracks = routes.Select(route =>
        {
            var points = ImmutableArray.CreateBuilder<Vector2>();
            Add(0);
            for (var arc = 0d; arc < route.Length;)
            {
                arc = Math.Min(arc + 1, route.Length);
                Add(arc);
            }
            return points.ToImmutable();

            void Add(double arc)
            {
                var point = route.Sample(arc).Position;
                points.Add(new((float)(point.X - origin.X), (float)(point.Z - origin.Z)));
            }
        }).ToImmutableArray();
        overviewOrigin = origin;
        return overview = new(plots, tracks);
    }
}
