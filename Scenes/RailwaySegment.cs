using System.Collections.Immutable;
using Train.Content.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes;

internal sealed record RailwaySegment(int Index, ScenePoint Origin, RenderedDesign Geometry,
    ImmutableDictionary<GridColumn, double> Heights, RailwayDependencies Dependencies, ImmutableArray<(double Start, double End)> Tunnels);
