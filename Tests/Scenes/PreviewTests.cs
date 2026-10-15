using System.Collections.Immutable;
using System.Numerics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes.Tests;

[TestClass]
public sealed class PreviewTests
{
    [TestMethod]
    [DataRow(8, false)]
    [DataRow(16, false)]
    [DataRow(8, true)]
    [DataRow(16, true)]
    public void LargeEquipmentGoodUsesRealReadOnlyPreview(int halfWidth, bool nested)
    {
        var composite = new CompositeDefinitionId("test:crane");
        var good = new GoodId.Composite(composite);
        var cells = Enumerable.Range(-halfWidth, halfWidth * 2 + 1)
            .Select(x => new GridPosition(x, 7, 0))
            .Concat(Enumerable.Range(0, 7).SelectMany(y => new[]
                { new GridPosition(-halfWidth, y, 0), new GridPosition(halfWidth, y, 0) }))
            .Select((at, index) => SceneFixtures.Cell(at, index + 1)).ToImmutableArray();
        var projection = SceneFixtures.Projection(nested
            ? cells.Select(cell => cell with
            {
                ObjectPath = [0], At = new(10, cell.At.Y + 3, cell.At.X + 20),
                Orientation = VoxelOrientation.PzPy, Number = new(1),
                Element = new DesignElementPath.FunctionalComposite([], 0)
            }).ToImmutableArray()
            : cells, nested
                ? [new NestedObjectProjection([0], new(10, 3, 20), VoxelOrientation.PzPy,
                    new NestedObjectDetails.FunctionalComposite(composite, new(1), null))]
                : []);
        if (nested) projection = projection with
        {
            Elements = [new(new(1), new DesignElementPath.FunctionalComposite([], 0),
                new ComputedValue<ImmutableArray<GridPosition>>.Known(projection.Voxels.Select(cell => cell.At).ToImmutableArray()), true, false)]
        };
        var evaluation = SceneFixtures.Evaluation(projection) with
        {
            Definition = SceneFixtures.Draft with
            {
                Specification = nested ? new DesignSpecification.Assembly(null)
                    : new DesignSpecification.FunctionalComposite([], null)
            }
        };
        var content = new Train.Content.ContractSupport.FixedContentView
        {
            Generation = new(1),
            Goods = new Dictionary<GoodId, ContentLookup<GoodPresentation>>
            {
                [good] = new ContentLookup<GoodPresentation>.Found(new(good,
                    new(new("Crane", ""), null, null, false), evaluation.Design, false))
            },
            Designs = new Dictionary<DesignHash, ContentLookup<DesignEvaluation>>
                { [evaluation.Design] = new ContentLookup<DesignEvaluation>.Found(evaluation) },
            VoxelAppearances = new Dictionary<VoxelDefinitionId, ContentLookup<VoxelAppearanceProjection>>
                { [SceneFixtures.Voxel] = new ContentLookup<VoxelAppearanceProjection>.Found(SceneFixtures.Cube) }
        };
        var presentation = ((ContentLookup<GoodPresentation>.Found)content.FindGood(good)).Value;
        Assert.IsFalse(presentation.Missing);
        var design = ((ContentLookup<DesignEvaluation>.Found)content.FindDesign(presentation.DisplayDesign!.Value)).Value;
        var preview = ((SceneResult<IDesignPreview>.Success)ScenesFactory.Create().PreviewDesign(content, design)).Value;
        var frame = ((SceneResult<ISceneFrame>.Success)preview.Present()).Value;
        preview.Dispose();

        Assert.AreEqual(SceneReadiness.Ready, frame.Readiness);
        Assert.IsEmpty(frame.Diagnostics);
        Assert.IsNull(frame.Update.Base);
        Assert.AreEqual(0d, frame.SimulationSeconds);
        Assert.IsFalse(frame.Update.Upserts.SelectMany(item => item.Visuals).OfType<SceneVisual.Missing>().Any());
        var body = frame.Update.Upserts.Single(item => item.Visuals.OfType<SceneVisual.Geometry>().Any());
        var vertices = body.Visuals.OfType<SceneVisual.Geometry>().SelectMany(item => item.Mesh.Vertices)
            .Select(vertex => vertex.Position).ToArray();
        Assert.AreEqual((float)-halfWidth, vertices.Min(point => point.X));
        Assert.AreEqual((float)halfWidth + 1, vertices.Max(point => point.X));
        Assert.AreEqual(0f, vertices.Min(point => point.Y));
        Assert.AreEqual(8f, vertices.Max(point => point.Y));
        foreach (var x in new[] { -halfWidth, halfWidth })
        {
            Assert.IsTrue(vertices.Contains(new Vector3(x, 0, 0)), "Both pillars must reach ground level.");
            Assert.IsTrue(vertices.Contains(new Vector3(x, 8, 0)), "The top beam must span both pillars.");
        }
        if (nested)
        {
            Assert.IsNotNull(body.Parent);
            var world = vertices.Select(point => Vector3.Transform(point, body.Pose.Rotation) + body.Pose.Position).ToArray();
            Assert.AreEqual(10f, world.Min(point => point.X), 0.0001f);
            Assert.AreEqual(11f, world.Max(point => point.X), 0.0001f);
            Assert.AreEqual(3f, world.Min(point => point.Y), 0.0001f);
            Assert.AreEqual(11f, world.Max(point => point.Y), 0.0001f);
            Assert.AreEqual(20f - halfWidth, world.Min(point => point.Z), 0.0001f);
            Assert.AreEqual(21f + halfWidth, world.Max(point => point.Z), 0.0001f);
        }
        else Assert.IsNull(body.Parent);
        Assert.IsInstanceOfType<ScenePick.Miss>(frame.Pick(new(new(-100, 0.5f, 0.5f), Vector3.UnitX, 200)));
    }

    [TestMethod]
    public void CompletePreviewIsReadOnlyAndRetainedFrameSurvivesDisposal()
    {
        var evaluation = SceneFixtures.Evaluation(SceneFixtures.Projection([SceneFixtures.Cell(new(0, 0, 0))]));
        var preview = ((SceneResult<IDesignPreview>.Success)ScenesFactory.Create().PreviewDesign(SceneFixtures.Content(), evaluation)).Value;
        Assert.AreEqual(new ContentGeneration(1), preview.Content);
        var first = ((SceneResult<ISceneFrame>.Success)preview.Present()).Value;
        var second = ((SceneResult<ISceneFrame>.Success)preview.Present()).Value;
        Assert.IsTrue(second.Update.Frame.Revision > first.Update.Frame.Revision);
        Assert.IsNull(first.Update.Base);
        Assert.IsNull(second.Update.Base);
        Assert.AreEqual(0d, first.SimulationSeconds);
        Assert.AreEqual(0d, second.SimulationSeconds);
        preview.Dispose();
        preview.Dispose();
        Assert.ThrowsExactly<ObjectDisposedException>(() => preview.Present());
        Assert.IsTrue(first.Update.Upserts.SelectMany(item => item.Visuals).OfType<SceneVisual.Geometry>().Any());
        Assert.IsInstanceOfType<ScenePick.Miss>(first.Pick(new(new(-1, 0.5f, 0.5f), Vector3.UnitX, 3)));
    }

    [TestMethod]
    public void NestedPreviewUsesObjectLocalCoordinatesAndOrientationForTextures()
    {
        var content = SceneFixtures.Content(appearance: SceneFixtures.TexturedCube(4));
        var cell = SceneFixtures.Cell(new(1, 2, 3)) with { LocalOrientation = VoxelOrientation.PxPz, Shape = new VoxelShape("cube") };
        var nestedCell = cell with { ObjectPath = [0], At = new(101, -42, 73), Orientation = VoxelOrientation.NzNy };
        var nested = new NestedObjectProjection([0], new(100, -40, 70), VoxelOrientation.NxPy, new NestedObjectDetails.Assembly(null));
        using var direct = ((SceneResult<IDesignPreview>.Success)ScenesFactory.Create().PreviewDesign(content,
            SceneFixtures.Evaluation(SceneFixtures.Projection([cell])))).Value;
        using var placed = ((SceneResult<IDesignPreview>.Success)ScenesFactory.Create().PreviewDesign(content,
            SceneFixtures.Evaluation(SceneFixtures.Projection([nestedCell], [nested])))).Value;
        var directFrame = ((SceneResult<ISceneFrame>.Success)direct.Present()).Value;
        var nestedFrame = ((SceneResult<ISceneFrame>.Success)placed.Present()).Value;
        var child = nestedFrame.Update.Upserts.Single(item => item.Parent is not null);
        Assert.AreNotEqual(SceneFixtures.Identity, child.Pose);
        var directMeshes = directFrame.Update.Upserts.SelectMany(item => item.Visuals).OfType<SceneVisual.Geometry>().ToArray();
        var nestedMeshes = child.Visuals.OfType<SceneVisual.Geometry>().ToArray();
        Assert.IsNotEmpty(directMeshes);
        CollectionAssert.AreEqual(directMeshes.SelectMany(item => item.Mesh.Vertices).ToArray(),
            nestedMeshes.SelectMany(item => item.Mesh.Vertices).ToArray());
        CollectionAssert.AreEqual(directMeshes.Select(item => item.Mesh.Material).ToArray(),
            nestedMeshes.Select(item => item.Mesh.Material).ToArray());
    }

    [TestMethod]
    public void PartialPreviewPreservesKnownCellAndUnknownCompositeSources()
    {
        var voxelReference = new ContentReference.Core(new CoreDefinitionReference.Voxel(SceneFixtures.Voxel));
        var compositeId = new CompositeDefinitionId("missing:composite");
        var compositeReference = new ContentReference.Core(new CoreDefinitionReference.Composite(compositeId));
        var diagnostics = ImmutableArray.Create(
            Diagnostic(voxelReference, "voxel-package"), Diagnostic(compositeReference, "composite-package"));
        var projection = SceneFixtures.Projection([SceneFixtures.Cell(new(0, 0, 0), missing: true)],
            [new([0], new(3, 0, 0), VoxelOrientation.PxPy, new NestedObjectDetails.MissingFunctionalComposite(compositeId, new(2)))]);
        var partial = new DesignEvaluation.Partial(SceneFixtures.Draft,
            new(new ComputedValue<double>.Unknown(diagnostics), new ComputedValue<Money>.Unknown(diagnostics),
                new ComputedValue<long>.Unknown(diagnostics), new ComputedValue<ImmutableArray<PartProjection>>.Unknown(diagnostics),
                new ComputedValue<ImmutableArray<GoodsQuantity>>.Unknown(diagnostics), new ComputedValue<int>.Unknown(diagnostics)),
            new(0, new(0), 0, [], []), null, projection, diagnostics);
        using var preview = ((SceneResult<IDesignPreview>.Success)ScenesFactory.Create().PreviewDesign(SceneFixtures.Content(), partial)).Value;
        var frame = ((SceneResult<ISceneFrame>.Success)preview.Present()).Value;
        var markers = frame.Update.Upserts.SelectMany(item => item.Visuals).OfType<SceneVisual.Missing>().ToArray();
        Assert.IsTrue(markers.Any(item => item.Reference == voxelReference && item.KnownCell));
        Assert.IsTrue(markers.Any(item => item.Reference == compositeReference && !item.KnownCell));
        Assert.IsTrue(frame.Diagnostics.Any(item => item.Content == voxelReference && item.Package == new PackageId("voxel-package")));
        Assert.IsTrue(frame.Diagnostics.Any(item => item.Content == compositeReference && item.Package == new PackageId("composite-package")));
        var built = DesignGeometry.Build(SceneFixtures.Content(), partial, SceneFixtures.Subject, SceneFixtures.Identity, SceneFixtures.NextId());
        var pickable = SceneFixtures.Frame(built);
        Assert.AreEqual(new GridPosition(0, 0, 0), ((ScenePick.Hit)pickable.Pick(new(new(-1, 0.5f, 0.5f), Vector3.UnitX, 6))).Value.Cell);
        Assert.IsInstanceOfType<ScenePick.Miss>(pickable.Pick(new(new(2, 0.5f, 0.5f), Vector3.UnitX, 3)));
    }

    [TestMethod]
    public void MissingAppearanceLookupKeepsSourceInMarkerDiagnostics()
    {
        var reference = new ContentReference.Core(new CoreDefinitionReference.Voxel(SceneFixtures.Voxel));
        var diagnostic = Diagnostic(reference, "lookup-package") with { Code = "appearance_missing" };
        var content = new Train.Content.ContractSupport.FixedContentView
        {
            Generation = new(1), VoxelAppearances = new Dictionary<VoxelDefinitionId, ContentLookup<VoxelAppearanceProjection>>
            { [SceneFixtures.Voxel] = new ContentLookup<VoxelAppearanceProjection>.Missing([diagnostic]) }
        };
        var evaluation = SceneFixtures.Evaluation(SceneFixtures.Projection([SceneFixtures.Cell(new(0, 0, 0))]));
        using var preview = ((SceneResult<IDesignPreview>.Success)ScenesFactory.Create().PreviewDesign(content, evaluation)).Value;
        var frame = ((SceneResult<ISceneFrame>.Success)preview.Present()).Value;
        Assert.IsTrue(frame.Diagnostics.Any(problem => problem.Code == "content_missing" &&
            problem.Content == reference && problem.Package == diagnostic.Source!.Package));
    }

    [TestMethod]
    public void FailedEvaluationReturnsInputFailureWithSource()
    {
        var diagnostic = Diagnostic(new ContentReference.Design(SceneFixtures.Design), "broken-package");
        var result = ScenesFactory.Create().PreviewDesign(SceneFixtures.Content(), new DesignEvaluation.Failed([diagnostic]));
        var failure = (SceneResult<IDesignPreview>.Failure)result;
        Assert.AreEqual(1, failure.Problems.Length);
        Assert.AreEqual(SceneStage.Input, failure.Problems[0].Stage);
        Assert.AreEqual(diagnostic.Code, failure.Problems[0].Code);
        Assert.AreEqual(diagnostic.Subject, failure.Problems[0].Content);
        Assert.AreEqual(diagnostic.Source!.Package, failure.Problems[0].Package);
    }

    private static ContentDiagnostic Diagnostic(ContentReference reference, string package) => new("missing", ContentDiagnosticSeverity.Error,
        "evaluate", reference, new(new(package), "design.json"), "voxel", new DesignElementPath.Cell([], 0), "Missing content");
}
