using System.Numerics;
using System.Text.Json;
using Train.Scenes.Contracts;

namespace Train.App.Measurement;

internal static class FollowRouteAnalysis
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true
    };

    internal sealed record Coverage(double? AheadMeters, bool? VehicleCentersCovered,
        bool? SampledBodyCovered, bool BoundaryLimited, bool ObservationLimited, string? Missing,
        ScenePoint? FirstMissingPoint);
    internal sealed record FrameResult(long Draw, bool FirstInSpace, Coverage Scenes, Coverage Displayed);
    internal sealed record Summary(double? MinimumScenesAheadMeters, double? MinimumDisplayedAheadMeters,
        int UnknownScenesFrames, int UnknownDisplayedFrames, int MissingDisplayedCenterFrames,
        int MissingDisplayedSampledBodyFrames);
    internal sealed record Report(string Scope, double MaximumSampleSpacingMeters, FrameResult[] Frames, Summary Summary);

    internal static Report Analyze(string directory)
    {
        var frames = Read<RouteFrame>(Path.Combine(directory, "route-frames.jsonl"));
        var ranges = Read<RouteRanges>(Path.Combine(directory, "route-ranges.jsonl")).ToDictionary(item => item.Id);
        var report = Analyze(frames, ranges);
        File.WriteAllText(Path.Combine(directory, "route-analysis.json"), JsonSerializer.Serialize(report, Json));
        return report;
    }

    internal static Report Analyze(RouteFrame[] frames, IReadOnlyDictionary<long, RouteRanges> ranges)
    {
        var results = frames.Select((frame, index) => new FrameResult(frame.Draw,
            index == 0 || !SameCoordinates(frames[index - 1], frame),
            Measure(index, false), Measure(index, true))).ToArray();
        double? Minimum(Func<FrameResult, Coverage> select) => results.Select(select)
            .Where(value => !value.BoundaryLimited && !value.ObservationLimited && value.AheadMeters.HasValue)
            .Select(value => value.AheadMeters).DefaultIfEmpty(null).Min();
        return new("AABB coverage estimate along future observed vehicle-end trajectories; not exact track or full curved-body coverage. " +
            "Distances are sampled polyline lengths. Unknown poses/ranges and truncated observations are not passes.", .5,
            results, new(Minimum(item => item.Scenes), Minimum(item => item.Displayed),
                results.Count(item => item.Scenes.AheadMeters is null), results.Count(item => item.Displayed.AheadMeters is null),
                results.Count(item => item.Displayed.VehicleCentersCovered == false),
                results.Count(item => item.Displayed.SampledBodyCovered == false)));

        Coverage Measure(int index, bool displayed)
        {
            var frame = frames[index];
            var rangeId = displayed ? frame.DisplayedRanges : frame.ScenesRanges;
            if (frame.Latest is null || rangeId is null || !ranges.TryGetValue(rangeId.Value, out var range))
                return Unknown("ranges_missing");
            if (range.Coordinates != frame.Latest.Space || range.Frame.Session != frame.Latest.Session ||
                range.Frame.Request != frame.Latest.Request || range.Frame.Space != frame.Latest.Space)
                return Unknown("ranges_coordinates_mismatch");
            var vehicles = displayed ? frame.DisplayedVehicles : frame.Vehicles;
            if (!CompleteVehicles(frame, vehicles)) return Unknown("vehicle_pose_missing");
            var centers = vehicles.All(vehicle => Missing(vehicle.Position, range) is null);
            bool? body = true;
            foreach (var vehicle in vehicles)
            {
                if (End(vehicle, true) is not { } first || End(vehicle, false) is not { } last)
                {
                    if (body != false) body = null;
                    continue;
                }
                if (Samples(first, last).Any(point => Missing(point, range) is not null)) body = false;
            }
            if (Head(vehicles, frame.FirstEndLeading) is not { } head)
                return new(null, centers, body, false, false, "vehicle_length_or_rotation_missing", null);
            var firstMissing = Missing(head, range);
            if (firstMissing is not null) return new(0, centers, body, false, false, firstMissing, head);
            var previous = head;
            var cells = 0d;
            for (var next = index + 1; next < frames.Length; next++)
            {
                var future = frames[next];
                if (!SameCoordinates(frame, future) || frame.FirstEndLeading != future.FirstEndLeading)
                    return new(cells / 2, centers, body, true, false, null, null);
                var futureVehicles = displayed ? future.DisplayedVehicles : future.Vehicles;
                if (!CompleteVehicles(future, futureVehicles) || Head(futureVehicles, future.FirstEndLeading) is not { } point)
                    return new(cells / 2, centers, body, false, true, "future_vehicle_pose_or_length_missing", null);
                var lastSample = previous;
                foreach (var sample in Samples(previous, point).Skip(1))
                {
                    if (Missing(sample, range) is { } reason)
                        return new(cells / 2, centers, body, false, false, reason, sample);
                    cells += Distance(lastSample, sample);
                    lastSample = sample;
                }
                previous = point;
            }
            return new(cells / 2, centers, body, false, true, null, null);
        }
    }

    private static T[] Read<T>(string path) => File.ReadLines(path).Where(line => !string.IsNullOrWhiteSpace(line))
        .Select(line => JsonSerializer.Deserialize<T>(line, Json) ?? throw new InvalidDataException("Empty route record.")).ToArray();

    private static Coverage Unknown(string reason) => new(null, null, null, false, false, reason, null);
    private static bool SameCoordinates(RouteFrame first, RouteFrame second) => first.Latest is { } a && second.Latest is { } b &&
        a.Session == b.Session && a.Request == b.Request && a.Space == b.Space;
    private static bool CompleteVehicles(RouteFrame frame, RouteVehicle[] vehicles) => vehicles.Length > 0 &&
        vehicles.Select(item => item.Id).SequenceEqual(frame.ExpectedVehicles ?? frame.Vehicles.Select(item => item.Id).ToArray());
    private static ScenePoint? Head(RouteVehicle[] vehicles, bool firstLeading) => vehicles.Length == 0 ? null :
        End(firstLeading ? vehicles[0] : vehicles[^1], firstLeading);
    private static ScenePoint? End(RouteVehicle vehicle, bool first)
    {
        if (vehicle.LengthCells is not > 0 || vehicle.Rotation.Length != 4 || vehicle.Rotation.Any(value => !double.IsFinite(value))) return null;
        var rotation = new Quaternion((float)vehicle.Rotation[0], (float)vehicle.Rotation[1], (float)vehicle.Rotation[2], (float)vehicle.Rotation[3]);
        if (rotation.LengthSquared() < 1e-8f) return null;
        var offset = Vector3.Transform(Vector3.UnitZ, Quaternion.Normalize(rotation)) * (float)(vehicle.LengthCells.Value / 2 * (first ? 1 : -1));
        return new(vehicle.Position.X + offset.X, vehicle.Position.Y + offset.Y, vehicle.Position.Z + offset.Z);
    }

    private static string? Missing(ScenePoint point, RouteRanges range) => range.Fog.Any(bounds =>
        point.X >= bounds.Minimum.X && point.X < bounds.Maximum.X && point.Z >= bounds.Minimum.Z && point.Z < bounds.Maximum.Z) ? "fog" :
        !range.Terrain.Any(item => Contains(item.Bounds, point)) ? "terrain_bounds_missing" :
        !range.Rails.Any(item => Contains(item.Bounds, point)) ? "rail_bounds_missing" : null;

    // Height is deliberately excluded: track/ground AABBs do not share the vehicle's reference height.
    private static bool Contains(RouteBounds bounds, ScenePoint point) => point.X >= bounds.Minimum.X && point.X <= bounds.Maximum.X &&
        point.Z >= bounds.Minimum.Z && point.Z <= bounds.Maximum.Z;
    private static double Distance(ScenePoint a, ScenePoint b) => Math.Sqrt(
        (a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z));
    private static IEnumerable<ScenePoint> Samples(ScenePoint from, ScenePoint to)
    {
        var count = Math.Max(1, (int)Math.Ceiling(Distance(from, to)));
        for (var i = 0; i <= count; i++)
        {
            var fraction = i / (double)count;
            yield return new(from.X + (to.X - from.X) * fraction, from.Y + (to.Y - from.Y) * fraction,
                from.Z + (to.Z - from.Z) * fraction);
        }
    }
}
