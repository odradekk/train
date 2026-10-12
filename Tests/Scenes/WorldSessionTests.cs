using System.Collections.Immutable;
using System.Numerics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using Train.Scenes.Contracts;
using Train.Scenes.ContractSupport;

namespace Train.Scenes.Tests;

[TestClass]
public sealed class WorldSessionTests
{
    private static readonly InstanceId Vehicle = new InstanceId.Vehicle(new(1));
    private static readonly WorldLocation Location = new WorldLocation.Node(new(1));
    private static readonly WorldSceneTarget Target = new WorldSceneTarget.Location(Location);
    private static readonly SceneFrameRequest FrameRequest = new(0, null, SceneDebugLayer.None);
    private static readonly SceneRay Ray = new(new(0.5f, 0.5f, -2), Vector3.UnitZ, 10);
    private static DesignEvaluation.Complete Evaluation => SceneFixtures.Evaluation(SceneFixtures.Projection([SceneFixtures.Cell(new(0, 0, 0))]));
    private static InstanceView Instance => new(Vehicle, SceneFixtures.Design, new("composition"), Location,
        SettlementRelation.PlayerOwned, new InstanceDetails.Vehicle(SizeClass.Small, null), InstanceLock.None,
        new(10, 10), [], []);

    private static SceneWorldSnapshot Snapshot(ulong steps = 0, ulong sequence = 0,
        InstanceView? instance = null, bool removed = false, IContentView? content = null) => new(new FixedCoreReadView
    {
        Position = new(steps, sequence), InputIdentity = new("scenes-test"), Revision = new(sequence),
        Nodes = new Dictionary<NodeId, Lookup<NodeView>>
        {
            [new(1)] = new Lookup<NodeView>.Found(Node(1)), [new(2)] = new Lookup<NodeView>.Found(Node(2)),
            [new(99)] = new Lookup<NodeView>.Missing()
        },
        Instances = new Dictionary<InstanceId, Lookup<InstanceView>>
        { [Vehicle] = removed ? new Lookup<InstanceView>.Missing() : new Lookup<InstanceView>.Found(instance ?? Instance) }
    }, content ?? SceneFixtures.Content(Evaluation));
    private static NodeView Node(ulong id) => new(new(new(id), new(0, 0), new(new("test:terrain"), 0, []), new NodeOccupant.Empty(), [], 0, 0, null), []);
    private static WorldSceneContent Terrain() => new([
        new(new(0, 0, 0), SceneFixtures.Cube, null, null, new PickSubject.Terrain(Location), [])], [], [], [], []);
    private static WorldSceneContent ObjectContent() => new([], [new(Vehicle, SceneFixtures.Identity)], [], [], []);
    private static WorldSceneSession Session(SceneWorldSnapshot snapshot, WorldSceneContent? content = null) => new(
        new(Guid.NewGuid()), snapshot, (_, _, _) => content ?? Terrain());
    private static async Task<ISceneFrame> Ready(WorldSceneSession session)
    {
        Assert.IsInstanceOfType<SceneResult<SceneRequestId>.Success>(session.RequestView(Target));
        await session.PendingBuild!;
        return Present(session);
    }
    private static ISceneFrame Present(WorldSceneSession session, ISceneFrame? displayed = null) =>
        ((SceneResult<ISceneFrame>.Success)session.Present(FrameRequest, displayed)).Value;
    private static SceneHit Hit(ISceneFrame frame) => ((ScenePick.Hit)frame.Pick(Ray)).Value;
    private static string Code<T>(SceneResult<T> result) where T : notnull => ((SceneResult<T>.Failure)result).Problems[0].Code;

    [TestMethod]
    public void OpenWorldRejectsUnpairedInputAndFactoryNamesDeferredFeatures()
    {
        var factory = ScenesFactory.Create();
        var snapshot = Snapshot();
        var mismatch = snapshot with { Core = new FixedCoreReadView { InputIdentity = new("other") } };
        Assert.ThrowsExactly<ArgumentException>(() => factory.OpenWorld(new(Guid.NewGuid()), mismatch));
        StringAssert.Contains(Assert.ThrowsExactly<NotImplementedException>(() => factory.OpenEditor(snapshot.Content, Evaluation)).Message, "#333");
        StringAssert.Contains(Assert.ThrowsExactly<NotImplementedException>(() => factory.PreviewTemplate(snapshot.Content, null!, new(1))).Message, "#389");
    }

    [TestMethod]
    public async Task ProductionWorldGenerationReportsExplicitFaultInsteadOfEmptySuccess()
    {
        await using var session = (WorldSceneSession)((SceneResult<IWorldSceneSession>.Success)ScenesFactory.Create().OpenWorld(new(Guid.NewGuid()), Snapshot())).Value;
        session.RequestView(Target);
        await Assert.ThrowsExactlyAsync<NotImplementedException>(async () => await session.PendingBuild!);
        var fault = (SceneResult<ISceneFrame>.Faulted)session.Present(FrameRequest, null);
        Assert.AreEqual("scene_internal_fault", fault.Problem.Code);
        StringAssert.Contains(fault.Problem.Detail, "#316");
    }

    [TestMethod]
    public async Task LoadingDoesNotWaitForGenerationAndDisposeRevokesCallsBeforeWaiting()
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new WorldSceneSession(new(Guid.NewGuid()), Snapshot(), (_, _, _) =>
        { entered.SetResult(); release.Wait(); return Terrain(); });
        try
        {
            session.RequestView(Target);
            await entered.Task;
            var loading = Present(session);
            Assert.AreEqual(SceneReadiness.Loading, loading.Readiness);
            Assert.IsFalse(loading.Update.Environment.FoggedRegions.IsEmpty);
            Assert.IsInstanceOfType<ScenePick.NotReady>(loading.Pick(Ray));
            var closing = session.DisposeAsync();
            Assert.IsFalse(closing.IsCompleted);
            Assert.ThrowsExactly<ObjectDisposedException>(() => session.RequestView(Target));
            Assert.ThrowsExactly<ObjectDisposedException>(() => session.Present(FrameRequest, null));
            Assert.IsInstanceOfType<ScenePick.NotReady>(loading.Pick(Ray));
            release.Set(); await closing; await session.DisposeAsync();
        }
        finally { release.Set(); await session.DisposeAsync(); }
    }

    [TestMethod]
    public async Task RealSessionPassesSharedOldRequestScenarioIncludingSameTarget()
    {
        await using var session = Session(Snapshot());
        var frame = await Ready(session);
        WorldRequestScenario.AssertOldHitExpires(session, Hit(frame), Target);
        await session.PendingBuild!;
        var next = Present(session, frame);
        Assert.IsNull(next.Update.Base);
        Assert.AreNotEqual(frame.Update.Frame.Space, next.Update.Frame.Space);
    }

    [TestMethod]
    public async Task InvalidTargetLeavesCurrentRequestAndFrameUsable()
    {
        await using var session = Session(Snapshot());
        var frame = await Ready(session);
        Assert.AreEqual("target_missing", Code(session.RequestView(new WorldSceneTarget.Location(new WorldLocation.Node(new(99))))));
        Assert.IsInstanceOfType<SceneResult<PickSubject>.Success>(session.Resolve(Hit(frame)));
        Assert.AreEqual(frame.Update.Frame.Request, Present(session, frame).Update.Frame.Request);
    }

    [TestMethod]
    public async Task ObserveRejectsInvalidBoundariesAndEventOrderWithoutChangingState()
    {
        await using var session = Session(Snapshot(2, 1));
        var frame = await Ready(session);
        Assert.ThrowsExactly<ArgumentException>(() => session.Observe(Snapshot(1), []));
        Assert.ThrowsExactly<ArgumentException>(() => session.Observe(Snapshot(2, 2), default));
        var item = new EventEnvelope(new(2, 2), 0, new CoreEvent.TrainArrived(new(1), new(1)));
        Assert.ThrowsExactly<ArgumentException>(() => session.Observe(Snapshot(2, 2), [item, item]));
        Assert.ThrowsExactly<ArgumentException>(() => session.Observe(Snapshot(2, 1), [item]));
        Assert.AreEqual(frame.SimulationSeconds, Present(session).SimulationSeconds);
        session.Observe(Snapshot(2, 2), [item]);
        Assert.ThrowsExactly<ArgumentException>(() => session.Observe(Snapshot(2, 3), [item]));
        session.Observe(Snapshot(3), []);
        Assert.AreEqual(3 * SimulationTime.StepSeconds, Present(session).SimulationSeconds);
    }

    [TestMethod]
    public async Task ObserveRejectsContentGenerationAndBusinessIdentityChanges()
    {
        await using var session = Session(Snapshot());
        await Ready(session);
        var content = new FixedContentView { Generation = new(2), Core = SceneFixtures.Content().Core };
        Assert.ThrowsExactly<ArgumentException>(() => session.Observe(Snapshot(content: content), []));
        var changed = new FixedContentView { Generation = new(1), Core = SceneFixtures.Content().Core with { Identity = new("other") } };
        Assert.ThrowsExactly<ArgumentException>(() => session.Observe(Snapshot(content: changed), []));
    }

    [TestMethod]
    public async Task LateOldRequestCannotReplaceNewContent()
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = new WorldSceneSession(new(Guid.NewGuid()), Snapshot(), (_, value, _) =>
        {
            if (value == Target) { entered.SetResult(); release.Wait(); return Terrain(); }
            return Terrain() with { Terrain = [new(new(5, 0, 0), SceneFixtures.Cube, null, null, new PickSubject.Terrain(new WorldLocation.Node(new(2))), [])] };
        });
        try
        {
            session.RequestView(Target); await entered.Task;
            var oldTask = session.PendingBuild!;
            session.RequestView(new WorldSceneTarget.Location(new WorldLocation.Node(new(2))));
            await session.PendingBuild!;
            var next = Present(session);
            Assert.IsInstanceOfType<ScenePick.Miss>(next.Pick(Ray));
            release.Set(); await oldTask;
            var after = Present(session, next);
            Assert.IsInstanceOfType<ScenePick.Miss>(after.Pick(Ray));
            Assert.AreEqual(next.Update.Frame.Request, after.Update.Frame.Request);
        }
        finally { release.Set(); }
    }

    [TestMethod]
    public async Task LateBuildReconcilesCurrentRemovalInsteadOfRevivingObject()
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = new WorldSceneSession(new(Guid.NewGuid()), Snapshot(), (_, _, _) =>
        { entered.SetResult(); release.Wait(); return ObjectContent(); });
        try
        {
            session.RequestView(Target); await entered.Task;
            session.Observe(Snapshot(1, removed: true), []);
            release.Set(); await session.PendingBuild!;
            var frame = Present(session);
            Assert.IsTrue(frame.Update.Upserts.IsEmpty);
            Assert.IsInstanceOfType<ScenePick.Miss>(frame.Pick(Ray));
        }
        finally { release.Set(); }
    }

    [TestMethod]
    public async Task DeletedObjectExpiresHitButRetainedFrameKeepsGeometryAndPicking()
    {
        await using var session = Session(Snapshot(), ObjectContent());
        var frame = await Ready(session);
        var hit = Hit(frame);
        var originalMesh = ((SceneVisual.Geometry)frame.Update.Upserts[0].Visuals[0]).Mesh;
        session.Observe(Snapshot(1), []);
        Assert.IsInstanceOfType<SceneResult<PickSubject>.Success>(session.Resolve(hit));
        var unchanged = Present(session, frame);
        Assert.IsTrue(unchanged.Update.Upserts.IsEmpty);
        session.Observe(Snapshot(2, removed: true), []);
        Assert.AreEqual("stale_hit", Code(session.Resolve(hit)));
        var removed = Present(session, unchanged);
        Assert.AreEqual(frame.Update.Upserts[0].Id, removed.Update.Removed.Single());
        Assert.AreSame(originalMesh, ((SceneVisual.Geometry)frame.Update.Upserts[0].Visuals[0]).Mesh);
        Assert.AreEqual(hit, Hit(frame));
        await session.DisposeAsync();
        Assert.AreEqual(hit, Hit(frame));
        Assert.ThrowsExactly<ObjectDisposedException>(() => session.Resolve(hit));
    }

    [TestMethod]
    public async Task DesignChangesAndDestroyedPartsExpireOldHitAndRebuildOnlyChangedObject()
    {
        await using var session = Session(Snapshot(), ObjectContent());
        var first = await Ready(session);
        var oldHit = Hit(first);
        var hash = new DesignHash("new-design");
        var evaluation = Evaluation with { Design = hash, Scene = Evaluation.Scene with
        { Design = hash, Voxels = [SceneFixtures.Cell(new(3, 0, 0))] } };
        var content = new FixedContentView
        {
            Generation = new(1), Core = SceneFixtures.Content().Core,
            Designs = new Dictionary<DesignHash, ContentLookup<DesignEvaluation>> { [hash] = new ContentLookup<DesignEvaluation>.Found(evaluation) },
            VoxelAppearances = new Dictionary<VoxelDefinitionId, ContentLookup<VoxelAppearanceProjection>> { [SceneFixtures.Voxel] = new ContentLookup<VoxelAppearanceProjection>.Found(SceneFixtures.Cube) }
        };
        var instance = Instance with { Design = hash };
        session.Observe(Snapshot(1, instance: instance, content: content), []);
        Assert.AreEqual("stale_hit", Code(session.Resolve(oldHit)));
        var second = Present(session, first);
        Assert.AreEqual(first.Update.Upserts[0].Id, second.Update.Upserts[0].Id);
        Assert.IsInstanceOfType<ScenePick.Miss>(second.Pick(Ray));
        var newHit = ((ScenePick.Hit)second.Pick(Ray with { Origin = new(3.5f, 0.5f, -2) })).Value;
        session.Observe(Snapshot(2, instance: instance with { Parts = [new(new(1), new(0, 10), false, true)] }, content: content), []);
        Assert.AreEqual("stale_hit", Code(session.Resolve(newHit)));
        Assert.IsInstanceOfType<ScenePick.Miss>(Present(session, second).Pick(Ray with { Origin = new(3.5f, 0.5f, -2) }));
    }

    [TestMethod]
    public async Task DisplayedFrameIsActualDeltaBaseAndNullRecoversFullReplacement()
    {
        await using var session = Session(Snapshot(), ObjectContent());
        var displayed = await Ready(session);
        var prepared = Present(session, displayed);
        var retry = Present(session, displayed);
        Assert.AreEqual(displayed.Update.Frame, prepared.Update.Base);
        Assert.AreEqual(displayed.Update.Frame, retry.Update.Base);
        Assert.IsTrue(retry.Update.Frame.Revision > prepared.Update.Frame.Revision);
        var replacement = Present(session);
        Assert.IsNull(replacement.Update.Base);
        Assert.AreEqual(displayed.Update.Upserts[0].Id, replacement.Update.Upserts[0].Id);
        Assert.IsNull(replacement.NodePanBounds);
        Assert.IsInstanceOfType<Lookup<WorldLocation>.Missing>(replacement.Locate(Vector3.Zero));
        Assert.IsInstanceOfType<Lookup<double>.Missing>(replacement.TerrainHeight(0, 0));
        Assert.IsTrue(replacement.Update.Effects.IsEmpty);
        Assert.IsTrue(replacement.Update.Environment.TerrainCollision.IsEmpty);
    }

    [TestMethod]
    public async Task FailedWorldDesignPreservesPackageAndElementDiagnostics()
    {
        var element = new DesignElementPath.Cell([], 0);
        var diagnostic = new ContentDiagnostic("design_invalid", ContentDiagnosticSeverity.Error, "design",
            new ContentReference.Design(SceneFixtures.Design), new(new("source-package"), "design.json"),
            "layout.cells[0]", element, "Invalid saved design.");
        await using var session = Session(Snapshot(content: SceneFixtures.Content(new DesignEvaluation.Failed([diagnostic]))), ObjectContent());
        var frame = await Ready(session);
        Assert.IsTrue(frame.Diagnostics.Any(problem => problem.Code == diagnostic.Code &&
            problem.Package == diagnostic.Source!.Package && problem.Element == element));
        Assert.IsTrue(frame.Diagnostics.Any(problem => problem.Code == "content_missing" &&
            problem.Content == diagnostic.Subject && problem.Package == diagnostic.Source!.Package));
        Assert.IsInstanceOfType<ScenePick.Miss>(frame.Pick(Ray));
        Assert.IsTrue(frame.Update.Upserts.SelectMany(item => item.Visuals).OfType<SceneVisual.Missing>().All(marker => !marker.KnownCell));
    }

    [TestMethod]
    public async Task RemovedNestedObjectGetsNewIdOnRepairAndOldHitStaysExpired()
    {
        var evaluation = SceneFixtures.Evaluation(SceneFixtures.Projection([
            SceneFixtures.Cell(new(3, 0, 0)), SceneFixtures.Cell(new(0, 0, 0), 9, path: [0])],
            [new([0], new(0, 0, 0), VoxelOrientation.PxPy, new NestedObjectDetails.FunctionalComposite(new("test:part"), new(9), null))]));
        var content = SceneFixtures.Content(evaluation);
        var healthy = Instance with { Parts = [new(new(9), new(10, 10), false, false)] };
        await using var session = Session(Snapshot(instance: healthy, content: content), ObjectContent());
        var first = await Ready(session);
        var oldHit = Hit(first);
        session.Observe(Snapshot(1, instance: healthy with { Parts = [new(new(9), new(0, 10), false, true)] }, content: content), []);
        var destroyed = Present(session, first);
        Assert.IsTrue(destroyed.Update.Removed.Contains(oldHit.Object!.Value));
        session.Observe(Snapshot(2, instance: healthy, content: content), []);
        var repaired = Present(session, destroyed);
        Assert.AreNotEqual(oldHit.Object, Hit(repaired).Object);
        Assert.AreEqual("stale_hit", Code(session.Resolve(oldHit)));
        Assert.IsInstanceOfType<SceneResult<PickSubject>.Success>(session.Resolve(Hit(repaired)));
        session.Observe(Snapshot(3, removed: true, content: content), []);
        var removed = Present(session, repaired);
        session.Observe(Snapshot(4, instance: healthy, content: content), []);
        var restored = Present(session, removed);
        Assert.AreNotEqual(Hit(repaired).Object, Hit(restored).Object);
    }

    [TestMethod]
    public async Task LateBuildUsesCurrentDesignAndDestroyedPartsOnFirstVisibleFrame()
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = new WorldSceneSession(new(Guid.NewGuid()), Snapshot(), (_, _, _) =>
        { entered.SetResult(); release.Wait(); return ObjectContent(); });
        try
        {
            session.RequestView(Target); await entered.Task;
            var hash = new DesignHash("late-current-design");
            var evaluation = Evaluation with { Design = hash, Scene = Evaluation.Scene with
            { Design = hash, Voxels = [SceneFixtures.Cell(new(0, 0, 0)), SceneFixtures.Cell(new(3, 0, 0), 2)],
                Elements = SceneFixtures.Projection([SceneFixtures.Cell(new(0, 0, 0)), SceneFixtures.Cell(new(3, 0, 0), 2)]).Elements } };
            var content = new FixedContentView
            {
                Generation = new(1), Core = SceneFixtures.Content().Core,
                Designs = new Dictionary<DesignHash, ContentLookup<DesignEvaluation>> { [hash] = new ContentLookup<DesignEvaluation>.Found(evaluation) },
                VoxelAppearances = new Dictionary<VoxelDefinitionId, ContentLookup<VoxelAppearanceProjection>> { [SceneFixtures.Voxel] = new ContentLookup<VoxelAppearanceProjection>.Found(SceneFixtures.Cube) }
            };
            session.Observe(Snapshot(1, instance: Instance with { Design = hash, Parts = [new(new(1), new(0, 10), false, true)] }, content: content), []);
            release.Set(); await session.PendingBuild!;
            var visible = Present(session);
            Assert.IsInstanceOfType<ScenePick.Miss>(visible.Pick(Ray));
            var hit = ((ScenePick.Hit)visible.Pick(Ray with { Origin = new(3.5f, 0.5f, -2) })).Value;
            Assert.AreEqual(hash, ((PickSubject.WorldObject)hit.Subject).Design);
            Assert.AreEqual(new PartId(2), ((PickSubject.WorldObject)hit.Subject).Part);
            Assert.IsInstanceOfType<SceneResult<PickSubject>.Success>(session.Resolve(hit));
        }
        finally { release.Set(); }
    }

    [TestMethod]
    public async Task ForeignFramesAndInvalidRequestsAreRejectedBeforeFrameMutation()
    {
        await using var session = Session(Snapshot());
        await using var other = Session(Snapshot());
        var frame = await Ready(session);
        var foreign = await Ready(other);
        Assert.ThrowsExactly<ArgumentException>(() => session.Present(FrameRequest, foreign));
        Assert.ThrowsExactly<ArgumentException>(() => session.Present(FrameRequest with { Interpolation = double.NaN }, frame));
        Assert.AreEqual(frame.Update.Frame, Present(session, frame).Update.Base);
    }
}
