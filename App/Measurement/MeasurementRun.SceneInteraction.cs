using System.Collections;
using System.Reflection;
using Godot;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Rendering;
using Train.Scenes.Contracts;
using NVector = System.Numerics.Vector3;
using NQuaternion = System.Numerics.Quaternion;
using NMatrix = System.Numerics.Matrix4x4;

namespace Train.App.Measurement;

internal sealed partial class MeasurementRun
{
    private int _scenePickCount;
    private readonly Dictionary<ulong, Vector3[]> _probeMeshPoints = [];
    private List<ProbeExpected>? _probeExpected;
    private ProbeExpected? _probeNearest;
    private double? _probeNearestPicked;
    private sealed record ProbeExpected(string Key, WorldObjectId? Subject, SceneBounds Bounds, bool Rail);
    private static readonly BindingFlags ProbeFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static object? ProbeRead(object? owner, string name) => owner is null ? null :
        owner.GetType().GetField(name, ProbeFlags)?.GetValue(owner) ?? owner.GetType().GetProperty(name, ProbeFlags)?.GetValue(owner);

    private void ResetSceneProbe()
    {
        _probeMeshPoints.Clear();
        _probeExpected = null;
        _probeNearest = null;
        _probeNearestPicked = null;
        _scenePickCount = 0;
    }

    private bool CheckNearArea(ISceneFrame frame, WorldObjectId? follow, out object detail)
    {
        var controller = _app.WorldScenes!;
        var display = controller.Display!;
        var session = (IWorldSceneSession?)ProbeRead(controller, "_session");
        var camera = controller.View.CameraRig;
        var center = new NVector(camera.Focus.X, camera.Focus.Y, camera.Focus.Z);
        var node = ProbeRead(session, "node");
        var points = new List<object>();
        var clear = session is not null;
        var pointPositions = new List<NVector> { center };
        foreach (var radius in new[] { 32f, 64f })
            for (var angle = 0; angle < 8; angle++)
                pointPositions.Add(center + new NVector(MathF.Cos(angle * MathF.PI / 4) * radius, 0,
                    MathF.Sin(angle * MathF.PI / 4) * radius));
        foreach (var point in pointPositions)
        {
            var fogged = frame.Update.Environment.FoggedRegions.Any(bounds => ProbeInside(point, bounds));
            var terrainHeight = frame.TerrainHeight(point.X, point.Z);
            _scenePickCount++;
            var pick = frame.Pick(new(point + new NVector(0, 4096, 0), -NVector.UnitY, 8192));
            var hit = pick is ScenePick.Hit;
            string? resolved = null;
            if (points.Count == 0 && follow is null && ProbeRead(session, "target") is WorldSceneTarget.Location { Value: WorldLocation.Edge expectedEdge })
            {
                var subject = pick is ScenePick.Hit actual && session?.Resolve(actual.Value) is SceneResult<PickSubject>.Success success
                    ? success.Value : null;
                resolved = subject?.ToString();
                hit &= subject is PickSubject.Terrain { Location: WorldLocation.Edge actualEdge } && actualEdge.Id == expectedEdge.Id;
            }
            clear &= !fogged && hit && terrainHeight is Lookup<double>.Found;
            points.Add(new { x = point.X, z = point.Z, fogged, terrain = terrainHeight.GetType().Name,
                pick = pick.GetType().Name, resolved });
        }
        var intersectingFog = frame.Update.Environment.FoggedRegions.Count(bounds => ProbeDistance(center, bounds) <= 64);
        clear &= intersectingFog == 0;
        var expectedKnown = node is null || ProbePrepareExpected(node, frame, center);
        clear &= expectedKnown;
        var results = new List<object>();
        foreach (var expected in _probeExpected ?? [])
        {
            var result = ProbeBusiness(frame, session, display, expected);
            clear &= result.Visible && result.Picked;
            results.Add(new { expected.Key, expected = expected.Subject?.ToString() ?? "Terrain(node): position identity only",
                pickContract = expected.Rail ? "Terrain(node): position identity only" : "WorldObject: exact instance identity",
                bounds = new { minX = expected.Bounds.Minimum.X, minZ = expected.Bounds.Minimum.Z,
                    maxX = expected.Bounds.Maximum.X, maxZ = expected.Bounds.Maximum.Z },
                visible = result.Visible, picked = result.Picked, resolved = result.Resolved });
        }
        object? nearestBusiness = null;
        if (_probeNearest is { } nearest)
        {
            var result = ProbeBusiness(frame, session, display, nearest);
            if (result.Picked) _probeNearestPicked ??= Seconds();
            nearestBusiness = new { nearest.Key, expected = nearest.Subject?.ToString(), visible = result.Visible,
                picked = result.Picked, resolved = result.Resolved, pickedAtSeconds = _probeNearestPicked };
        }
        object? followed = null;
        if (follow is not null)
        {
            var result = ProbeBusiness(frame, session, display, new("follow", follow,
                new(center, center), false));
            clear &= result.Visible && result.Picked;
            followed = new { expected = follow.ToString(), visible = result.Visible, picked = result.Picked, resolved = result.Resolved };
        }
        detail = new { focus = new { x = center.X, y = center.Y, z = center.Z }, radiusMeters = 32,
            samples = points, intersectingFog, expectedKnown,
            business = results, emptyBusinessArea = expectedKnown && _probeExpected is { Count: 0 }, followed,
            nearestBusiness };
        return clear;
    }

    private bool ProbePrepareExpected(object node, ISceneFrame frame, NVector center)
    {
        if (_probeExpected is not null) return true;
        var layout = ProbeRead(node, "layout");
        var prepared = ProbeRead(node, "prepared");
        if (layout is null || prepared is null) return false;
        var all = new List<ProbeExpected>();
        var core = _app.Host!.GetStatus()!.View;
        var content = _app.Host.Content!;
        var origin = new NVector((float)frame.Origin.X, (float)frame.Origin.Y, (float)frame.Origin.Z);
        var nodeId = (NodeId)ProbeRead(node, "Node")!;
        if (core.World.Nodes.Single(item => item.Id == nodeId).Occupant is NodeOccupant.Settlement owner &&
            core.FindSettlement(owner.Id) is Lookup<SettlementView>.Found settlement &&
            content.FindSettlementLayout(owner.Id) is ContentLookup<SettlementLayoutProjection>.Found template)
        {
            foreach (var plot in settlement.Value.Plots)
            {
                if (plot.Occupant is not PlotOccupant.Building building) continue;
                var instance = ((Lookup<InstanceView>.Found)core.FindInstance(new InstanceId.Building(building.Id))).Value;
                var placed = template.Value.Plots.Single(item => item.Number == plot.TemplatePlotNumber);
                var bounds = ProbeBounds(ProbeDesign(instance.Design));
                all.Add(new(instance.Id.ToString()!, new WorldObjectId.Instance(instance.Id),
                    ProbeTransformBounds(bounds, ProbePlacement(placed.At, placed.Orientation), origin), false));
            }
            var parking = ((IEnumerable)ProbeRead(layout, "Parking")!).Cast<object>().ToArray();
            var used = new double[parking.Length];
            var settlementState = ProbeRead(node, "settlement")!;
            var limit = (int)settlementState.GetType().GetField("ParkedVehicleLimit",
                BindingFlags.Static | BindingFlags.NonPublic)!.GetRawConstantValue()!;
            foreach (var id in settlement.Value.ParkedVehicles.OrderBy(item => item.Value).Take(limit))
            {
                var instance = ((Lookup<InstanceView>.Found)core.FindInstance(new InstanceId.Vehicle(id))).Value;
                var design = ProbeDesign(instance.Design);
                var length = design switch
                {
                    DesignEvaluation.Complete { Core.Design.Kind: DesignKind.Vehicle vehicle } => vehicle.LengthCells,
                    DesignEvaluation.Partial { Metrics.VehicleLengthCells: ComputedValue<int>.Known known } => known.Value,
                    _ => throw new InvalidDataException("测量输入的停放车辆没有已知长度。")
                };
                var index = 0;
                while (index < parking.Length && used[index] + length > (double)ProbeRead(parking[index], "Length")!) index++;
                if (index == parking.Length) continue;
                var start = (ScenePoint)ProbeRead(parking[index], "Start")!;
                var forward = (NVector)ProbeRead(parking[index], "Forward")!;
                var centerAt = new NVector((float)start.X, (float)start.Y, (float)start.Z) + forward * (float)(used[index] + length / 2.0);
                used[index] += length + 4;
                var right = NVector.Normalize(NVector.Cross(NVector.UnitY, forward));
                var up = NVector.Cross(forward, right);
                var rotation = NQuaternion.CreateFromRotationMatrix(new NMatrix(right.X, right.Y, right.Z, 0,
                    up.X, up.Y, up.Z, 0, forward.X, forward.Y, forward.Z, 0, 0, 0, 0, 1));
                var bounds = ProbeBounds(design);
                var coordinates = node.GetType().Assembly.GetType("Train.Scenes.RailwayCoordinates")!;
                var dimensions = (Array)coordinates.GetField("WheelDimensions", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
                var size = ((InstanceDetails.Vehicle)instance.Details).Size;
                var wheel = (System.Runtime.CompilerServices.ITuple)dimensions.GetValue(size == SizeClass.Small ? 0 :
                    size is SizeClass.Medium or SizeClass.Large ? 1 : 2)!;
                var radius = (float)(double)wheel[2]!;
                var halfWidth = (float)((double)wheel[0]! / 2 + (double)wheel[3]!);
                bounds = new(NVector.Min(bounds.Minimum, new(-halfWidth, 0, bounds.Minimum.Z - radius)),
                    NVector.Max(bounds.Maximum, new(halfWidth, 2 * radius, bounds.Maximum.Z + radius)));
                all.Add(new(instance.Id.ToString()!, new WorldObjectId.Instance(instance.Id),
                    ProbeTransformBounds(bounds, new(centerAt, rotation), origin), false));
            }
        }
        _probeNearest = all.OrderBy(item => ProbeDistance(center, item.Bounds)).FirstOrDefault();
        foreach (var pair in (IEnumerable)ProbeRead(prepared, "Rails")!)
        {
            var rail = ProbeRead(pair, "Value")!;
            var bounds = (SceneBounds)ProbeRead(rail, "Bounds")!;
            all.Add(new((string)ProbeRead(rail, "Key")!, new WorldObjectId.Node((NodeId)ProbeRead(node, "Node")!),
                new(bounds.Minimum - origin, bounds.Maximum - origin), true));
        }
        _probeExpected = all.Where(item => ProbeDistance(center, item.Bounds) <= 64).ToList();
        return true;
    }

    private DesignEvaluation ProbeDesign(DesignHash hash) =>
        _app.Host!.Content!.FindDesign(hash) is ContentLookup<DesignEvaluation>.Found found
            ? found.Value : throw new InvalidDataException("测量输入的业务设计缺失。");

    private static SceneBounds ProbeBounds(DesignEvaluation design)
    {
        var projection = design switch
        {
            DesignEvaluation.Complete complete => complete.Scene,
            DesignEvaluation.Partial partial => partial.Scene,
            _ => throw new InvalidDataException("测量输入的业务设计无法求值。")
        };
        var bounds = projection.OccupiedBounds ?? throw new InvalidDataException("测量输入的业务对象没有已知占用范围。");
        return new(new(bounds.Minimum.X, bounds.Minimum.Y, bounds.Minimum.Z),
            new(bounds.Maximum.X + 1, bounds.Maximum.Y + 1, bounds.Maximum.Z + 1));
    }

    internal static ScenePose ProbePlacement(GridPosition at, VoxelOrientation orientation)
    {
        static NVector Axis(string name) => name switch
        {
            "Px" => NVector.UnitX, "Nx" => -NVector.UnitX, "Py" => NVector.UnitY,
            "Ny" => -NVector.UnitY, "Pz" => NVector.UnitZ, "Nz" => -NVector.UnitZ,
            _ => throw new ArgumentException("Invalid orientation.", nameof(orientation))
        };
        var axes = orientation.ToString();
        var right = Axis(axes[..2]); var up = Axis(axes[2..]); var forward = NVector.Cross(right, up);
        var rotation = NQuaternion.CreateFromRotationMatrix(new NMatrix(right.X, right.Y, right.Z, 0,
            up.X, up.Y, up.Z, 0, forward.X, forward.Y, forward.Z, 0, 0, 0, 0, 1));
        var center = new NVector(.5f);
        return new(new NVector(at.X, at.Y, at.Z) + center - NVector.Transform(center, rotation), rotation);
    }

    private (bool Visible, bool Picked, string? Resolved) ProbeBusiness(ISceneFrame frame,
        IWorldSceneSession? session, SceneDisplay display, ProbeExpected expected)
    {
        var objects = (Dictionary<SceneObjectId, SceneDisplay.DisplayObject>)ProbeRead(display, "_objects")!;
        var railIds = expected.Rail && ProbeRead(session, "ids") is Dictionary<string, SceneObjectId> ids
            ? ids.Where(pair => pair.Key.StartsWith("node:" + expected.Key + ":", StringComparison.Ordinal)).Select(pair => pair.Value).ToHashSet()
            : null;
        var candidates = objects.Values.Where(item => expected.Rail
            ? railIds!.Contains(item.Source.Id) && item.Source.Visuals.OfType<SceneVisual.Geometry>().Any(visual => visual.Mesh.Material is SceneMaterial.BuiltIn { Kind: BuiltInMaterial.RailSteel })
            : item.Source.Subject == expected.Subject).ToArray();
        var visible = false;
        string? resolved = null;
        foreach (var item in candidates)
        foreach (var mesh in expected.Rail
            ? item.Source.Visuals.Select((visual, index) => (visual, index))
                .Where(pair => pair.visual is SceneVisual.Geometry { Mesh.Material: SceneMaterial.BuiltIn { Kind: BuiltInMaterial.RailSteel } })
                .SelectMany(pair => ProbeMeshes(item.Visuals[pair.index].Node))
            : ProbeMeshes(item.Root))
        {
            if (!mesh.IsVisibleInTree() || mesh.Mesh is null) continue;
            var key = mesh.Mesh.GetInstanceId();
            if (!_probeMeshPoints.TryGetValue(key, out var candidatesOnMesh))
            {
                var faces = mesh.Mesh.GetFaces();
                var triangles = faces.Length / 3;
                candidatesOnMesh = new Vector3[Math.Min(24, triangles)];
                for (var index = 0; index < candidatesOnMesh.Length; index++)
                {
                    var face = index * triangles / candidatesOnMesh.Length * 3;
                    candidatesOnMesh[index] = (faces[face] + faces[face + 1] + faces[face + 2]) / 3;
                }
                _probeMeshPoints.Add(key, candidatesOnMesh);
            }
            foreach (var candidate in candidatesOnMesh)
            {
                var point = mesh.GlobalTransform * candidate;
                var relative = display.Root.GlobalTransform.AffineInverse() * point;
                if (expected.Rail && !ProbeInside(new(relative.X, relative.Y, relative.Z), expected.Bounds, 1)) continue;
                visible = true;
                var camera = _app.WorldScenes!.View.CameraRig.Camera;
                if (camera.IsPositionBehind(point)) continue;
                var screen = camera.UnprojectPosition(point);
                if (!camera.GetViewport().GetVisibleRect().HasPoint(screen)) continue;
                var inverse = display.Root.GlobalTransform.AffineInverse();
                var start = inverse * camera.ProjectRayOrigin(screen);
                var direction = (inverse.Basis * camera.ProjectRayNormal(screen)).Normalized();
                _scenePickCount++;
                if (frame.Pick(new(new(start.X, start.Y, start.Z), new(direction.X, direction.Y, direction.Z), camera.Far)) is not ScenePick.Hit hit ||
                    session?.Resolve(hit.Value) is not SceneResult<PickSubject>.Success success) continue;
                resolved = success.Value.ToString();
                if (expected.Rail ? success.Value is PickSubject.Terrain { Location: WorldLocation.Node nodeLocation } &&
                    expected.Subject is WorldObjectId.Node expectedNode && nodeLocation.Id == expectedNode.Id &&
                    ProbeInside(hit.Value.Point, expected.Bounds, 1) :
                    success.Value is PickSubject.WorldObject subject && subject.Object == expected.Subject)
                    return (true, true, resolved);
            }
        }
        return (visible, false, resolved);
    }

    private static IEnumerable<MeshInstance3D> ProbeMeshes(Node node)
    {
        if (node is MeshInstance3D mesh) yield return mesh;
        foreach (var child in node.GetChildren())
            foreach (var nested in ProbeMeshes(child)) yield return nested;
    }
    private static bool ProbeInside(NVector point, SceneBounds bounds, float padding = 0) =>
        point.X >= bounds.Minimum.X - padding && point.X <= bounds.Maximum.X + padding &&
        point.Z >= bounds.Minimum.Z - padding && point.Z <= bounds.Maximum.Z + padding;
    internal static double ProbeDistance(NVector point, SceneBounds bounds) => Math.Sqrt(
        Math.Pow(Math.Max(Math.Max(bounds.Minimum.X - point.X, 0), point.X - bounds.Maximum.X), 2) +
        Math.Pow(Math.Max(Math.Max(bounds.Minimum.Z - point.Z, 0), point.Z - bounds.Maximum.Z), 2));
    internal static SceneBounds ProbeTransformBounds(SceneBounds bounds, ScenePose pose, NVector origin)
    {
        var minimum = new NVector(float.PositiveInfinity);
        var maximum = new NVector(float.NegativeInfinity);
        foreach (var x in new[] { bounds.Minimum.X, bounds.Maximum.X })
        foreach (var y in new[] { bounds.Minimum.Y, bounds.Maximum.Y })
        foreach (var z in new[] { bounds.Minimum.Z, bounds.Maximum.Z })
        {
            var point = NVector.Transform(new(x, y, z), pose.Rotation) + pose.Position - origin;
            minimum = NVector.Min(minimum, point);
            maximum = NVector.Max(maximum, point);
        }
        return new(minimum, maximum);
    }
}

