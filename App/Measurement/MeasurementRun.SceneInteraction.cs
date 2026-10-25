using System.Security.Cryptography;
using System.Text.Json;
using Godot;
using Train.Core.Contracts;
using Train.Rendering;
using Train.Scenes.Contracts;
using NVector = System.Numerics.Vector3;

namespace Train.App.Measurement;

internal sealed partial class MeasurementRun
{
    private int _scenePickCount;
    private const float InnerSampleRadiusCells = 32; // 16 米
    private const float NearAreaRadiusCells = 64; // 32 米，也是参考匹配半径
    private const float ReferenceCaptureRadiusCells = 128; // 64 米
    private const float ReferenceSpacingCells = 32; // 16 米，小于 32 米参考匹配半径
    private const double ReferenceMatchRadiusMeters = 32;
    private double _probeFogSeconds, _probePointSeconds, _probeBusinessSeconds;
    private int _probeFogRegionCount;
    private ProbeState _probeState;
    private readonly record struct ProbeState(string Stage, SceneSpaceId? Space, int FogRegions,
        int PointsPassed, int BusinessCount, int BusinessPassed, int RailCount, int RailPassed, bool FollowedPicked);
    private readonly Dictionary<ulong, Vector3[]> _probeMeshPoints = [];
    private readonly string? _sceneReferencePath;
    private WorldObjectId? _sceneTarget;
    private ProbeReference? _probeReference;
    private ProbeValidation? _probeReferenceValidation;
    private readonly Dictionary<ProbeReferenceView, HashSet<(int, int)>> _probeTerrainBands = [];
    private readonly List<ProbeReferenceView> _probeRecordedViews = [];
    private bool _probeReadyChecked;
    private ProbeReadySnapshot? _probeReadySnapshot;
    private readonly Dictionary<SceneSpaceId, NVector> _probeReferenceCenters = [];
    private sealed record ProbeExpected(string Key, string Subject, SceneBounds Bounds);
    private sealed record ProbeReference(string BuildCommit, string InputSha256, ProbeReferenceView[] Views);
    private sealed record ProbeReferenceView(string Target, int? Node, int? Edge, double? ArcMeters,
        double[] Center, ProbeReferenceObject[] Objects, int[][] Terrain);
    private sealed record ProbeReferenceObject(string? Subject, string[] Decorations, double[] Position,
        double[] Rotation, double[] Bounds);
    private sealed record ProbePick(bool Visible, bool Picked, string? Resolved, object Evidence);
    private sealed record ProbeValidation(bool Valid, ProbeReferenceObject[]? Expected,
        ProbeReferenceObject[] Actual, double PoseBoundsToleranceCells, string? Reason);
    private sealed record ProbeReadySnapshot(ProbeReferenceView? Reference, NVector Center,
        ProbeReferenceObject[] Objects, double At, SceneFrameStamp Frame, SimulationPosition Position);

    private void LoadSceneReference()
    {
        if (_scenario is "scene-reference" or "scene-map" or "scene-editor" || IsRouteMeasurement) return;
        if (_sceneReferencePath is null) throw new ArgumentException("场景测量需要 --measure-reference；先单独运行 scene-reference。");
        _probeReference = JsonSerializer.Deserialize<ProbeReference>(File.ReadAllText(_sceneReferencePath), SceneJson)
            ?? throw new InvalidDataException("参考运行数据缺失。");
        if (_probeReference.BuildCommit != _identity["build_commit"]?.ToString() ||
            _probeReference.InputSha256 != SceneInputHash()) throw new InvalidDataException("参考运行与本次 HEAD 或输入不同。");
        foreach (var view in _probeReference.Views)
            _probeTerrainBands.Add(view, view.Terrain.Select(cell => (cell[0], cell[1])).ToHashSet());
        _sceneEvidence["reference"] = new { path = _sceneReferencePath, sha256 =
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(_sceneReferencePath))).ToLowerInvariant() };
    }

    private string SceneInputHash() => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(_input))).ToLowerInvariant();
    private void WriteSceneReference() => File.WriteAllText(Path.Combine(Path.GetDirectoryName(_output)!, "scene-reference.json"),
        JsonSerializer.Serialize(new ProbeReference(_identity["build_commit"]!.ToString()!, SceneInputHash(), [.. _probeRecordedViews]), SceneJson));

    private void ResetSceneProbe()
    {
        _probeMeshPoints.Clear();
        _probeReadyChecked = false;
        _probeReadySnapshot = null;
        _probeReferenceCenters.Clear();
        _probeReferenceValidation = null;
        _scenePickCount = 0;
    }

    private bool CheckNearArea(ISceneFrame frame, WorldObjectId? follow, out object detail)
    {
        var stageStarted = Seconds();
        var controller = _app.WorldScenes!;
        var display = controller.Display!;
        var camera = controller.View.CameraRig;
        var (center, location, followedFound) = LocateProbeFocus(frame, follow);
        if (!followedFound)
        {
            detail = new { focusTarget = follow!.ToString(), reason = "follow_pose_missing" };
            return false;
        }
        var node = location is WorldLocation.Node n ? (int?)n.Id.Value : null;
        var edge = location as WorldLocation.Edge;
        var absolute = center + ProbeOrigin(frame);
        var displayed = display.DisplayedFrame!;
        var readyGeometry = displayed.Readiness == SceneReadiness.Ready &&
            displayed.Update.Frame.Space == frame.Update.Frame.Space && frame.Readiness == SceneReadiness.Ready;
        var reference = FindSceneReference(absolute, location);
        if (_scenario == "scene-reference")
        {
            if (readyGeometry &&
                (!_probeReferenceCenters.TryGetValue(frame.Update.Frame.Space, out var recordedCenter) ||
                    NVector.Distance(absolute, recordedCenter) >= ReferenceSpacingCells))
            {
                _probeReferenceCenters[frame.Update.Frame.Space] = absolute;
                _probeRecordedViews.Add(CaptureSceneReference(frame, center, node, edge));
            }
            detail = new { frame = frame.Update.Frame.ToString(), referenceOnly = true };
            _probeState = new("reference", frame.Update.Frame.Space, 0, 0, 0, 0, 0, 0, false);
            return false;
        }
        var terrainBand = reference is not null ? _probeTerrainBands.GetValueOrDefault(reference) : null;
        var known = node is not null ? reference is not null : edge is not null && terrainBand is not null &&
            terrainBand.Contains(((int)Math.Floor(absolute.X), (int)Math.Floor(absolute.Z)));
        var fogTransform = display.Root.GlobalTransform.AffineInverse() * controller.View.GlobalTransform;
        NVector FogPoint(NVector point)
        {
            var transformed = fogTransform * new Vector3(point.X, point.Y, point.Z);
            return new(transformed.X, transformed.Y, transformed.Z);
        }
        var fogRegions = display.DisplayedFrame!.Update.Environment.FoggedRegions;
        _probeFogRegionCount = fogRegions.Length;
        // 显示组相对交互坐标只做原点平移；参考地形带使用绝对格。
        var fogOrigin = FogPoint(NVector.Zero);
        var absoluteFog = fogRegions.Select(bounds => new SceneBounds(
            bounds.Minimum - fogOrigin + ProbeOrigin(frame), bounds.Maximum - fogOrigin + ProbeOrigin(frame))).ToArray();
        var intersectingFog = absoluteFog.Count(bounds => node is not null
            ? ProbeFogOverlaps(absolute, NearAreaRadiusCells, bounds)
            : terrainBand is not null && ProbeFogOverlapsBand(absolute, NearAreaRadiusCells, bounds, terrainBand));
        if (intersectingFog > 0)
        {
            _probeState = new("near_fog", frame.Update.Frame.Space, intersectingFog, 0, 0, 0, 0, 0, false);
            detail = new { focus = new { x = center.X, y = center.Y, z = center.Z }, radiusMeters = 32,
                frame = frame.Update.Frame.ToString(), location = location?.ToString(),
                samplingDomain = node is not null ? "node-circle" : "railway-reference-terrain-band",
                intersectingFog, fogClear = false, expectedKnown = known, checksSkipped = "near_fog" };
            _probeFogSeconds += Seconds() - stageStarted;
            return false;
        }
        _probeFogSeconds += Seconds() - stageStarted;
        CaptureReadyReference(frame, follow);
        stageStarted = Seconds();
        var actualReferenceObjects = reference?.Objects.Any(item => item.Decorations.Length > 0 &&
            ProbeDistance(absolute, ReferenceBounds(item)) <= NearAreaRadiusCells) == true
            ? ReferenceObjects(frame, center, NearAreaRadiusCells).ToArray() : [];
        _probeBusinessSeconds += Seconds() - stageStarted;
        stageStarted = Seconds();
        var pointPositions = new List<NVector> { center };
        foreach (var radius in new[] { InnerSampleRadiusCells, NearAreaRadiusCells })
        {
            if (edge is not null && terrainBand is not null)
            {
                var ring = ProbeBandRing(absolute, radius, terrainBand);
                known &= ring.Length == 8;
                pointPositions.AddRange(ring.Select(point => point - ProbeOrigin(frame)));
            }
            else for (var angle = 0; angle < 8; angle++)
                pointPositions.Add(center + new NVector(MathF.Cos(angle * MathF.PI / 4) * radius, 0,
                    MathF.Sin(angle * MathF.PI / 4) * radius));
        }
        var points = new List<object>();
        var samplesPassed = 0;
        foreach (var point in pointPositions)
        {
            var fogged = fogRegions.Any(bounds => ProbeFogContains(FogPoint(point), bounds));
            var terrain = frame.TerrainHeight(point.X, point.Z);
            _scenePickCount++;
            var inspection = controller.InspectPick(new SceneRay(point + new NVector(0, 4096, 0), -NVector.UnitY, 8192));
            var pick = inspection?.Pick;
            var hit = inspection is { Pick: ScenePick.Hit, Resolution: SceneResult<PickSubject>.Success };
            string? resolved = null;
            if (points.Count == 0 && _sceneTarget is WorldObjectId.Edge expectedEdge)
            {
                var subject = inspection?.Resolution is SceneResult<PickSubject>.Success success
                    ? success.Value : null;
                resolved = subject?.ToString();
                hit &= subject is PickSubject.Terrain { Location: WorldLocation.Edge actualEdge } && actualEdge.Id == expectedEdge.Id;
            }
            var passed = !fogged && hit && terrain is Lookup<double>.Found;
            if (passed) samplesPassed++;
            points.Add(new { x = point.X, z = point.Z, fogged, terrain = terrain.GetType().Name,
                pick = pick?.GetType().Name, resolved, passed });
        }
        _probePointSeconds += Seconds() - stageStarted;
        stageStarted = Seconds();
        var expectedBusiness = (reference?.Objects ?? []).Where(item => item.Subject is not null)
            .Select((item, index) => new ProbeExpected("reference:" + index,
                item.Subject!, new(ReferenceBounds(item).Minimum - ProbeOrigin(frame), ReferenceBounds(item).Maximum - ProbeOrigin(frame))));
        var railGeometry = (reference?.Objects ?? []).Where(item => item.Decorations.Length > 0 &&
            ProbeDistance(absolute, ReferenceBounds(item)) <= NearAreaRadiusCells).Select(item =>
            {
                var expectedCount = reference!.Objects.Count(other => ReferenceMatches(item, other));
                var publishedCount = actualReferenceObjects.Count(actual => ReferenceMatches(item, actual));
                return new { item.Decorations, item.Position, item.Rotation, item.Bounds,
                    expectedCount, publishedCount, ready = publishedCount >= expectedCount };
            }).ToArray();
        var railGeometryPassed = railGeometry.All(item => item.ready);
        var nearBusiness = expectedBusiness.Where(item => ProbeDistance(center, item.Bounds) <= NearAreaRadiusCells).ToArray();
        var results = new List<object>();
        var businessPassed = true;
        var businessPassedCount = 0;
        foreach (var expected in nearBusiness)
        {
            var result = ProbeBusiness(frame, display, expected);
            businessPassed &= result.Visible && result.Picked;
            if (result.Visible && result.Picked) businessPassedCount++;
            results.Add(new { expected.Key, expected = expected.Subject, bounds = new[] { expected.Bounds.Minimum.X,
                expected.Bounds.Minimum.Y, expected.Bounds.Minimum.Z, expected.Bounds.Maximum.X, expected.Bounds.Maximum.Y,
                expected.Bounds.Maximum.Z }, result.Visible, result.Picked, result.Resolved, result.Evidence });
        }
        ProbePick? followed = follow is not null ? ProbeBusiness(frame, display,
            new("follow", follow.ToString()!, new(center, center))) : null;
        _probeState = new("near_check", frame.Update.Frame.Space, 0, samplesPassed, nearBusiness.Length,
            businessPassedCount, railGeometry.Length, railGeometry.Count(item => item.ready), followed?.Picked == true);
        detail = new { focus = new { x = center.X, y = center.Y, z = center.Z }, radiusMeters = 32,
            cameraFocus = new { x = camera.Focus.X, y = camera.Focus.Y, z = camera.Focus.Z },
            frame = frame.Update.Frame.ToString(), origin = frame.Origin, simulationSeconds = frame.SimulationSeconds,
            location = location?.ToString(), samplingDomain = node is not null ? "node-circle" : "railway-reference-terrain-band",
            samples = points, samplesPassed, nearPointsPassed = samplesPassed == 17,
            intersectingFog, expectedKnown = known, railGeometryPassed, railGeometry,
            businessPassed, business = results, emptyBusinessArea = known && nearBusiness.Length == 0,
            followed };
        _probeBusinessSeconds += Seconds() - stageStarted;
        return known && samplesPassed == 17 && railGeometryPassed && businessPassed &&
            (followed is null || followed.Visible && followed.Picked);
    }

    internal static NVector[] ProbeBandRing(NVector center, float radius, HashSet<(int, int)> terrain)
    {
        // Only the independent Ready reference defines eligibility; loading holes cannot shorten the check.
        var eligible = Enumerable.Range(0, 1024).Select(index => center + new NVector(
            MathF.Cos(index * MathF.PI / 512) * radius, 0, MathF.Sin(index * MathF.PI / 512) * radius))
            .Where(point => terrain.Contains(((int)Math.Floor(point.X), (int)Math.Floor(point.Z)))).ToArray();
        return eligible.Length < 8 ? [] : Enumerable.Range(0, 8).Select(index => eligible[index * eligible.Length / 8]).ToArray();
    }

    private ProbeReferenceView CaptureSceneReference(ISceneFrame frame, NVector center, int? node, WorldLocation.Edge? edge)
    {
        int[][] terrain = [];
        var origin = ProbeOrigin(frame);
        if (edge is not null)
        {
            var meshes = _app.WorldScenes!.Display!.Objects.Values.Where(item => item.Source.Subject is null)
                .SelectMany(item => ProbeMeshes(item.Root)).Where(item => item.Mesh is not null).ToArray();
            terrain = CaptureReferenceTerrain(center + origin, meshes.Select(mesh => MeshBounds(mesh, frame)),
                (x, z) => frame.TerrainHeight(x - origin.X, z - origin.Z) is Lookup<double>.Found);
        }
        return new(_sceneTarget!.ToString()!, node, edge is null ? null : (int)edge.Id.Value, edge?.DistanceMeters,
            [center.X + origin.X, center.Y + origin.Y, center.Z + origin.Z],
            [.. ReferenceObjects(frame, center, ReferenceCaptureRadiusCells)], terrain);
    }

    internal static int[][] CaptureReferenceTerrain(NVector center, IEnumerable<SceneBounds> meshes,
        Func<double, double, bool> hasTerrain)
    {
        var bounds = meshes.ToArray();
        if (bounds.Length == 0) return [];
        // A matched focus and its probe ring fit inside the existing reference capture radius.
        var min = NVector.Max(bounds.Select(item => item.Minimum).Aggregate(NVector.Min),
            center - new NVector(ReferenceCaptureRadiusCells));
        var max = NVector.Min(bounds.Select(item => item.Maximum).Aggregate(NVector.Max),
            center + new NVector(ReferenceCaptureRadiusCells));
        var terrain = new List<int[]>();
        for (var z = (int)Math.Floor(min.Z); z < max.Z; z++)
        for (var x = (int)Math.Floor(min.X); x < max.X; x++)
            if (hasTerrain(x + .5, z + .5)) terrain.Add([x, z]);
        return [.. terrain];
    }

    private ProbeReferenceView? FindSceneReference(NVector absolute, WorldLocation? location) =>
        _probeReference?.Views.FirstOrDefault(view =>
            NVector.Distance(absolute, new((float)view.Center[0], (float)view.Center[1], (float)view.Center[2])) <= NearAreaRadiusCells &&
            (location is WorldLocation.Node node ? view.Node == (int)node.Id.Value :
                location is WorldLocation.Edge edge && view.Edge == (int)edge.Id.Value &&
                view.ArcMeters is { } arc && Math.Abs(arc - edge.DistanceMeters) <= ReferenceMatchRadiusMeters));

    private (NVector Center, WorldLocation? Location, bool FollowedFound) LocateProbeFocus(ISceneFrame frame, WorldObjectId? follow)
    {
        var focus = _app.WorldScenes!.View.CameraRig.Focus;
        var center = new NVector(focus.X, focus.Y, focus.Z);
        var followedFound = follow is null;
        if (follow is not null && frame.FindPose(follow) is Lookup<ScenePose>.Found pose)
        {
            center = pose.Value.Position;
            followedFound = true;
        }
        var location = frame.Locate(center) is Lookup<WorldLocation>.Found found ? found.Value : null;
        return (center, location, followedFound);
    }

    private void CaptureReadyReference(ISceneFrame frame, WorldObjectId? follow)
    {
        if (_scenario == "scene-reference" || _probeReadyChecked || frame.Readiness != SceneReadiness.Ready ||
            _app.WorldScenes!.Display?.DisplayedFrame is not { Readiness: SceneReadiness.Ready } drawn ||
            drawn.Update.Frame.Space != frame.Update.Frame.Space) return;
        var started = Seconds();
        var (center, location, _) = LocateProbeFocus(frame, follow);
        var absolute = center + ProbeOrigin(frame);
        _probeReadySnapshot = new(FindSceneReference(absolute, location), absolute,
            ReferenceObjects(frame, center, NearAreaRadiusCells).ToArray(), started, frame.Update.Frame,
            _app.Host!.GetStatus()!.View.Position);
        _probeReadyChecked = true;
    }

    private void ValidateReadyReference()
    {
        if (_probeReadySnapshot is not { } snapshot)
        {
            _probeReferenceValidation = new(false, null, [], .001, "ready_snapshot_missing");
            return;
        }
        var expected = snapshot.Reference?.Objects.Where(item => ProbeDistance(snapshot.Center, ReferenceBounds(item)) <= NearAreaRadiusCells).ToArray();
        var objectsValid = expected is not null && expected.Length == snapshot.Objects.Length && expected.All(item =>
            expected.Count(other => ReferenceMatches(item, other)) == snapshot.Objects.Count(other => ReferenceMatches(item, other)));
        _probeReferenceValidation = new(objectsValid, expected, snapshot.Objects, .001,
            snapshot.Reference is null ? "reference_missing" : null);
    }

    private IEnumerable<ProbeReferenceObject> ReferenceObjects(ISceneFrame frame, NVector center, double radius)
    {
        var core = _app.Host!.GetStatus()!.View;
        var parked = core.World.Nodes.Where(item => item.Occupant is NodeOccupant.Settlement)
            .SelectMany(item => ((Lookup<SettlementView>.Found)core.FindSettlement(((NodeOccupant.Settlement)item.Occupant!).Id)).Value.ParkedVehicles)
            .Select(id => new WorldObjectId.Instance(new InstanceId.Vehicle(id)).ToString()).ToHashSet();
        foreach (var item in _app.WorldScenes!.Display!.Objects.Values)
        {
            var decorations = item.Source.Subject is null ? item.Source.Visuals.OfType<SceneVisual.Geometry>()
                .Select(visual => visual.Mesh.Material).OfType<SceneMaterial.BuiltIn>()
                .Where(material => material.Kind is BuiltInMaterial.RailSteel or BuiltInMaterial.Sleeper)
                .Select(material => material.Kind.ToString()).Distinct().Order(StringComparer.Ordinal).ToArray() : [];
            if (decorations.Length == 0 && (item.Source.Parent is not null ||
                item.Source.Subject is not WorldObjectId.Instance { Id: InstanceId.Building } &&
                !parked.Contains(item.Source.Subject?.ToString() ?? ""))) continue;
            var meshes = ProbeMeshes(item.Root).Where(mesh => mesh.IsVisibleInTree() && mesh.Mesh is not null).ToArray();
            if (meshes.Length == 0) continue;
            var bounds = meshes.Select(mesh => MeshBounds(mesh, frame)).ToArray();
            var minimum = bounds.Select(bound => bound.Minimum).Aggregate(NVector.Min);
            var maximum = bounds.Select(bound => bound.Maximum).Aggregate(NVector.Max);
            if (ProbeDistance(center + ProbeOrigin(frame), new(minimum, maximum)) > radius) continue;
            var transform = _app.WorldScenes.View.GlobalTransform.AffineInverse() * item.Root.GlobalTransform;
            var at = transform.Origin; var rotation = transform.Basis.GetRotationQuaternion(); var origin = ProbeOrigin(frame);
            yield return new(item.Source.Subject?.ToString(), decorations, [at.X + origin.X, at.Y + origin.Y, at.Z + origin.Z],
                [rotation.X, rotation.Y, rotation.Z, rotation.W], [minimum.X, minimum.Y, minimum.Z, maximum.X, maximum.Y, maximum.Z]);
        }
    }

    private static SceneBounds ReferenceBounds(ProbeReferenceObject item) =>
        new(new((float)item.Bounds[0], (float)item.Bounds[1], (float)item.Bounds[2]),
            new((float)item.Bounds[3], (float)item.Bounds[4], (float)item.Bounds[5]));
    private static bool ReferenceMatches(ProbeReferenceObject left, ProbeReferenceObject right) =>
        left.Subject == right.Subject && left.Decorations.SequenceEqual(right.Decorations) &&
        left.Position.Concat(left.Rotation).Concat(left.Bounds).Zip(right.Position.Concat(right.Rotation).Concat(right.Bounds))
            .All(pair => Math.Abs(pair.First - pair.Second) <= .001);
    private static NVector ProbeOrigin(ISceneFrame frame) => new((float)frame.Origin.X, (float)frame.Origin.Y, (float)frame.Origin.Z);

    private SceneBounds MeshBounds(MeshInstance3D mesh, ISceneFrame frame)
    {
        var aabb = mesh.Mesh!.GetAabb();
        var transform = _app.WorldScenes!.View.GlobalTransform.AffineInverse() * mesh.GlobalTransform;
        var min = new NVector(float.PositiveInfinity); var max = new NVector(float.NegativeInfinity);
        for (var index = 0; index < 8; index++)
        {
            var point = transform * aabb.GetEndpoint(index);
            var at = new NVector(point.X, point.Y, point.Z) + ProbeOrigin(frame);
            min = NVector.Min(min, at); max = NVector.Max(max, at);
        }
        return new(min, max);
    }

    private ProbePick ProbeBusiness(ISceneFrame frame, SceneDisplay display, ProbeExpected expected)
    {
        var candidates = display.Objects.Values.Where(item => item.Source.Subject?.ToString() == expected.Subject).ToArray();
        var visible = false; string? resolved = null;
        var behind = 0; var offscreen = 0; var rays = 0; var points = 0;
        var outcomes = new Dictionary<string, int>(); object? firstFailure = null;
        foreach (var item in candidates)
        foreach (var mesh in ProbeMeshes(item.Root))
        {
            if (!mesh.IsVisibleInTree() || mesh.Mesh is null) continue;
            var key = mesh.Mesh.GetInstanceId();
            if (!_probeMeshPoints.TryGetValue(key, out var candidatesOnMesh))
            {
                var faces = mesh.Mesh.GetFaces(); var triangles = faces.Length / 3;
                candidatesOnMesh = new Vector3[Math.Min(48, triangles)];
                for (var index = 0; index < candidatesOnMesh.Length; index++)
                {
                    var face = index * triangles / candidatesOnMesh.Length * 3;
                    candidatesOnMesh[index] = (faces[face] + faces[face + 1] + faces[face + 2]) / 3;
                }
                _probeMeshPoints.Add(key, candidatesOnMesh);
            }
            foreach (var candidate in candidatesOnMesh)
            {
                points++;
                var point = mesh.GlobalTransform * candidate;
                var camera = _app.WorldScenes!.View.CameraRig.Camera;
                if (camera.IsPositionBehind(point)) { behind++; continue; }
                var screen = camera.UnprojectPosition(point);
                if (!camera.GetViewport().GetVisibleRect().HasPoint(screen)) { offscreen++; continue; }
                visible = true;
                _scenePickCount++; rays++;
                var inspection = _app.WorldScenes.InspectPick(screen);
                var pick = inspection?.Pick;
                var result = inspection?.Resolution;
                var problems = result switch { SceneResult<PickSubject>.Failure failure => failure.Problems.ToArray(),
                    SceneResult<PickSubject>.Faulted fault => [fault.Problem], _ => Array.Empty<SceneProblem>() };
                var outcome = pick?.GetType().Name + "/" + result?.GetType().Name +
                    (problems.Length == 0 ? "" : ":" + string.Join(',', problems.Select(problem => problem.Code)));
                outcomes[outcome] = outcomes.GetValueOrDefault(outcome) + 1;
                if (result is SceneResult<PickSubject>.Success success)
                {
                    resolved = success.Value.ToString();
                    if (success.Value is PickSubject.WorldObject subject && subject.Object.ToString() == expected.Subject)
                    {
                        return new(true, true, resolved, Evidence());
                    }
                }
                firstFailure ??= new { screen = new { screen.X, screen.Y }, point = new { point.X, point.Y, point.Z },
                    ray = inspection?.Ray,
                    pick = pick?.ToString(), resolution = result?.ToString(), source = item.Source.Id.ToString(),
                    problems,
                    displayedPose = item.Source.Pose.ToString(), worldTransform = mesh.GlobalTransform.ToString() };
                if (problems.Length > 0 && _scenePresentedStamp is { } presented &&
                    (presented.Session != frame.Update.Frame.Session || presented.Request != frame.Update.Frame.Request ||
                     presented.Space != frame.Update.Frame.Space))
                    // 同一过期帧的其它候选射线也无法确认当前对象，留到下一绘制帧再检查。
                    return new(visible, false, resolved, Evidence());
            }
        }
        return new(visible, false, resolved, Evidence());
        object Evidence() => new { candidates = candidates.Length, points, behind, offscreen, rays, outcomes, firstFailure };
    }

    private static IEnumerable<MeshInstance3D> ProbeMeshes(Node node)
    {
        if (node is MeshInstance3D mesh) yield return mesh;
        foreach (var child in node.GetChildren()) foreach (var nested in ProbeMeshes(child)) yield return nested;
    }
    // A vertical pick on a fog box's maximum face belongs to the adjacent loaded area.
    internal static bool ProbeFogContains(NVector point, SceneBounds bounds) => point.X >= bounds.Minimum.X && point.X < bounds.Maximum.X &&
        point.Z >= bounds.Minimum.Z && point.Z < bounds.Maximum.Z;
    internal static bool ProbeFogOverlaps(NVector center, double radius, SceneBounds bounds)
    {
        var nearest = NVector.Clamp(center, bounds.Minimum, bounds.Maximum);
        var distance = ProbeDistance(center, bounds);
        return distance < radius || distance == radius && ProbeFogContains(nearest, bounds);
    }
    internal static double ProbeDistance(NVector point, SceneBounds bounds) => Math.Sqrt(
        Math.Pow(Math.Max(Math.Max(bounds.Minimum.X - point.X, 0), point.X - bounds.Maximum.X), 2) +
        Math.Pow(Math.Max(Math.Max(bounds.Minimum.Z - point.Z, 0), point.Z - bounds.Maximum.Z), 2));
    internal static bool ProbeFogOverlapsBand(NVector center, double radius, SceneBounds fog, HashSet<(int, int)> terrain)
    {
        if (!ProbeFogOverlaps(center, radius, fog)) return false;
        var minX = (int)Math.Floor(Math.Max(center.X - radius, fog.Minimum.X));
        var maxX = (int)Math.Floor(Math.Min(center.X + radius, fog.Maximum.X));
        var minZ = (int)Math.Floor(Math.Max(center.Z - radius, fog.Minimum.Z));
        var maxZ = (int)Math.Floor(Math.Min(center.Z + radius, fog.Maximum.Z));
        for (var z = minZ; z <= maxZ; z++)
        for (var x = minX; x <= maxX; x++)
        {
            if (!terrain.Contains((x, z))) continue;
            var minimum = new NVector(Math.Max(x, fog.Minimum.X), 0, Math.Max(z, fog.Minimum.Z));
            var maximum = new NVector(Math.Min(x + 1, fog.Maximum.X), 0, Math.Min(z + 1, fog.Maximum.Z));
            if (minimum.X < maximum.X && minimum.Z < maximum.Z &&
                ProbeFogOverlaps(center, radius, new(minimum, maximum))) return true;
        }
        return false;
    }
}
