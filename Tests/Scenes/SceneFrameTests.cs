using System.Collections.Immutable;
using System.Numerics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Scenes.Contracts;

namespace Train.Scenes.Tests;

[TestClass]
public sealed class SceneFrameTests
{
    [TestMethod]
    public void Stable_geometry_keeps_dynamic_deltas_and_replacement_origin_shifts()
    {
        SceneObject Object(ulong id, float x) => new(new(id), null,
            new(new(x, 0, 0), Quaternion.Identity), null, [], [], []);
        var fixedObject = Object(1, 10);
        var geometry = SceneFrame.StaticGeometry.Empty.Apply(new Dictionary<string, RenderedDesign?> { ["fixed"] = new([fixedObject], [], []) });
        SceneFrame Frame(ulong revision, SceneFrame.StaticGeometry shared, ImmutableArray<SceneObject> moving,
            SceneFrame? previous = null, double origin = 0) => new(
                new(default, new(1), new(1), revision), new(origin, 0, 0), 0, SceneReadiness.Ready,
                moving, [], [], [], previous, staticGeometry: shared);
        var first = Frame(1, geometry, [Object(2, 2), Object(3, 3)]);
        var next = Frame(2, geometry, [Object(2, 4)], first);
        CollectionAssert.AreEqual(new[] { new SceneObjectId(2) }, next.Update.Upserts.Select(item => item.Id).ToArray());
        CollectionAssert.AreEqual(new[] { new SceneObjectId(3) }, next.Update.Removed.ToArray());
        Assert.HasCount(2, next.Objects);
        var shiftedGeometry = geometry.Apply(new Dictionary<string, RenderedDesign?> { ["fixed"] = new([Object(1, 0), Object(4, 5)], [], []) });
        var shifted = Frame(3, shiftedGeometry, [Object(2, -6)], next, 10);
        Assert.AreEqual(new Vector3(-10, 0, 0), shifted.Update.ShiftExistingRoots);
        CollectionAssert.AreEqual(new[] { new SceneObjectId(4) }, shifted.Update.Upserts.Select(item => item.Id).ToArray());
        Assert.IsEmpty(shifted.Update.Removed);
        var removed = Frame(4, shiftedGeometry.Apply(new Dictionary<string, RenderedDesign?> { ["fixed"] = new([Object(4, 5)], [], []) }), [Object(2, -6)], shifted, 10);
        Assert.IsEmpty(removed.Update.Upserts);
        CollectionAssert.AreEqual(new[] { new SceneObjectId(1) }, removed.Update.Removed.ToArray());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Static_geometry_requires_existing_acyclic_parents(bool cycle)
    {
        SceneObject Object(ulong id, ulong parent) => new(new(id), new(parent),
            SceneFixtures.Identity, null, [], [], []);
        var geometry = new RenderedDesign(cycle ? [Object(1, 2), Object(2, 1)] : [Object(1, 2)], [], []);
        Assert.ThrowsExactly<ArgumentException>(() => SceneFrame.StaticGeometry.Empty.Apply(
            new Dictionary<string, RenderedDesign?> { ["invalid"] = geometry }));
    }

    [TestMethod]
    public void Part_changes_accumulate_until_confirmation_and_old_history_falls_back_to_snapshots()
    {
        RenderedDesign Part(ulong id, float x) => new([
            new(new(id), null, new(new(x, 0, 0), Quaternion.Identity), null, [], [], [])], [], []);
        SceneFrame Frame(ulong revision, SceneFrame.StaticGeometry geometry, SceneFrame? displayed = null) => new(
            new(default, new(1), new(1), revision), default, 0, SceneReadiness.Ready,
            [], [], [], [], displayed, staticGeometry: geometry);
        var first = SceneFrame.StaticGeometry.Empty.Apply(new Dictionary<string, RenderedDesign?>
            { ["a"] = Part(1, 0), ["b"] = Part(2, 0) });
        var displayed = Frame(1, first);
        var changed = first.Apply(new Dictionary<string, RenderedDesign?> { ["a"] = Part(1, 4) });
        CollectionAssert.AreEquivalent(new[] { new SceneObjectId(1) }, changed.ChangedSince(first).ToArray());
        Assert.IsTrue(first.TryGetObject(new(2), out var before));
        Assert.IsTrue(changed.TryGetObject(new(2), out var after));
        Assert.AreSame(before, after);
        var withoutB = changed.Apply(new Dictionary<string, RenderedDesign?> { ["b"] = null });
        var transient = withoutB.Apply(new Dictionary<string, RenderedDesign?> { ["temp"] = Part(3, 0) });
        var latest = transient.Apply(new Dictionary<string, RenderedDesign?> { ["temp"] = null });
        var offered = Frame(2, latest, displayed);
        CollectionAssert.AreEqual(new[] { new SceneObjectId(1) }, offered.Update.Upserts.Select(item => item.Id).ToArray());
        CollectionAssert.AreEqual(new[] { new SceneObjectId(2) }, offered.Update.Removed.ToArray());
        Frame(3, latest, offered);
        CollectionAssert.AreEquivalent(new[] { new SceneObjectId(1), new SceneObjectId(2) }, latest.ChangedSince(first).ToArray());
        var oldReceipt = Frame(4, latest, displayed);
        CollectionAssert.AreEqual(new[] { new SceneObjectId(1) }, oldReceipt.Update.Upserts.Select(item => item.Id).ToArray());
        CollectionAssert.AreEqual(new[] { new SceneObjectId(2) }, oldReceipt.Update.Removed.ToArray());
        CollectionAssert.AreEqual(new[] { new SceneObjectId(2) }, oldReceipt.RemovedSince(displayed).ToArray());
        Assert.HasCount(2, displayed.Objects);
    }
}
