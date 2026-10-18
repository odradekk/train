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
    private static readonly SceneFrameRequest Request = new(1, null, SceneDebugLayer.None);
    private static readonly SceneRay Down = new(new(0.5f, 30, 0.5f), -Vector3.UnitY, 60);

    [TestMethod]
    public void OpenWorldRejectsUnpairedInputAndEditorOpens()
    {
        var factory = ScenesFactory.Create();
        var snapshot = RailwayFixtures.Snapshot();
        var mismatch = snapshot with { Core = new FixedCoreReadView { InputIdentity = new("other") } };
        Assert.ThrowsExactly<ArgumentException>(() => factory.OpenWorld(new(Guid.NewGuid()), mismatch));
        using var editor = ((SceneResult<IDesignEditSession>.Success)factory.OpenEditor(snapshot.Content, SceneFixtures.Evaluation(SceneFixtures.Projection([SceneFixtures.Cell(new(0, 0, 0))])))).Value;
    }

    [TestMethod]
    public async Task CombatAtANodeOpensTheNodeScene()
    {
        var snapshot = WithCombat(NodeFixtures.Snapshot(platforms: 0, trains: [], instances: []), new(1),
            new Lookup<CombatView>.Found(new(new(1), NodeFixtures.Location, 0, [])));
        await using var session = Open(snapshot, RailwayFixtures.EmptyNode);
        Assert.IsInstanceOfType<SceneResult<SceneRequestId>.Success>(session.RequestView(new WorldSceneTarget.Combat(new(1))));
        var frame = Present(session);
        Assert.IsNotNull(frame.NodePanBounds);
        Assert.AreNotEqual(SceneReadiness.Failed, frame.Readiness);
    }

    [TestMethod]
    public async Task CombatAtAnEdgeMatchesTheLocationView()
    {
        var place = new WorldLocation.Edge(RailwayFixtures.Edge, 2);
        var snapshot = WithCombat(RailwayFixtures.Snapshot(4, 4, 0), new(1),
            new Lookup<CombatView>.Found(new(new(1), place, 0, [])));
        await using var combat = Factory(snapshot);
        await using var located = Factory(snapshot);
        combat.RequestView(new WorldSceneTarget.Combat(new(1)));
        located.RequestView(new WorldSceneTarget.Location(place));
        var combatFrame = Present(combat);
        var locatedFrame = Present(located);
        Assert.IsNull(combatFrame.NodePanBounds);
        Assert.AreEqual(locatedFrame.Origin, combatFrame.Origin);
        Assert.AreEqual(locatedFrame.Locate(Vector3.Zero), combatFrame.Locate(Vector3.Zero));
        var combatCamera = Present(combat, combatFrame, new SceneCameraSample(combatFrame, Vector3.Zero));
        var locatedCamera = Present(located, locatedFrame, new SceneCameraSample(locatedFrame, Vector3.Zero));
        Assert.AreEqual(locatedCamera.Origin, combatCamera.Origin);
    }

    [TestMethod]
    public async Task LoadingDoesNotWaitForTheNodeBuildAndDisposeRevokesCallsBeforeWaiting()
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = Open(NodeFixtures.Snapshot(platforms: 0, trains: [], instances: []), (snapshot, id, previous, token) =>
        {
            entered.TrySetResult();
            release.Wait();
            return RailwayFixtures.EmptyNode(snapshot, id, previous, token);
        });
        try
        {
            session.RequestView(new WorldSceneTarget.Location(NodeFixtures.Location));
            await entered.Task;
            var loading = Present(session);
            Assert.AreEqual(SceneReadiness.Loading, loading.Readiness);
            Assert.IsFalse(loading.Update.Environment.FoggedRegions.IsEmpty);
            var box = loading.Update.Environment.FoggedRegions[0];
            var above = new Vector3((box.Minimum.X + box.Maximum.X) / 2, box.Maximum.Y + 1, (box.Minimum.Z + box.Maximum.Z) / 2);
            Assert.IsInstanceOfType<ScenePick.NotReady>(loading.Pick(new(above, -Vector3.UnitY, box.Maximum.Y - box.Minimum.Y + 2)));
            var closing = session.DisposeAsync();
            Assert.IsFalse(closing.IsCompleted);
            Assert.ThrowsExactly<ObjectDisposedException>(() => session.RequestView(new WorldSceneTarget.Location(NodeFixtures.Location)));
            Assert.ThrowsExactly<ObjectDisposedException>(() => session.Present(Request, null));
            Assert.IsInstanceOfType<ScenePick.NotReady>(loading.Pick(new(above, -Vector3.UnitY, box.Maximum.Y - box.Minimum.Y + 2)));
            release.Set();
            await closing;
        }
        finally
        {
            release.Set();
            await session.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task RealSessionPassesSharedOldRequestScenarioIncludingSameTarget()
    {
        var target = new WorldSceneTarget.Location(new WorldLocation.Edge(RailwayFixtures.Edge, 0));
        await using var session = Open(RailwayFixtures.Snapshot(4, 4, 0));
        session.RequestView(target);
        var frame = Present(session);
        WorldRequestScenario.AssertOldHitExpires(session, TerrainHit(frame, new WorldLocation.Edge(RailwayFixtures.Edge, 0)), target);
        var next = Present(session, frame);
        Assert.IsNull(next.Update.Base);
        Assert.AreNotEqual(frame.Update.Frame.Space, next.Update.Frame.Space);
    }

    [TestMethod]
    public async Task InvalidTargetLeavesCurrentRequestAndFrameUsable()
    {
        var basis = RailwayFixtures.Snapshot(4, 4, 0);
        await using var session = Open(basis with { Core = new CoreOverlay(basis.Core, missingNode: new(99)) });
        var target = new WorldSceneTarget.Location(new WorldLocation.Edge(RailwayFixtures.Edge, 0));
        session.RequestView(target);
        var frame = Present(session);
        var hit = TerrainHit(frame, new WorldLocation.Edge(RailwayFixtures.Edge, 0));
        Assert.AreEqual("target_missing", Code(session.RequestView(new WorldSceneTarget.Location(new WorldLocation.Node(new(99))))));
        Assert.IsInstanceOfType<SceneResult<PickSubject>.Success>(session.Resolve(hit));
        Assert.AreEqual(frame.Update.Frame.Request, Present(session, frame).Update.Frame.Request);
    }

    [TestMethod]
    public async Task ObserveRejectsInvalidBoundariesAndEventOrderWithoutChangingState()
    {
        var content = RailwayFixtures.Content();
        await using var session = Open(Line(content, 2, 1));
        session.RequestView(new WorldSceneTarget.Location(new WorldLocation.Edge(RailwayFixtures.Edge, 0)));
        var frame = Present(session);
        Assert.ThrowsExactly<ArgumentException>(() => session.Observe(Line(content, 1), []));
        Assert.ThrowsExactly<ArgumentException>(() => session.Observe(Line(content, 2, 2), default));
        var item = new EventEnvelope(new(2, 2), 0, new CoreEvent.TrainArrived(new(1), new(1)));
        Assert.ThrowsExactly<ArgumentException>(() => session.Observe(Line(content, 2, 2), [item, item]));
        Assert.ThrowsExactly<ArgumentException>(() => session.Observe(Line(content, 2, 1), [item]));
        Assert.AreEqual(frame.SimulationSeconds, Present(session).SimulationSeconds);
        session.Observe(Line(content, 2, 2), [item]);
        Assert.ThrowsExactly<ArgumentException>(() => session.Observe(Line(content, 2, 3), [item]));
        session.Observe(Line(content, 3), []);
        Assert.AreEqual(3 * SimulationTime.StepSeconds, Present(session).SimulationSeconds);
    }

    [TestMethod]
    public async Task ObserveRejectsContentGenerationAndBusinessIdentityChanges()
    {
        var content = RailwayFixtures.Content();
        await using var session = Open(Line(content));
        session.RequestView(new WorldSceneTarget.Location(new WorldLocation.Edge(RailwayFixtures.Edge, 0)));
        Present(session);
        Assert.ThrowsExactly<ArgumentException>(() => session.Observe(Line(RailwayFixtures.Content(generation: 2)), []));
        var changed = new FixedContentView { Generation = new(1), Core = SceneFixtures.Content().Core with { Identity = new("other") } };
        Assert.ThrowsExactly<ArgumentException>(() => session.Observe(Line(changed), []));
    }

    [TestMethod]
    public async Task ForeignFramesAndInvalidRequestsAreRejectedBeforeFrameMutation()
    {
        await using var session = Open(RailwayFixtures.Snapshot(4, 4, 0));
        await using var other = Open(RailwayFixtures.Snapshot(4, 4, 0));
        var target = new WorldSceneTarget.Location(new WorldLocation.Edge(RailwayFixtures.Edge, 0));
        session.RequestView(target);
        other.RequestView(target);
        var frame = Present(session);
        var foreign = Present(other);
        Assert.ThrowsExactly<ArgumentException>(() => session.Present(Request, foreign));
        Assert.ThrowsExactly<ArgumentException>(() => session.Present(Request with { Interpolation = double.NaN }, frame));
        Assert.AreEqual(frame.Update.Frame, Present(session, frame).Update.Base);
    }

    [TestMethod]
    public async Task DisplayedFrameIsTheDeltaBaseAndANullDisplayRequestsAFullFrame()
    {
        await using var session = Open(RailwayFixtures.Snapshot(4, 4, 0));
        session.RequestView(new WorldSceneTarget.Location(new WorldLocation.Edge(RailwayFixtures.Edge, 0)));
        var frame = Present(session);
        var again = Present(session, frame);
        Assert.AreEqual(frame.Update.Frame, again.Update.Base);
        Assert.IsNull(Present(session).Update.Base);
    }

    [TestMethod]
    public async Task FollowVehicleMustBelongToRequestedTrain()
    {
        var snapshot = RailwayFixtures.Snapshot(4, 4, 0, trains: [RailwayFixtures.Train(1)], instances: [RailwayFixtures.Instance()]);
        await using var session = Open(snapshot);
        Assert.AreEqual("target_missing", Code(session.RequestView(new WorldSceneTarget.Follow(new(1), new(2)))));
    }

    [TestMethod]
    [DataRow(double.NaN)]
    [DataRow(-1d)]
    public async Task EdgeDistanceMustBeFiniteAndNonnegative(double distance)
    {
        await using var session = Open(RailwayFixtures.Snapshot(4, 4, 0));
        Assert.ThrowsExactly<ArgumentException>(() =>
            session.RequestView(new WorldSceneTarget.Location(new WorldLocation.Edge(RailwayFixtures.Edge, distance))));
    }

    [TestMethod]
    public async Task PresentReportsTargetMissingAfterTheCombatDisappears()
    {
        var content = RailwayFixtures.Content();
        var place = new WorldLocation.Edge(RailwayFixtures.Edge, 1);
        await using var session = Open(WithCombat(Line(content), new(1), new Lookup<CombatView>.Found(new(new(1), place, 0, []))));
        session.RequestView(new WorldSceneTarget.Combat(new(1)));
        Present(session);
        session.Observe(WithCombat(Line(content, 1), new(1), new Lookup<CombatView>.Missing()), []);
        Assert.AreEqual("target_missing", Code(session.Present(Request, null)));
    }

    [TestMethod]
    public async Task RemovedVehicleExpiresItsHitWhileTheDisplayedFrameKeepsPicking()
    {
        var content = RailwayFixtures.Content(design: RailwayFixtures.Vehicle());
        var instance = RailwayFixtures.Instance();
        var session = Open(RailwayFixtures.Snapshot(4, 4, 0, content: content,
            trains: [RailwayFixtures.Train(1)], instances: [instance]));
        try
        {
            var place = new WorldLocation.Edge(RailwayFixtures.Edge, 0);
            session.RequestView(new WorldSceneTarget.Location(place));
            var frame = await RailwayFixtures.Settled(session, Request);
            var terrain = (ScenePick.Hit)frame.Pick(Down);
            Assert.IsInstanceOfType<SceneResult<PickSubject>.Success>(session.Resolve(terrain.Value));
            var body = frame.Objects.Single(item => item.Parent is null && item.Subject == new WorldObjectId.Instance(instance.Id));
            var hit = new SceneHit(frame.Update.Frame, body.Id,
                new PickSubject.WorldObject(new WorldObjectId.Instance(instance.Id), instance.Design, null),
                new(0, 0, 0), GridFace.PositiveY, Vector3.Zero, Vector3.UnitY, 1, []);
            Assert.IsInstanceOfType<SceneResult<PickSubject>.Success>(session.Resolve(hit));
            session.Observe(RailwayFixtures.Snapshot(4, 4, 0, steps: 1, content: content,
                trains: [RailwayFixtures.Train(1, [])], instances: []), []);
            var removed = await RailwayFixtures.Settled(session, Request, frame);
            Assert.IsTrue(removed.Update.Removed.Contains(body.Id));
            Assert.AreEqual("stale_hit", Code(session.Resolve(hit)));
            Assert.AreEqual(terrain.Value, ((ScenePick.Hit)frame.Pick(Down)).Value);
            await session.DisposeAsync();
            Assert.AreEqual(terrain.Value, ((ScenePick.Hit)frame.Pick(Down)).Value);
            Assert.ThrowsExactly<ObjectDisposedException>(() => session.Present(Request, frame));
            Assert.ThrowsExactly<ObjectDisposedException>(() => session.Resolve(hit));
        }
        finally { await session.DisposeAsync(); }
    }

    [TestMethod]
    public async Task DesignChangeAndDestroyedPartExpireTheResolvedHit()
    {
        var content = RailwayFixtures.Content(design: RailwayFixtures.Vehicle());
        var instance = RailwayFixtures.Instance();
        await using var session = Open(RailwayFixtures.Snapshot(4, 4, 0, content: content,
            trains: [RailwayFixtures.Train(1)], instances: [instance]));
        session.RequestView(new WorldSceneTarget.Follow(new(1), new(1)));
        var frame = Present(session);
        var subject = new PickSubject.WorldObject(new WorldObjectId.Instance(instance.Id), instance.Design, null);
        var hit = new SceneHit(frame.Update.Frame, null, subject, new(0, 0, 0), GridFace.PositiveY, Vector3.Zero, Vector3.UnitY, 1, []);
        Assert.IsInstanceOfType<SceneResult<PickSubject>.Success>(session.Resolve(hit));
        var destroyed = instance with { Parts = [new(new(1), new(0, 100), false, true)] };
        session.Observe(RailwayFixtures.Snapshot(4, 4, 0, steps: 1, content: content,
            trains: [RailwayFixtures.Train(1)], instances: [destroyed]), []);
        Assert.AreEqual("stale_hit", Code(session.Resolve(hit with { Subject = subject with { Part = new(1) } })));
        session.Observe(RailwayFixtures.Snapshot(4, 4, 0, steps: 1, sequence: 1, content: content,
            trains: [RailwayFixtures.Train(1)], instances: [destroyed with { Design = new("other") }]), []);
        Assert.AreEqual("stale_hit", Code(session.Resolve(hit)));
    }

    [TestMethod]
    public async Task DestroyedCompositeGetsANewObjectIdWhenRepaired()
    {
        var evaluation = RailwayFixtures.Vehicle() with
        {
            Scene = SceneFixtures.Projection(
                [SceneFixtures.Cell(new(0, 0, 1)), SceneFixtures.Cell(new(0, 0, 0), 9, path: [0])],
                [new([0], new(0, 0, 0), VoxelOrientation.PxPy,
                    new NestedObjectDetails.FunctionalComposite(new("test:part"), new(9), null))])
        };
        var content = RailwayFixtures.Content(design: evaluation);
        var healthy = RailwayFixtures.Instance() with { Parts = [new(new(9), new(10, 10), false, false)] };
        var subject = new WorldObjectId.Instance(healthy.Id);
        await using var session = Open(RailwayFixtures.Snapshot(4, 4, 0, content: content,
            trains: [RailwayFixtures.Train(1)], instances: [healthy]));
        session.RequestView(new WorldSceneTarget.Follow(new(1), new(1)));
        var first = await RailwayFixtures.Settled(session, Request);
        var root = first.Objects.Single(item => item.Parent is null && item.Subject == subject);
        var nested = first.Objects.Single(item => item.Parent == root.Id);
        session.Observe(RailwayFixtures.Snapshot(4, 4, 0, steps: 1, content: content, trains: [RailwayFixtures.Train(1)],
            instances: [healthy with { Parts = [new(new(9), new(0, 10), false, true)] }]), []);
        var destroyed = await RailwayFixtures.Settled(session, Request, first);
        Assert.IsTrue(destroyed.Update.Removed.Contains(nested.Id));
        session.Observe(RailwayFixtures.Snapshot(4, 4, 0, steps: 2, content: content,
            trains: [RailwayFixtures.Train(1)], instances: [healthy]), []);
        var repaired = await RailwayFixtures.Settled(session, Request, destroyed);
        var restored = repaired.Objects.Single(item => item.Parent == repaired.Objects.Single(candidate =>
            candidate.Parent is null && candidate.Subject == subject).Id);
        Assert.AreNotEqual(nested.Id, restored.Id);
        var expired = new SceneHit(repaired.Update.Frame, nested.Id,
            new PickSubject.WorldObject(subject, healthy.Design, new(9)),
            new(0, 0, 0), GridFace.PositiveY, Vector3.Zero, Vector3.UnitY, 1, []);
        Assert.AreEqual("stale_hit", Code(session.Resolve(expired)));
        Assert.IsInstanceOfType<SceneResult<PickSubject>.Success>(session.Resolve(expired with { Object = restored.Id }));
    }

    private static SceneWorldSnapshot Line(IContentView content, ulong steps = 0, ulong sequence = 0) =>
        RailwayFixtures.Snapshot(4, 4, 0, steps: steps, sequence: sequence, content: content);

    private static SceneWorldSnapshot WithCombat(SceneWorldSnapshot snapshot, CombatId id, Lookup<CombatView> combat) =>
        snapshot with { Core = new CoreOverlay(snapshot.Core, new Dictionary<CombatId, Lookup<CombatView>> { [id] = combat }) };

    private static WorldSceneSession Open(SceneWorldSnapshot snapshot,
        Func<SceneWorldSnapshot, NodeId, NodeScene.Cached?, CancellationToken, NodeScene.Built>? build = null) =>
        new(new(Guid.NewGuid()), snapshot, new(), new(), build);

    private static IWorldSceneSession Factory(SceneWorldSnapshot snapshot) =>
        ((SceneResult<IWorldSceneSession>.Success)ScenesFactory.Create().OpenWorld(new(Guid.NewGuid()), snapshot)).Value;

    private static SceneFrame Present(IWorldSceneSession session, ISceneFrame? displayed = null, SceneCameraSample? camera = null) =>
        (SceneFrame)((SceneResult<ISceneFrame>.Success)session.Present(camera is null ? Request : Request with { Camera = camera }, displayed)).Value;

    private static SceneHit TerrainHit(SceneFrame frame, WorldLocation location) =>
        new(frame.Update.Frame, null, new PickSubject.Terrain(location), new(0, 0, 0), GridFace.PositiveY, Vector3.Zero, Vector3.UnitY, 1, []);

    private static string Code<T>(SceneResult<T> result) where T : notnull => ((SceneResult<T>.Failure)result).Problems[0].Code;

    private sealed class CoreOverlay(ICoreReadView inner, IReadOnlyDictionary<CombatId, Lookup<CombatView>>? combats = null, NodeId? missingNode = null) : ICoreReadView
    {
        public Lookup<CombatView> FindCombat(CombatId id) =>
            combats is not null && combats.TryGetValue(id, out var combat) ? combat : inner.FindCombat(id);
        public Lookup<NodeView> FindNode(NodeId id) =>
            missingNode is { } missing && missing == id ? new Lookup<NodeView>.Missing() : inner.FindNode(id);
        public SimulationPosition Position => inner.Position;
        public CoreRevision Revision => inner.Revision;
        public CoreInputId InputIdentity => inner.InputIdentity;
        public CoreFault? Fault => inner.Fault;
        public WorldSummary World => inner.World;
        public EconomyView Economy => inner.Economy;
        public Lookup<EdgeView> FindEdge(EdgeId id) => inner.FindEdge(id);
        public Lookup<LocationView> FindLocation(WorldLocation location) => inner.FindLocation(location);
        public Lookup<TrainView> FindTrain(TrainId id) => inner.FindTrain(id);
        public Lookup<InstanceView> FindInstance(InstanceId id) => inner.FindInstance(id);
        public Lookup<SettlementView> FindSettlement(SettlementId id) => inner.FindSettlement(id);
        public Lookup<InventoryView> FindInventory(InventoryId id) => inner.FindInventory(id);
        public Lookup<LeaseView> FindLease(LeaseId id) => inner.FindLease(id);
        public Lookup<FacilityView> FindFacility(FacilityKey key) => inner.FindFacility(key);
        public Lookup<JobView> FindJob(JobId id) => inner.FindJob(id);
        public Lookup<CreatureGroupView> FindCreatureGroup(CreatureGroupId id) => inner.FindCreatureGroup(id);
        public Lookup<CreatureView> FindCreature(CreatureId id) => inner.FindCreature(id);
        public Lookup<LairView> FindLair(LairId id) => inner.FindLair(id);
        public CoreResult<TradeQuote> QuoteTrade(TradeQuoteRequest request) => inner.QuoteTrade(request);
        public CoreResult<VehicleTradeQuote> QuoteVehicle(VehicleTradeQuoteRequest request) => inner.QuoteVehicle(request);
        public CoreResult<LeaseQuote> QuoteLease(LeaseRequest request) => inner.QuoteLease(request);
        public CoreResult<JobQuote> GetJobQuote(JobRequest request, JobFunding funding) => inner.GetJobQuote(request, funding);
        public CoreResult<SettlementFoundingPreview> PreviewSettlement(NodeId node) => inner.PreviewSettlement(node);
    }
}
