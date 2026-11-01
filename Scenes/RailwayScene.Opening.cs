using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes;

internal sealed partial class RailwayScene
{
    private ScenePoint? openingFocus;
    private HashSet<int>? openingNear;

    private void BeginOpening(double focal, int minimum, int maximum, bool foreground)
    {
        if (!foreground) return;
        var focus = terrainFocus ?? path!.Sample(focal).Position;
        if (openingFocus is { } existingFocus)
        {
            if (existingFocus != focus) openingNear = null;
            return;
        }
        openingFocus = focus;
        // 临时实验按路径包络近似探针的实际对象包围盒，正式方案另定。
        openingNear = Enumerable.Range(minimum, maximum - minimum + 1).Where(index =>
        {
            var begin = index * RailwayCoordinates.SegmentLengthCells;
            var end = Math.Min(path!.LengthCells, begin + RailwayCoordinates.SegmentLengthCells);
            var bounds = path.Bounds(begin, end, focus, RailwayCoordinates.TerrainHalfWidth(path.Edge.Tracks));
            var x = Math.Max(0d, Math.Max(bounds.Minimum.X, -bounds.Maximum.X));
            var z = Math.Max(0d, Math.Max(bounds.Minimum.Z, -bounds.Maximum.Z));
            return Math.Sqrt(x * x + z * z) <= 32 * RailwayCoordinates.CellsPerMeter;
        }).ToHashSet();
    }

    private void FinishOpening()
    {
        if (openingNear is not null && openingNear.All(index => segments.ContainsKey(index) && !staleSegments.Contains(index)))
            openingNear = null;
    }

    private void ReconcileSegments(SceneWorldSnapshot snapshot, double focal, int middle, int minimum, int maximum, TrainView? followed, bool allowBuild, bool foreground)
    {
        BeginOpening(focal, minimum, maximum, foreground);
        farBuilds.Pause();
        if (fogRange != (minimum, maximum)) { fogRange = (minimum, maximum); fogDirty = true; }
        Streaming?.Demand(SceneStreamingPartKind.RailwaySegment, Enumerable.Range(minimum, maximum - minimum + 1).Select(RailwayStreamingCoverage.SegmentKey));
        var (retainMinimum, retainMaximum) = NeededRange(middle, followed,
            followed is null ? 0 : RailwayCoordinates.MaxPrefetchSegments);
        foreach (var index in pendingSegments.Keys.Where(index => index < retainMinimum || index > retainMaximum).ToArray())
        {
            var work = pendingSegments[index];
            work.Cancellation.Cancel();
            if (work.Attempt is { } cancelled) Streaming?.Cancel(cancelled);
            retiredSegments.Add(work);
            pendingSegments.Remove(index);
        }
        foreach (var index in segments.Keys.Where(index => index < retainMinimum || index > retainMaximum).ToArray())
        { segments.Remove(index); fogDirty = true; }
        foreach (var pair in pendingSegments.Where(pair => pair.Value.Task.IsCompleted).ToArray())
        {
            pendingSegments.Remove(pair.Key);
            try
            {
                var result = pair.Value.Task.GetAwaiter().GetResult();
                if (result.Dependencies.Matches(snapshot, path!, result.Index)) Connect(result, pair.Value.Attempt);
                else if (pair.Value.Attempt is { } rejected) Streaming?.Cancel(rejected);
            }
            catch (RailwayBuildException error) { Fail(error); }
            catch (OperationCanceledException) { }
            finally { pair.Value.Cancellation.Dispose(); }
        }
        FinishOpening();
        foreach (var index in Enumerable.Range(minimum, maximum - minimum + 1).OrderBy(index => Math.Abs(index - middle)))
        {
            if (segments.ContainsKey(index) && !staleSegments.Contains(index)) continue;
            if (pendingSegments.ContainsKey(index)) continue;
            if (cache.Find(snapshot, path!, index) is { } cached)
            {
                var attempt = Streaming?.Begin(SceneStreamingPartKind.RailwaySegment, RailwayStreamingCoverage.SegmentKey(index));
                if (attempt is { } hit) Streaming?.CacheHit(hit);
                Connect(cached, attempt);
            }
            else if (allowBuild && failure is null && pendingSegments.Count + retiredSegments.Count(work => !work.Task.IsCompleted) +
                farBuilds.RunningCount < RailwayCoordinates.MaxConcurrentBuilds +
                (foreground && openingNear?.Contains(index) == true ? 1 : 0))
            {
                var currentPath = path!;
                var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
                var attempt = Streaming?.Begin(SceneStreamingPartKind.RailwaySegment, RailwayStreamingCoverage.SegmentKey(index));
                if (attempt is { } queued) Streaming?.Queue(queued);
                pendingSegments.Add(index, new(Task.Run(() => attempt is { } captured
                    ? Streaming!.Build(captured, () => build(snapshot, currentPath, index, stop.Token))
                    : build(snapshot, currentPath, index, stop.Token), stop.Token), stop, attempt));
            }
        }
        waitingSegments = Enumerable.Range(minimum, maximum - minimum + 1).Any(index => !segments.ContainsKey(index));
        cache.Publish(Edge, owner, segments.Values.Select(item => item.Source));
        FinishOpening();
    }

}
