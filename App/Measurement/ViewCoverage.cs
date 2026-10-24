using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.App.Measurement;

internal sealed record CoverageRay(ScenePoint Origin, ScenePoint Direction, double NearDistance, double FarDistance);
internal sealed record CoveragePartCount(SceneStreamingPartId? Part, string Stage, int Terrain, int FogOnly, int Neither);
internal sealed record CoverageSample(int Eligible, int Terrain, int FogOnly, int Neither,
    int ExcludedNoForward, int ExcludedNear, int ExcludedFar,
    CoveragePartCount[]? PartCategories = null)
{
    public bool Known => Eligible > 0;
    public int MissingTerrain => FogOnly + Neither;
    public double? MissingTerrainRatio => Known ? MissingTerrain / (double)Eligible : null;
    public double? NeitherRatio => Known ? Neither / (double)Eligible : null;
}
internal sealed record CoverageObservation(double Seconds, SceneFrameStamp? Frame, string Window, CoverageSample? Sample, long Draw = 0);
internal sealed record CoverageGap(double StartSeconds, double EndSeconds, bool LeftCensored, bool RightCensored)
{
    public double DurationSeconds => EndSeconds - StartSeconds;
}
internal sealed record CoverageDistribution(int Count, double TotalSeconds, double? P50Seconds, double? P99Seconds, double? MaxSeconds);
internal sealed record CoverageGapSummary(CoverageGap[] Intervals, CoverageDistribution Durations);
internal sealed record CoverageAnalysis(CoverageGapSummary MissingTerrain, CoverageGapSummary Neither, CoverageDistribution SampleIntervals);

internal static class ViewCoverage
{
    internal const int Columns = 32;
    internal const int Rows = 18;

    // Near/Far are distances along each ray, not the camera's view-space clipping depths.
    internal static CoverageSample Sample(ISceneFrame displayedFrame, IEnumerable<CoverageRay> rays,
        ISceneStreamingCoverage? coverage = null, Func<SceneStreamingPartId, string>? category = null)
    {
        int terrain = 0, fogOnly = 0, neither = 0, noForward = 0, near = 0, far = 0;
        var planeY = -displayedFrame.Origin.Y;
        var categories = new Dictionary<(SceneStreamingPartId? Part, string Stage), CoveragePartCount>();
        foreach (var ray in rays)
        {
            var direction = ray.Direction;
            var length = Math.Sqrt(direction.X * direction.X + direction.Y * direction.Y + direction.Z * direction.Z);
            var distance = (planeY - ray.Origin.Y) / (direction.Y / length);
            if (!double.IsFinite(distance) || distance <= 0) { noForward++; continue; }
            if (distance < ray.NearDistance) { near++; continue; }
            if (distance > ray.FarDistance) { far++; continue; }
            var point = new ScenePoint(ray.Origin.X + direction.X / length * distance, planeY,
                ray.Origin.Z + direction.Z / length * distance);
            var hasTerrain = displayedFrame.TerrainHeight(point.X, point.Z) is Lookup<double>.Found;
            var inFog = displayedFrame.Update.Environment.FoggedRegions.Any(box =>
                point.X >= box.Minimum.X && point.X <= box.Maximum.X &&
                point.Y >= box.Minimum.Y && point.Y <= box.Maximum.Y &&
                point.Z >= box.Minimum.Z && point.Z <= box.Maximum.Z);
            var parts = coverage?.FindTerrainParts(point.X, point.Z);
            if (parts is null) Count(null, "layout_unknown");
            else if (parts.Value.IsEmpty) Count(null, "outside_terrain_domain");
            else foreach (var part in parts.Value.Distinct()) Count(part, category?.Invoke(part) ?? "unknown");
            if (hasTerrain) terrain++;
            else if (inFog) fogOnly++;
            else neither++;

            void Count(SceneStreamingPartId? part, string stage)
            {
                var key = (part, stage);
                var count = categories.GetValueOrDefault(key) ?? new(part, stage, 0, 0, 0);
                categories[key] = count with
                {
                    Terrain = count.Terrain + (hasTerrain ? 1 : 0),
                    FogOnly = count.FogOnly + (!hasTerrain && inFog ? 1 : 0),
                    Neither = count.Neither + (!hasTerrain && !inFog ? 1 : 0)
                };
            }
        }
        return new(terrain + fogOnly + neither, terrain, fogOnly, neither, noForward, near, far, categories.Values.ToArray());
    }

    internal static CoverageAnalysis Analyze(IReadOnlyList<CoverageObservation> observations, double? endedSeconds = null)
    {
        for (var i = 0; i < observations.Count; i++)
            if (!double.IsFinite(observations[i].Seconds) || (i > 0 && observations[i].Seconds < observations[i - 1].Seconds))
                throw new ArgumentException("覆盖采样时间必须有限且单调。", nameof(observations));
        if (endedSeconds is { } end && (!double.IsFinite(end) || observations.Count > 0 && end < observations[^1].Seconds))
            throw new ArgumentException("覆盖结束时间不得早于最后采样。", nameof(endedSeconds));
        var spacings = new List<double>();
        for (var i = 1; i < observations.Count; i++)
            spacings.Add(observations[i].Seconds - observations[i - 1].Seconds);
        return new(Gaps(sample => sample.MissingTerrain > 0), Gaps(sample => sample.Neither > 0), Distribution(spacings));

        CoverageGapSummary Gaps(Func<CoverageSample, bool> isGap)
        {
            var gaps = new List<CoverageGap>();
            double? start = null;
            bool left = false;
            for (var i = 0; i < observations.Count; i++)
            {
                var current = observations[i];
                var connected = i > 0 && Connected(observations[i - 1], current);
                if (!connected && start is { } interrupted)
                {
                    // The preceding sample holds until this observed boundary; its continuation is unknown.
                    gaps.Add(new(interrupted, current.Seconds, left, true));
                    start = null;
                }
                var known = current.Frame is not null && current.Sample is { Known: true };
                var gap = known && isGap(current.Sample!);
                if (gap && start is null) { start = current.Seconds; left = !connected; }
                else if (known && !gap && start is { } ended)
                {
                    gaps.Add(new(ended, current.Seconds, left, false));
                    start = null;
                }
            }
            if (start is { } tail) gaps.Add(new(tail, endedSeconds ?? observations[^1].Seconds, left, true));
            return new(gaps.ToArray(), Distribution(gaps.Select(gap => gap.DurationSeconds)));
        }
    }

    private static bool SameDomain(CoverageObservation a, CoverageObservation b) => a.Window == b.Window &&
        a.Frame is { } af && b.Frame is { } bf && af.Session == bf.Session && af.Request == bf.Request && af.Space == bf.Space;
    private static bool Connected(CoverageObservation a, CoverageObservation b) => SameDomain(a, b) &&
        a.Sample is { Known: true } && b.Sample is { Known: true };
    private static CoverageDistribution Distribution(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        double? Percentile(double p) => sorted.Length == 0 ? null : sorted[(int)Math.Ceiling(sorted.Length * p) - 1];
        return new(sorted.Length, sorted.Sum(), Percentile(.5), Percentile(.99), sorted.Length == 0 ? null : sorted[^1]);
    }
}
