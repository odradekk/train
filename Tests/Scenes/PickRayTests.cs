using System.Numerics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Scenes.Contracts;

namespace Train.Scenes.Tests;

[TestClass]
public sealed class PickRayTests
{
    private static readonly SceneRay Ray = new(new(2, 3, 4), new(.6f, 0, .8f), 10);
    private static readonly SceneFrameRequest Request = new(1, null, SceneDebugLayer.PickRay);

    [TestMethod]
    public void SampleUsesSourceOriginAndKeepsItsFullDirectionAndLengthAcrossRevisions()
    {
        var stamp = new SceneFrameStamp(new(Guid.NewGuid()), new(1), new(1), 1);
        var source = Frame(stamp, new(100, 200, 300));
        var sample = new ScenePickRaySample(source, Ray);
        var frame = Frame(stamp with { Revision = 2 }, new(90, 180, 270), sample);
        var line = frame.DebugLines.Single();
        Assert.AreEqual(new Vector3(12, 23, 34), line.From);
        Assert.AreEqual(new Vector3(18, 23, 42), line.To);
        var retained = frame.DebugLines;
        source.Pick(Ray);
        frame.Pick(Ray);
        var later = Frame(stamp with { Revision = 3 }, new(80, 160, 240), sample);
        Assert.AreEqual(new Vector3(22, 43, 64), later.DebugLines.Single().From);
        CollectionAssert.AreEqual(retained.ToArray(), frame.DebugLines.ToArray());
        Assert.IsTrue(source.DebugLines.IsEmpty);
    }

    [TestMethod]
    public void SampleRequiresEnabledLayerAndMatchingSessionRequestAndSpace()
    {
        var stamp = new SceneFrameStamp(new(Guid.NewGuid()), new(1), new(1), 1);
        var source = Frame(stamp, default);
        var sample = new ScenePickRaySample(source, Ray);
        Assert.IsTrue(Frame(stamp, default).DebugLines.IsEmpty);
        Assert.IsTrue(Frame(stamp, default, sample, SceneDebugLayer.None).DebugLines.IsEmpty);
        foreach (var current in new[]
        {
            stamp with { Session = new(Guid.NewGuid()) },
            stamp with { Request = new(2) },
            stamp with { Space = new(2) }
        })
            Assert.IsTrue(Frame(current, default, sample).DebugLines.IsEmpty);
    }

    [TestMethod]
    public async Task PresentRejectsInvalidSamplesBeforeAdvancingRevisionAndRemainsUsable()
    {
        await using var session = new WorldSceneSession(new(Guid.NewGuid()),
            NodeFixtures.Snapshot(trains: [], instances: []),
            new(), new(), nodeBuild: RailwayFixtures.EmptyNode);
        Assert.IsInstanceOfType<SceneResult<SceneRequestId>.Success>(
            session.RequestView(new WorldSceneTarget.Location(NodeFixtures.Location)));
        var before = await RailwayFixtures.Settled(session, Request);
        var foreign = Frame(before.Update.Frame with { Session = new(Guid.NewGuid()) }, default);
        var invalid = new List<ScenePickRaySample>
        {
            new(null!, Ray), new(foreign, Ray),
            new(before, Ray with { Origin = new(float.NaN, 0, 0) })
        };
        foreach (var sample in invalid)
            Assert.ThrowsExactly<ArgumentException>(() => session.Present(Request with { PickRay = sample }, before));
        var next = (SceneResult<ISceneFrame>.Success)session.Present(
            Request with { PickRay = new(before, Ray) }, before);
        Assert.AreEqual(before.Update.Frame.Revision + 1, next.Value.Update.Frame.Revision);
        Assert.AreEqual(SceneReadiness.Ready, next.Value.Readiness);
        Assert.AreEqual(1, next.Value.DebugLines.Length);
        var retained = next.Value.DebugLines;
        next.Value.Pick(Ray);
        foreach (var stale in new[]
        {
            before.Update.Frame with { Request = new(0) },
            before.Update.Frame with { Space = new(0) }
        })
        {
            var result = (SceneResult<ISceneFrame>.Success)session.Present(
                Request with { PickRay = new(Frame(stale, default), Ray) }, next.Value);
            Assert.IsTrue(result.Value.DebugLines.IsEmpty);
        }
        CollectionAssert.AreEqual(retained.ToArray(), next.Value.DebugLines.ToArray());
    }

    private static SceneFrame Frame(SceneFrameStamp stamp, ScenePoint origin,
        ScenePickRaySample? sample = null, SceneDebugLayer layers = SceneDebugLayer.PickRay) =>
        new(stamp, origin, 0, SceneReadiness.Ready, [], [], [], [], debugLayers: layers, pickRay: sample);
}
