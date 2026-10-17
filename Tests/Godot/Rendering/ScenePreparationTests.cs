using System.Collections.Immutable;
using System.Diagnostics;
using System.Numerics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Rendering;
using Train.Scenes.Contracts;

namespace Train.Godot.Tests.Rendering;

[TestClass]
public sealed class ScenePreparationTests
{
    [TestMethod]
    public void LatestCandidateMergesUntilOriginalWindowEndsThenTakeStartsANewWindow()
    {
        var preparation = Create();
        var first = Frame(1);
        var latest = Frame(2);
        Assert.AreSame(first, preparation.Select(first, 0));
        Assert.AreSame(latest, preparation.Select(latest, Ticks(.4)));
        Assert.AreSame(latest, preparation.Select(Frame(3), Ticks(.5)));
        Assert.AreSame(latest, preparation.Select(Frame(4), Ticks(1)));
        preparation.Take();
        var next = Frame(5);
        Assert.AreSame(next, preparation.Select(next, Ticks(1)));
        var replacement = Frame(6);
        Assert.AreSame(replacement, preparation.Select(replacement, Ticks(1.4)));
        preparation.Cancel();
    }

    [TestMethod]
    public void ContinuousNewResourcesCannotPreventFiniteTargetCompletion()
    {
        var preparation = Create();
        var visual = Visual();
        ISceneFrame? committed = null;
        var produced = new List<ISceneFrame>();
        var created = new List<FakeVisual>();
        for (var round = 0; round < 40; round++)
        {
            // Producer keeps adding work; the simulated frame budget admits one resource.
            var incoming = Frame((ulong)round + 1, Enumerable.Range(1, round + 3).Select(id => Object((ulong)id, visual)).ToArray());
            produced.Add(incoming);
            var selected = preparation.Select(incoming, Ticks(round * .1));
            var sources = selected.Update.Upserts.ToDictionary(item => item.Id);
            preparation.Retain(sources, (_, _, _) => false);
            foreach (var item in sources.Values)
            {
                if (preparation.TryGet((item.Id, 0), out _)) continue;
                preparation.GetOrCreate((item.Id, 0), () => { var value = new FakeVisual(visual); created.Add(value); return value; });
                break;
            }
            if (!sources.Values.All(item => preparation.TryGet((item.Id, 0), out _))) continue;
            committed = selected;
            Assert.IsTrue(selected.Update.Upserts.Length < incoming.Update.Upserts.Length,
                "Completion must occur while the producer still adds unprepared objects.");
            var transferred = preparation.Take();
            Assert.AreEqual(selected.Update.Upserts.Length, transferred.Count);
            preparation.Cancel();
            Assert.IsTrue(transferred.All(item => item.Disposals == 0));
            foreach (var item in transferred) item.Dispose();
            break;
        }
        Assert.IsNotNull(committed, "A finite candidate must complete despite continuous arrivals.");
        Assert.IsTrue(produced.Count > 1);
        Assert.IsTrue(created.All(item => item.Disposals == 1));
    }

    [TestMethod]
    public void RetainReusesCompatibleResourcesAndDisposesRemovedReplacedOrDisplayedResourcesOnce()
    {
        var preparation = Create();
        var visual = Visual();
        var kept = preparation.GetOrCreate((new(1), 0), () => new FakeVisual(visual));
        var removed = preparation.GetOrCreate((new(2), 0), () => new FakeVisual(visual));
        var replaced = preparation.GetOrCreate((new(3), 0), () => new FakeVisual(visual));
        var displayed = preparation.GetOrCreate((new(4), 0), () => new FakeVisual(visual));
        var shortened = preparation.GetOrCreate((new(5), 1), () => new FakeVisual(visual));
        var sources = new[] { Object(1, visual with { Surface = new(false, 0, .5f, false) }),
            Object(3, Visual()), Object(4, visual), Object(5, visual) }.ToDictionary(item => item.Id);
        preparation.Retain(sources, (id, _, _) => id == new SceneObjectId(4));
        preparation.Retain(sources, (id, _, _) => id == new SceneObjectId(4));
        Assert.AreSame(kept, preparation.GetOrCreate((new(1), 0), () => throw new AssertFailedException("Compatible resource was rebuilt.")));
        Assert.AreEqual(0, kept.Disposals);
        foreach (var obsolete in new[] { removed, replaced, displayed, shortened }) Assert.AreEqual(1, obsolete.Disposals);
        Assert.IsFalse(preparation.TryGet((new(4), 0), out _), "Committed reusable resources take precedence over pending ones.");
        preparation.Cancel();
        Assert.AreEqual(1, kept.Disposals);
        foreach (var obsolete in new[] { removed, replaced, displayed, shortened }) Assert.AreEqual(1, obsolete.Disposals);
    }

    [TestMethod]
    public void AnyScopeChangeCancelsOwnedResourcesAndResetsTheMergeWindow()
    {
        var first = Frame(1);
        var stamp = first.Update.Frame;
        var changed = new[] {
            Frame(2, baseline: stamp),
            Frame(2, stamp: stamp with { Session = new(Guid.Parse("af907b20-2df9-4d11-bc80-4686c546d526")), Revision = 2 }),
            Frame(2, stamp: stamp with { Request = new(2), Revision = 2 }),
            Frame(2, stamp: stamp with { Space = new(2), Revision = 2 }) };
        foreach (var incoming in changed)
        {
            var preparation = Create();
            preparation.Select(first, 0);
            var old = preparation.GetOrCreate((new(1), 0), () => new FakeVisual(Visual()));
            Assert.IsFalse(preparation.SameScope(incoming));
            Assert.AreSame(incoming, preparation.Select(incoming, Ticks(1)));
            Assert.AreEqual(1, old.Disposals);
            Assert.IsFalse(preparation.TryGet((new(1), 0), out _));
            var newer = Frame(3, stamp: incoming.Update.Frame with { Revision = 3 }, baseline: incoming.Update.Base);
            Assert.AreSame(newer, preparation.Select(newer, Ticks(1.4)));
            preparation.Cancel();
            Assert.AreEqual(1, old.Disposals);
        }
    }

    [TestMethod]
    public void FactoryFailureCancelsEarlierResourcesAndPreservesTheOriginalException()
    {
        var preparation = Create();
        preparation.Select(Frame(1), 0);
        var old = preparation.GetOrCreate((new(1), 0), () => new FakeVisual(Visual()));
        var failure = new InvalidOperationException("resource preparation failed");
        var actual = Assert.ThrowsExactly<InvalidOperationException>(() => preparation.GetOrCreate((new(2), 0), () => throw failure));
        Assert.AreSame(failure, actual);
        Assert.IsNull(preparation.Frame);
        Assert.AreEqual(1, old.Disposals);
        Assert.IsFalse(preparation.TryGet((new(1), 0), out _));
        preparation.Cancel();
        Assert.AreEqual(1, old.Disposals);
    }

    [TestMethod]
    public void TakeTransfersOwnershipAndCancelDoesNotDisposeTransferredResources()
    {
        var preparation = Create();
        preparation.Select(Frame(1), 0);
        var visual = preparation.GetOrCreate((new(1), 0), () => new FakeVisual(Visual()));
        var owned = preparation.Take();
        preparation.Cancel();
        Assert.IsNull(preparation.Frame);
        Assert.IsFalse(preparation.TryGet((new(1), 0), out _));
        Assert.AreSame(visual, owned.Single());
        Assert.AreEqual(0, visual.Disposals);
        owned.Single().Dispose();
        Assert.AreEqual(1, visual.Disposals);
    }

    private static ScenePreparation<FakeVisual> Create() => new((prepared, next) =>
        next is SceneVisual.Geometry geometry && ReferenceEquals(prepared.Source.Mesh, geometry.Mesh));
    private static long Ticks(double seconds) => (long)(seconds * Stopwatch.Frequency);
    private static SceneVisual.Geometry Visual() => new(new(new SceneMaterial.BuiltIn(BuiltInMaterial.RailSteel),
        [new(Vector3.Zero, Vector3.UnitY, default, default, ExposedFaceEdges.None, 0, 0)], []), new(false, 0, 0, false));
    private static SceneObject Object(ulong id, SceneVisual visual) => new(new(id), null,
        new(Vector3.Zero, Quaternion.Identity), null, [visual], [], []);
    private static TestFrame Frame(ulong revision, SceneObject[]? objects = null, SceneFrameStamp? stamp = null, SceneFrameStamp? baseline = null) => new()
    {
        Update = new(baseline, stamp ?? new(new(Guid.Empty), new(1), new(1), revision), default,
            objects is null ? [] : [.. objects], [], new([], []), [])
    };
    private sealed class FakeVisual(SceneVisual.Geometry source) : IDisposable
    {
        internal SceneVisual.Geometry Source { get; } = source;
        internal int Disposals { get; private set; }
        public void Dispose() => Disposals++;
    }
    private sealed class TestFrame : ISceneFrame
    {
        public required SceneUpdate Update { get; init; }
        public ScenePoint Origin => default;
        public ScenePanBounds? NodePanBounds => null;
        public SceneReadiness Readiness => SceneReadiness.Ready;
        public double SimulationSeconds => 0;
        public ImmutableArray<SceneProblem> Diagnostics => [];
        public ImmutableArray<SceneDebugLine> DebugLines => [];
        public ScenePick Pick(SceneRay ray) => throw new NotSupportedException();
        public Lookup<ScenePose> FindPose(WorldObjectId subject) => throw new NotSupportedException();
        public Lookup<WorldLocation> Locate(Vector3 point) => throw new NotSupportedException();
        public Lookup<double> TerrainHeight(double x, double z) => throw new NotSupportedException();
    }
}

