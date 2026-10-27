
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
[TestCategory("fast")]
public sealed class GeometryObservationTests
{
    [TestMethod]
    public void Capture_preserves_world_geometry_inventory_and_identity()
    {
        var frame = new TestFrame();
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
        Assert.IsTrue(captured.Observation.triangles == 5 && captured.Observation.railSteelTriangles == 4,
            "deduplicate world triangles, retain material and all intersections including containing and crossing triangles");
        Assert.IsTrue(captured.Geometry is not null, "capture must retain a per-object geometry inventory");
        var inventory = captured.Geometry!;
        Assert.IsTrue(inventory.objects.Length == 6 && captured.Observation.objectCount == 6,
            "only objects owning a conservative-circle triangle enter the observed object set");
        Assert.IsTrue(inventory.fully_inside.objects == 3 && inventory.fully_inside.triangles == 2 &&
            inventory.crossing_boundary.objects == 3 && inventory.crossing_boundary.triangles == 3,
            "classify whole objects by all Geometry vertices, then independently deduplicate category triangles");
        var originObject = inventory.objects.Single(o => o.id == new SceneObjectId(2));
        Assert.IsTrue(originObject.parent == parent.Id && originObject.subject == "Node { Id = NodeId { Value = 42 } }" && originObject.worldPose.Position == Vector3.Zero &&
            originObject.worldBounds.Minimum == Vector3.Zero && originObject.worldBounds.Maximum == new Vector3(1, 0, 1) &&
            originObject.category == "fully_inside" && originObject.localTriangles == 1 &&
            originObject.materials.SequenceEqual(new[] { "BuiltIn { Kind = RailSteel }" }), "inventory must expose public identity and exact world geometry metadata");
        Assert.IsTrue(captured.Triangles.Contains("BuiltIn { Kind = RailSteel }|0,0,0;0,0,1000;1000,0,0"), "hierarchy and origin must produce original world coordinates");
        Assert.IsTrue(captured.Triangles.Any(key => key.Contains("192001,0,0")), "the conservative radius and .001 quantization must remain");
        string Sha(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        Assert.IsTrue(captured.Observation.terrainHeightSha256 == Sha("0,0,1000\n1,0,-1001"), "height hash keeps original sorted .001 quantization");
        var single = Probe.CaptureLocal(new() { [new(1)] = parent, [new(2)] = triangles[new(2)] }, frame.Origin,
            Vector2.Zero, exact, new(SceneReadiness.Ready, 0, 2, 2), 4);
        var triangleHash = Sha("BuiltIn { Kind = RailSteel }|0,0,0;0,0,1000;1000,0,0");
        Assert.IsTrue(single.Observation.triangleWorldCoordinatesMaterialSha256 == triangleHash && single.Observation.railSteelSha256 == triangleHash,
            "both geometry digests hash sorted quantized world coordinates and material");
        Assert.IsTrue(originObject.triangleWorldCoordinatesMaterialSha256 == triangleHash, "each object records its local triangle digest");
        Assert.IsTrue(captured.Observation.objectSetSha256 == Sha("2\n3\n4\n5\n7\n8"), "runtime object-set digest uses public opaque ids");
        var renumbered = triangles.Values.Select(o => o with { Id = new(o.Id.Value + 100),
            Parent = o.Parent is { } p ? new SceneObjectId(p.Value + 100) : null }).ToDictionary(o => o.Id);
        var renumberedCapture = Probe.CaptureLocal(renumbered, frame.Origin, Vector2.Zero, exact, new(SceneReadiness.Ready, 0, 2, 2), 20);
        Assert.IsTrue(renumberedCapture.Observation.objectSetSha256 != captured.Observation.objectSetSha256,
            "replacement opaque ids change the current-session object set");
        Assert.IsTrue(renumberedCapture.Geometry!.fully_inside == inventory.fully_inside && renumberedCapture.Geometry.crossing_boundary == inventory.crossing_boundary,
            "cross-scale category hashes and counts must not depend on opaque object or parent ids");
        var identityWindow = new Probe.LocalStabilityWindow(0);
        identityWindow.Track(captured.Observation);
        identityWindow.Track(renumberedCapture.Observation);
        Assert.IsTrue(!identityWindow.IsStable(34) && identityWindow.IsStable(50), "same geometry with new opaque ids still restarts the stability window");
        var empty = Probe.CaptureLocal([], frame.Origin, Vector2.Zero, exact, new(SceneReadiness.Ready, 0, 2, 2), 14);
        Assert.IsTrue(empty.Observation.triangles == 0 && empty.Observation.railSteelTriangles == 0,
            "each snapshot must be fresh rather than retaining triangles from the first observation");
    }

    [TestMethod]
    public void Geometry_cache_reuses_only_current_mesh_pose_and_focus()
    {
        var cacheMaterial = new SceneMaterial.BuiltIn(BuiltInMaterial.RailSteel);
        var cachedSource = Triangle(20, null, Vector3.Zero, cacheMaterial, Vector3.Zero, Vector3.UnitX, Vector3.UnitZ);
        var cache = new Probe.LocalGeometryCache();
        var atOrigin = new ScenePose(Vector3.Zero, Quaternion.Identity);
        var cachedGeometry = cache.GeometryFor(cachedSource, atOrigin, Vector2.Zero);
        var changedSurface = cachedSource with { Visuals = [new SceneVisual.Geometry(
            ((SceneVisual.Geometry)cachedSource.Visuals[0]).Mesh, new(true, 9, .5f, true))],
            Effects = [new(PersistentEffectKind.Smoke, Vector3.Zero, 1, 1)] };
        Assert.IsTrue(ReferenceEquals(cachedGeometry, cache.GeometryFor(changedSurface, atOrigin, Vector2.Zero)),
            "unchanged mesh references, world pose and focus reuse the same cached keys despite surface/effect changes");
        var newMesh = Triangle(20, null, Vector3.Zero, cacheMaterial, Vector3.Zero, new(2, 0, 0), Vector3.UnitZ);
        var replacedGeometry = cache.GeometryFor(newMesh, atOrigin, Vector2.Zero);
        Assert.IsTrue(!ReferenceEquals(cachedGeometry, replacedGeometry) && cachedGeometry.TriangleSha256 != replacedGeometry.TriangleSha256,
            "changed mesh references replace the one current cache entry");
        var movedGeometry = cache.GeometryFor(newMesh, new(Vector3.One, Quaternion.Identity), Vector2.Zero);
        Assert.IsTrue(movedGeometry.TriangleSha256 != replacedGeometry.TriangleSha256, "world pose invalidates cached world keys");
        var farFocus = cache.GeometryFor(newMesh, new(Vector3.One, Quaternion.Identity), new(1000, 1000));
        Assert.IsTrue(farFocus.Triangles.Count == 0, "a changed focus invalidates cached local membership and far bounds prefilter triangles");
        cache.Retain(new Dictionary<SceneObjectId, SceneObject> { [newMesh.Id] = newMesh });
        Assert.IsTrue(ReferenceEquals(farFocus, cache.GeometryFor(newMesh, new(Vector3.One, Quaternion.Identity), new(1000, 1000))), "retained ids preserve current cache entries");
        cache.Retain(new Dictionary<SceneObjectId, SceneObject>());
        Assert.IsTrue(!ReferenceEquals(farFocus, cache.GeometryFor(newMesh, new(Vector3.One, Quaternion.Identity), new(1000, 1000))), "removed ids release cached keys without mutating a live dictionary iterator");
        var edgeSource = Triangle(30, null, Vector3.Zero, cacheMaterial, new(192, 0, 0), new(191, 0, 0), new(191, 0, 1));
        var edgeGeometry = cache.GeometryFor(edgeSource, atOrigin, Vector2.Zero);
        Assert.IsTrue(edgeGeometry.FullyInside && edgeGeometry.Triangles.Count == 1, "a vertex exactly at radius192 stays fully_inside without expanding the radius");
        var beyondEdge = edgeSource with { Visuals = [new SceneVisual.Geometry(
            ((SceneVisual.Geometry)edgeSource.Visuals[0]).Mesh with { Vertices = ((SceneVisual.Geometry)edgeSource.Visuals[0]).Mesh.Vertices.SetItem(0,
                new(new(192.0005f, 0, 0), Vector3.UnitY, Vector2.Zero, Vector2.Zero, ExposedFaceEdges.None, 0, 0)) }, new(false, 0, 0, false))] };
        Assert.IsTrue(!cache.GeometryFor(beyondEdge, atOrigin, Vector2.Zero).FullyInside,
            "radius192.0005 belongs to crossing_boundary even though the conservative triangle clipping still includes it");
        var unusedSource = cachedSource with { Visuals = [new SceneVisual.Geometry(
            ((SceneVisual.Geometry)cachedSource.Visuals[0]).Mesh with { Vertices = ((SceneVisual.Geometry)cachedSource.Visuals[0]).Mesh.Vertices.Add(
                new(new(300, 0, 0), Vector3.UnitY, Vector2.Zero, Vector2.Zero, ExposedFaceEdges.None, 0, 0)) }, new(false, 0, 0, false))] };
        var unusedGeometry = cache.GeometryFor(unusedSource, atOrigin, Vector2.Zero);
        Assert.IsTrue(!unusedGeometry.FullyInside && unusedGeometry.Bounds!.Maximum.X == 300 && unusedGeometry.Triangles.Count == 1,
            "classification and reported bounds include every Geometry vertex, including unused vertices");
    }

    private static SceneObject Triangle(ulong id, SceneObjectId? parent, Vector3 position, SceneMaterial material, params Vector3[] points) =>
        new(new(id), parent, new(position, Quaternion.Identity), null,
            [new SceneVisual.Geometry(new(material, points.Select(p => new SceneVertex(p, Vector3.UnitY, Vector2.Zero,
                Vector2.Zero, ExposedFaceEdges.None, 0, 0)).ToImmutableArray(), [0, 1, 2]), new(false, 0, 0, false))], [], []);
}
