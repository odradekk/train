using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes.Tests;

[TestClass]
public sealed class RailwayDependencyTests
{
    [TestMethod]
    public void CollisionVariantsWithNoPropertiesDoNotReuseDependencies()
    {
        var original = Snapshot(voxel: SceneFixtures.Cube with { Collision = new VoxelCollision.None() });
        var changed = Snapshot(voxel: SceneFixtures.Cube with { Collision = new VoxelCollision.FullBox() });
        Assert.IsFalse(Capture(original).Matches(changed, new(changed, RailwayFixtures.Edge), 0));
    }

    [TestMethod]
    public void SameShapedCoreReferenceVariantsInDiagnosticsDoNotReuseDependencies()
    {
        var original = Snapshot(model: Model(new CoreDefinitionReference.Voxel(new("same-id"))));
        var changed = Snapshot(model: Model(new CoreDefinitionReference.Terrain(new("same-id"))));
        Assert.IsFalse(Capture(original, models: [SceneFixtures.Design]).Matches(changed,
            new(changed, RailwayFixtures.Edge), 0));
    }

    [TestMethod]
    public void NestedCoreReferenceValuesAreIncludedInDependencies()
    {
        var original = Snapshot(model: Model(new CoreDefinitionReference.Voxel(new("first"))));
        var changed = Snapshot(model: Model(new CoreDefinitionReference.Voxel(new("second"))));
        Assert.IsFalse(Capture(original, models: [SceneFixtures.Design]).Matches(changed,
            new(changed, RailwayFixtures.Edge), 0));
    }

    [TestMethod]
    public void MissingLookupDiagnosticsRetainNestedReferenceTypes()
    {
        var original = Snapshot(lookup: new ContentLookup<DesignEvaluation>.Missing(
            [Diagnostic(new CoreDefinitionReference.Voxel(new("same-id")))]));
        var changed = Snapshot(lookup: new ContentLookup<DesignEvaluation>.Missing(
            [Diagnostic(new CoreDefinitionReference.Terrain(new("same-id")))]));
        Assert.IsFalse(Capture(original, models: [SceneFixtures.Design]).Matches(changed,
            new(changed, RailwayFixtures.Edge), 0));
    }

    [TestMethod]
    public void EquivalentValuesInNewInstancesReuseDependencies()
    {
        var original = Snapshot(voxel: SceneFixtures.TexturedCube(2),
            model: Model(new CoreDefinitionReference.Voxel(new("same-id"))));
        var equivalent = Snapshot(voxel: SceneFixtures.TexturedCube(2),
            model: Model(new CoreDefinitionReference.Voxel(new("same-id"))));
        Assert.AreNotSame(original.Content, equivalent.Content);
        Assert.AreNotSame(((ContentLookup<DesignEvaluation>.Found)original.Content.FindDesign(SceneFixtures.Design)).Value,
            ((ContentLookup<DesignEvaluation>.Found)equivalent.Content.FindDesign(SceneFixtures.Design)).Value);
        Assert.IsTrue(Capture(original, models: [SceneFixtures.Design]).Matches(equivalent,
            new(equivalent, RailwayFixtures.Edge), 0));
    }

    private static RailwayDependencies Capture(SceneWorldSnapshot snapshot, ImmutableArray<DesignHash> models = default) =>
        RailwayDependencies.Capture(snapshot, new(snapshot, RailwayFixtures.Edge), 0,
            [], [], models.IsDefault ? [] : models, [SceneFixtures.Voxel], false, false);

    private static SceneWorldSnapshot Snapshot(VoxelAppearanceProjection? voxel = null,
        DesignEvaluation.Complete? model = null, ContentLookup<DesignEvaluation>? lookup = null) =>
        RailwayFixtures.Snapshot(4, 4, relief: 0, content: RailwayFixtures.Content(vehicle: model,
            voxels: voxel is null ? null : new Dictionary<VoxelDefinitionId, ContentLookup<VoxelAppearanceProjection>>
            { [SceneFixtures.Voxel] = new ContentLookup<VoxelAppearanceProjection>.Found(voxel) },
            designs: lookup is null ? null : new Dictionary<DesignHash, ContentLookup<DesignEvaluation>>
            { [SceneFixtures.Design] = lookup }));

    private static DesignEvaluation.Complete Model(CoreDefinitionReference reference) =>
        SceneFixtures.Evaluation(SceneFixtures.Projection([SceneFixtures.Cell(new(0, 0, 0))]), [Diagnostic(reference)]);

    private static ContentDiagnostic Diagnostic(CoreDefinitionReference reference) =>
        new("test_diagnostic", ContentDiagnosticSeverity.Warning, "test", new ContentReference.Core(reference),
            new(new("test-package"), "test.json"), "field", null, "test diagnostic");
}
