using System.Collections.Immutable;
using System.Numerics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes.Tests;

[TestClass]
public sealed class RailwayTerrainSharingTests
{
    [TestMethod]
    public void EqualVoxelColumnsKeepTheirLayersAndExactEdgeDistanceAcrossTheSegmentSeam()
    {
        var snapshot = RailwayFixtures.Snapshot(400, 400, relief: 0, tracks: 0);
        var path = new RailwayPath(snapshot, RailwayFixtures.Edge);
        foreach (var index in new[] { 1, 2 })
        {
            var segment = RailwayTerrain.Build(snapshot, path, index, CancellationToken.None);
            var grid = segment.Geometry.Picking.Single().Grid;
            var frame = SceneFixtures.Frame(segment.Geometry);
            Assert.IsTrue(segment.Geometry.Objects.SelectMany(item => item.Visuals).OfType<SceneVisual.Geometry>()
                .All(visual => visual.Mesh.Material is SceneMaterial.Voxel));
            Assert.IsTrue(segment.Tunnels.IsEmpty);
            Assert.AreEqual(98 * 128 * 6, grid.Cells.Count());
            var area = segment.Geometry.Objects.SelectMany(item => item.Visuals).OfType<SceneVisual.Geometry>()
                .Sum(visual => visual.Mesh.TriangleIndices.Chunk(3).Sum(triangle =>
                {
                    var a = visual.Mesh.Vertices[triangle[0]].Position;
                    var b = visual.Mesh.Vertices[triangle[1]].Position;
                    var c = visual.Mesh.Vertices[triangle[2]].Position;
                    return Vector3.Cross(b - a, c - a).Length() / 2;
                }));
            Assert.AreEqual(2f * (98 * 128 + 98 * 6 + 128 * 6), area);
            foreach (var worldZ in index == 1 ? new[] { 128, 130, 255 } : new[] { 256, 258 })
            {
                var localZ = worldZ - (int)segment.Origin.Z;
                var column = grid.Cells.Where(cell => cell.At.X == -1 && cell.At.Z == localZ).ToArray();
                CollectionAssert.AreEquivalent(Enumerable.Range(-9, 6).Select(y => new GridPosition(-1, y, localZ)).ToArray(),
                    column.Select(cell => cell.At).ToArray());
                Assert.AreEqual(-3d, segment.Heights[new(-1, localZ)]);
                foreach (var cell in column)
                {
                    Assert.AreEqual(SceneFixtures.Cube, cell.Appearance);
                    Assert.IsNull(cell.MissingReference);
                    AssertHit(frame, cell.At, Vector3.Zero, (worldZ + .5) / 2);
                }
            }
        }
    }

    [TestMethod]
    public void CachedTerrainKeepsItsPickDistancesWhenAttachedAndPlacedAgainWithoutChangingAnOldFrame()
    {
        var snapshot = RailwayFixtures.Snapshot(400, 400, relief: 0, tracks: 0);
        var path = new RailwayPath(snapshot, RailwayFixtures.Edge);
        var segment = RailwayTerrain.Build(snapshot, path, 2, CancellationToken.None);
        var cache = new RailwayCache();
        var owner = cache.OpenSession(RailwayFixtures.Edge);
        cache.Publish(RailwayFixtures.Edge, owner, [segment]);
        var local = new GridPosition(-1, -4, 0);
        var oldShift = new Vector3(43, 11, -57);
        var oldFrame = SceneFixtures.Frame(AttachAndPlace(segment.Geometry, oldShift, 100));
        AssertHit(oldFrame, local, oldShift, 128.25);

        cache.OpenSession(RailwayFixtures.Edge);
        var reopened = cache.Find(snapshot, new(snapshot, RailwayFixtures.Edge), 2);
        Assert.IsNotNull(reopened);
        var newShift = new Vector3(-91, -7, 23);
        var newFrame = SceneFixtures.Frame(AttachAndPlace(reopened.Geometry, newShift, 200));
        AssertHit(newFrame, local, newShift, 128.25);
        AssertHit(oldFrame, local, oldShift, 128.25);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void DifferentLayerVoxelsKeepTheirAppearanceMissingMarkersAndReadDependencies(bool missingRock)
    {
        var surface = new VoxelDefinitionId("test:surface");
        var subsurface = new VoxelDefinitionId("test:subsurface");
        var rock = new VoxelDefinitionId("test:rock");
        var terrain = RailwayFixtures.TerrainAppearance with
        { SurfaceVoxel = surface, SubsurfaceVoxel = subsurface, RockVoxel = rock };
        var appearances = new Dictionary<VoxelDefinitionId, ContentLookup<VoxelAppearanceProjection>>
        {
            [surface] = new ContentLookup<VoxelAppearanceProjection>.Found(Appearance(surface, "surface")),
            [subsurface] = new ContentLookup<VoxelAppearanceProjection>.Found(Appearance(subsurface, "subsurface")),
            [rock] = missingRock ? new ContentLookup<VoxelAppearanceProjection>.Missing([]) :
                new ContentLookup<VoxelAppearanceProjection>.Found(Appearance(rock, "rock"))
        };
        var snapshot = RailwayFixtures.Snapshot(400, 400, relief: 0, tracks: 0,
            content: RailwayFixtures.Content(terrain: terrain, voxels: appearances));
        var path = new RailwayPath(snapshot, RailwayFixtures.Edge);
        var segment = RailwayTerrain.Build(snapshot, path, 2, CancellationToken.None);
        var grid = segment.Geometry.Picking.Single().Grid;
        var missingReference = new ContentReference.Core(new CoreDefinitionReference.Voxel(rock));
        foreach (var y in Enumerable.Range(-9, 6))
        {
            var cell = grid.Find(new(-1, y, 0));
            Assert.IsNotNull(cell);
            var id = y == -4 ? surface : y >= -6 ? subsurface : rock;
            var expected = appearances[id] is ContentLookup<VoxelAppearanceProjection>.Found found ? found.Value : null;
            Assert.AreEqual(expected, cell.Value.Appearance);
            Assert.AreEqual(missingRock && id == rock ? missingReference : null, cell.Value.MissingReference);
        }
        var textures = segment.Geometry.Objects.SelectMany(item => item.Visuals).OfType<SceneVisual.Geometry>()
            .Select(item => ((SceneMaterial.Voxel)item.Mesh.Material).Texture.Value).Distinct().ToArray();
        CollectionAssert.AreEquivalent(missingRock ? new[] { "surface", "subsurface" } : new[] { "surface", "subsurface", "rock" }, textures);
        Assert.AreEqual(missingRock ? 1 : 0, segment.Geometry.Diagnostics.Count(problem =>
            problem.Code == "content_missing" && problem.Content == missingReference));

        appearances[rock] = new ContentLookup<VoxelAppearanceProjection>.Found(Appearance(rock, "changed-rock"));
        var changed = snapshot with { Content = RailwayFixtures.Content(terrain: terrain, voxels: appearances) };
        Assert.IsFalse(segment.Dependencies.Matches(changed, new(changed, RailwayFixtures.Edge), 2),
            "Both missing and found emitted voxels are read dependencies.");
        appearances[rock] = missingRock ? new ContentLookup<VoxelAppearanceProjection>.Missing([]) :
            new ContentLookup<VoxelAppearanceProjection>.Found(Appearance(rock, "rock"));
        appearances[SceneFixtures.Voxel] = new ContentLookup<VoxelAppearanceProjection>.Missing([]);
        var unusedBallast = snapshot with { Content = RailwayFixtures.Content(terrain: terrain, voxels: appearances) };
        Assert.IsTrue(segment.Dependencies.Matches(unusedBallast, new(unusedBallast, RailwayFixtures.Edge), 2),
            "A zero-track segment does not read the rule's ballast voxel.");
    }

    private static VoxelAppearanceProjection Appearance(VoxelDefinitionId id, string texture) => SceneFixtures.Cube with
    {
        Definition = id,
        Presentation = new VoxelPresentation.Cube([new("cube")], MaterialCategory.PaintedMetal, 1,
            new(texture), new(texture), new(texture), new(texture))
    };

    private static RenderedDesign AttachAndPlace(RenderedDesign geometry, Vector3 shift, ulong firstId)
    {
        var keys = geometry.Objects.ToImmutableDictionary(item => item.Id, item => item.Id.Value.ToString());
        var attached = WorldSceneSession.Attach(new(geometry, keys), "terrain:", _ => new(firstId++));
        return SceneGeometry.Place(attached.Geometry, new(shift, Quaternion.Identity));
    }

    private static void AssertHit(SceneFrame frame, GridPosition cell, Vector3 shift, double distanceMeters)
    {
        var origin = new Vector3(cell.X + .5f, cell.Y + .5f, cell.Z + .5f) + shift;
        var pick = frame.Pick(new(origin, Vector3.UnitX, .25));
        Assert.IsInstanceOfType<ScenePick.Hit>(pick);
        var hit = ((ScenePick.Hit)pick).Value;
        Assert.AreEqual(cell, hit.Cell);
        Assert.AreEqual(new PickSubject.Terrain(new WorldLocation.Edge(RailwayFixtures.Edge, distanceMeters)), hit.Subject);
    }
}
