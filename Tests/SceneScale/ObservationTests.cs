
using System.Collections.Immutable;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Train.Core.Contracts;
using Train.Scenes.Contracts;
using Train.SceneScale;

using Microsoft.VisualStudio.TestTools.UnitTesting;
namespace Train.SceneScale.Tests;
[TestClass]
public sealed class ObservationTests
{
    [TestMethod]
    [TestCategory("fast")]
    [DataRow("complete") ]
    [DataRow("readiness") ]
    [DataRow("missing") ]
    [DataRow("fog") ]
    [DataRow("repeat-time") ]
    [DataRow("drift") ]
    [DataRow("stability-window") ]
    [DataRow("updates") ]
    [DataRow("capture") ]
    [DataRow("geometry-cache") ]
    [DataRow("nonrail") ]
    public void Observe(string name)
    {
var frame = new TestFrame();
var heights = new Dictionary<(int X, int Z), double>();
var columns = Probe.LocalColumns(Vector2.Zero);
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
Probe.LocalCoverage Observe() => Probe.ObserveLocalCoverage(frame, Vector2.Zero, columns, heights);
switch (name)
{
    case "complete":
        Check(columns.Count == 115816, "the original fixed 192-cell circle must keep every column");
        Check(Observe().Complete && heights.Count == columns.Count, "ready and exact heights must complete immediately");
        Check(heights[(-1, -1)] == 7, "world height includes the floating origin");
        break;
    case "readiness":
        foreach (var state in new[] { SceneReadiness.Loading, SceneReadiness.Partial, SceneReadiness.Failed })
        {
            frame.Readiness = state;
            Check(!Observe().Complete, "non-ready frame cannot complete");
            Check(!new Probe.LocalCoverage(state, 0, 1, 1).Complete, "precise local coverage alone cannot substitute for Ready");
        }
        break;
    case "missing":
        Check(Observe().Complete, "the first precise frame must be complete");
        frame.Missing = (-192, -1);
        Check(!Observe().Complete && heights.Count == columns.Count - 1, "one missing precise column must block completion");
        frame.Missing = null;
        Check(Observe().Complete, "all exact columns must recover");
        break;
    case "fog":
        frame.Fog = [new(new(182, -1, -21), new(184, 1, -19))];
        Check(!Observe().Complete, "fog touching the fixed world circle must block after origin conversion");
        frame.Fog = [new(new(182.01f, -1, -21), new(184, 1, -19))];
        Check(Observe().Complete, "fog beyond the world circle must not block");
        frame.Fog = [new(new(-11, -1, -21), new(-9, 1, -19))];
        Check(!Observe().Complete, "a fog box enclosing the center must block");
        break;
    case "repeat-time":
        var timed = new Probe.LocalObservation(4, 3, "geometry", 2, "rail", 10, 10, 0, 0, "height");
        Check(!timed.CanRepeatAt(13.999), "second sample cannot be early");
        Check(timed.CanRepeatAt(14) && timed.CanRepeatAt(14.001), "second sample starts at ten seconds");
        break;
    case "drift":
        var first = new Probe.LocalObservation(4, 3, "geometry", 2, "rail", 10, 10, 0, 0, "height");
        Check(first.SameContent(first with { seconds = 14 }), "observation time must not alter content equality");
        foreach (var changed in new[] { first with { triangleWorldCoordinatesMaterialSha256 = "changed" },
            first with { railSteelSha256 = "changed" }, first with { terrainHeightSha256 = "changed" },
            first with { triangles = 4 }, first with { railSteelTriangles = 3 },
            first with { heightColumns = 11 }, first with { heightFound = 9 },
            first with { heightMissing = 1 }, first with { foggedRegionsIntersecting = 1 },
            first with { objectSetSha256 = "another-id" }, first with { objectCount = 1 },
            first with { nonRailTriangles = 1 }, first with { nonRailTriangleWorldCoordinatesMaterialSha256 = "changed" } })
            Check(!first.SameContent(changed), "hash or coverage drift must fail stability");
        break;
    case "stability-window":
        var observation = new Probe.LocalObservation(10, 3, "geometry", 2, "rail", 10, 10, 0, 0, "height", "ids", 2);
        var window = new Probe.LocalStabilityWindow(0);
        Check(!window.IsStable(39.999) && window.UnchangedSinceSeconds is null, "an empty window is not stable and has no interval start");
        window.Track(observation);
        window.Track(observation with { seconds = 39.999, railSteelSha256 = "other-rail", terrainHeightSha256 = "other-height" });
        Check(window.UnchangedSinceSeconds == 10 && !window.IsStable(39.999) && window.IsStable(40) && window.IsStable(40.001),
            "unchanged object set and geometry need a full thirty seconds, excluding rail/height from this wait");
        foreach (var changed in new[] { observation with { triangles = 4 },
            observation with { triangleWorldCoordinatesMaterialSha256 = "other-geometry" },
            observation with { objectSetSha256 = "replacement-id" }, observation with { objectCount = 3 } })
        {
            var changedWindow = new Probe.LocalStabilityWindow(0);
            changedWindow.Track(observation);
            changedWindow.Track(changed with { seconds = 39 });
            Check(changedWindow.UnchangedSinceSeconds == 39 && !changedWindow.IsStable(40) && !changedWindow.IsStable(68.999) && changedWindow.IsStable(69),
                "object identity, count, geometry hash or triangle count changes restart thirty seconds");
        }
        window.Reset();
        Check(window.UnchangedSinceSeconds is null && !window.IsStable(100), "lost coverage clears the stability window");
        window.Track(observation with { seconds = 120 });
        Check(!window.IsStable(149.999) && window.IsStable(150), "coverage recovery starts a fresh window");
        Check(!window.TimedOut(299.999) && window.TimedOut(300) && window.TimedOut(300.001),
            "single-position wall clock deadline stays at three hundred seconds despite resets");
        var movedWindow = new Probe.LocalStabilityWindow(1000);
        Check(!movedWindow.TimedOut(1299.999) && movedWindow.TimedOut(1300), "deadline is relative to this camera position");
        break;
    case "updates":
        var root = new SceneObject(new(1), null, new(new(2, 3, 4), Quaternion.Identity), null, [], [], []);
        var child = new SceneObject(new(2), new(1), new(new(5, 6, 7), Quaternion.Identity), null, [], [], []);
        var objects = new Dictionary<SceneObjectId, SceneObject> { [root.Id] = root, [child.Id] = child };
        var stamp = frame.Update.Frame;
        Probe.ApplyUpdate(objects, new(stamp, stamp with { Revision = 2 }, new(-10, -20, -30), [], [], new([], []), []));
        Check(objects[root.Id].Pose.Position == new Vector3(-8, -17, -26), "incremental origin shift moves retained roots");
        Check(objects[child.Id].Pose.Position == child.Pose.Position, "children stay relative to their shifted parent");
        var replaced = root with { Pose = new(Vector3.One, Quaternion.Identity) };
        Probe.ApplyUpdate(objects, new(stamp, stamp with { Revision = 3 }, Vector3.One, [replaced], [child.Id], new([], []), []));
        Check(objects.Count == 1 && objects[root.Id] == replaced, "upserts replace after shift, removed ids disappear");
        Probe.ApplyUpdate(objects, new(null, stamp with { Revision = 4 }, Vector3.One, [child with { Parent = null }], [], new([], []), []));
        Check(objects.Count == 1 && !objects.ContainsKey(root.Id) && objects[child.Id].Pose == child.Pose,
            "a full snapshot discards old objects and does not shift its upserts");
        break;
    case "capture":
        SceneObject Triangle(ulong id, SceneObjectId? parent, Vector3 position, SceneMaterial material, params Vector3[] points) =>
            new(new(id), parent, new(position, Quaternion.Identity), null,
                [new SceneVisual.Geometry(new(material, points.Select(p => new SceneVertex(p, Vector3.UnitY, Vector2.Zero,
                    Vector2.Zero, ExposedFaceEdges.None, 0, 0)).ToImmutableArray(), [0, 1, 2]), new(false, 0, 0, false))], [], []);
        var material = new SceneMaterial.BuiltIn(BuiltInMaterial.RailSteel);
        var parent = new SceneObject(new(1), null, new(new(-10, -5, -20), Quaternion.Identity), null, [], [], []);
        var triangles = new Dictionary<SceneObjectId, SceneObject>
        {
            [new(1)] = parent,
            [new(2)] = Triangle(2, new(1), Vector3.Zero, material, new(0, 0, 0), new(1, 0, 0), new(0, 0, 1)) with { Subject = new WorldObjectId.Node(new(42)) },
            [new(3)] = Triangle(3, new(1), Vector3.Zero, material, new(1, 0, 0), new(0, 0, 1), new(0, 0, 0)),
            [new(4)] = Triangle(4, new(1), Vector3.Zero, new SceneMaterial.BuiltIn(BuiltInMaterial.Sleeper), new(0, 0, 0), new(1, 0, 0), new(0, 0, 1)),
            [new(5)] = Triangle(5, new(1), new(192.0005f, 0, 0), material, new(0, 0, 0), new(1, 0, 0), new(0, 0, 1)),
            [new(6)] = Triangle(6, new(1), new(192.002f, 0, 0), material, new(0, 0, 0), new(1, 0, 0), new(0, 0, 1)),
            [new(7)] = Triangle(7, new(1), Vector3.Zero, material, new(-300, 0, -300), new(300, 0, -300), new(0, 0, 300)),
            [new(8)] = Triangle(8, new(1), Vector3.Zero, material, new(-300, 0, 0), new(300, 0, 0), new(300, 0, 300))
        };
        var exact = new Dictionary<(int X, int Z), double> { [(0, 0)] = 1.0004, [(1, 0)] = -1.0005 };
        var captured = Probe.CaptureLocal(triangles, frame.Origin, Vector2.Zero, exact, new(SceneReadiness.Ready, 0, 2, 2), 4);
        Check(captured.Observation.triangles == 5 && captured.Observation.railSteelTriangles == 4,
            "deduplicate world triangles, retain material and all intersections including containing and crossing triangles");
        Check(captured.Geometry is not null, "capture must retain a per-object geometry inventory");
        var inventory = captured.Geometry!;
        Check(inventory.objects.Length == 6 && captured.Observation.objectCount == 6,
            "only objects owning a conservative-circle triangle enter the observed object set");
        Check(inventory.fully_inside.objects == 3 && inventory.fully_inside.triangles == 2 &&
            inventory.crossing_boundary.objects == 3 && inventory.crossing_boundary.triangles == 3,
            "classify whole objects by all Geometry vertices, then independently deduplicate category triangles");
        var originObject = inventory.objects.Single(o => o.id == new SceneObjectId(2));
        Check(originObject.parent == parent.Id && originObject.subject == "Node { Id = NodeId { Value = 42 } }" && originObject.worldPose.Position == Vector3.Zero &&
            originObject.worldBounds.Minimum == Vector3.Zero && originObject.worldBounds.Maximum == new Vector3(1, 0, 1) &&
            originObject.category == "fully_inside" && originObject.localTriangles == 1 &&
            originObject.materials.SequenceEqual(new[] { "BuiltIn { Kind = RailSteel }" }), "inventory must expose public identity and exact world geometry metadata");
        Check(captured.Triangles.Contains("BuiltIn { Kind = RailSteel }|0,0,0;0,0,1000;1000,0,0"), "hierarchy and origin must produce original world coordinates");
        Check(captured.Triangles.Any(key => key.Contains("192001,0,0")), "the conservative radius and .001 quantization must remain");
        string Sha(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        Check(captured.Observation.terrainHeightSha256 == Sha("0,0,1000\n1,0,-1001"), "height hash keeps original sorted .001 quantization");
        var single = Probe.CaptureLocal(new() { [new(1)] = parent, [new(2)] = triangles[new(2)] }, frame.Origin,
            Vector2.Zero, exact, new(SceneReadiness.Ready, 0, 2, 2), 4);
        var triangleHash = Sha("BuiltIn { Kind = RailSteel }|0,0,0;0,0,1000;1000,0,0");
        Check(single.Observation.triangleWorldCoordinatesMaterialSha256 == triangleHash && single.Observation.railSteelSha256 == triangleHash,
            "both geometry digests hash sorted quantized world coordinates and material");
        Check(originObject.triangleWorldCoordinatesMaterialSha256 == triangleHash, "each object records its local triangle digest");
        Check(captured.Observation.objectSetSha256 == Sha("2\n3\n4\n5\n7\n8"), "runtime object-set digest uses public opaque ids");
        var renumbered = triangles.Values.Select(o => o with { Id = new(o.Id.Value + 100),
            Parent = o.Parent is { } p ? new SceneObjectId(p.Value + 100) : null }).ToDictionary(o => o.Id);
        var renumberedCapture = Probe.CaptureLocal(renumbered, frame.Origin, Vector2.Zero, exact, new(SceneReadiness.Ready, 0, 2, 2), 20);
        Check(renumberedCapture.Observation.objectSetSha256 != captured.Observation.objectSetSha256,
            "replacement opaque ids change the current-session object set");
        Check(renumberedCapture.Geometry!.fully_inside == inventory.fully_inside && renumberedCapture.Geometry.crossing_boundary == inventory.crossing_boundary,
            "cross-scale category hashes and counts must not depend on opaque object or parent ids");
        var identityWindow = new Probe.LocalStabilityWindow(0);
        identityWindow.Track(captured.Observation);
        identityWindow.Track(renumberedCapture.Observation);
        Check(!identityWindow.IsStable(34) && identityWindow.IsStable(50), "same geometry with new opaque ids still restarts the stability window");
        var empty = Probe.CaptureLocal([], frame.Origin, Vector2.Zero, exact, new(SceneReadiness.Ready, 0, 2, 2), 14);
        Check(empty.Observation.triangles == 0 && empty.Observation.railSteelTriangles == 0,
            "each snapshot must be fresh rather than retaining triangles from the first observation");
        break;
    case "geometry-cache":
        var cacheMaterial = new SceneMaterial.BuiltIn(BuiltInMaterial.RailSteel);
        var cachedSource = Triangle(20, null, Vector3.Zero, cacheMaterial, Vector3.Zero, Vector3.UnitX, Vector3.UnitZ);
        var cache = new Probe.LocalGeometryCache();
        var atOrigin = new ScenePose(Vector3.Zero, Quaternion.Identity);
        var cachedGeometry = cache.GeometryFor(cachedSource, atOrigin, Vector2.Zero);
        var changedSurface = cachedSource with { Visuals = [new SceneVisual.Geometry(
            ((SceneVisual.Geometry)cachedSource.Visuals[0]).Mesh, new(true, 9, .5f, true))],
            Effects = [new(PersistentEffectKind.Smoke, Vector3.Zero, 1, 1)] };
        Check(ReferenceEquals(cachedGeometry, cache.GeometryFor(changedSurface, atOrigin, Vector2.Zero)),
            "unchanged mesh references, world pose and focus reuse the same cached keys despite surface/effect changes");
        var newMesh = Triangle(20, null, Vector3.Zero, cacheMaterial, Vector3.Zero, new(2, 0, 0), Vector3.UnitZ);
        var replacedGeometry = cache.GeometryFor(newMesh, atOrigin, Vector2.Zero);
        Check(!ReferenceEquals(cachedGeometry, replacedGeometry) && cachedGeometry.TriangleSha256 != replacedGeometry.TriangleSha256,
            "changed mesh references replace the one current cache entry");
        var movedGeometry = cache.GeometryFor(newMesh, new(Vector3.One, Quaternion.Identity), Vector2.Zero);
        Check(movedGeometry.TriangleSha256 != replacedGeometry.TriangleSha256, "world pose invalidates cached world keys");
        var farFocus = cache.GeometryFor(newMesh, new(Vector3.One, Quaternion.Identity), new(1000, 1000));
        Check(farFocus.Triangles.Count == 0, "a changed focus invalidates cached local membership and far bounds prefilter triangles");
        cache.Retain(new Dictionary<SceneObjectId, SceneObject> { [newMesh.Id] = newMesh });
        Check(ReferenceEquals(farFocus, cache.GeometryFor(newMesh, new(Vector3.One, Quaternion.Identity), new(1000, 1000))), "retained ids preserve current cache entries");
        cache.Retain(new Dictionary<SceneObjectId, SceneObject>());
        Check(!ReferenceEquals(farFocus, cache.GeometryFor(newMesh, new(Vector3.One, Quaternion.Identity), new(1000, 1000))), "removed ids release cached keys without mutating a live dictionary iterator");
        var edgeSource = Triangle(30, null, Vector3.Zero, cacheMaterial, new(192, 0, 0), new(191, 0, 0), new(191, 0, 1));
        var edgeGeometry = cache.GeometryFor(edgeSource, atOrigin, Vector2.Zero);
        Check(edgeGeometry.FullyInside && edgeGeometry.Triangles.Count == 1, "a vertex exactly at radius192 stays fully_inside without expanding the radius");
        var beyondEdge = edgeSource with { Visuals = [new SceneVisual.Geometry(
            ((SceneVisual.Geometry)edgeSource.Visuals[0]).Mesh with { Vertices = ((SceneVisual.Geometry)edgeSource.Visuals[0]).Mesh.Vertices.SetItem(0,
                new(new(192.0005f, 0, 0), Vector3.UnitY, Vector2.Zero, Vector2.Zero, ExposedFaceEdges.None, 0, 0)) }, new(false, 0, 0, false))] };
        Check(!cache.GeometryFor(beyondEdge, atOrigin, Vector2.Zero).FullyInside,
            "radius192.0005 belongs to crossing_boundary even though the conservative triangle clipping still includes it");
        var unusedSource = cachedSource with { Visuals = [new SceneVisual.Geometry(
            ((SceneVisual.Geometry)cachedSource.Visuals[0]).Mesh with { Vertices = ((SceneVisual.Geometry)cachedSource.Visuals[0]).Mesh.Vertices.Add(
                new(new(300, 0, 0), Vector3.UnitY, Vector2.Zero, Vector2.Zero, ExposedFaceEdges.None, 0, 0)) }, new(false, 0, 0, false))] };
        var unusedGeometry = cache.GeometryFor(unusedSource, atOrigin, Vector2.Zero);
        Check(!unusedGeometry.FullyInside && unusedGeometry.Bounds!.Maximum.X == 300 && unusedGeometry.Triangles.Count == 1,
            "classification and reported bounds include every Geometry vertex, including unused vertices");
        break;
    default: NonRailCases.Run(name); break;
}


    }
}
sealed class TestFrame : ISceneFrame
{
    public (int X, int Z)? Missing;
    public ImmutableArray<SceneBounds> Fog = [];
    public ScenePoint Origin => new(10, 5, 20);
    public SceneReadiness Readiness { get; set; } = SceneReadiness.Ready;
    public ScenePanBounds? NodePanBounds => null;
    public SceneNodeOverview? NodeOverview => null;
    public double SimulationSeconds => 0;
    public SceneUpdate Update => new(null, new(new(Guid.Empty), new(1), new(1), 1), Vector3.Zero, [], [], new(Fog, []), []);
    public ImmutableArray<SceneProblem> Diagnostics => [];
    public ImmutableArray<SceneDebugLine> DebugLines => [];
    public Lookup<double> TerrainHeight(double x, double z) =>
        Missing == ((int)Math.Floor(x + Origin.X), (int)Math.Floor(z + Origin.Z))
            ? new Lookup<double>.Missing() : new Lookup<double>.Found(2);
    public Lookup<double> DisplayTerrainHeight(double x, double z) => throw new NotSupportedException();
    public ScenePick Pick(SceneRay ray) => throw new NotSupportedException();
    public Lookup<ScenePose> FindPose(WorldObjectId subject) => throw new NotSupportedException();
    public Lookup<WorldLocation> Locate(Vector3 point) => throw new NotSupportedException();
}
