using System.Numerics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Rendering;
using Train.Scenes.Contracts;
using Train.Scenes.ContractSupport;

namespace Train.Godot.Tests.Rendering;

[TestClass]
public sealed class ScenePreparationTests
{
    [TestMethod]
    public void PreparationLocksTheFirstCandidateUntilOwnershipIsTransferred()
    {
        var preparation = Create();
        var first = Frame(1);
        Assert.AreSame(first, preparation.Select(first));
        Assert.AreSame(first, preparation.Select(Frame(2)));
        Assert.AreSame(first, preparation.Select(Frame(3)));
        preparation.Take();
        var next = Frame(4);
        Assert.AreSame(next, preparation.Select(next));
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
            var selected = preparation.Select(incoming);
            var sources = selected.Update.Upserts.ToDictionary(item => item.Id);
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
    public void AnyScopeChangeCancelsOwnedResourcesAndLocksTheReplacement()
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
            preparation.Select(first);
            var old = preparation.GetOrCreate((new(1), 0), () => new FakeVisual(Visual()));
            Assert.IsFalse(preparation.SameScope(incoming));
            Assert.AreSame(incoming, preparation.Select(incoming));
            Assert.AreEqual(1, old.Disposals);
            Assert.IsFalse(preparation.TryGet((new(1), 0), out _));
            var newer = Frame(3, stamp: incoming.Update.Frame with { Revision = 3 }, baseline: incoming.Update.Base);
            Assert.AreSame(incoming, preparation.Select(newer));
            preparation.Cancel();
            Assert.AreEqual(1, old.Disposals);
        }
    }

    [TestMethod]
    public void FactoryFailureCancelsEarlierResourcesAndPreservesTheOriginalException()
    {
        var preparation = Create();
        preparation.Select(Frame(1));
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
        preparation.Select(Frame(1));
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

    private static ScenePreparation<FakeVisual> Create() => new();
    private static SceneVisual.Geometry Visual() => new(new(new SceneMaterial.BuiltIn(BuiltInMaterial.RailSteel),
        [new(Vector3.Zero, Vector3.UnitY, default, default, ExposedFaceEdges.None, 0, 0)], []), new(false, 0, 0, false));
    private static SceneObject Object(ulong id, SceneVisual visual) => new(new(id), null,
        new(Vector3.Zero, Quaternion.Identity), null, [visual], [], []);
    private static FixedSceneFrame Frame(ulong revision, SceneObject[]? objects = null, SceneFrameStamp? stamp = null, SceneFrameStamp? baseline = null) => new()
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
}
