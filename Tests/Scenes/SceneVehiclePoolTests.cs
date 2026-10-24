using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Scenes.Contracts;
using Train.Content.Contracts;
using Train.Core.Contracts;
using System.Numerics;

namespace Train.Scenes.Tests;

[TestClass]
public sealed class SceneVehiclePoolTests
{
    private static SceneObjectId Id(string _) => new(1);

    [TestMethod]
    public async Task ClosingRailwayCandidateDoesNotWaitForSharedVehicleBuild()
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var vehicles = new SceneVehicles(build: (content, instance) =>
        {
            entered.SetResult();
            release.Wait();
            return RailwayVehicles.Build(content, instance);
        });
        var snapshot = RailwayFixtures.Snapshot(content: RailwayFixtures.Content(design: RailwayFixtures.Vehicle()));
        var scene = RailwayFixtures.Controlled(snapshot, vehicles);
        Task? closing = null;
        try
        {
            vehicles.Reconcile(snapshot.Content, RailwayFixtures.Instance(), Id);
            await entered.Task;
            closing = scene.DisposeAsync().AsTask();
            Assert.IsTrue(closing.IsCompletedSuccessfully, "A candidate with no segment work must close while the shared body build is blocked.");
            Assert.IsFalse(vehicles.PendingWork.Single().IsCompleted);
            await closing;
        }
        finally
        {
            release.Set();
            await (closing ?? scene.DisposeAsync().AsTask());
        }
    }

    [TestMethod]
    public async Task LateOldBindingBuildCannotRepopulateTheRetainedCache()
    {
        var cache = new SceneVehicles.Cache();
        using var release = new ManualResetEventSlim();
        await using var old = new SceneVehicles(cache, (content, instance) =>
        {
            release.Wait();
            return RailwayVehicles.Build(content, instance);
        });
        var content = RailwayFixtures.Content(design: RailwayFixtures.Vehicle());
        var instance = RailwayFixtures.Instance();
        old.Reconcile(content, instance, Id);
        var builds = 0;
        await using var current = new SceneVehicles(cache, (view, member) =>
        {
            builds++;
            return RailwayVehicles.Build(view, member);
        });
        current.Retain([instance.Id]);
        release.Set();
        await SceneFixtures.AwaitPendingWork(old.PendingWork);
        old.Reconcile(content, instance, Id);
        Assert.IsNull(current.Reconcile(content, instance, Id));
        await SceneFixtures.AwaitPendingWork(current.PendingWork);
        Assert.AreEqual(1, builds, "A late old binding must not publish a body into the new binding cache.");
    }

    [TestMethod]
    public async Task PartDurabilityChangePublishesANewBodyAndTheFrameReturnsToReady()
    {
        var content = RailwayFixtures.Content(design: RailwayFixtures.Vehicle());
        var healthy = RailwayFixtures.Instance() with { Parts = [new(new(1), new(100, 100), false, false)] };
        var damaged = healthy with { Parts = [new(new(1), new(40, 100), false, false)] };
        await using var vehicles = new SceneVehicles();
        Assert.IsNull(vehicles.Reconcile(content, healthy, Id));
        await SceneFixtures.AwaitPendingWork(vehicles.PendingWork);
        var first = vehicles.Reconcile(content, healthy, Id);
        Assert.IsNotNull(first);
        Assert.IsNull(vehicles.Reconcile(content, damaged, Id));
        await SceneFixtures.AwaitPendingWork(vehicles.PendingWork);
        var second = vehicles.Reconcile(content, damaged, Id);
        Assert.IsNotNull(second);
        Assert.AreNotSame(first, second);

        await using var session = new WorldSceneSession(new(Guid.NewGuid()),
            RailwayFixtures.Snapshot(4, 4, 0, content: content, trains: [RailwayFixtures.Train(0)], instances: [healthy]), new(), new());
        session.RequestView(new WorldSceneTarget.Location(RailwayFixtures.Location));
        var before = await RailwayFixtures.Settled(session, new(1, null, SceneDebugLayer.None));
        Assert.AreEqual(SceneReadiness.Ready, before.Readiness);
        session.Observe(RailwayFixtures.Snapshot(4, 4, 0, steps: 1, content: content,
            trains: [RailwayFixtures.Train(0)], instances: [damaged]), []);
        var after = await RailwayFixtures.Settled(session, new(1, null, SceneDebugLayer.None), before);
        Assert.AreEqual(SceneReadiness.Ready, after.Readiness);
        Assert.IsTrue(after.Objects.Any(item => item.Subject == new WorldObjectId.Instance(damaged.Id) &&
            item.Visuals.OfType<SceneVisual.Geometry>().Any()));
    }

    [TestMethod]
    public void UnknownLengthForwardsTheDesignDiagnosticSource()
    {
        var instance = RailwayFixtures.Instance();
        var diagnostic = new ContentDiagnostic("vehicle_warning", ContentDiagnosticSeverity.Warning, "evaluate",
            new ContentReference.Design(instance.Design), new(new PackageId("vehicles"), "vehicles.json"), "design", null, "Vehicle warning.");
        var evaluation = RailwayFixtures.Vehicle() with { Core = null, Diagnostics = [diagnostic] };
        var geometry = SceneVehicles.Unplaced(RailwayFixtures.Content(design: evaluation), instance,
            new ContentLookup<DesignEvaluation>.Found(evaluation), null, new(Vector3.Zero, Quaternion.Identity), Id);
        var warning = geometry.Diagnostics.Single(problem => problem.Code == "vehicle_warning");
        var unknown = geometry.Diagnostics.Single(problem => problem.Code == "vehicle_length_unknown");
        Assert.AreEqual(warning.Package, unknown.Package);
        Assert.AreEqual(warning.Element, unknown.Element);
        Assert.IsNotNull(unknown.Package);
    }

    [TestMethod]
    public async Task EquivalentDesignValuesReuseTheBodyAcrossContentBindings()
    {
        var cache = new SceneVehicles.Cache();
        var instance = RailwayFixtures.Instance();
        var diagnostic = new ContentDiagnostic("vehicle_warning", ContentDiagnosticSeverity.Warning, "evaluate",
            new ContentReference.Design(instance.Design), new(new PackageId("vehicles"), "vehicles.json"), "design", null, "Vehicle warning.");
        var original = RailwayFixtures.Vehicle() with { Diagnostics = [diagnostic] };
        var equivalent = RailwayFixtures.Vehicle() with { Diagnostics = [diagnostic with { }] };
        Assert.AreNotSame(original, equivalent);
        Assert.AreNotSame(original.Scene, equivalent.Scene);
        BuiltObject? ready;
        await using (var first = new SceneVehicles(cache))
        {
            var content = RailwayFixtures.Content(design: original);
            Assert.IsNull(first.Prepare(content, instance));
            await SceneFixtures.AwaitPendingWork(first.PendingWork);
            ready = first.Prepare(content, instance);
            Assert.IsNotNull(ready);
        }
        await using var rebound = new SceneVehicles(cache);
        var reboundContent = RailwayFixtures.Content(design: equivalent, generation: 2);
        Assert.IsNull(rebound.Prepare(reboundContent, instance));
        await SceneFixtures.AwaitPendingWork(rebound.PendingWork);
        var reused = rebound.Prepare(reboundContent, instance);
        Assert.AreSame(ready, reused);
        Assert.AreEqual(diagnostic.Message, reused!.Geometry.Geometry.Diagnostics.Single(problem => problem.Code == diagnostic.Code).Detail);
    }

    [TestMethod]
    public async Task BuildsAcrossScenesShareTheConcurrencyLimit()
    {
        using var release = new ManualResetEventSlim();
        await using var pool = new SceneVehicles(build: (content, instance) =>
        {
            release.Wait();
            return RailwayVehicles.Build(content, instance);
        });
        var content = RailwayFixtures.Content(design: RailwayFixtures.Vehicle());
        try
        {
            for (ulong id = 1; id <= (ulong)RailwayCoordinates.MaxConcurrentBuilds + 3; id++)
                Assert.IsNull(pool.Reconcile(content, RailwayFixtures.Instance(id), Id));
            Assert.AreEqual(RailwayCoordinates.MaxConcurrentBuilds, pool.PendingWork.Length);
        }
        finally { release.Set(); }
        await SceneFixtures.AwaitPendingWork(pool.PendingWork);
    }

    [TestMethod]
    public async Task ChangedProjectionWithTheSameDesignHashRebuildsTheBody()
    {
        var cache = new SceneVehicles.Cache();
        var instance = RailwayFixtures.Instance();
        var original = RailwayFixtures.Vehicle();
        var changed = RailwayFixtures.Vehicle(cells: [new(0, 0, 0), new(1, 0, 7)]);
        Assert.AreEqual(original.Design, changed.Design);
        BuiltObject? ready;
        await using (var first = new SceneVehicles(cache))
        {
            var content = RailwayFixtures.Content(design: original);
            first.Prepare(content, instance);
            await SceneFixtures.AwaitPendingWork(first.PendingWork);
            ready = first.Prepare(content, instance);
            Assert.IsNotNull(ready);
        }
        await using var rebound = new SceneVehicles(cache);
        var changedContent = RailwayFixtures.Content(design: changed);
        Assert.IsNull(rebound.Prepare(changedContent, instance));
        await SceneFixtures.AwaitPendingWork(rebound.PendingWork);
        var rebuilt = rebound.Prepare(changedContent, instance);
        Assert.IsNotNull(rebuilt);
        Assert.AreNotSame(ready, rebuilt);
        Assert.IsNotNull(rebuilt.Geometry.Geometry.Picking.Single().Grid.Find(new(1, 0, 7)));
        Assert.IsNull(rebuilt.Geometry.Geometry.Picking.Single().Grid.Find(new(0, 0, 7)));
        Assert.IsNotNull(ready!.Geometry.Geometry.Picking.Single().Grid.Find(new(0, 0, 7)));
    }

    [TestMethod]
    public async Task ChangedVoxelAppearanceRebuildsTheBodyWithTheSameEvaluation()
    {
        var cache = new SceneVehicles.Cache();
        var instance = RailwayFixtures.Instance();
        var evaluation = RailwayFixtures.Vehicle();
        var content = RailwayFixtures.Content(design: evaluation);
        BuiltObject? ready;
        await using (var first = new SceneVehicles(cache))
        {
            first.Prepare(content, instance);
            await SceneFixtures.AwaitPendingWork(first.PendingWork);
            ready = first.Prepare(content, instance);
            Assert.IsNotNull(ready);
        }
        await using var rebound = new SceneVehicles(cache);
        var changedContent = RailwayFixtures.Content(design: evaluation, wheelsetMarkers: true);
        Assert.AreSame(((ContentLookup<DesignEvaluation>.Found)content.FindDesign(instance.Design)).Value,
            ((ContentLookup<DesignEvaluation>.Found)changedContent.FindDesign(instance.Design)).Value);
        Assert.IsNull(rebound.Prepare(changedContent, instance));
        await SceneFixtures.AwaitPendingWork(rebound.PendingWork);
        var rebuilt = rebound.Prepare(changedContent, instance);
        Assert.IsNotNull(rebuilt);
        Assert.AreNotSame(ready, rebuilt);
        Assert.IsFalse(ready!.Geometry.Geometry.Objects.SelectMany(item => item.Visuals).OfType<SceneVisual.Wheelset>().Any());
        Assert.AreEqual(2, rebuilt.Geometry.Geometry.Objects.SelectMany(item => item.Visuals).OfType<SceneVisual.Wheelset>().Count());
    }

    [TestMethod]
    public async Task InFlightBodyThatNoLongerMatchesTheCurrentInstanceIsDiscarded()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var builds = 0;
        await using var pool = new SceneVehicles(build: (content, instance) =>
        {
            if (Interlocked.Increment(ref builds) == 1)
            {
                entered.Set();
                release.Wait();
            }
            return RailwayVehicles.Build(content, instance);
        });
        var first = RailwayFixtures.Vehicle();
        var second = RailwayFixtures.Vehicle(lengthCells: 16, design: new("other"));
        var content = RailwayFixtures.Content(designs: new Dictionary<DesignHash, ContentLookup<DesignEvaluation>>
        {
            [first.Design] = new ContentLookup<DesignEvaluation>.Found(first),
            [second.Design] = new ContentLookup<DesignEvaluation>.Found(second)
        });
        var instance = RailwayFixtures.Instance();
        try
        {
            Assert.IsNull(pool.Reconcile(content, instance, Id));
            Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(2)));
            var updated = instance with { Design = second.Design };
            Assert.IsNull(pool.Reconcile(content, updated, Id));
            release.Set();
            await SceneFixtures.AwaitPendingWork(pool.PendingWork);
            Assert.IsNull(pool.Reconcile(content, updated, Id));
            await SceneFixtures.AwaitPendingWork(pool.PendingWork);
            Assert.IsNotNull(pool.Reconcile(content, updated, Id));
            Assert.AreEqual(2, builds);
        }
        finally { release.Set(); }
    }
}

