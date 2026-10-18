using System.Text.Json;
using Train.Scenes.Contracts;

namespace Train.App.Measurement;

internal static class FollowRouteAnalysis
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true };
    internal sealed record Coverage(double? AheadMeters, bool? VehicleCentersCovered,
        bool? SampledBodyCovered, bool BoundaryLimited, bool ObservationLimited, string? Missing,
        ScenePoint? FirstMissingPoint, string? BodyUnknown = null);
    internal sealed record FrameResult(long Draw, bool FirstInSpace, Coverage Scenes, Coverage Displayed,
        bool DecisionEligible = true, string? ExcludedReason = null);
    internal sealed record Summary(double? MinimumScenesAheadMeters, double? MinimumDisplayedAheadMeters,
        int MissingDisplayedCenterFrames,
        int MissingDisplayedSampledBodyFrames, int UnknownScenesBodyFrames, int UnknownDisplayedBodyFrames,
        int ExcludedPreoperableFrames, int ObservationLimitedScenesFrames, int ObservationLimitedDisplayedFrames,
        double? MinimumScenesAheadLowerBoundMeters, double? MinimumDisplayedAheadLowerBoundMeters,
        int UnknownScenes32MetersFrames, int UnknownDisplayed32MetersFrames,
        int UnknownScenes64MetersFrames, int UnknownDisplayed64MetersFrames);
    internal sealed record StartupResult(double? FirstOperableAt, double? FirstReadyAt, double ReadyBudgetSeconds,
        string Status, string? Reason, int ExcludedFrames);
    internal sealed record Report(string Scope, double MaximumSampleSpacingMeters, FrameResult[] Frames, Summary Summary, StartupResult Startup);
    private sealed record PathPoint(ScenePoint? Head, ScenePoint Offset, int History, string Reason,
        double Arc, ScenePoint? Center, double CenterArc, double TargetArc, int Start, int End);
    private sealed record Ahead(double Cells, bool Boundary, bool Observation, string? Missing, ScenePoint? Point);

    internal static Report Analyze(string directory)
    {
        var frames = File.ReadLines(Path.Combine(directory, "route-frames.jsonl")).Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonSerializer.Deserialize<RouteFrame>(line, Json)!).ToArray();
        var report = Analyze(frames, ReadRanges(File.ReadLines(Path.Combine(directory, "route-ranges.jsonl"))));
        File.WriteAllText(Path.Combine(directory, "route-analysis.json"), JsonSerializer.Serialize(report, Json));
        return report;
    }

    internal static Dictionary<long, RouteRanges> ReadRanges(IEnumerable<string> lines)
    {
        var states = new Dictionary<long, RouteRanges>();
        foreach (var line in lines.Where(line => !string.IsNullOrWhiteSpace(line)))
        {
            var change = JsonSerializer.Deserialize<RouteRangeChange>(line, Json)!;
            var previous = change.Base is { } baseline ? states[baseline] : null;
            var terrain = previous?.Terrain.ToDictionary(item => new RouteRangeKey(item.Object, item.Visual)) ?? [];
            var rails = previous?.Rails.ToDictionary(item => new RouteRangeKey(item.Object, item.Visual)) ?? [];
            foreach (var key in change.Removed) { terrain.Remove(key); rails.Remove(key); }
            foreach (var item in change.Terrain) { var key = new RouteRangeKey(item.Object, item.Visual); rails.Remove(key); terrain[key] = item; }
            foreach (var item in change.Rails) { var key = new RouteRangeKey(item.Object, item.Visual); terrain.Remove(key); rails[key] = item; }
            states.Add(change.Id, new(change.Id, change.Frame, change.Coordinates,
                terrain.Values.ToArray(), rails.Values.ToArray(), change.Fog ?? previous?.Fog ?? []));
        }
        return states;
    }

    internal static Report Analyze(RouteFrame[] frames, IReadOnlyDictionary<long, RouteRanges> ranges)
    {
        var paths = new[] { BuildPath(frames, false), BuildPath(frames, true) };
        var spaceEnds = new int[frames.Length];
        for (var index = frames.Length - 1; index >= 0; index--)
            spaceEnds[index] = index + 1 < frames.Length && SameCoordinates(frames[index], frames[index + 1])
                ? spaceEnds[index + 1] : index;
        var aheadCache = new Dictionary<(long Range, bool Displayed, int Index), Ahead>();
        var firstOperable = Array.FindIndex(frames, frame => frame.Operable);
        var initial = Array.FindIndex(frames, frame => frame.Latest is not null);
        var initialEnd = initial < 0 ? -1 : spaceEnds[initial];
        var startupOperable = initial < 0 ? -1 : Array.FindIndex(frames, initial, initialEnd - initial + 1, frame => frame.Operable);
        var startupAt = startupOperable < 0 ? (double?)null : frames[startupOperable].At;
        bool StartupFrame(int index) => startupAt is { } at && index >= startupOperable && index <= initialEnd && frames[index].At < at + 5;
        var ready = startupOperable < 0 ? -1 : Array.FindIndex(frames, startupOperable, initialEnd - startupOperable + 1,
            frame => frame.DisplayedReadiness == SceneReadiness.Ready && frame.Latest is { } latest &&
                frame.Displayed is { } displayed && SameSpace(latest, displayed));
        var readinessUnknown = startupOperable >= 0 && frames.Skip(startupOperable).Take(initialEnd - startupOperable + 1)
            .Any(frame => frame.At <= startupAt + 5 && frame.DisplayedReadiness is null);
        string startupStatus;
        string? startupReason;
        if (startupAt is null)
            (startupStatus, startupReason) = ("not_observed", "initial_operable_unobserved");
        else if (ready >= 0 && frames[ready].At <= startupAt + 5)
            (startupStatus, startupReason) = ("passed", null);
        else if (readinessUnknown)
            (startupStatus, startupReason) = ("unknown", "readiness_unobserved");
        else if (ready >= 0 || frames[initialEnd].At >= startupAt + 5)
            (startupStatus, startupReason) = ("failed", "ready_deadline_missed");
        else
            (startupStatus, startupReason) = ("unknown", initialEnd + 1 < frames.Length ? "initial_space_left_before_ready" : "ready_unobserved");
        var results = frames.Select((frame, index) => new FrameResult(frame.Draw,
            index == 0 || !SameCoordinates(frames[index - 1], frame), Measure(index, false), Measure(index, true),
            frame.DecisionSample && firstOperable >= 0 && index >= firstOperable && !StartupFrame(index),
            !frame.DecisionSample ? "postroll" : StartupFrame(index) ? "initial_startup" : firstOperable >= 0 && index >= firstOperable ? null : "initial_preoperable")).ToArray();
        var eligible = results.Where(item => item.DecisionEligible).ToArray();
        double? Minimum(Func<FrameResult, Coverage> select) => eligible.Select(select)
            .Where(value => !value.BoundaryLimited && !value.ObservationLimited && value.AheadMeters.HasValue)
            .Select(value => value.AheadMeters).DefaultIfEmpty(null).Min();
        int UnknownAt(Func<FrameResult, Coverage> select, double target) => eligible.Select(select).Count(value =>
            value.AheadMeters is null || (!value.BoundaryLimited && value.ObservationLimited && value.AheadMeters < target));
        int Limited(Func<FrameResult, Coverage> select) => eligible.Select(select)
            .Count(value => !value.BoundaryLimited && value.ObservationLimited && value.AheadMeters.HasValue);
        double? LowerBound(Func<FrameResult, Coverage> select) => eligible.Select(select)
            .Where(value => !value.BoundaryLimited && value.ObservationLimited && value.AheadMeters.HasValue)
            .Select(value => value.AheadMeters).DefaultIfEmpty(null).Min();
        return new("XZ AABB coverage estimate. Body follows stitched observed head history for the full train length; " +
            "centers are diagnostic only. Forward distance uses same-space future head polylines. " +
            "Space boundaries are estimated from the first new-space head minus half the train length, with max(2 m, crossing-frame travel) tolerance. " +
            "Unknown or truncated observations are not passes.", .5,
            results, new(Minimum(item => item.Scenes), Minimum(item => item.Displayed),
                eligible.Count(item => item.Displayed.VehicleCentersCovered == false), eligible.Count(item => item.Displayed.SampledBodyCovered == false),
                eligible.Count(item => item.Scenes.SampledBodyCovered is null), eligible.Count(item => item.Displayed.SampledBodyCovered is null),
                results.Count(item => item.ExcludedReason == "initial_preoperable"),
                Limited(item => item.Scenes), Limited(item => item.Displayed),
                LowerBound(item => item.Scenes), LowerBound(item => item.Displayed),
                UnknownAt(item => item.Scenes, 32), UnknownAt(item => item.Displayed, 32),
                UnknownAt(item => item.Scenes, 64), UnknownAt(item => item.Displayed, 64)),
            new(startupAt, ready >= 0 ? frames[ready].At : null, 5, startupStatus, startupReason,
                results.Count(item => item.ExcludedReason == "initial_startup")));

        Coverage Measure(int index, bool displayed)
        {
            var frame = frames[index];
            var id = displayed ? frame.DisplayedRanges : frame.ScenesRanges;
            if (frame.Latest is null || id is null || !ranges.TryGetValue(id.Value, out var range)) return Unknown("ranges_missing");
            if (range.Coordinates != frame.Latest.Space || !SameSpace(range.Frame, frame.Latest)) return Unknown("ranges_coordinates_mismatch");
            var vehicles = displayed ? frame.DisplayedVehicles : frame.Vehicles;
            if (!CompleteVehicles(frame, vehicles)) return Unknown("vehicle_pose_missing");
            var centers = vehicles.All(vehicle => Missing(vehicle.Position, range) is null);
            var path = paths[displayed ? 1 : 0];
            if (path[index].Head is null)
            {
                var reason = double.IsFinite(path[index].TargetArc) ? "head_future_unobserved" : "leading_vehicle_length_missing";
                return new(null, centers, null, false, true, reason, null, reason);
            }
            var body = Body(index, path, range);
            var ahead = Forward(index, displayed, range, path);
            return new(ahead.Cells / 2, centers, body.Covered, ahead.Boundary, ahead.Observation, ahead.Missing, ahead.Point, body.Unknown);
        }

        (bool? Covered, string? Unknown) Body(int index, PathPoint[] path, RouteRanges range)
        {
            if (frames[index].TrainLengthMeters is not > 0) return (null, "train_length_missing");
            var length = frames[index].TrainLengthMeters!.Value * 2;
            if (path[index].Arc + 1e-8 < length) return (null, path[index].Reason);
            foreach (var point in ArcSamples(path, index, path[index].TargetArc - length, path[index].TargetArc))
                if (Missing(Subtract(point, path[index].Offset), range) is not null) return (false, null);
            return (true, null);
        }

        (double Arc, double Tolerance)? EstimatedBoundary(int index, PathPoint[] path)
        {
            var end = spaceEnds[index];
            if (end + 1 >= frames.Length || !IsSpaceChange(frames[end], frames[end + 1]) ||
                path[end + 1].History != path[index].History || path[end + 1].Head is null ||
                frames[end + 1].TrainLengthMeters is not > 0) return null;
            // Core changes space at the reference center. All arcs are in 0.5 m cells.
            return (path[end + 1].TargetArc - frames[end + 1].TrainLengthMeters!.Value,
                Math.Max(4, path[end + 1].TargetArc - path[end].TargetArc));
        }

        Ahead Forward(int index, bool displayed, RouteRanges range, PathPoint[] path)
        {
            if (aheadCache.TryGetValue((range.Id, displayed, index), out var cached)) return cached;
            var pending = new List<(int Index, double Length)>();
            var cursor = index;
            Ahead result;
            while (true)
            {
                if (aheadCache.TryGetValue((range.Id, displayed, cursor), out result!)) break;
                var boundary = EstimatedBoundary(cursor, path);
                if (boundary is { } reached && path[cursor].TargetArc >= reached.Arc)
                { result = new(0, true, false, null, null); break; }
                var head = Subtract(path[cursor].Head!.Value, path[cursor].Offset);
                if (Missing(head, range) is { } missing)
                {
                    result = new(0, NearBoundary(path[cursor].TargetArc), false, missing, head);
                    break;
                }
                var next = cursor + 1;
                if (next == frames.Length) { result = new(0, false, true, null, null); break; }
                if (IsSpaceChange(frames[cursor], frames[next]) && boundary is null)
                { result = new(0, false, true, "boundary_position_unknown", null); break; }
                if (!SameCoordinates(frames[cursor], frames[next]) && !IsSpaceChange(frames[cursor], frames[next]))
                { result = new(0, false, true, "future_coordinate_context_changed", null); break; }
                if (path[cursor].History != path[next].History)
                { result = new(0, false, true, "future_path_changed", null); break; }
                if (path[next].Head is null) { result = new(0, false, true, "future_center_history_insufficient", null); break; }
                var endArc = path[next].TargetArc;
                var boundaryClipped = boundary is { } limit && endArc >= limit.Arc;
                if (boundaryClipped) endArc = boundary!.Value.Arc;
                var length = 0d;
                var previous = head;
                var gapArc = path[cursor].TargetArc;
                string? reason = null; ScenePoint? missingPoint = null;
                foreach (var unified in ArcSamples(path, cursor, path[cursor].TargetArc, endArc).Skip(1))
                {
                    var point = Subtract(unified, path[cursor].Offset);
                    if (Missing(point, range) is { } gap)
                    {
                        gapArc = path[cursor].TargetArc + length + Distance(previous, point);
                        reason = gap; missingPoint = point;
                        break;
                    }
                    length += Distance(previous, point); previous = point;
                }
                if (reason is not null) { result = new(length, NearBoundary(gapArc), false, reason, missingPoint); break; }
                if (boundaryClipped) { result = new(length, true, false, null, null); break; }
                pending.Add((cursor, length)); cursor = next;

                bool NearBoundary(double arc) => boundary is { } value && Math.Abs(arc - value.Arc) <= value.Tolerance;
            }
            Cache(cursor, result);
            for (var i = pending.Count - 1; i >= 0; i--)
            {
                result = result with { Cells = result.Cells + pending[i].Length };
                Cache(pending[i].Index, result);
            }
            return result;

            void Cache(int at, Ahead value)
            {
                // Only frames that actually reference this frozen state can reuse its suffix.
                if ((displayed ? frames[at].DisplayedRanges : frames[at].ScenesRanges) == range.Id)
                    aheadCache[(range.Id, displayed, at)] = value;
            }
        }
    }

    private static PathPoint[] BuildPath(RouteFrame[] frames, bool displayed)
    {
        var result = new PathPoint[frames.Length];
        var offset = new ScenePoint(); var history = 0; var reason = "initial_history_insufficient";
        for (var index = 0; index < frames.Length; index++)
        {
            var frame = frames[index];
            if (index > 0)
            {
                var previous = frames[index - 1];
                var reset = frame.FirstEndLeading != previous.FirstEndLeading ? "orientation_changed" :
                    !Expected(frame).SequenceEqual(Expected(previous)) ? "vehicle_membership_changed" :
                    NodePathChanged(previous.Position, frame.Position) ? "node_path_changed" : null;
                if (!SameCoordinates(previous, frame))
                {
                    if (frame.Transition is { } transition && previous.Latest is { } from && frame.Latest is { } to &&
                        SameSpace(transition.From, from) && SameSpace(transition.To, to))
                    {
                        offset = Add(offset, Subtract(transition.FromOrigin, transition.ToOrigin));
                        if (transition.MultipleTracks) reset = "multitrack_boundary";
                    }
                    else { offset = default; reset = "transition_missing"; }
                }
                if (result[index - 1].Center is null) reset ??= "history_pose_missing";
                if (reset is not null) { history++; reason = reset; }
            }
            var vehicles = displayed ? frame.DisplayedVehicles : frame.Vehicles;
            var vehicle = CompleteVehicles(frame, vehicles) ? (frame.FirstEndLeading ? vehicles[0] : vehicles[^1]) : null;
            if (vehicle is null && index > 0 && result[index - 1].Center is not null)
            { history++; reason = "history_pose_missing"; }
            var unified = vehicle is not null ? Add(vehicle.Position, offset) : (ScenePoint?)null;
            var arc = 0d;
            if (index > 0 && result[index - 1] is { Center: { } last } previousPoint &&
                unified is { } current && previousPoint.History == history)
            {
                var distance = Distance(last, current);
                arc = previousPoint.CenterArc + distance;
            }
            var target = vehicle?.LengthCells is > 0 ? arc + vehicle.LengthCells.Value / 2 : double.NaN;
            result[index] = new(null, offset, history, reason, 0, unified, arc, target, 0, 0);
        }
        for (var start = 0; start < result.Length;)
        {
            var end = start;
            while (end + 1 < result.Length && result[end + 1].History == result[start].History) end++;
            double? initialHeadArc = null;
            for (var index = start; index <= end; index++)
            {
                var point = result[index];
                result[index] = point with { Start = start, End = end };
                var head = AtArc(result, index, point.TargetArc);
                if (head is not null) initialHeadArc ??= point.TargetArc;
                result[index] = result[index] with { Head = head, Arc = initialHeadArc is { } first ? point.TargetArc - first : 0 };
            }
            start = end + 1;
        }
        return result;
    }

    private static ScenePoint? AtArc(PathPoint[] path, int index, double arc)
    {
        var point = path[index];
        if (!double.IsFinite(arc) || arc < path[point.Start].CenterArc || arc > path[point.End].CenterArc + 1e-8) return null;
        var low = point.Start; var high = point.End;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (path[middle].CenterArc < arc) low = middle + 1; else high = middle;
        }
        if (path[low].Center is not { } to) return null;
        if (low == point.Start || path[low].CenterArc == arc) return to;
        if (path[low - 1].Center is not { } from) return null;
        var length = path[low].CenterArc - path[low - 1].CenterArc;
        return length <= 0 ? to : Lerp(from, to, (arc - path[low - 1].CenterArc) / length);
    }

    private static IEnumerable<ScenePoint> ArcSamples(PathPoint[] path, int index, double startArc, double endArc)
    {
        if (AtArc(path, index, startArc) is not { } previous) yield break;
        yield return previous;
        var low = path[index].Start; var high = path[index].End;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (path[middle].CenterArc <= startArc) low = middle + 1; else high = middle;
        }
        for (var next = low; next <= path[index].End && path[next].CenterArc < endArc; next++)
        {
            if (path[next].Center is not { } center) yield break;
            foreach (var sample in Samples(previous, center).Skip(1)) yield return sample;
            previous = center;
        }
        if (AtArc(path, index, endArc) is not { } end) yield break;
        foreach (var sample in Samples(previous, end).Skip(1)) yield return sample;
    }

    private static bool NodePathChanged(JsonElement before, JsonElement after)
    {
        if (before.ValueKind != JsonValueKind.Object || after.ValueKind != JsonValueKind.Object ||
            !before.TryGetProperty("platform_number", out _) || !after.TryGetProperty("platform_number", out _)) return false;
        return new[] { "platform_number", "next_edge", "entered_from" }.Any(name =>
            before.TryGetProperty(name, out var a) && after.TryGetProperty(name, out var b) && a.GetRawText() != b.GetRawText());
    }
    private static Coverage Unknown(string reason) => new(null, null, null, false, false, reason, null, reason);
    private static bool SameSpace(SceneFrameStamp a, SceneFrameStamp b) => a.Session == b.Session && a.Request == b.Request && a.Space == b.Space;
    private static bool IsSpaceChange(RouteFrame first, RouteFrame second) => first.Latest is { } a && second.Latest is { } b &&
        a.Session == b.Session && a.Request == b.Request && a.Space != b.Space;
    private static bool SameCoordinates(RouteFrame first, RouteFrame second) => first.Latest is { } a && second.Latest is { } b && SameSpace(a, b);
    private static ulong[] Expected(RouteFrame frame) => frame.ExpectedVehicles ?? frame.Vehicles.Select(item => item.Id).ToArray();
    private static bool CompleteVehicles(RouteFrame frame, RouteVehicle[] vehicles) => vehicles.Length > 0 && vehicles.Select(item => item.Id).SequenceEqual(Expected(frame));
    private static string? Missing(ScenePoint point, RouteRanges range) => range.Fog.Any(bounds =>
        point.X >= bounds.Minimum.X && point.X < bounds.Maximum.X && point.Z >= bounds.Minimum.Z && point.Z < bounds.Maximum.Z) ? "fog" :
        !range.Terrain.Any(item => Contains(item.Bounds, point)) ? "terrain_bounds_missing" :
        !range.Rails.Any(item => Contains(item.Bounds, point)) ? "rail_bounds_missing" : null;
    // Reference heights differ; these are XZ bounds estimates, not surface or triangle tests.
    private static bool Contains(RouteBounds bounds, ScenePoint point) => point.X >= bounds.Minimum.X && point.X <= bounds.Maximum.X && point.Z >= bounds.Minimum.Z && point.Z <= bounds.Maximum.Z;
    private static ScenePoint Add(ScenePoint a, ScenePoint b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    private static ScenePoint Subtract(ScenePoint a, ScenePoint b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    private static double Distance(ScenePoint a, ScenePoint b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z));
    private static ScenePoint Lerp(ScenePoint from, ScenePoint to, double fraction) => new(from.X + (to.X - from.X) * fraction, from.Y + (to.Y - from.Y) * fraction, from.Z + (to.Z - from.Z) * fraction);
    private static IEnumerable<ScenePoint> Samples(ScenePoint from, ScenePoint to)
    {
        var count = Math.Max(1, (int)Math.Ceiling(Distance(from, to)));
        for (var i = 0; i <= count; i++) yield return Lerp(from, to, i / (double)count);
    }
}
