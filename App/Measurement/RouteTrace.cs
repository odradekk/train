using System.Text.Json;
using Train.Scenes.Contracts;

namespace Train.App.Measurement;

internal sealed record RouteVehicle(ulong Id, ScenePoint Position, double? LengthCells);
internal sealed record RouteBounds(ScenePoint Minimum, ScenePoint Maximum);
internal sealed record RouteRange(ulong Object, int Visual, RouteBounds Bounds);
internal sealed record RouteRangeKey(ulong Object, int Visual);
internal sealed record RouteRanges(long Id, SceneFrameStamp Frame, SceneSpaceId Coordinates,
    RouteRange[] Terrain, RouteRange[] Rails, RouteBounds[] Fog);
internal sealed record RouteRangeChange(long Id, long? Base, SceneFrameStamp Frame, SceneSpaceId Coordinates,
    RouteRange[] Terrain, RouteRange[] Rails, RouteRangeKey[] Removed, RouteBounds[]? Fog);
internal sealed record RouteTransition(SceneFrameStamp From, ScenePoint FromOrigin,
    SceneFrameStamp To, ScenePoint ToOrigin, bool MultipleTracks);
// At and Steps support per-frame gap durations and alignment with profiling traces.
internal sealed record RouteFrame(long Draw, double At, ulong Steps,
    SceneFrameStamp? Latest, SceneFrameStamp? Displayed,
    long? ScenesRanges, long? DisplayedRanges, RouteVehicle[] Vehicles, RouteVehicle[] DisplayedVehicles,
    bool FirstEndLeading, JsonElement Position, double ObservationSeconds,
    ulong[]? ExpectedVehicles = null, double? TrainLengthMeters = null,
    bool Operable = true, RouteTransition? Transition = null, bool DecisionSample = true, SceneReadiness? DisplayedReadiness = null);
