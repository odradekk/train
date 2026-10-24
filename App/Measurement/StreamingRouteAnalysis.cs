using Train.Core.Contracts;
using Train.Rendering;
using Train.Scenes.Contracts;

namespace Train.App.Measurement;

internal sealed record RouteWindow(double? NodeEntryCoreMeters, double? NodeExitCoreMeters, string[] Labels)
{
    public string Key => Labels.Length == 0 ? "outside-windows" : string.Join('|', Labels);
}
internal sealed record RouteCameraMeasurement(ScenePoint Position, ScenePoint Focus, double DistanceCells,
    double YawRadians, double PitchRadians, double NearCells, double FarCells);
internal sealed record StreamingRouteFrame(long Draw, double At, ulong Steps, SceneFrameStamp? Coordinates,
    SceneFrameStamp? Displayed, ScenePoint? DisplayedPosition, RouteTransition? Transition,
    RouteWindow Window, RouteCameraMeasurement Camera, long? CoverageDraw,
    Dictionary<string, int>? MissingParts = null);
internal sealed record StreamingHostEvent(int Display, double At, SceneDisplayEvent Value);
internal sealed record StreamingSpeed(long Draw, double Started, double Ended, string Window,
    double ActualSimulationRate, double? SceneMetersPerRealSecond, string? Unknown);
internal sealed record StreamingSpeedSummary(string Window, int Intervals, int UnknownSceneIntervals,
    double Seconds, double ActualSimulationRate, double? SceneMetersPerRealSecond,
    TimingDistribution? FrameSceneMetersPerRealSecond);
internal sealed record HostPreparationLatency(int Display, long Candidate, SceneFrameStamp Frame,
    SceneObjectId Object, double ResourcesPreparedAt, double CommittedAt)
{
    public double PreparedToCommitSeconds => CommittedAt - ResourcesPreparedAt;
}

internal static class StreamingRouteAnalysis
{
    internal static RouteWindow Window(TrainPosition position, double lengthMeters)
    {
        var labels = new List<string>();
        double? entry = null, exit = null;
        if (position is TrainPosition.Edge edge)
        {
            var progress = edge.Direction == TravelDirection.Forward ? edge.DistanceMeters : lengthMeters - edge.DistanceMeters;
            var remaining = lengthMeters - progress;
            if (remaining <= 128) { entry = -remaining; labels.Add("node-entry"); }
            if (progress < 128) { exit = progress; labels.Add("node-exit"); }
            if (progress >= 128 && remaining >= 128) labels.Add("line-middle");
        }
        else if (position is TrainPosition.Node node)
        {
            entry = node.DistanceMeters;
            exit = node.DistanceMeters - lengthMeters;
            if (entry is >= -128 and < 128) labels.Add("node-entry");
            if (exit is >= -128 and < 128) labels.Add("node-exit");
        }
        return new(entry, exit, labels.Order(StringComparer.Ordinal).ToArray());
    }

    internal static StreamingSpeed[] Speeds(IReadOnlyList<StreamingRouteFrame> frames)
    {
        var result = new List<StreamingSpeed>();
        for (var i = 1; i < frames.Count; i++)
        {
            var before = frames[i - 1]; var after = frames[i];
            var seconds = after.At - before.At;
            if (seconds <= 0 || after.Steps < before.Steps)
                throw new ArgumentException("路线时间和完成步数必须单调。", nameof(frames));
            string? unknown = before.DisplayedPosition is null || after.DisplayedPosition is null ? "displayed_pose_missing" : null;
            var previous = before.DisplayedPosition ?? default;
            if (before.Coordinates is not { } from || after.Coordinates is not { } to)
                unknown ??= "coordinates_missing";
            else if (!SameSpace(from, to))
            {
                if (from.Session != to.Session || from.Request != to.Request || after.Transition is not { } transition ||
                    !SameSpace(transition.From, from) || !SameSpace(transition.To, to))
                    unknown ??= "transition_missing";
                else if (transition.MultipleTracks) unknown ??= "multitrack_boundary";
                else previous = new(previous.X + transition.ToOrigin.X - transition.FromOrigin.X,
                    previous.Y + transition.ToOrigin.Y - transition.FromOrigin.Y,
                    previous.Z + transition.ToOrigin.Z - transition.FromOrigin.Z);
            }
            var current = after.DisplayedPosition ?? default;
            var distance = Math.Sqrt(Math.Pow(current.X - previous.X, 2) + Math.Pow(current.Y - previous.Y, 2) +
                Math.Pow(current.Z - previous.Z, 2)) * .5;
            result.Add(new(after.Draw, before.At, after.At, before.Window.Key,
                (after.Steps - before.Steps) / (double)SimulationTime.StepsPerSecond / seconds,
                unknown is null ? distance / seconds : null, unknown));
        }
        return result.ToArray();
    }

    internal static StreamingSpeedSummary[] SummarizeSpeeds(IReadOnlyList<StreamingSpeed> speeds) =>
        speeds.GroupBy(sample => sample.Window).Select(group =>
        {
            var seconds = group.Sum(sample => sample.Ended - sample.Started);
            var known = group.Where(sample => sample.SceneMetersPerRealSecond.HasValue).ToArray();
            var knownSeconds = known.Sum(sample => sample.Ended - sample.Started);
            return new StreamingSpeedSummary(group.Key, group.Count(), group.Count() - known.Length, seconds,
                group.Sum(sample => sample.ActualSimulationRate * (sample.Ended - sample.Started)) / seconds,
                knownSeconds > 0 ? known.Sum(sample => sample.SceneMetersPerRealSecond!.Value *
                    (sample.Ended - sample.Started)) / knownSeconds : null,
                known.Length > 0 ? FrameMeasurements.Distribution(known.Select(sample => sample.SceneMetersPerRealSecond!.Value)) : null);
        }).ToArray();

    internal static HostPreparationLatency[] HostLatencies(IEnumerable<StreamingHostEvent> events)
    {
        var prepared = new Dictionary<(int Display, long Candidate, SceneFrameStamp Frame, SceneObjectId Object), double>();
        var result = new List<HostPreparationLatency>();
        foreach (var item in events)
        {
            var value = item.Value;
            if (value.Stage == SceneDisplayStage.HostCancelled)
            {
                foreach (var cancelled in prepared.Keys.Where(key => key.Display == item.Display &&
                    key.Candidate == value.Candidate && key.Frame == value.Frame).ToArray()) prepared.Remove(cancelled);
                continue;
            }
            if (value.Object is not { } id) continue;
            var key = (item.Display, value.Candidate, value.Frame, id);
            if (value.Stage == SceneDisplayStage.ResourcesPrepared) prepared.Add(key, item.At);
            else if (value.Stage == SceneDisplayStage.Committed && prepared.Remove(key, out var at))
                result.Add(new(item.Display, value.Candidate, value.Frame, id, at, item.At));
        }
        return result.ToArray();
    }

    internal static bool SameSpace(SceneFrameStamp a, SceneFrameStamp b) =>
        a.Session == b.Session && a.Request == b.Request && a.Space == b.Space;
}
