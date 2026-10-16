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
    public async Task ReadyBodiesRemainAvailableAcrossSceneAndContentBindings()
    {
        var cache = new SceneVehicles.Cache();
        var content = RailwayFixtures.Content(design: RailwayFixtures.Vehicle());
        var instance = RailwayFixtures.Instance();
        await using (var first = new SceneVehicles(cache))
        {
            Assert.IsNull(first.Reconcile(content, instance, Id));
            await SceneFixtures.AwaitPendingWork(first.PendingWork);
            var ready = first.Reconcile(content, instance, Id);
            Assert.IsNotNull(ready);
            Assert.IsNotNull(first.Reconcile(content, instance, Id));
            Assert.AreEqual(0, first.PendingWork.Length);
        }
        await using var rebound = new SceneVehicles(cache, (_, _) => throw new AssertFailedException("Equal content must reuse the built body."));
        var reboundContent = RailwayFixtures.Content(design: RailwayFixtures.Vehicle(), generation: 2);
        Assert.IsNull(rebound.Reconcile(reboundContent, instance, Id));
        await SceneFixtures.AwaitPendingWork(rebound.PendingWork);
        Assert.IsNotNull(rebound.Reconcile(reboundContent, instance, Id));
        Assert.AreEqual(0, rebound.PendingWork.Length);
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
    public async Task ChangedContentDoesNotReuseTheOldBindingBody()
    {
        var cache = new SceneVehicles.Cache();
        var instance = RailwayFixtures.Instance();
        await using (var first = new SceneVehicles(cache))
        {
            first.Reconcile(RailwayFixtures.Content(design: RailwayFixtures.Vehicle(lengthCells: 8)), instance, Id);
            await SceneFixtures.AwaitPendingWork(first.PendingWork);
            Assert.IsNotNull(first.Reconcile(RailwayFixtures.Content(design: RailwayFixtures.Vehicle(lengthCells: 8)), instance, Id));
        }
        var builds = 0;
        await using var rebound = new SceneVehicles(cache, (view, member) =>
        {
            builds++;
            return RailwayVehicles.Build(view, member);
        });
        var changed = RailwayFixtures.Content(design: RailwayFixtures.Vehicle(lengthCells: 16));
        Assert.IsNull(rebound.Reconcile(changed, instance, Id));
        await SceneFixtures.AwaitPendingWork(rebound.PendingWork);
        Assert.IsNotNull(rebound.Reconcile(changed, instance, Id));
        Assert.AreEqual(1, builds, "A changed projection must rebuild even when the design hash remains the same.");
    }
}

