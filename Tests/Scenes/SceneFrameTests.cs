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
        var geometry = new RenderedDesign([fixedObject], [], []);
        SceneFrame Frame(ulong revision, RenderedDesign shared, ImmutableArray<SceneObject> moving,
            SceneFrame? previous = null, double origin = 0) => new(
                new(default, new(1), new(1), revision), new(origin, 0, 0), 0, SceneReadiness.Ready,
                moving, [], [], [], previous, staticGeometry: shared);
        var first = Frame(1, geometry, [Object(2, 2), Object(3, 3)]);
        var next = Frame(2, geometry, [Object(2, 4)], first);
        CollectionAssert.AreEqual(new[] { new SceneObjectId(2) }, next.Update.Upserts.Select(item => item.Id).ToArray());
        CollectionAssert.AreEqual(new[] { new SceneObjectId(3) }, next.Update.Removed.ToArray());
        Assert.HasCount(2, next.Objects);
        var shiftedGeometry = new RenderedDesign([Object(1, 0), Object(4, 5)], [], []);
        var shifted = Frame(3, shiftedGeometry, [Object(2, -6)], next, 10);
        Assert.AreEqual(new Vector3(-10, 0, 0), shifted.Update.ShiftExistingRoots);
        CollectionAssert.AreEqual(new[] { new SceneObjectId(4) }, shifted.Update.Upserts.Select(item => item.Id).ToArray());
        Assert.IsEmpty(shifted.Update.Removed);
        var removed = Frame(4, new([Object(4, 5)], [], []), [Object(2, -6)], shifted, 10);
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
        Assert.ThrowsExactly<ArgumentException>(() => new SceneFrame(
            new(default, new(1), new(1), 1), default, 0, SceneReadiness.Ready,
            [], [], [], [], staticGeometry: geometry));
    }
}
