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
    public void CompletePreviewIsReadOnlyAndRetainedFrameSurvivesDisposal()
    {
        var evaluation = SceneFixtures.Evaluation(SceneFixtures.Projection([SceneFixtures.Cell(new(0, 0, 0))]));
        var preview = ((SceneResult<IDesignPreview>.Success)ScenesFactory.Create().PreviewDesign(SceneFixtures.Content(), evaluation)).Value;
        Assert.AreEqual(new ContentGeneration(1), preview.Content);
        var first = ((SceneResult<ISceneFrame>.Success)preview.Present()).Value;
        var second = ((SceneResult<ISceneFrame>.Success)preview.Present()).Value;
        Assert.IsTrue(second.Update.Frame.Revision > first.Update.Frame.Revision);
        var objects = first.Update.Upserts;
        preview.Dispose();
        Assert.ThrowsExactly<ObjectDisposedException>(() => preview.Present());
        Assert.AreEqual(objects, first.Update.Upserts);
        Assert.IsTrue(first.Update.Upserts.SelectMany(item => item.Visuals).OfType<SceneVisual.Geometry>().Any());
        Assert.IsInstanceOfType<ScenePick.Miss>(first.Pick(new(new(-1, 0.5f, 0.5f), Vector3.UnitX, 3)));
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
        var built = DesignGeometry.Build(SceneFixtures.Content(), partial, SceneFixtures.Subject, SceneFixtures.Identity, NextId());
        var pickable = SceneFixtures.Frame(built);
        Assert.AreEqual(new GridPosition(0, 0, 0), ((ScenePick.Hit)pickable.Pick(new(new(-1, 0.5f, 0.5f), Vector3.UnitX, 6))).Value.Cell);
        Assert.IsInstanceOfType<ScenePick.Miss>(pickable.Pick(new(new(2, 0.5f, 0.5f), Vector3.UnitX, 3)));
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
    private static Func<string, SceneObjectId> NextId()
    {
        ulong id = 0;
        return _ => new(++id);
    }
}
