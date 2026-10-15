using System.Collections.Immutable;
using System.Numerics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes.Tests;

[TestClass]
public sealed class GeometryTests
{
    [TestMethod]
    public void CubeTrianglesFaceOutwardWithMatchingNormalsAndValidIndices()
    {
        var geometry = SceneFixtures.Build(SceneFixtures.Projection([SceneFixtures.Cell(new(0, 0, 0))]));
        var meshes = Meshes(geometry);
        Assert.AreEqual(12, meshes.Sum(mesh => mesh.TriangleIndices.Length / 3));
        foreach (var mesh in meshes)
        for (var i = 0; i < mesh.TriangleIndices.Length; i += 3)
        {
            var a = mesh.Vertices[mesh.TriangleIndices[i]];
            var b = mesh.Vertices[mesh.TriangleIndices[i + 1]];
            var c = mesh.Vertices[mesh.TriangleIndices[i + 2]];
            Assert.IsTrue(Vector3.Dot(Vector3.Cross(b.Position - a.Position, c.Position - a.Position), a.Normal) > 0);
            Assert.AreEqual(a.Normal, b.Normal);
            Assert.AreEqual(a.Normal, c.Normal);
            Assert.AreEqual(1f, a.Normal.Length());
            Assert.IsTrue(Vector3.Dot(a.Normal, a.Position - new Vector3(0.5f)) > 0);
        }
    }

    [TestMethod]
    public void SharedFaceIsCulledAcrossPositiveAndNegativeChunkBoundaries()
    {
        foreach (var x in new[] { -33, -1, 31 })
        {
            var result = SceneFixtures.Build(SceneFixtures.Projection([
                SceneFixtures.Cell(new(x, 0, 0)), SceneFixtures.Cell(new(x + 1, 0, 0), 2)]));
            Assert.AreEqual(20, Meshes(result).Sum(mesh => mesh.TriangleIndices.Length / 3));
            var hit = (ScenePick.Hit)SceneFixtures.Frame(result).Pick(new(new(x - 2, 0.5f, 0.5f), Vector3.UnitX, 5));
            Assert.AreEqual(new GridPosition(x, 0, 0), hit.Value.Cell);
            Assert.AreEqual(2d, hit.Value.DistanceCells);
        }
    }

    [TestMethod]
    public void YAndZChunkBoundariesPreserveLookupOcclusionAndPicking()
    {
        foreach (var axis in new[] { Vector3.UnitY, Vector3.UnitZ })
        foreach (var coordinate in new[] { -33, -1, 31 })
        {
            var at = new GridPosition(0, (int)axis.Y * coordinate, (int)axis.Z * coordinate);
            var next = new GridPosition(0, at.Y + (int)axis.Y, at.Z + (int)axis.Z);
            var geometry = SceneFixtures.Build(SceneFixtures.Projection([
                SceneFixtures.Cell(at), SceneFixtures.Cell(next, 2)]));
            var grid = geometry.Picking.Single().Grid;
            Assert.AreEqual(at, grid.Find(at)?.At);
            Assert.AreEqual(next, grid.Find(next)?.At);
            Assert.IsNull(grid.Find(new(1, at.Y, at.Z)));
            Assert.AreEqual(20, Meshes(geometry).Sum(mesh => mesh.TriangleIndices.Length / 3));
            var frame = SceneFixtures.Frame(geometry);
            var position = new Vector3(at.X, at.Y, at.Z);
            var before = ((ScenePick.Hit)frame.Pick(new(position + new Vector3(0.5f) - axis * 2.5f, axis, 5))).Value;
            var after = ((ScenePick.Hit)frame.Pick(new(position + new Vector3(0.5f) + axis * 3.5f, -axis, 5))).Value;
            Assert.AreEqual(at, before.Cell);
            Assert.AreEqual(next, after.Cell);
            Assert.AreEqual(2d, before.DistanceCells);
            Assert.AreEqual(2d, after.DistanceCells);
            Assert.AreEqual(-axis, before.Normal);
            Assert.AreEqual(axis, after.Normal);
        }
    }

    [TestMethod]
    public void RevisitedChunksKeepCellsAndRejectOverlaps()
    {
        var first = new SceneCell(new(0, 0, 0), new(VoxelOrientation.PxPy, SceneFixtures.Cube, null,
            null, new PickSubject.Terrain(new WorldLocation.Node(new(1))), []));
        ImmutableArray<SceneCell> cells = [first, first with { At = new(32, 0, 0) }, first with { At = new(1, 0, 0) }];
        var grid = new VoxelGrid(cells);
        Assert.AreEqual(cells.Length, grid.Cells.Count());
        foreach (var cell in cells) Assert.AreEqual(cell, grid.Find(cell.At));
        Assert.ThrowsExactly<ArgumentException>(() => new VoxelGrid([.. cells, first]));
    }

    [TestMethod]
    public void SameAppearanceKeepsIndependentPaintSubjectsAndPathsWithinChunk()
    {
        var first = new SceneCell(new(0, 0, 0), new(VoxelOrientation.PxPy, SceneFixtures.Cube, null,
            new("#ff0000"), new PickSubject.WorldObject(SceneFixtures.Subject, SceneFixtures.Design, new(1)), [1]));
        SceneCell[] cells = [first,
            new(new(2, 0, 0), new(first.Orientation, first.Appearance, first.Shape, new("#0000ff"), first.Subject, first.Path)),
            new(new(4, 0, 0), new(first.Orientation, first.Appearance, first.Shape, first.Paint,
                new PickSubject.WorldObject(SceneFixtures.Subject, SceneFixtures.Design, new(2)), first.Path)),
            new(new(6, 0, 0), new(first.Orientation, first.Appearance, first.Shape, first.Paint, first.Subject, [2])),
            new(new(8, 0, 0), new(VoxelOrientation.NxPy, first.Appearance, first.Shape, first.Paint, first.Subject, first.Path))];
        var grid = new VoxelGrid([.. cells]);
        var meshes = DesignGeometry.Meshes(grid, false).OfType<SceneVisual.Geometry>().Select(item => item.Mesh).ToArray();
        Assert.HasCount(2, meshes);
        foreach (var mesh in meshes)
        {
            var paint = ((SceneMaterial.Voxel)mesh.Material).Paint;
            var expected = cells.Where(cell => cell.Paint == paint).ToArray();
            Assert.AreEqual(expected.Length * 12, mesh.TriangleIndices.Length / 3);
            Assert.IsTrue(mesh.Vertices.All(vertex => expected.Any(cell =>
                vertex.Position.X >= cell.At.X && vertex.Position.X <= cell.At.X + 1)));
        }
        var geometry = SceneFixtures.Build(SceneFixtures.Projection([SceneFixtures.Cell(new(0, 0, 0))]));
        var frame = SceneFixtures.Frame(geometry with { Picking = [new(geometry.Objects[0].Id, SceneFixtures.Identity, grid)] });
        foreach (var cell in cells)
        {
            Assert.AreEqual(cell.Orientation, grid.Find(cell.At)?.Orientation);
            Assert.AreEqual(cell.Paint, grid.Find(cell.At)?.Paint);
            var hit = ((ScenePick.Hit)frame.Pick(new(new(cell.At.X + 0.5f, -2, 0.5f), Vector3.UnitY, 4))).Value;
            Assert.AreEqual(cell.At, hit.Cell);
            Assert.AreEqual(cell.Subject, hit.Subject);
            CollectionAssert.AreEqual(cell.Path.ToArray(), hit.NestedObjectPath.ToArray());
        }
    }

    [TestMethod]
    public void MissingDefinitionsRemainDistinctWithOtherwiseEqualTerrainProperties()
    {
        var subject = new PickSubject.Terrain(new WorldLocation.Node(new(1)));
        SceneCell[] cells = Enumerable.Range(0, 2).Select(x => new SceneCell(new(x, 0, 0),
            new(VoxelOrientation.PxPy, null, null, null, subject, [],
                new ContentReference.Core(new CoreDefinitionReference.Voxel(new("missing:" + x)))))).ToArray();
        var grid = new VoxelGrid([.. cells]);
        Assert.IsEmpty(DesignGeometry.Meshes(grid, false));
        var basis = SceneFixtures.Build(SceneFixtures.Projection([SceneFixtures.Cell(new(0, 0, 0))]));
        var frame = SceneFixtures.Frame(basis with { Picking = [new(basis.Objects[0].Id, SceneFixtures.Identity, grid)] });
        foreach (var cell in cells)
        {
            Assert.AreEqual(cell.MissingReference, grid.Find(cell.At)?.MissingReference);
            var hit = ((ScenePick.Hit)frame.Pick(new(new(cell.At.X + 0.5f, 0.5f, -2), Vector3.UnitZ, 4))).Value;
            Assert.AreEqual(cell.At, hit.Cell);
            Assert.AreEqual(subject, hit.Subject);
            Assert.AreEqual(GridFace.NegativeZ, hit.Face);
        }
        var adjacentCube = new SceneCell(new(2, 0, 0), new(VoxelOrientation.PxPy, SceneFixtures.Cube,
            null, null, subject, []));
        var meshes = DesignGeometry.Meshes(new VoxelGrid([.. cells, adjacentCube]), false)
            .OfType<SceneVisual.Geometry>().Select(visual => visual.Mesh).ToArray();
        Assert.AreEqual(12, meshes.Sum(mesh => mesh.TriangleIndices.Length / 3));
        Assert.IsTrue(meshes.SelectMany(mesh => mesh.Vertices)
            .Any(vertex => vertex.Position.X == 2 && vertex.Normal == -Vector3.UnitX));
    }

    [TestMethod]
    public void MissingAppearanceRemainsPickableWithoutOccludingOrBecomingAnEmptySlot()
    {
        var missingVoxel = new VoxelDefinitionId("missing:cube");
        var otherMissingVoxel = new VoxelDefinitionId("missing:other");
        var content = new FixedContentView
        {
            VoxelAppearances = new Dictionary<VoxelDefinitionId, ContentLookup<VoxelAppearanceProjection>>
            {
                [SceneFixtures.Voxel] = new ContentLookup<VoxelAppearanceProjection>.Found(SceneFixtures.Cube),
                [missingVoxel] = new ContentLookup<VoxelAppearanceProjection>.NotFound(),
                [otherMissingVoxel] = new ContentLookup<VoxelAppearanceProjection>.NotFound()
            }
        };
        var geometry = SceneFixtures.Build(SceneFixtures.Projection([
            SceneFixtures.Cell(new(0, 0, 0)),
            SceneFixtures.Cell(new(1, 0, 0), 2) with { Voxel = missingVoxel },
            SceneFixtures.Cell(new(3, 0, 0), 3),
            SceneFixtures.Cell(new(4, 0, 0), 4) with { Voxel = otherMissingVoxel }]), content: content);
        var grid = geometry.Picking.Single().Grid;
        var reference = new ContentReference.Core(new CoreDefinitionReference.Voxel(missingVoxel));
        Assert.AreEqual(reference, grid.Find(new(1, 0, 0))?.MissingReference);
        var otherReference = new ContentReference.Core(new CoreDefinitionReference.Voxel(otherMissingVoxel));
        Assert.AreEqual(otherReference, grid.Find(new(4, 0, 0))?.MissingReference);
        var markers = geometry.Objects.SelectMany(item => item.Visuals).OfType<SceneVisual.Missing>().ToArray();
        Assert.IsTrue(markers.Any(marker => marker.Reference == reference && marker.KnownCell));
        Assert.IsTrue(markers.Any(marker => marker.Reference == otherReference && marker.KnownCell));
        Assert.IsNull(grid.Find(new(2, 0, 0)));
        Assert.AreEqual(24, Meshes(geometry).Sum(mesh => mesh.TriangleIndices.Length / 3));
        Assert.IsTrue(Meshes(geometry).SelectMany(mesh => mesh.Vertices)
            .Any(vertex => vertex.Position.X == 1 && vertex.Normal == Vector3.UnitX));
        var frame = SceneFixtures.Frame(geometry);
        var hit = ((ScenePick.Hit)frame.Pick(new(new(1.5f, 0.5f, -2), Vector3.UnitZ, 4))).Value;
        Assert.AreEqual(new GridPosition(1, 0, 0), hit.Cell);
        Assert.AreEqual(new PickSubject.WorldObject(SceneFixtures.Subject, SceneFixtures.Design, new(2)), hit.Subject);
        var otherHit = ((ScenePick.Hit)frame.Pick(new(new(4.5f, 0.5f, -2), Vector3.UnitZ, 4))).Value;
        Assert.AreEqual(new GridPosition(4, 0, 0), otherHit.Cell);
        Assert.AreEqual(new PickSubject.WorldObject(SceneFixtures.Subject, SceneFixtures.Design, new(4)), otherHit.Subject);
        Assert.IsInstanceOfType<ScenePick.Miss>(frame.Pick(new(new(2.5f, 0.5f, -2), Vector3.UnitZ, 4)));
    }

    [TestMethod]
    public void ShapesAndModelsOccupyWholePickCellWithoutOccludingCube()
    {
        var cube = SceneFixtures.Cube;
        var meshAppearance = cube with { Presentation = new VoxelPresentation.Mesh(new("model"), []) };
        foreach (var appearance in new[] { cube, meshAppearance })
        {
            var shaped = appearance == cube ? new VoxelShape("slope") : (VoxelShape?)null;
            var grid = new VoxelGrid([
                new(new(0, 0, 0), new(VoxelOrientation.PxPy, cube, null, null, new PickSubject.Terrain(new WorldLocation.Node(new(1))), [])),
                new(new(1, 0, 0), new(VoxelOrientation.PxPy, appearance, shaped, null, new PickSubject.Terrain(new WorldLocation.Node(new(1))), [])),
                new(new(-1, 0, 0), new(VoxelOrientation.PxPy, cube, null, null, new PickSubject.Terrain(new WorldLocation.Node(new(1))), []))]);
            var vertices = DesignGeometry.Meshes(grid, false).OfType<SceneVisual.Geometry>()
                .SelectMany(item => item.Mesh.Vertices).ToArray();
            Assert.IsTrue(vertices.Any(vertex => vertex.Normal == Vector3.UnitX && vertex.Position.X == 1));
            Assert.IsFalse(vertices.Any(vertex => vertex.Normal == -Vector3.UnitX && vertex.Position.X == 0));
            var hit = grid.Pick(new(new(3, 0.05f, 0.05f), -Vector3.UnitX, 3));
            Assert.IsNotNull(hit);
            Assert.AreEqual(new GridPosition(1, 0, 0), hit.Value.Cell.At);
            Assert.AreEqual(1d, hit.Value.Distance);
        }
    }

    [TestMethod]
    public void DdaHonorsDistanceAndSkipsCellsTouchedOnlyAtCorners()
    {
        var result = SceneFixtures.Build(SceneFixtures.Projection([
            SceneFixtures.Cell(new(1, 0, 0)), SceneFixtures.Cell(new(2, 2, 2), 2)]));
        var frame = SceneFixtures.Frame(result);
        var direction = Vector3.Normalize(Vector3.One);
        var ray = new SceneRay(new(-1, -1, -1), direction, 10);
        var hit = (ScenePick.Hit)frame.Pick(ray);
        Assert.AreEqual(new GridPosition(2, 2, 2), hit.Value.Cell);
        Assert.IsTrue(Math.Abs(hit.Value.DistanceCells - 3 * Math.Sqrt(3)) < 0.00001);
        Assert.IsInstanceOfType<ScenePick.Miss>(frame.Pick(ray with { MaximumDistanceCells = 5 }));
        Assert.IsInstanceOfType<ScenePick.Miss>(frame.Pick(new(new(-1, 0.5f, 0.5f), Vector3.UnitX, 1.9)));
    }

    [TestMethod]
    public void DdaSkipsEdgeOnlyContactAndFrameChoosesNearestObject()
    {
        var edgeGeometry = SceneFixtures.Build(SceneFixtures.Projection([
            SceneFixtures.Cell(new(1, 0, 0)), SceneFixtures.Cell(new(2, 2, 0), 2)]));
        var edgeHit = (ScenePick.Hit)SceneFixtures.Frame(edgeGeometry).Pick(
            new(new(-1, -1, 0.5f), Vector3.Normalize(new(1, 1, 0)), 10));
        Assert.AreEqual(new GridPosition(2, 2, 0), edgeHit.Value.Cell);
        var farther = SceneFixtures.Build(SceneFixtures.Projection([SceneFixtures.Cell(new(5, 0, 0))]));
        var closer = SceneFixtures.Build(SceneFixtures.Projection([SceneFixtures.Cell(new(2, 0, 0))]));
        var nearObject = closer.Objects[0] with { Id = new(100) };
        var combined = farther with
        {
            Objects = farther.Objects.Add(nearObject),
            Picking = farther.Picking.Add(closer.Picking[0] with { Object = nearObject.Id })
        };
        var hit = (ScenePick.Hit)SceneFixtures.Frame(combined).Pick(new(new(-1, 0.5f, 0.5f), Vector3.UnitX, 10));
        Assert.AreEqual(nearObject.Id, hit.Value.Object);
        Assert.AreEqual(3d, hit.Value.DistanceCells);
    }

    [TestMethod]
    public void RotatedNestedPickPreservesPartPathAndRootPose()
    {
        var nested = new NestedObjectProjection([0], new(4, 0, 0), VoxelOrientation.NxPy, new NestedObjectDetails.Assembly(null));
        var projection = SceneFixtures.Projection([SceneFixtures.Cell(new(0, 0, 0), 7, path: [0])], [nested]);
        var root = new ScenePose(new(10, 0, 0), Quaternion.Identity);
        var frame = SceneFixtures.Frame(SceneFixtures.Build(projection, root));
        var hit = ((ScenePick.Hit)frame.Pick(new(new(12, 0.5f, 0.5f), Vector3.UnitX, 5))).Value;
        Assert.AreEqual(new PartId(7), ((PickSubject.WorldObject)hit.Subject).Part);
        CollectionAssert.AreEqual(new[] { 0 }, hit.NestedObjectPath.ToArray());
        Assert.AreEqual(new Vector3(14, 0.5f, 0.5f), hit.Point);
        Assert.AreEqual(-Vector3.UnitX, hit.Normal);
        Assert.AreEqual(root, ((Lookup<ScenePose>.Found)frame.FindPose(SceneFixtures.Subject)).Value);
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(4)]
    public void AllOrientationsSelectLocalTexturesWithoutMirroringImageAxes(int span)
    {
        var content = SceneFixtures.Content(appearance: SceneFixtures.TexturedCube(span, front: true));
        foreach (var orientation in Enum.GetValues<VoxelOrientation>())
        {
            var (right, up, forward) = ExpectedAxes(orientation);
            var cell = SceneFixtures.Cell(new(3, -5, 7)) with { LocalOrientation = orientation };
            var meshes = Meshes(SceneFixtures.Build(SceneFixtures.Projection([cell]), content: content));
            foreach (var (normal, imageUp, texture) in new[] {
                (right, up, "side"), (-right, up, "side"),
                (up, forward, "top"), (-up, forward, "bottom"),
                (forward, up, "front"), (-forward, up, "side") })
            {
                var face = meshes.SelectMany(mesh => mesh.Vertices.Where(vertex => vertex.Normal == normal)
                    .Select(vertex => (Vertex: vertex, Material: (SceneMaterial.Voxel)mesh.Material))).ToArray();
                Assert.HasCount(4, face, $"{orientation}, {normal}");
                Assert.IsTrue(face.All(item => item.Material.Texture == new AssetRevision(texture)));
                var u = Vector3.Cross(imageUp, normal);
                var v = -imageUp;
                var width = texture == "front" ? 1f : 1f / span;
                for (var i = 0; i < face.Length; i++)
                {
                    var a = face[i].Vertex;
                    var b = face[(i + 1) % face.Length].Vertex;
                    var delta = b.Position - a.Position;
                    Assert.AreEqual(Vector3.Dot(delta, u) * width, b.TextureUv.X - a.TextureUv.X);
                    Assert.AreEqual(Vector3.Dot(delta, v) * width, b.TextureUv.Y - a.TextureUv.Y);
                    Assert.IsTrue(a.TextureUv.X is >= 0 and <= 1 && a.TextureUv.Y is >= 0 and <= 1);
                }
            }
        }
    }

    [TestMethod]
    [DataRow(2, 0f, 0f)]
    [DataRow(4, 0.5f, 0f)]
    [DataRow(8, 0.75f, 0.5f)]
    public void TileSpanSelectsObjectGridSubtile(int span, float minimumU, float minimumV)
    {
        var content = SceneFixtures.Content(appearance: SceneFixtures.TexturedCube(span));
        var geometry = SceneFixtures.Build(SceneFixtures.Projection([SceneFixtures.Cell(new(1, 2, 3))]), content: content);
        var top = Meshes(geometry).SelectMany(mesh => mesh.Vertices).Where(vertex => vertex.Normal == Vector3.UnitY).ToArray();
        foreach (var vertex in top)
        {
            var expected = new Vector2(minimumU + (2 - vertex.Position.X) / span,
                minimumV + (4 - vertex.Position.Z) / span);
            Assert.AreEqual(expected, vertex.TextureUv);
        }
    }

    [TestMethod]
    [DataRow(2)]
    [DataRow(4)]
    [DataRow(8)]
    public void RotatedTilesJoinAcrossNegativeCoordinatesAndChunkBoundaries(int span)
    {
        var content = SceneFixtures.Content(appearance: SceneFixtures.TexturedCube(span));
        foreach (var orientation in new[] { VoxelOrientation.PxPy, VoxelOrientation.PzPy, VoxelOrientation.PxPz })
        {
            var (right, up, forward) = ExpectedAxes(orientation);
            foreach (var (normal, imageUp) in new[] { (up, forward), (right, up) })
            foreach (var (axis, component) in new[] { (Vector3.Cross(imageUp, normal), 0), (-imageUp, 1) })
            foreach (var coordinate in new[] { -33, -32, -1, 0, 1, 30, 31, 32 })
            {
                var at = new GridPosition(coordinate, coordinate, coordinate);
                var next = new GridPosition(at.X + (int)axis.X, at.Y + (int)axis.Y, at.Z + (int)axis.Z);
                var geometry = SceneFixtures.Build(SceneFixtures.Projection([
                    SceneFixtures.Cell(at) with { LocalOrientation = orientation },
                    SceneFixtures.Cell(next, 2) with { LocalOrientation = orientation }]), content: content);
                var quads = Meshes(geometry).SelectMany(mesh => mesh.Vertices)
                    .Where(vertex => vertex.Normal == normal).Chunk(4)
                    .OrderBy(quad => quad.Average(vertex => Vector3.Dot(vertex.Position, axis))).ToArray();
                Assert.HasCount(2, quads);
                var shared = (from a in quads[0] from b in quads[1] where a.Position == b.Position select (a, b)).ToArray();
                Assert.HasCount(2, shared);
                foreach (var (a, b) in shared)
                {
                    var aUv = component == 0 ? a.TextureUv.X : a.TextureUv.Y;
                    var bUv = component == 0 ? b.TextureUv.X : b.TextureUv.Y;
                    if ((int)Vector3.Dot(a.Position, axis) % span == 0)
                    {
                        Assert.AreEqual(1f, aUv);
                        Assert.AreEqual(0f, bUv);
                    }
                    else Assert.AreEqual(aUv, bUv);
                    Assert.AreEqual(component == 0 ? a.TextureUv.Y : a.TextureUv.X,
                        component == 0 ? b.TextureUv.Y : b.TextureUv.X);
                }
            }
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void OnlyDistinctFrontTextureUsesWholeImage(bool front)
    {
        var content = SceneFixtures.Content(appearance: SceneFixtures.TexturedCube(4, front));
        var geometry = SceneFixtures.Build(SceneFixtures.Projection([SceneFixtures.Cell(new(1, 2, 3))]), content: content);
        var mesh = Meshes(geometry).Single(item => item.Vertices.Any(vertex => vertex.Normal == Vector3.UnitZ));
        Assert.AreEqual(new AssetRevision(front ? "front" : "side"), ((SceneMaterial.Voxel)mesh.Material).Texture);
        var vertices = mesh.Vertices.Where(vertex => vertex.Normal == Vector3.UnitZ).ToArray();
        Assert.AreEqual(front ? 0f : 0.25f, vertices.Min(vertex => vertex.TextureUv.X));
        Assert.AreEqual(front ? 1f : 0.5f, vertices.Max(vertex => vertex.TextureUv.X));
        Assert.AreEqual(front ? 0f : 0.25f, vertices.Min(vertex => vertex.TextureUv.Y));
        Assert.AreEqual(front ? 1f : 0.5f, vertices.Max(vertex => vertex.TextureUv.Y));
    }

    [TestMethod]
    public void LargeIntegerGridCoordinatesKeepTexturePhase()
    {
        var content = SceneFixtures.Content(appearance: SceneFixtures.TexturedCube(8));
        foreach (var orientation in Enum.GetValues<VoxelOrientation>())
        {
            Vector2[] Uvs(GridPosition at) => Meshes(SceneFixtures.Build(SceneFixtures.Projection([
                SceneFixtures.Cell(at) with { LocalOrientation = orientation }]), content: content))
                .SelectMany(mesh => mesh.Vertices.Select(vertex => vertex.TextureUv)).ToArray();
            CollectionAssert.AreEqual(Uvs(new(1, 6, 3)), Uvs(new(1_000_000_001, -1_000_000_002, 1_000_000_003)));
        }
    }

    [TestMethod]
    public void ExplicitCubeRendersAndOccludesLikeUnspecifiedShape()
    {
        var implicitCube = SceneFixtures.Cell(new(0, 0, 0));
        var explicitCube = implicitCube with { Shape = new VoxelShape("cube") };
        var implicitMesh = Meshes(SceneFixtures.Build(SceneFixtures.Projection([implicitCube]))).Single();
        var explicitMesh = Meshes(SceneFixtures.Build(SceneFixtures.Projection([explicitCube]))).Single();
        CollectionAssert.AreEqual(implicitMesh.Vertices.ToArray(), explicitMesh.Vertices.ToArray());
        CollectionAssert.AreEqual(implicitMesh.TriangleIndices.ToArray(), explicitMesh.TriangleIndices.ToArray());
        foreach (var x in new[] { -33, -1, 31 })
        {
            var geometry = SceneFixtures.Build(SceneFixtures.Projection([
                explicitCube with { At = new(x, 0, 0), LocalAt = new(x, 0, 0) },
                SceneFixtures.Cell(new(x + 1, 0, 0), 2)]));
            Assert.AreEqual(20, Meshes(geometry).Sum(mesh => mesh.TriangleIndices.Length / 3));
        }
    }

    [TestMethod]
    public void TextureRotationPreservesFaceCoordinatesAndExposedEdges()
    {
        var geometry = SceneFixtures.Build(SceneFixtures.Projection([
            SceneFixtures.Cell(new(0, 0, 0)) with { LocalOrientation = VoxelOrientation.PzNx },
            SceneFixtures.Cell(new(1, 0, 0), 2)]));
        var top = Meshes(geometry).SelectMany(mesh => mesh.Vertices).Where(vertex => vertex.Normal == Vector3.UnitY).Take(4).ToArray();
        foreach (var vertex in top)
        {
            Assert.AreEqual(new Vector2(vertex.Position.X, 1 - vertex.Position.Z), vertex.FaceUv);
            Assert.AreEqual(ExposedFaceEdges.MinimumU | ExposedFaceEdges.MinimumV | ExposedFaceEdges.MaximumV, vertex.ExposedEdges);
        }
    }

    [TestMethod]
    public void RootPoseChangesLeaveLocalMeshAndTextureCoordinatesUnchanged()
    {
        var projection = SceneFixtures.Projection([SceneFixtures.Cell(new(-3, 2, 5)) with { LocalOrientation = VoxelOrientation.PyNz }]);
        var content = SceneFixtures.Content(appearance: SceneFixtures.TexturedCube(4));
        var original = SceneFixtures.Build(projection, content: content);
        var moved = SceneFixtures.Build(projection, new(new(100, -20, 40), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.7f)), content);
        Assert.AreNotEqual(original.Objects[0].Pose, moved.Objects[0].Pose);
        CollectionAssert.AreEqual(Meshes(original).SelectMany(mesh => mesh.Vertices).ToArray(),
            Meshes(moved).SelectMany(mesh => mesh.Vertices).ToArray());
    }

    private static (Vector3 Right, Vector3 Up, Vector3 Forward) ExpectedAxes(VoxelOrientation orientation)
    {
        var axes = new Dictionary<string, Vector3> {
            ["Px"] = Vector3.UnitX, ["Nx"] = -Vector3.UnitX,
            ["Py"] = Vector3.UnitY, ["Ny"] = -Vector3.UnitY,
            ["Pz"] = Vector3.UnitZ, ["Nz"] = -Vector3.UnitZ };
        var name = orientation.ToString();
        var right = axes[name[..2]];
        var up = axes[name[2..]];
        return (right, up, Vector3.Cross(right, up));
    }

    private static SceneMesh[] Meshes(RenderedDesign rendered) => rendered.Objects.SelectMany(item => item.Visuals)
        .OfType<SceneVisual.Geometry>().Select(item => item.Mesh).ToArray();
}
