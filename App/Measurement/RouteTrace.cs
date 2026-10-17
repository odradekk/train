using System.Text.Json;
using Train.Scenes.Contracts;

namespace Train.App.Measurement;

internal sealed record RouteVehicle(ulong Id, ScenePoint Position, double[] Rotation, double? LengthCells);
internal sealed record RouteBounds(ScenePoint Minimum, ScenePoint Maximum);
internal sealed record RouteRange(ulong Object, int Visual, RouteBounds Bounds);
internal sealed record RouteRanges(long Id, SceneFrameStamp Frame, SceneSpaceId Coordinates,
    RouteRange[] Terrain, RouteRange[] Rails, RouteBounds[] Fog);
internal sealed record RouteFrame(long Draw, double At, double FrameSeconds, ulong Steps, ulong StepDelta,
    string RequestedSpeed, SceneFrameStamp? Latest, SceneFrameStamp? Displayed, SceneFrameStamp? Posed,
    long? ScenesRanges, long? DisplayedRanges, RouteVehicle[] Vehicles, RouteVehicle[] DisplayedVehicles,
    bool FirstEndLeading, JsonElement Position, double SimulationSeconds, double ObservationSeconds,
    ulong[]? ExpectedVehicles = null);
