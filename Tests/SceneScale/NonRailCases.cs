
using System.Collections.Immutable;
using System.Numerics;
using Train.Core.Contracts;
using Train.Scenes.Contracts;
using Train.SceneScale;

using Microsoft.VisualStudio.TestTools.UnitTesting;
namespace Train.SceneScale.Tests;
[TestClass]
[TestCategory("fast")]
public sealed class NonRailCases
{
    [TestMethod]
    public void Nonrail_signatures_exclude_track_materials_and_preserve_object_categories()
    {
        SceneVisual.Geometry Triangle(SceneMaterial material, float x, float width = 1) => new(
            new(material, new[] { new Vector3(x, 0, 0), new Vector3(x + width, 0, 0), new Vector3(x, 0, 1) }
                .Select(point => new SceneVertex(point, Vector3.UnitY, Vector2.Zero, Vector2.Zero, ExposedFaceEdges.None, 0, 0)).ToImmutableArray(),
                [0, 1, 2]), new(false, 0, 0, false));
        var steel = new SceneMaterial.BuiltIn(BuiltInMaterial.RailSteel);
        var sleeper = new SceneMaterial.BuiltIn(BuiltInMaterial.Sleeper);
        var water = new SceneMaterial.BuiltIn(BuiltInMaterial.Water);
        var construction = new SceneMaterial.BuiltIn(BuiltInMaterial.Construction);
        var voxel = new SceneMaterial.Voxel(new("ground"), Train.Content.Contracts.MaterialCategory.StoneSoil, null);
        var mixed = new SceneObject(new(1), null, new(Vector3.Zero, Quaternion.Identity), null,
            [Triangle(steel, 0), Triangle(sleeper, 2), Triangle(water, 4), Triangle(construction, 6), Triangle(voxel, 8)], [], []);
        var cache = new Probe.LocalGeometryCache();
        Probe.LocalCapture Capture(SceneObject item, double seconds) => Probe.CaptureLocal(new() { [item.Id] = item },
            new(0, 0, 0), Vector2.Zero, new(), new(SceneReadiness.Ready, 0, 0, 0), seconds, cache);
        var baseline = Capture(mixed, 0);
        const string expectedNonRailHash = "C45371F5377973241369F24AEEA67A811A4590DE6F453FC8E384C2527B39A710";
        var expectedKeys = new[]
        {
            "BuiltIn { Kind = Construction }|6000,0,0;6000,0,1000;7000,0,0",
            "BuiltIn { Kind = Water }|4000,0,0;4000,0,1000;5000,0,0",
            "Voxel { Texture = AssetRevision { Value = ground }, Category = StoneSoil, Paint =  }|8000,0,0;8000,0,1000;9000,0,0"
        };
        Assert.IsTrue(baseline.NonRailTriangles.SetEquals(expectedKeys) && baseline.Observation.nonRailTriangles == 3 &&
            baseline.Observation.nonRailTriangleWorldCoordinatesMaterialSha256 == expectedNonRailHash,
            "mixed objects must keep exactly Water, Construction and Voxel triangles with their original world/material keys");
        Assert.IsTrue(baseline.Observation.triangles == 5 && baseline.Observation.railSteelTriangles == 1,
            "nonrail reporting must preserve the original whole-mesh and RailSteel counts");
        var item = baseline.Geometry!.objects.Single();
        Assert.IsTrue(item.nonRailTriangles == 3 && item.nonRailTriangleWorldCoordinatesMaterialSha256 == expectedNonRailHash &&
            baseline.Geometry.fully_inside.nonRailTriangles == 3 &&
            baseline.Geometry.fully_inside.nonRailTriangleWorldCoordinatesMaterialSha256 == expectedNonRailHash,
            "per-object and per-category reports must retain the same nonrail count and digest");
        foreach (var change in new[] { (Index: 0, Mesh: Triangle(steel, 0, 1.5f)), (Index: 1, Mesh: Triangle(sleeper, 2, 1.5f)) })
        {
            var altered = Capture(mixed with { Visuals = mixed.Visuals.SetItem(change.Index, change.Mesh) }, 20);
            Assert.IsTrue(altered.NonRailTriangles.SetEquals(expectedKeys) && altered.Observation.nonRailTriangles == 3 &&
                altered.Observation.nonRailTriangleWorldCoordinatesMaterialSha256 == expectedNonRailHash,
                "RailSteel or Sleeper mesh changes must not change the mixed object's nonrail result");
            Assert.IsTrue(altered.Observation.triangleWorldCoordinatesMaterialSha256 != baseline.Observation.triangleWorldCoordinatesMaterialSha256 &&
                !baseline.Observation.SameContent(altered.Observation), "same-position ten-second verification must still reject a changed track mesh");
            var window = new Probe.LocalStabilityWindow(0);
            window.Track(baseline.Observation);
            window.Track(altered.Observation);
            Assert.IsTrue(!window.IsStable(30) && window.IsStable(50), "the thirty-second window must still track the whole mesh");
        }
        foreach (var change in new[] { (Index: 2, Mesh: Triangle(water, 4, 1.5f)),
            (Index: 3, Mesh: Triangle(construction, 6, 1.5f)), (Index: 4, Mesh: Triangle(voxel, 8, 1.5f)) })
        {
            var altered = Capture(mixed with { Visuals = mixed.Visuals.SetItem(change.Index, change.Mesh) }, 20);
            Assert.IsTrue(altered.Observation.nonRailTriangles == 3 && altered.Observation.nonRailTriangleWorldCoordinatesMaterialSha256 != expectedNonRailHash,
                "every Water, Construction and Voxel mesh change must affect the strict nonrail digest");
        }
        var trackOnly = Capture(mixed with { Visuals = [mixed.Visuals[0], mixed.Visuals[1]] }, 20);
        Assert.IsTrue(trackOnly.NonRailTriangles.Count == 0 && trackOnly.Observation.nonRailTriangles == 0 &&
            trackOnly.Observation.nonRailTriangleWorldCoordinatesMaterialSha256 == "E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855",
            "track-only objects retain an explicit empty nonrail digest");
        var crossing = mixed with { Id = new(2), Visuals = [Triangle(water, 192)] };
        var split = Probe.CaptureLocal(new() { [mixed.Id] = mixed, [crossing.Id] = crossing }, new(0, 0, 0), Vector2.Zero,
            new(), new(SceneReadiness.Ready, 0, 0, 0), 20, cache);
        Assert.IsTrue(split.Observation.nonRailTriangles == 4 && split.Geometry!.fully_inside.nonRailTriangles == 3 &&
            split.Geometry.fully_inside.nonRailTriangleWorldCoordinatesMaterialSha256 == expectedNonRailHash &&
            split.Geometry.crossing_boundary.nonRailTriangles == 1 &&
            split.Geometry.crossing_boundary.nonRailTriangleWorldCoordinatesMaterialSha256 == "12FB89B16F9F787E8286C338AB9EFFCF2E0A54C78F11E8D58B5C8AEFCFBDB79A",
            "fully_inside and crossing_boundary independently aggregate nonrail world-triangle keys");
    }
}
