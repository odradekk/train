using System.Collections.Immutable;
using System.Numerics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes.Tests;

[TestClass]
public sealed class TemplatePreviewTests
{
    private static readonly BlueprintId Model = new("test:scene-model");
    private static readonly BlueprintId Building = new("test:building");

    [TestMethod]
    public void FrameContainsModelPlacedBuildingAndAlwaysVisiblePlotOutline()
    {
        var model = SceneFixtures.Evaluation(SceneFixtures.Projection([SceneFixtures.Cell(new(-2, 0, 4))]));
        var building = SceneFixtures.Evaluation(SceneFixtures.Projection([SceneFixtures.Cell(new(2, 1, 3))])) with
        { Definition = SceneFixtures.Draft with { Specification = new DesignSpecification.Building(PlotSize.Small, BuildingKind.Station, []) } };
        var content = Content(Found(Model, model), Found(Building, building));
        var draft = Draft([new(9, PlotSize.SmallStation, new(100, 3, 200), VoxelOrientation.PzPy, Building)]) with
        { SceneModel = Model, FoundingBuildings = [new(9, new("test:founding-only"))] };
        var preview = Preview(content, draft, new(42));
        var frame = preview.Frame;
        Assert.AreEqual(new ContentGeneration(7), preview.Content);
        Assert.AreEqual(new EditRevision(42), preview.Revision);
        Assert.AreEqual(SceneReadiness.Ready, frame.Readiness);
        Assert.AreEqual(0d, frame.SimulationSeconds);
        Assert.AreEqual(new ScenePoint(0, 0, 0), frame.Origin);
        Assert.IsNull(frame.NodePanBounds);
        Assert.IsNull(frame.Update.Base);
        Assert.IsEmpty(preview.Diagnostics);
        Assert.IsEmpty(frame.Diagnostics);
        Assert.AreEqual(frame.Update.Upserts.Length, frame.Update.Upserts.Select(item => item.Id).Distinct().Count());
        Assert.IsTrue(frame.Update.Upserts.All(item => item.Subject is null));

        var plot = preview.Plots.Single();
        Assert.AreEqual(0, plot.DraftIndex);
        Assert.AreEqual(new GridBounds(new(-96, 0, -56), new(95, 47, 55)), plot.Bounds);
        Near(new(101, 3, 200), plot.Pose.Position);
        Near(Vector3.UnitZ, Vector3.Transform(Vector3.UnitX, plot.Pose.Rotation));
        Near(-Vector3.UnitX, Vector3.Transform(Vector3.UnitZ, plot.Pose.Rotation));
        var modelObject = frame.Update.Upserts.Single(item => item.Pose.Position == Vector3.Zero);
        var buildingObject = frame.Update.Upserts.Single(item => item.Pose == plot.Pose);
        var modelVertices = Vertices(modelObject);
        Near(new(-2, 0, 4), modelVertices.Aggregate(Vector3.Min));
        Near(new(-1, 1, 5), modelVertices.Aggregate(Vector3.Max));
        var buildingVertices = Vertices(buildingObject)
            .Select(point => Vector3.Transform(point, buildingObject.Pose.Rotation) + buildingObject.Pose.Position).ToArray();
        Near(new(97, 4, 202), buildingVertices.Aggregate(Vector3.Min));
        Near(new(98, 5, 203), buildingVertices.Aggregate(Vector3.Max));
        var corners = new Vector3[] { new(157, 3, 104), new(157, 3, 296), new(45, 3, 296), new(45, 3, 104) };
        Assert.AreEqual(4, frame.DebugLines.Length);
        for (var i = 0; i < corners.Length; i++)
        {
            Near(corners[i], frame.DebugLines[i].From);
            Near(corners[(i + 1) % corners.Length], frame.DebugLines[i].To);
            Assert.IsTrue(frame.DebugLines[i].Color.W > 0);
        }
        Assert.AreEqual(0, Pick(preview, new(new(156, 10, 105), -Vector3.UnitY, 7)));
        Assert.AreEqual(0, Pick(preview, new(new(157, 10, 104), -Vector3.UnitY, 7)));
        Assert.IsInstanceOfType<Lookup<int>.Missing>(preview.PickPlot(new(new(160, 10, 105), -Vector3.UnitY, 20)));
        Assert.IsInstanceOfType<ScenePick.Miss>(frame.Pick(new(new(-1.5f, 10, 4.5f), -Vector3.UnitY, 20)));
        Assert.IsInstanceOfType<ScenePick.Miss>(frame.Pick(new(new(97.5f, 10, 202.5f), -Vector3.UnitY, 20)));
    }

    [TestMethod]
    [DataRow(PlotSize.Small, 48, 48, 48)]
    [DataRow(PlotSize.Medium, 96, 96, 80)]
    [DataRow(PlotSize.Large, 192, 192, 128)]
    [DataRow(PlotSize.Giant, 384, 384, 256)]
    [DataRow(PlotSize.SmallStation, 192, 112, 48)]
    [DataRow(PlotSize.LargeStation, 256, 208, 64)]
    public void PlotBoundsUseInclusiveBuildingEnvelope(PlotSize size, int width, int depth, int height)
    {
        var preview = Preview(Content(), Draft([new(1, size, new(0, 0, 0), VoxelOrientation.PxPy, null)]));
        Assert.AreEqual(new GridBounds(new(-width / 2, 0, -depth / 2), new(width / 2 - 1, height - 1, depth / 2 - 1)),
            preview.Plots.Single().Bounds);
    }

    [TestMethod]
    public void DuplicateNumbersKeepEveryArrayIndexAndDiagnoseEveryDuplicate()
    {
        var preview = Preview(Content(), Draft([
            new(8, PlotSize.Small, new(100, 0, 0), VoxelOrientation.PxPy, null),
            new(8, PlotSize.Small, new(0, 0, 0), VoxelOrientation.PxPy, null),
            new(2, PlotSize.Small, new(200, 0, 0), VoxelOrientation.PxPy, null)]));
        CollectionAssert.AreEqual(new[] { 0, 1, 2 }, preview.Plots.Select(plot => plot.DraftIndex).ToArray());
        Assert.AreEqual(0, Pick(preview, Down(100)));
        Assert.AreEqual(1, Pick(preview, Down(0)));
        Assert.AreEqual(2, Pick(preview, Down(200)));
        var problems = preview.Diagnostics.Where(problem => problem.Code == "duplicate_plot_number").ToArray();
        Assert.AreEqual(2, problems.Length);
        Assert.IsTrue(problems.Any(problem => problem.Detail.Contains("plots[0].number", StringComparison.Ordinal)));
        Assert.IsTrue(problems.Any(problem => problem.Detail.Contains("plots[1].number", StringComparison.Ordinal)));
        Assert.AreEqual(12, preview.Frame.DebugLines.Length);
    }

    [TestMethod]
    public void PickUsesNearestBottomPlaneThenArrayIndexWithoutVoxelOcclusion()
    {
        var preview = Preview(Content(Found(Model, SceneFixtures.Evaluation(SceneFixtures.Projection([SceneFixtures.Cell(new(0, 8, 0))])))),
            Draft([new(100, PlotSize.Small, new(0, 0, 0), VoxelOrientation.PxPy, null),
                new(20, PlotSize.Small, new(0, 5, 0), VoxelOrientation.PxPy, null),
                new(1, PlotSize.Small, new(0, 5, 0), VoxelOrientation.PxPy, null)]) with { SceneModel = Model });
        Assert.AreEqual(1, Pick(preview, Down(0)));
        Assert.AreEqual(1, Pick(preview, new(new(0, 10, 0), -Vector3.UnitY, 5)));
        Assert.AreEqual(1, Pick(preview, new(new(0, 1, 0), Vector3.UnitY, 4)));
        Assert.IsInstanceOfType<Lookup<int>.Missing>(preview.PickPlot(new(new(0, 10, 0), -Vector3.UnitY, 4.9)));
        Assert.IsInstanceOfType<Lookup<int>.Missing>(preview.PickPlot(new(new(0, 10, 0), Vector3.UnitX, 100)));
        Assert.IsInstanceOfType<Lookup<int>.Missing>(preview.PickPlot(new(new(0, -1, 0), -Vector3.UnitY, 100)));
    }

    [TestMethod]
    [DataRow(-24f, -24f, true)]
    [DataRow(24f, 24f, true)]
    [DataRow(-24.01f, 0f, false)]
    [DataRow(0f, 24.01f, false)]
    public void PickUsesClosedFootprintEdges(float x, float z, bool hits)
    {
        var preview = Preview(Content(), Draft([new(1, PlotSize.Small, new(0, 0, 0), VoxelOrientation.PxPy, null)]));
        var result = preview.PickPlot(new(new(x, 10, z), -Vector3.UnitY, 10));
        Assert.AreEqual(hits, result is Lookup<int>.Found { Value: 0 });
    }

    [TestMethod]
    public void NonUprightOrientationUsesTheSamePlacementAndBottomPlane()
    {
        var preview = Preview(Content(), Draft([new(1, PlotSize.Small, new(10, 2, 30), VoxelOrientation.PxPz, null)]));
        var plot = preview.Plots.Single();
        Near(new(10, 3, 30), plot.Pose.Position);
        Near(Vector3.UnitZ, Vector3.Transform(Vector3.UnitY, plot.Pose.Rotation));
        Assert.IsTrue(preview.Frame.DebugLines.All(line => Math.Abs(line.From.Z - 30) < 0.0001));
        Assert.AreEqual(0, Pick(preview, new(new(10, 3, 40), -Vector3.UnitZ, 10)));
        Assert.AreEqual(0, Pick(preview, new(new(34, -21, 40), -Vector3.UnitZ, 10)));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void MissingOrFailedBlueprintsKeepMarkersAndSourcePackages(bool failed)
    {
        var modelDiagnostic = Diagnostic("model-package");
        var buildingDiagnostic = Diagnostic("building-package");
        ContentLookup<BlueprintRecord> model = failed
            ? Found(Model, new DesignEvaluation.Failed([modelDiagnostic])).Value
            : new ContentLookup<BlueprintRecord>.Missing([modelDiagnostic]);
        var preview = Preview(Content((Model, model), (Building, new ContentLookup<BlueprintRecord>.Missing([buildingDiagnostic]))),
            Draft([new(4, PlotSize.Small, new(50, 2, 60), VoxelOrientation.PxPy, Building)]) with { SceneModel = Model });
        Assert.AreEqual(SceneReadiness.Ready, preview.Frame.Readiness);
        var markers = preview.Frame.Update.Upserts.ToArray();
        Assert.AreEqual(2, markers.Length);
        foreach (var (id, package, position) in new[]
        {
            (Model, "model-package", Vector3.Zero), (Building, "building-package", new Vector3(50, 2, 60))
        })
        {
            var reference = new ContentReference.Blueprint(new(BlueprintLibrary.Packages, id));
            var marker = markers.Single(item => item.Visuals.Contains(new SceneVisual.Missing(reference, false)));
            Near(position, marker.Pose.Position);
            Assert.IsTrue(preview.Diagnostics.Any(problem => problem.Code == "content_missing" && problem.Content == reference &&
                problem.Package == new PackageId(package) && problem.Detail == "Unavailable reference."));
        }
        CollectionAssert.AreEqual(preview.Diagnostics.ToArray(), preview.Frame.Diagnostics.ToArray());
        Assert.AreEqual(0, Pick(preview, new(new(50, 10, 60), -Vector3.UnitY, 20)));
        Assert.IsInstanceOfType<ScenePick.Miss>(preview.Frame.Pick(new(new(50, 10, 60), -Vector3.UnitY, 20)));
    }

    [TestMethod]
    public void NotFoundBlueprintProducesExplicitDiagnosticWithoutInventedPackage()
    {
        var preview = Preview(Content((Model, new ContentLookup<BlueprintRecord>.NotFound())), Draft([]) with { SceneModel = Model });
        var problem = preview.Diagnostics.Single();
        Assert.AreEqual("content_missing", problem.Code);
        Assert.AreEqual(new ContentReference.Blueprint(new(BlueprintLibrary.Packages, Model)), problem.Content);
        Assert.IsNull(problem.Package);
        Assert.IsInstanceOfType<SceneVisual.Missing>(preview.Frame.Update.Upserts.Single().Visuals.Single());
    }

    [TestMethod]
    public void PartialBlueprintPreservesKnownGeometryAndDiagnostics()
    {
        var diagnostic = Diagnostic("partial-package");
        var partial = new DesignEvaluation.Partial(SceneFixtures.Draft,
            new(new ComputedValue<double>.Known(0), new ComputedValue<Money>.Known(new(0)),
                new ComputedValue<long>.Known(0), new ComputedValue<ImmutableArray<PartProjection>>.Known([]),
                new ComputedValue<ImmutableArray<GoodsQuantity>>.Known([]), new ComputedValue<int>.Known(0)),
            new(0, new(0), 0, [], []), null, SceneFixtures.Projection([SceneFixtures.Cell(new(1, 0, 2))]), [diagnostic]);
        var preview = Preview(Content(Found(Model, partial)), Draft([]) with { SceneModel = Model });
        Near(new(1, 0, 2), Vertices(preview.Frame.Update.Upserts.Single()).Aggregate(Vector3.Min));
        var problem = preview.Diagnostics.Single();
        Assert.AreEqual(diagnostic.Code, problem.Code);
        Assert.AreEqual(diagnostic.Source!.Package, problem.Package);
        Assert.AreEqual(diagnostic.Subject, problem.Content);
    }

    [TestMethod]
    public void UnspecifiedModelAndDanglingPlotReferencesRemainSuccessfulWithDiagnostics()
    {
        var process = new ProcessRef.Production(new("test:process"));
        var draft = Draft([new(4, PlotSize.Small, new(0, 0, 0), VoxelOrientation.PxPy, null)]) with
        {
            FoundingBuildings = [new(4, Building), new(9, Building)],
            ProductionPlans = [new(4, CapabilityKind.Production, process, true, 0), new(10, CapabilityKind.Production, process, true, 0)]
        };
        var preview = Preview(Content(), draft);
        Assert.AreEqual(SceneReadiness.Ready, preview.Frame.Readiness);
        Assert.IsEmpty(preview.Frame.Update.Upserts);
        var missingModel = preview.Diagnostics.Single(problem => problem.Code == "content_missing");
        Assert.IsNull(missingModel.Content);
        Assert.IsNull(missingModel.Package);
        var targets = preview.Diagnostics.Where(problem => problem.Code == "target_missing").ToArray();
        Assert.AreEqual(2, targets.Length);
        Assert.IsTrue(targets.Any(problem => problem.Detail.Contains("founding[1]", StringComparison.Ordinal)));
        Assert.IsTrue(targets.Any(problem => problem.Detail.Contains("production_plans[1]", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task NewPreviewCannotChangeRetainedFramePlotsOrPicking()
    {
        var factory = ScenesFactory.Create();
        var draft = Draft([new(7, PlotSize.Small, new(0, 0, 0), VoxelOrientation.PxPy, null)]) with { SceneModel = Model };
        var content = Content(Found(Model, SceneFixtures.Evaluation(SceneFixtures.Projection([SceneFixtures.Cell(new(2, 0, 0))]))));
        var first = ((SceneResult<ITemplatePreview>.Success)factory.PreviewTemplate(content, draft, new(1))).Value;
        var oldFrame = first.Frame;
        var oldObjects = oldFrame.Update.Upserts.ToArray();
        var oldVertices = oldObjects.SelectMany(Vertices).ToArray();
        var oldLines = oldFrame.DebugLines.ToArray();
        var second = await Task.Run(() => ((SceneResult<ITemplatePreview>.Success)factory.PreviewTemplate(content,
            draft with { Plots = [new(8, PlotSize.Giant, new(500, 0, 0), VoxelOrientation.NxPy, null)] }, new(2))).Value);
        Assert.AreEqual(new EditRevision(1), first.Revision);
        Assert.AreEqual(new EditRevision(2), second.Revision);
        Assert.AreNotEqual(oldFrame.Update.Frame.Session, second.Frame.Update.Frame.Session);
        Assert.AreSame(oldFrame, first.Frame);
        CollectionAssert.AreEqual(oldObjects, first.Frame.Update.Upserts.ToArray());
        CollectionAssert.AreEqual(oldVertices, first.Frame.Update.Upserts.SelectMany(Vertices).ToArray());
        CollectionAssert.AreEqual(oldLines, first.Frame.DebugLines.ToArray());
        Assert.AreEqual(new GridBounds(new(-24, 0, -24), new(23, 47, 23)), first.Plots.Single().Bounds);
        Near(Vector3.Zero, first.Plots.Single().Pose.Position);
        Assert.AreEqual(0, Pick(first, Down(0)));
        Assert.IsInstanceOfType<Lookup<int>.Missing>(second.PickPlot(Down(0)));
        Assert.AreEqual(0, Pick(second, Down(500)));
    }

    [TestMethod]
    public void ProgrammingPreconditionsThrowInsteadOfReturningFalseSuccess()
    {
        var factory = ScenesFactory.Create();
        var content = Content();
        var draft = Draft([]);
        Assert.ThrowsExactly<ArgumentNullException>(() => factory.PreviewTemplate(null!, draft, new(1)));
        Assert.ThrowsExactly<ArgumentNullException>(() => factory.PreviewTemplate(content, null!, new(1)));
        foreach (var invalid in new[] { draft with { Plots = default }, draft with { FoundingBuildings = default },
            draft with { Inventory = default }, draft with { ProductionPlans = default },
            draft with { Plots = [new(1, (PlotSize)(-1), new(0, 0, 0), VoxelOrientation.PxPy, null)] },
            draft with { Plots = [new(1, PlotSize.Small, new(0, 0, 0), (VoxelOrientation)(-1), null)] } })
            Assert.ThrowsExactly<ArgumentException>(() => factory.PreviewTemplate(content, invalid, new(1)));
        var preview = Preview(content, draft);
        Assert.ThrowsExactly<ArgumentNullException>(() => preview.PickPlot(null!));
        foreach (var ray in new[] { new SceneRay(Vector3.Zero, Vector3.Zero, 10),
            new(new(float.NaN, 0, 0), Vector3.UnitY, 10), new(Vector3.Zero, Vector3.UnitY, 0),
            new(Vector3.Zero, Vector3.UnitY, double.PositiveInfinity) })
            Assert.ThrowsExactly<ArgumentException>(() => preview.PickPlot(ray));
    }

    private static SettlementTemplateDraft Draft(ImmutableArray<TemplatePlotDraft> plots) => new("preview", null, null, null, plots, [], [], []);
    private static FixedContentView Content(params (BlueprintId Id, ContentLookup<BlueprintRecord> Value)[] blueprints) => new()
    {
        Generation = new(7),
        BlueprintRecords = blueprints.ToDictionary(item => new BlueprintAddress(BlueprintLibrary.Packages, item.Id), item => item.Value),
        VoxelAppearances = new Dictionary<VoxelDefinitionId, ContentLookup<VoxelAppearanceProjection>>
        { [SceneFixtures.Voxel] = new ContentLookup<VoxelAppearanceProjection>.Found(SceneFixtures.Cube) }
    };
    private static (BlueprintId Id, ContentLookup<BlueprintRecord> Value) Found(BlueprintId id, DesignEvaluation evaluation) =>
        (id, new ContentLookup<BlueprintRecord>.Found(new(new(BlueprintLibrary.Packages, id), evaluation switch
        {
            DesignEvaluation.Complete complete => complete.Definition,
            DesignEvaluation.Partial partial => partial.Definition,
            _ => SceneFixtures.Draft
        }, [], evaluation)));
    private static ITemplatePreview Preview(IContentView content, SettlementTemplateDraft draft, EditRevision revision = default) =>
        ((SceneResult<ITemplatePreview>.Success)ScenesFactory.Create().PreviewTemplate(content, draft, revision)).Value;
    private static SceneRay Down(float x) => new(new(x, 10, 0), -Vector3.UnitY, 20);
    private static int Pick(ITemplatePreview preview, SceneRay ray) => ((Lookup<int>.Found)preview.PickPlot(ray)).Value;
    private static Vector3[] Vertices(SceneObject item) => item.Visuals.OfType<SceneVisual.Geometry>()
        .SelectMany(visual => visual.Mesh.Vertices).Select(vertex => vertex.Position).ToArray();
    private static void Near(Vector3 expected, Vector3 actual) => Assert.IsTrue(Vector3.Distance(expected, actual) < 0.0001f,
        $"Expected {expected}, got {actual}.");
    private static ContentDiagnostic Diagnostic(string package) => new("reference_missing", ContentDiagnosticSeverity.Error,
        "lookup", new ContentReference.Blueprint(new(BlueprintLibrary.Packages, Building)), new(new(package), "blueprint.json"),
        "layout", null, "Unavailable reference.");
}
