using System.Collections.Immutable;
using System.Numerics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes.Tests;

[TestClass]
public sealed class NodeCandidateTests
{
    private static readonly SceneFrameRequest Request = new(1, null, SceneDebugLayer.None);
    private static readonly WorldSceneTarget Follow = new WorldSceneTarget.Follow(new(1), new(1));

    [TestMethod]
    public async Task Null_returns_the_full_candidate_and_its_receipt_enables_a_delta()
    {
        await using var session = Open(Snapshot());
        Assert.IsNull(((SceneResult<SceneUpdate?>.Success)session.PresentCandidate(null)).Value);
        session.RequestView(Follow);
        await Prepare(session);

        var full = Candidate(session, null);
        Assert.IsNull(full.Base);
        Assert.IsTrue(full.Upserts.Any(item => !item.Visuals.IsEmpty));
        AssertVisualOnly(full);
        var delta = Candidate(session, full.Frame);
        Assert.AreEqual(full.Frame, delta.Base);
        Assert.AreEqual(full.Frame.Space, delta.Frame.Space);
        Assert.IsTrue(delta.Upserts.IsEmpty);
        Assert.IsTrue(delta.Removed.IsEmpty);
        AssertVisualOnly(delta);

        var replacement = Candidate(session, null);
        Assert.IsNull(replacement.Base);
        CollectionAssert.AreEquivalent(full.Upserts.Select(item => item.Id).ToArray(),
            replacement.Upserts.Select(item => item.Id).ToArray());
    }

    [TestMethod]
    public async Task An_unacknowledged_update_is_locked_and_candidate_receipts_do_not_advance_active_D()
    {
        await using var session = Open(Snapshot());
        session.RequestView(Follow);
        var displayed = await Prepare(session);
        var offered = Candidate(session, null);
        Present(session, displayed);
        Assert.AreSame(offered, Candidate(session, null));
        Candidate(session, offered.Frame);

        var active = Present(session, displayed);
        Assert.AreEqual(displayed.Update.Frame, active.Update.Base,
            "Preparing a candidate must not acknowledge any active frame.");
        Assert.AreNotEqual(offered.Frame.Space, active.Update.Frame.Space);
    }

    [TestMethod]
    public async Task RequestView_cancels_the_candidate_and_an_old_receipt_cannot_acknowledge_its_replacement()
    {
        await using var session = Open(Snapshot());
        session.RequestView(Follow);
        await Prepare(session);
        var old = Candidate(session, null);
        session.RequestView(Follow);
        Assert.IsNull(((SceneResult<SceneUpdate?>.Success)session.PresentCandidate(old.Frame)).Value);
        await SceneFixtures.AwaitPendingWork(session.PendingPrebuilds);
        await Prepare(session);

        var replacement = Candidate(session, old.Frame);
        Assert.IsNull(replacement.Base);
        Assert.AreNotEqual(old.Frame.Space, replacement.Frame.Space);
        Assert.AreNotEqual(old.Frame.Request, replacement.Frame.Request);
        Assert.IsTrue(replacement.Upserts.Any(item => !item.Visuals.IsEmpty));
    }

    [TestMethod]
    public async Task Promotion_reuses_candidate_space_and_object_ids_with_a_formal_origin_replacement()
    {
        await using var session = Open(Snapshot());
        session.RequestView(Follow);
        var displayed = await Prepare(session);
        var candidate = Candidate(session, null);
        Candidate(session, candidate.Frame);
        var train = Train() with
        { Position = new TrainPosition.Node(new(1), new(1), 1, null, null, NodeTravelPhase.Approaching, 0, null) };
        session.Observe(Snapshot(train, 1), []);
        var promoted = Present(session, displayed);

        Assert.AreEqual(candidate.Frame.Space, promoted.Update.Frame.Space);
        Assert.IsNull(promoted.Update.Base, "An active frame from another space cannot serve as the promotion base.");
        foreach (var prepared in candidate.Upserts)
        {
            var actual = promoted.Update.Upserts.Single(item => item.Id == prepared.Id);
            Assert.AreEqual(prepared.Pose.Position + RailwayCoordinates.Relative(default, promoted.Origin), actual.Pose.Position);
            CollectionAssert.AreEqual(prepared.Visuals.ToArray(), actual.Visuals.ToArray());
        }
        Assert.IsNull(((SceneResult<SceneUpdate?>.Success)session.PresentCandidate(candidate.Frame)).Value);
    }

    [TestMethod]
    public async Task A_failed_candidate_reports_failure_without_faulting_the_active_scene()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = new WorldSceneSession(new(Guid.NewGuid()), Snapshot(), new(), new(),
            (_, _, _, _) =>
            {
                entered.TrySetResult();
                throw new RailwayBuildException("Controlled candidate failure.");
            });
        session.RequestView(Follow);
        var displayed = await RailwayFixtures.Settled(session, Request);
        await entered.Task;
        foreach (var work in session.PendingPrebuilds)
        {
            try { await work; }
            catch (RailwayBuildException) { }
        }
        var active = Present(session, displayed);
        var failed = session.PresentCandidate(null);
        Assert.IsInstanceOfType<SceneResult<SceneUpdate?>.Failure>(failed);
        Assert.IsTrue(((SceneResult<SceneUpdate?>.Failure)failed).Problems.Any(problem => problem.Code == "build_failed"));
        Assert.IsNull(session.PrebuiltLocation);
        active = Present(session, active);
        Assert.AreNotEqual(SceneReadiness.Failed, active.Readiness);
        Assert.IsFalse(active.Diagnostics.Any(problem => problem.Code is "build_failed" or "scene_internal_fault"));
        Assert.IsNull(((SceneResult<SceneUpdate?>.Success)session.PresentCandidate(null)).Value);
    }

    private static TrainView Train() => RailwayFixtures.Train(1, direction: TravelDirection.Reverse, speed: 1);

    private static SceneWorldSnapshot Snapshot(TrainView? train = null, ulong steps = 0)
    {
        train ??= Train();
        return RailwayFixtures.Snapshot(4, 4, 0, steps: steps,
            content: RailwayFixtures.Content(rules: RailwayFixtures.Rules with { MinimumCurveRadiusMeters = 1 },
                design: RailwayFixtures.Vehicle()), trains: [train], instances: [RailwayFixtures.Instance()]);
    }

    private static WorldSceneSession Open(SceneWorldSnapshot snapshot) => new(new(Guid.NewGuid()), snapshot, new(), new(),
        (current, node, previous, token) =>
        {
            var built = RailwayFixtures.EmptyNode(current, node, previous, token);
            var geometry = WorldSceneSession.BuildGeometry(id => new(
                [new(id("marker"), null, new(new(7, 0, 9), Quaternion.Identity), null,
                    [new SceneVisual.Wheelset(1, 1, 1, 0)], [], [])], [], []));
            return built with { Parts = built.Parts.Add("marker", new("fixed", geometry,
                ImmutableDictionary<GridColumn, double>.Empty)) };
        });

    private static async Task<SceneFrame> Prepare(WorldSceneSession session)
    {
        var displayed = await RailwayFixtures.Settled(session, Request);
        Assert.AreEqual(new WorldLocation.Node(new(1)), session.PrebuiltLocation);
        await SceneFixtures.AwaitPendingWork(session.PendingPrebuilds);
        return Present(session, displayed);
    }

    private static SceneFrame Present(WorldSceneSession session, ISceneFrame? displayed) =>
        (SceneFrame)((SceneResult<ISceneFrame>.Success)session.Present(Request, displayed)).Value;

    private static SceneUpdate Candidate(WorldSceneSession session, SceneFrameStamp? prepared)
    {
        var result = session.PresentCandidate(prepared);
        Assert.IsInstanceOfType<SceneResult<SceneUpdate?>.Success>(result);
        var update = ((SceneResult<SceneUpdate?>.Success)result).Value;
        Assert.IsNotNull(update);
        return update;
    }

    private static void AssertVisualOnly(SceneUpdate update)
    {
        Assert.AreEqual(Vector3.Zero, update.ShiftExistingRoots);
        Assert.IsTrue(update.Effects.IsEmpty);
        Assert.IsTrue(update.Environment.FoggedRegions.IsEmpty);
        Assert.IsTrue(update.Environment.TerrainCollision.IsEmpty);
    }
}
