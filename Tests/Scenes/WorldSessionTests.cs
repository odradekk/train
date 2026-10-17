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
    private static readonly WorldSceneTarget Target = new WorldSceneTarget.Combat(new(1));
    private static readonly SceneFrameRequest FrameRequest = new(0, null, SceneDebugLayer.None);
    private static readonly SceneRay Ray = new(new(0.5f, 0.5f, -2), Vector3.UnitZ, 10);
    private static DesignEvaluation.Complete Evaluation => SceneFixtures.Evaluation(SceneFixtures.Projection([SceneFixtures.Cell(new(0, 0, 0))]));
    private static InstanceView Instance => new(Vehicle, SceneFixtures.Design, new("composition"), Location,
        SettlementRelation.PlayerOwned, new InstanceDetails.Vehicle(SizeClass.Small, null), InstanceLock.None,
        new(10, 10), [], []);

    private static SceneWorldSnapshot Snapshot(ulong steps = 0, ulong sequence = 0,
        InstanceView? instance = null, bool removed = false, IContentView? content = null) => new(new FixedCoreReadView
        {
            Position = new(steps, sequence),
            InputIdentity = new("scenes-test"),
            Nodes = new Dictionary<NodeId, Lookup<NodeView>>
            {
                [new(1)] = new Lookup<NodeView>.Found(Node(1)),
                [new(2)] = new Lookup<NodeView>.Found(Node(2)),
                [new(99)] = new Lookup<NodeView>.Missing()
            },
            Combats = new Dictionary<CombatId, Lookup<CombatView>>
            {
                [new(1)] = new Lookup<CombatView>.Found(new(new(1), Location, 0, [])),
                [new(2)] = new Lookup<CombatView>.Found(new(new(2), new WorldLocation.Node(new(2)), 0, []))
            },
            Instances = new Dictionary<InstanceId, Lookup<InstanceView>>
            { [Vehicle] = removed ? new Lookup<InstanceView>.Missing() : new Lookup<InstanceView>.Found(instance ?? Instance) }
        }, content ?? SceneFixtures.Content(Evaluation));
    private static NodeView Node(ulong id) => new(new(new(id), new(0, 0), new(new("test:terrain"), 0, []), new NodeOccupant.Empty(), [], 0, 0, null), [], 80, 0, 80);
    private static WorldSceneContent Terrain() => new([
        new(new(0, 0, 0), new(VoxelOrientation.PxPy, SceneFixtures.Cube, null, null, new PickSubject.Terrain(Location), []))], [], [], [], []);
    private static WorldSceneContent ObjectContent() => new([], [new(Vehicle, SceneFixtures.Identity)], [], [], []);
    private static WorldSceneSession Session(SceneWorldSnapshot snapshot, WorldSceneContent? content = null) => new(
        new(Guid.NewGuid()), snapshot, (_, _, _) => content ?? Terrain(), new(), new());
    private static async Task<ISceneFrame> Ready(WorldSceneSession session)
    {
        Assert.IsInstanceOfType<SceneResult<SceneRequestId>.Success>(session.RequestView(Target));
        await session.PendingBuild!;
        return await Settled(session);
    }
    private static async Task<ISceneFrame> Settled(WorldSceneSession session, ISceneFrame? displayed = null)
    {
        var frame = Present(session, displayed);
        while (session.PendingObjectBuilds.Length > 0)
        {
            await SceneFixtures.AwaitPendingWork(session.PendingObjectBuilds);
            frame = Present(session, displayed);
        }
        return frame;
    }
    private static ISceneFrame Present(WorldSceneSession session, ISceneFrame? displayed = null) =>
        ((SceneResult<ISceneFrame>.Success)session.Present(FrameRequest, displayed)).Value;
    private static SceneHit Hit(ISceneFrame frame) => ((ScenePick.Hit)frame.Pick(Ray)).Value;
    private static string Code<T>(SceneResult<T> result) where T : notnull => ((SceneResult<T>.Failure)result).Problems[0].Code;

    [TestMethod]
    public void OpenWorldRejectsUnpairedInputAndEditorOpens()
    {
        var factory = ScenesFactory.Create();
        var snapshot = Snapshot();
        var mismatch = snapshot with { Core = new FixedCoreReadView { InputIdentity = new("other") } };
        Assert.ThrowsExactly<ArgumentException>(() => factory.OpenWorld(new(Guid.NewGuid()), mismatch));
        using var editor = ((SceneResult<IDesignEditSession>.Success)factory.OpenEditor(snapshot.Content, Evaluation)).Value;
    }

    [TestMethod]
    public async Task ProductionWorldGenerationKeepsCombatExplicitlyDeferred()
    {
        var basis = Snapshot();
        var snapshot = basis with { Core = new FixedCoreReadView
        {
            InputIdentity = basis.Core.InputIdentity,
            Combats = new Dictionary<CombatId, Lookup<CombatView>> { [new(1)] = new Lookup<CombatView>.Found(new(new(1), Location, 0, [])) }
        } };
        await using var session = (WorldSceneSession)((SceneResult<IWorldSceneSession>.Success)ScenesFactory.Create().OpenWorld(new(Guid.NewGuid()), snapshot)).Value;
        session.RequestView(new WorldSceneTarget.Combat(new(1)));
        await Assert.ThrowsExactlyAsync<NotImplementedException>(async () => await session.PendingBuild!);
        var fault = (SceneResult<ISceneFrame>.Faulted)session.Present(FrameRequest, null);
        Assert.AreEqual("scene_internal_fault", fault.Problem.Code);
        StringAssert.Contains(fault.Problem.Detail, "#353");
    }

    [TestMethod]
    public async Task LoadingDoesNotWaitForGenerationAndDisposeRevokesCallsBeforeWaiting()
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new WorldSceneSession(new(Guid.NewGuid()), Snapshot(), (_, _, _) =>
        { entered.SetResult(); release.Wait(); return Terrain(); }, new(), new());
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
            return Terrain() with { Terrain = [new(new(5, 0, 0), new(VoxelOrientation.PxPy, SceneFixtures.Cube, null, null, new PickSubject.Terrain(new WorldLocation.Node(new(2))), []))] };
        }, new(), new());
        try
        {
            session.RequestView(Target); await entered.Task;
            var oldTask = session.PendingBuild!;
            session.RequestView(new WorldSceneTarget.Combat(new(2)));
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
        { entered.SetResult(); release.Wait(); return ObjectContent(); }, new(), new());
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
        var evaluation = Evaluation with
        {
            Design = hash,
            Scene = Evaluation.Scene with
            { Design = hash, Voxels = [SceneFixtures.Cell(new(3, 0, 0))] }
        };
        var content = SceneFixtures.Content(evaluation, hash);
        var instance = Instance with { Design = hash };
        session.Observe(Snapshot(1, instance: instance, content: content), []);
        Assert.AreEqual("stale_hit", Code(session.Resolve(oldHit)));
        var second = await Settled(session, first);
        Assert.AreEqual(first.Update.Upserts[0].Id, second.Update.Upserts[0].Id);
        Assert.IsInstanceOfType<ScenePick.Miss>(second.Pick(Ray));
        var newHit = ((ScenePick.Hit)second.Pick(Ray with { Origin = new(3.5f, 0.5f, -2) })).Value;
        session.Observe(Snapshot(2, instance: instance with { Parts = [new(new(1), new(0, 10), false, true)] }, content: content), []);
        Assert.AreEqual("stale_hit", Code(session.Resolve(newHit)));
        Assert.IsInstanceOfType<ScenePick.Miss>((await Settled(session, second)).Pick(Ray with { Origin = new(3.5f, 0.5f, -2) }));
    }

    [TestMethod]
    public async Task DisplayedFrameIsActualDeltaBaseAndNullRecoversFullReplacement()
    {
        await using var session = Session(Snapshot(), ObjectContent());
        var displayed = await Ready(session);
        var prepared = Present(session, displayed);
        session.Observe(Snapshot(1, removed: true), []);
        var removal = Present(session, prepared);
        var retry = Present(session, displayed);
        Assert.AreEqual(displayed.Update.Upserts[0].Id, removal.Update.Removed.Single());
        Assert.AreEqual(displayed.Update.Upserts[0].Id, retry.Update.Removed.Single());
        Assert.AreEqual(displayed.Update.Frame, prepared.Update.Base);
        Assert.AreEqual(displayed.Update.Frame, retry.Update.Base);
        Assert.IsTrue(retry.Update.Frame.Revision > prepared.Update.Frame.Revision);
        session.Observe(Snapshot(2), []);
        var replacement = await Settled(session);
        Assert.IsNull(replacement.Update.Base);
        Assert.AreNotEqual(displayed.Update.Upserts[0].Id, replacement.Update.Upserts[0].Id);
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
        var destroyed = await Settled(session, first);
        Assert.IsTrue(destroyed.Update.Removed.Contains(oldHit.Object!.Value));
        session.Observe(Snapshot(2, instance: healthy, content: content), []);
        var repaired = await Settled(session, destroyed);
        Assert.AreNotEqual(oldHit.Object, Hit(repaired).Object);
        Assert.AreEqual("stale_hit", Code(session.Resolve(oldHit)));
        Assert.IsInstanceOfType<SceneResult<PickSubject>.Success>(session.Resolve(Hit(repaired)));
        session.Observe(Snapshot(3, removed: true, content: content), []);
        var removed = Present(session, repaired);
        session.Observe(Snapshot(4, instance: healthy, content: content), []);
        var restored = await Settled(session, removed);
        Assert.AreNotEqual(Hit(repaired).Object, Hit(restored).Object);
    }

    [TestMethod]
    public async Task LateBuildUsesCurrentDesignAndDestroyedPartsOnFirstVisibleFrame()
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = new WorldSceneSession(new(Guid.NewGuid()), Snapshot(), (_, _, _) =>
        { entered.SetResult(); release.Wait(); return ObjectContent(); }, new(), new());
        try
        {
            session.RequestView(Target); await entered.Task;
            var hash = new DesignHash("late-current-design");
            var evaluation = Evaluation with
            {
                Design = hash,
                Scene = Evaluation.Scene with
                {
                    Design = hash,
                    Voxels = [SceneFixtures.Cell(new(0, 0, 0)), SceneFixtures.Cell(new(3, 0, 0), 2)],
                    Elements = SceneFixtures.Projection([SceneFixtures.Cell(new(0, 0, 0)), SceneFixtures.Cell(new(3, 0, 0), 2)]).Elements
                }
            };
            var content = SceneFixtures.Content(evaluation, hash);
            session.Observe(Snapshot(1, instance: Instance with { Design = hash, Parts = [new(new(1), new(0, 10), false, true)] }, content: content), []);
            release.Set(); await session.PendingBuild!;
            var first = Present(session);
            Assert.AreEqual(SceneReadiness.Partial, first.Readiness);
            Assert.IsTrue(first.Update.Upserts.IsEmpty);
            var visible = await Settled(session, first);
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

    [TestMethod]
    public async Task InitialObjectGeometryBuildRunsBeforePresentAndLoadingDoesNotWait()
    {
        using var gated = new SceneFixtures.BlockedAppearanceView(SceneFixtures.Content(Evaluation));
        await using var session = Session(Snapshot(content: gated), ObjectContent());
        session.RequestView(Target);
        try
        {
            await gated.Entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
            var loading = Present(session);
            Assert.AreEqual(SceneReadiness.Loading, loading.Readiness);
            Assert.IsTrue(loading.Update.Upserts.IsEmpty);
            gated.Release.Set();
            await session.PendingBuild!;
            var ready = Present(session, loading);
            Assert.AreEqual(SceneReadiness.Ready, ready.Readiness);
            Assert.AreEqual(new GridPosition(0, 0, 0), Hit(ready).Cell);
        }
        finally
        {
            gated.Release.Set();
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task BlockedRebuildKeepsOldGeometryAndDiscardsOutdatedResult(bool changeDesign)
    {
        await using var session = Session(Snapshot(), ObjectContent());
        var first = await Ready(session);
        var oldHit = Hit(first);
        var hash = new DesignHash("rebuilding-design");
        var evaluation = Evaluation with
        {
            Design = hash,
            Scene = Evaluation.Scene with
            { Design = hash, Voxels = [SceneFixtures.Cell(new(3, 0, 0))] }
        };
        using var gated = new SceneFixtures.BlockedAppearanceView(SceneFixtures.Content(evaluation, hash));
        var rebuilding = Instance with { Design = hash };
        session.Observe(Snapshot(1, instance: rebuilding, content: gated), []);
        var present = Task.Run(() => Present(session, first));
        try
        {
            await gated.Entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
            var during = await present.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.AreEqual(SceneReadiness.Partial, during.Readiness);
            Assert.AreEqual(oldHit.Cell, Hit(during).Cell);
            Assert.IsTrue(during.Update.Upserts.IsEmpty);
            Assert.AreEqual("stale_hit", Code(session.Resolve(oldHit)));
            var latest = changeDesign ? Instance : rebuilding with
            { Parts = [new(new(1), new(0, 10), false, true)] };
            var latestContent = changeDesign ? SceneFixtures.Content(Evaluation) : SceneFixtures.Content(evaluation, hash);
            session.Observe(Snapshot(2, instance: latest, content: latestContent), []);
            gated.Release.Set();
            await SceneFixtures.AwaitPendingWork(session.PendingObjectBuilds);
            var discarded = Present(session, during);
            Assert.AreEqual(oldHit.Cell, Hit(discarded).Cell);
            Assert.IsInstanceOfType<ScenePick.Miss>(discarded.Pick(Ray with { Origin = new(3.5f, 0.5f, -2) }));
            var settled = await Settled(session, discarded);
            Assert.AreEqual(SceneReadiness.Ready, settled.Readiness);
            if (changeDesign) Assert.AreEqual(oldHit.Cell, Hit(settled).Cell);
            else Assert.IsInstanceOfType<ScenePick.Miss>(settled.Pick(Ray));
            Assert.AreEqual(oldHit, Hit(first));
        }
        finally
        {
            gated.Release.Set();
            await present;
        }
    }

    [TestMethod]
    public async Task DisposeWaitsForBlockedObjectRebuildAndRetainedFrameSurvives()
    {
        var session = Session(Snapshot(), ObjectContent());
        var first = await Ready(session);
        using var gated = new SceneFixtures.BlockedAppearanceView(SceneFixtures.Content(Evaluation));
        session.Observe(Snapshot(1, instance: Instance with
        { Parts = [new(new(2), new(0, 10), false, true)] }, content: gated), []);
        var present = Task.Run(() => Present(session, first));
        try
        {
            await gated.Entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
            var retained = await present.WaitAsync(TimeSpan.FromSeconds(1));
            var closing = session.DisposeAsync();
            Assert.IsFalse(closing.IsCompleted);
            Assert.ThrowsExactly<ObjectDisposedException>(() => session.Present(FrameRequest, retained));
            Assert.ThrowsExactly<ObjectDisposedException>(() => session.Resolve(Hit(retained)));
            gated.Release.Set();
            await closing;
            Assert.AreEqual(Hit(first).Cell, Hit(retained).Cell);
        }
        finally
        {
            gated.Release.Set();
            await present;
            await session.DisposeAsync();
        }
    }


    [TestMethod]
    public async Task GeneratedDecorationAndUnknownMarkerDoNotInterceptTerrainAndFogIsPartial()
    {
        var kinds = new[] { BuiltInMaterial.RailSteel, BuiltInMaterial.Sleeper, BuiltInMaterial.Water };
        var meshes = kinds.Select((kind, i) =>
        {
            var rendered = SceneFixtures.Build(SceneFixtures.Projection([SceneFixtures.Cell(new(0, 0, i * 2))]));
            var mesh = rendered.Objects.SelectMany(item => item.Visuals).OfType<SceneVisual.Geometry>().Single().Mesh;
            return mesh with { Material = new SceneMaterial.BuiltIn(kind) };
        }).ToImmutableArray();
        var reference = new ContentReference.Design(new("unknown-composite"));
        var problem = new SceneProblem("content_missing", SceneStage.Build, null, reference, null, null, "Unknown extent.");
        var generated = Terrain() with
        {
            Terrain = [new(new(0, 0, 6), new(VoxelOrientation.PxPy, SceneFixtures.Cube, null, null, new PickSubject.Terrain(Location), []))],
            BuiltInMeshes = meshes,
            MissingMarkers = [new(new(new(0, 0, 5), Quaternion.Identity), reference, problem)],
            FoggedRegions = [new(new(2, 0, 1), new(3, 1, 2))]
        };
        await using var session = Session(Snapshot(), generated);
        var frame = await Ready(session);
        Assert.AreEqual(SceneReadiness.Partial, frame.Readiness);
        var hit = Hit(frame);
        Assert.AreEqual(new GridPosition(0, 0, 6), hit.Cell);
        Assert.AreEqual(8d, hit.DistanceCells);
        Assert.AreEqual(new PickSubject.Terrain(Location), hit.Subject);
        Assert.IsTrue(frame.Diagnostics.Contains(problem));
        Assert.IsInstanceOfType<ScenePick.NotReady>(frame.Pick(Ray with { Origin = new(2.5f, 0.5f, -2) }));
    }

    [TestMethod]
    public async Task GeneratedTerrainWithoutSubjectFaultsBeforeItCanHideOtherCells()
    {
        await using var session = Session(Snapshot(), Terrain() with
        { Terrain = [new(new(0, 0, 0), new(VoxelOrientation.PxPy, SceneFixtures.Cube, null, null, null, []))] });
        session.RequestView(Target);
        await Assert.ThrowsExactlyAsync<ArgumentException>(async () => await session.PendingBuild!);
        var result = (SceneResult<ISceneFrame>.Faulted)session.Present(FrameRequest, null);
        Assert.AreEqual("scene_internal_fault", result.Problem.Code);
        StringAssert.Contains(result.Problem.Detail, "Terrain cells must have a pick subject.");
    }

    [TestMethod]
    public async Task FollowVehicleMustBelongToRequestedTrain()
    {
        var train = new TrainView(new(1), new TrainPosition.Node(new(1), null, null, NodeTravelPhase.Waiting, 80, null), TrainActivity.Stopped,
            [new(2)], new(1), 0, 0, 0, 0, 0, 0, null, null, 0,
            new(TargetPriority.ArmedFirst, TargetScope.All, EncounterAction.Engage, 0),
            [], null, [], TrainOrderFacing.FirstEndLeading, null);
        var snapshot = Snapshot() with
        {
            Core = new FixedCoreReadView
            {
                InputIdentity = new("scenes-test"),
                Position = new(0, 0),
                Trains = new Dictionary<TrainId, Lookup<TrainView>> { [new(1)] = new Lookup<TrainView>.Found(train) }
            }
        };
        await using var session = Session(snapshot);
        Assert.AreEqual("target_missing", Code(session.RequestView(new WorldSceneTarget.Follow(new(1), new(1)))));
    }

    [TestMethod]
    [DataRow(double.NaN)]
    [DataRow(-1d)]
    public async Task EdgeDistanceMustBeFiniteAndNonnegative(double distance)
    {
        await using var session = Session(Snapshot());
        Assert.ThrowsExactly<ArgumentException>(() =>
            session.RequestView(new WorldSceneTarget.Location(new WorldLocation.Edge(new(1), distance))));
    }

    [TestMethod]
    public async Task PresentReportsTargetMissingAfterObservedTargetRemoval()
    {
        await using var session = Session(Snapshot());
        var frame = await Ready(session);
        var missing = Snapshot(1) with
        {
            Core = new FixedCoreReadView
            {
                InputIdentity = new("scenes-test"),
                Position = new(1, 0),
                Combats = new Dictionary<CombatId, Lookup<CombatView>> { [new(1)] = new Lookup<CombatView>.Missing() }
            }
        };
        session.Observe(missing, []);
        Assert.AreEqual("target_missing", Code(session.Present(FrameRequest, frame)));
    }


}
