using System.Numerics;
using System.Text.Json;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;
namespace Train.SceneScale;

// 沿用 #499 的 N/C/F、局部几何与高度观测定义；耗时只作等待信息，不是游戏性能指标。
internal static partial class Probe
{
    internal static async Task<bool> Run(string mode, double[]? observedFocus, ICoreReadView view,
        IContentView content, IScenesFactory scenes, bool verifyStable = false)
    {
        if (mode == "stats")
        {
            WriteStats(view, content, observedFocus!);
            return true;
        }
        var json = new JsonSerializerOptions { IncludeFields = true };
        await using var scene = ((SceneResult<IWorldSceneSession>.Success)scenes.OpenWorld(new(Guid.NewGuid()), new(view, content))).Value;
        if (scene.RequestView(new WorldSceneTarget.Location(new WorldLocation.Node(new(2)))) is not SceneResult<SceneRequestId>.Success) throw new InvalidDataException("场景请求失败。");
        ISceneFrame? frame = null;
        var all = new Dictionary<SceneObjectId, SceneObject>();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        object? firstReadySnapshot = null;
        object TerrainCounts() => new
        {
            label = "替代读数，不是N/C/F",
            objects = all.Values.Count(o => o.Parent == null && o.Subject == null && o.Visuals.OfType<SceneVisual.Geometry>().Any(g => g.Mesh.Material is not SceneMaterial.BuiltIn)),
            vertices = all.Values.Where(o => o.Parent == null && o.Subject == null).Sum(o => o.Visuals.OfType<SceneVisual.Geometry>().Where(g => g.Mesh.Material is not SceneMaterial.BuiltIn).Sum(g => (long)g.Mesh.Vertices.Length)),
            triangles = all.Values.Where(o => o.Parent == null && o.Subject == null).Sum(o => o.Visuals.OfType<SceneVisual.Geometry>().Where(g => g.Mesh.Material is not SceneMaterial.BuiltIn).Sum(g => (long)g.Mesh.TriangleIndices.Length / 3))
        };
        int stage = 0;
        var focusCenter = mode == "local" && observedFocus is not null ? new Vector2((float)observedFocus[0], (float)observedFocus[1]) : Vector2.Zero;
        var offsets = new[] { Vector2.Zero, new Vector2(64, 0), new Vector2(-64, 0), new Vector2(0, 64), new Vector2(0, -64) };
        var localTriangles = new HashSet<string>(StringComparer.Ordinal);
        var localRails = new HashSet<string>(StringComparer.Ordinal);
        var localNonRailTriangles = new HashSet<string>(StringComparer.Ordinal);
        var terrainSamples = new Dictionary<(int X, int Z), double>();
        var currentHeights = new Dictionary<(int X, int Z), double>();
        long sampleAttempts = 0;
        double maximumRepeatedHeightDifference = 0;
        var columns = LocalColumns(focusCenter);
        LocalCoverage? coverage = null;
        LocalObservation? first = null;
        var samples = new List<LocalStabilitySample>();
        var geometryCache = new LocalGeometryCache();
        var stableWindow = new LocalStabilityWindow(0);
        LocalCapture? lastCapture = null;
        LocalRailway? railCenterline = null;
        const string locationPrefetch = "Production Location request keeps all template parts prefetched in the background; only fixed local coverage gates this observation.";
        void WriteLocalResult(bool completed, string? failure = null)
        {
            var b = frame!.NodePanBounds;
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                completed, failure,
                outcome = completed ? "completed" : failure is "local_stability_timeout" or
                    "parking_track_coverage_unproven" or "near_railway_signature_empty" ? "unknown" : "failed",
                seconds = clock.Elapsed.TotalSeconds,
                terrainViewRadiusCells = 192,
                radiusCells = 192,
                conservativeTriangleRadiusCells = 192.001,
                quantizationCells = .001,
                maximumCoordinateQuantizationErrorCells = .0005,
                cameraOffsetsCells = offsets,
                cameraStages = samples.Count,
                triangles = localTriangles.Count,
                triangleWorldCoordinatesMaterialSha256 = Digest(localTriangles),
                railSteelTriangles = localRails.Count,
                railSteelSha256 = Digest(localRails),
                nonRailTriangles = localNonRailTriangles.Count,
                nonRailTriangleWorldCoordinatesMaterialSha256 = Digest(localNonRailTriangles),
                railCenterline,
                heightColumns = columns.Count,
                heightFound = terrainSamples.Count,
                heightMissing = columns.Count - terrainSamples.Count,
                heightSampleAttempts = sampleAttempts,
                minimumHeight = terrainSamples.Count == 0 ? (double?)null : terrainSamples.Values.Min(),
                maximumHeight = terrainSamples.Count == 0 ? (double?)null : terrainSamples.Values.Max(),
                maximumRepeatedHeightDifference,
                terrainHeightSha256 = Digest(terrainSamples.Select(p => $"{p.Key.X},{p.Key.Z},{Q(p.Value)}")),
                stability_verified = verifyStable ? (bool?)(completed && samples.All(sample => sample.equal == true)) : null,
                stability_wait_seconds = verifyStable ? (int?)10 : null,
                bounded_stability = completed,
                local_stability_seconds = 30,
                position_timeout_seconds = 300,
                stability_limit = "Background terrain has no public completion state; this proves stability only within the observation window.",
                stability_samples = samples,
                lastGeometry = lastCapture?.Geometry,
                lastCoverage = coverage,
                locationPrefetch,
                defaultFocus = new[] { 0d, 0d },
                navigationCenter = b is null ? null : new[] { frame.Origin.X + (b.Minimum.X + b.Maximum.X) / 2, frame.Origin.Z + (b.Minimum.Y + b.Maximum.Y) / 2 },
                diagnostics = frame.Diagnostics.Select(d => new { d.Code, d.Stage })
            }, json));
        }
        double nextProgressAt = 0;
        ProbeProgress.Write("present-start");
        for (int i = 0; ; i++)
        {
            if (mode != "local" && clock.Elapsed.TotalSeconds >= 300)
                throw new TimeoutException("场景未完成探针观测。");
            // 192 格仅供离线 96m 近景核对，不代表生产相机的完整地形视野。
            SceneCameraSample? camera = mode == "local" && stage > 0 && frame != null ? new(frame, new Vector3(focusCenter.X + offsets[stage].X - (float)frame.Origin.X, 0, focusCenter.Y + offsets[stage].Y - (float)frame.Origin.Z), 192) : null;
            var result = scene.Present(new(0, camera,
                mode == "local" && railCenterline is null ? SceneDebugLayer.Centerline : SceneDebugLayer.None), frame);
            if (result is not SceneResult<ISceneFrame>.Success success)
                throw new InvalidDataException("场景呈现失败。");
            frame = success.Value;
            if (frame.Readiness == SceneReadiness.Failed) throw new InvalidDataException("场景生成失败。");
            if (mode == "local" && railCenterline is null && frame.NodeOverview is not null)
            {
                railCenterline = CaptureRailway(frame, focusCenter);
                if (!railCenterline.parkingOutside || railCenterline.segment_count == 0)
                {
                    WriteLocalResult(false, !railCenterline.parkingOutside
                        ? "parking_track_coverage_unproven" : "near_railway_signature_empty");
                    return false;
                }
            }
            ApplyUpdate(all, frame.Update);
            if (mode != "bounds")
            {
                coverage = ObserveLocalCoverage(frame, focusCenter, columns, currentHeights);
                if (coverage.Readiness == SceneReadiness.Ready && coverage.FoggedRegionsIntersecting == 0) sampleAttempts += columns.Count;
                if (mode == "local" && !coverage.Complete) stableWindow.Reset();
                if (mode == "local" && (coverage.Complete || first is not null))
                {
                    lastCapture = CaptureLocal(all, frame.Origin, focusCenter, currentHeights, coverage,
                        clock.Elapsed.TotalSeconds, geometryCache);
                    if (first is not null && !first.SameContent(lastCapture.Observation))
                    {
                        samples[^1] = samples[^1] with { second = lastCapture.Observation,
                            secondGeometry = lastCapture.Geometry, equal = false };
                        WriteLocalResult(false, "local_stability_changed");
                        return false;
                    }
                    if (coverage.Complete) stableWindow.Track(lastCapture.Observation);
                }
            }
            if (mode == "local" && stableWindow.TimedOut(clock.Elapsed.TotalSeconds))
            {
                WriteLocalResult(false, "local_stability_timeout");
                return false;
            }
            if (clock.Elapsed.TotalSeconds >= nextProgressAt)
            {
                ProbeProgress.Write("present", new { iteration = i, cameraStage = stage, readiness = frame.Readiness.ToString(), objects = all.Count, coverage });
                nextProgressAt = clock.Elapsed.TotalSeconds + 1;
            }
            if (frame.Readiness == SceneReadiness.Ready && firstReadySnapshot is null)
                firstReadySnapshot = new { seconds = clock.Elapsed.TotalSeconds, terrain = TerrainCounts() };
            if (frame.NodePanBounds is { } b && (mode == "bounds" || coverage!.Complete))
            {
                if (mode == "local")
                {
                    if (first is null ? stableWindow.IsStable(lastCapture!.Observation.seconds) : first.CanRepeatAt(lastCapture!.Observation.seconds))
                    {
                        ProbeProgress.Write("local-capture", new { cameraStage = stage, repeat = first is not null, objects = all.Count });
                        var captured = lastCapture!;
                        if (first is null)
                        {
                            localTriangles.UnionWith(captured.Triangles);
                            localRails.UnionWith(captured.Rails);
                            localNonRailTriangles.UnionWith(captured.NonRailTriangles);
                            foreach (var (column, height) in currentHeights)
                            {
                                if (terrainSamples.TryGetValue(column, out var prior)) maximumRepeatedHeightDifference = Math.Max(maximumRepeatedHeightDifference, Math.Abs(prior - height));
                                terrainSamples[column] = height;
                            }
                            samples.Add(new(stage, offsets[stage], captured.Observation, null, null, captured.Geometry,
                                unchangedSinceSeconds: stableWindow.UnchangedSinceSeconds));
                            if (verifyStable) first = captured.Observation;
                        }
                        else
                        {
                            var equal = first.SameContent(captured.Observation);
                            samples[^1] = samples[^1] with { second = captured.Observation,
                                secondGeometry = captured.Geometry, equal = equal };
                            if (!equal)
                            {
                                WriteLocalResult(false, "local_stability_changed");
                                return false;
                            }
                            first = null;
                        }
                        if (!verifyStable || first is null)
                        {
                            if (stage + 1 == offsets.Length)
                            {
                                WriteLocalResult(true);
                                return true;
                            }
                            stage++;
                            stableWindow = new(clock.Elapsed.TotalSeconds);
                            lastCapture = null;
                        }
                    }
                }
                else
                {
                    ProbeProgress.Write("output-start");
                    Console.WriteLine(JsonSerializer.Serialize(new
                    {
                        frame.Readiness,
                        seconds = clock.Elapsed.TotalSeconds,
                        origin = frame.Origin,
                        min = new[] { b.Minimum.X, b.Minimum.Y },
                        max = new[] { b.Maximum.X, b.Maximum.Y },
                        focus = new[] { 0d, 0d },
                        navigationCenter = new[] { frame.Origin.X + (b.Minimum.X + b.Maximum.X) / 2, frame.Origin.Z + (b.Minimum.Y + b.Maximum.Y) / 2 },
                        sceneObjectCount = all.Count,
                        businessSceneRootsWithin32m = all.Values.Count(o => o.Parent == null && o.Subject is WorldObjectId.Instance
                        {
                            Id: InstanceId.Building
                        }
                        && Vector2.Distance(new(WorldPose(o, all, frame.Origin).Position.X, WorldPose(o, all, frame.Origin).Position.Z), Vector2.Zero) <= 64),
                        firstReadySnapshot,
                        stableSnapshotMethod = mode == "bounds" ? "PanBounds available" : "Ready; no fog intersects fixed radius192 circle; every precise TerrainHeight column Found; SceneUpdate replacement/root-shift semantics",
                        localCoverage = coverage,
                        locationPrefetch,
                        terrainReplacement = new
                        {
                            label = "替代读数，不是N/C/F",
                            criterion = "root Subject null Geometry with non BuiltIn material; BuiltIn materials reported separately",
                            geometries = all.Values.Where(o => o.Parent == null && o.Subject == null).Sum(o => o.Visuals.OfType<SceneVisual.Geometry>().Count(g => g.Mesh.Material is not SceneMaterial.BuiltIn)),
                            vertices = all.Values.Where(o => o.Parent == null && o.Subject == null).Sum(o => o.Visuals.OfType<SceneVisual.Geometry>().Where(g => g.Mesh.Material is not SceneMaterial.BuiltIn).Sum(g => (long)g.Mesh.Vertices.Length)),
                            triangles = all.Values.Where(o => o.Parent == null && o.Subject == null).Sum(o => o.Visuals.OfType<SceneVisual.Geometry>().Where(g => g.Mesh.Material is not SceneMaterial.BuiltIn).Sum(g => (long)g.Mesh.TriangleIndices.Length / 3)),
                            builtIn = all.Values.Where(o => o.Subject == null).SelectMany(o => o.Visuals.OfType<SceneVisual.Geometry>()).Where(g => g.Mesh.Material is SceneMaterial.BuiltIn).GroupBy(g => g.Mesh.Material.ToString()).Select(g => new
                            {
                                material = g.Key,
                                vertices = g.Sum(x => (long)x.Mesh.Vertices.Length),
                                triangles = g.Sum(x => (long)x.Mesh.TriangleIndices.Length / 3)
                            })
                        },
                        sceneGeometryCount = all.Values.Sum(o => o.Visuals.OfType<SceneVisual.Geometry>().Count()),
                        sceneVertices = all.Values.Sum(o => o.Visuals.OfType<SceneVisual.Geometry>().Sum(g => (long)g.Mesh.Vertices.Length)),
                        sceneTriangles = all.Values.Sum(o => o.Visuals.OfType<SceneVisual.Geometry>().Sum(g => (long)g.Mesh.TriangleIndices.Length / 3)),
                        terrainPatches = frame.Update.Environment.TerrainCollision.Length,
                        terrainHeightSamples = frame.Update.Environment.TerrainCollision.Sum(p => (long)p.Heights.Length),
                        diagnostics = frame.Diagnostics.Select(d => new { d.Code, d.Stage })
                    }, json));
                    return true;
                }
            }
            await Task.Delay(20);
        }
    }
}
