
using System.Collections.Immutable;
using System.Numerics;
using Train.Core.Contracts;
using Train.Scenes.Contracts;
using Train.SceneScale;

using Microsoft.VisualStudio.TestTools.UnitTesting;
namespace Train.SceneScale.Tests;
[TestClass]
[TestCategory("fast")]
public sealed class ObservationTests
{
    [TestMethod]
    public void Ready_exact_coverage_completes_with_world_heights()
    {
        var frame = new TestFrame();
        var heights = new Dictionary<(int X, int Z), double>();
        var columns = Probe.LocalColumns(Vector2.Zero);
        Probe.LocalCoverage Observe() => Probe.ObserveLocalCoverage(frame, Vector2.Zero, columns, heights);
        Assert.IsTrue(columns.Count == 115816, "the original fixed 192-cell circle must keep every column");
        Assert.IsTrue(Observe().Complete && heights.Count == columns.Count, "ready and exact heights must complete immediately");
        Assert.IsTrue(heights[(-1, -1)] == 7, "world height includes the floating origin");
    }

    [TestMethod]
    public void Non_ready_frames_cannot_complete()
    {
        var frame = new TestFrame();
        var heights = new Dictionary<(int X, int Z), double>();
        var columns = Probe.LocalColumns(Vector2.Zero);
        Probe.LocalCoverage Observe() => Probe.ObserveLocalCoverage(frame, Vector2.Zero, columns, heights);
        foreach (var state in new[] { SceneReadiness.Loading, SceneReadiness.Partial, SceneReadiness.Failed })
        {
            frame.Readiness = state;
            Assert.IsTrue(!Observe().Complete, "non-ready frame cannot complete");
            Assert.IsTrue(!new Probe.LocalCoverage(state, 0, 1, 1).Complete, "precise local coverage alone cannot substitute for Ready");
        }
    }

    [TestMethod]
    public void Missing_columns_block_until_recovered()
    {
        var frame = new TestFrame();
        var heights = new Dictionary<(int X, int Z), double>();
        var columns = Probe.LocalColumns(Vector2.Zero);
        Probe.LocalCoverage Observe() => Probe.ObserveLocalCoverage(frame, Vector2.Zero, columns, heights);
        Assert.IsTrue(Observe().Complete, "the first precise frame must be complete");
        frame.Missing = (-192, -1);
        Assert.IsTrue(!Observe().Complete && heights.Count == columns.Count - 1, "one missing precise column must block completion");
        frame.Missing = null;
        Assert.IsTrue(Observe().Complete, "all exact columns must recover");
    }

    [TestMethod]
    public void Fog_blocks_only_when_it_intersects_world_circle()
    {
        var frame = new TestFrame();
        var heights = new Dictionary<(int X, int Z), double>();
        var columns = Probe.LocalColumns(Vector2.Zero);
        Probe.LocalCoverage Observe() => Probe.ObserveLocalCoverage(frame, Vector2.Zero, columns, heights);
        frame.Fog = [new(new(182, -1, -21), new(184, 1, -19))];
        Assert.IsTrue(!Observe().Complete, "fog touching the fixed world circle must block after origin conversion");
        frame.Fog = [new(new(182.01f, -1, -21), new(184, 1, -19))];
        Assert.IsTrue(Observe().Complete, "fog beyond the world circle must not block");
        frame.Fog = [new(new(-11, -1, -21), new(-9, 1, -19))];
        Assert.IsTrue(!Observe().Complete, "a fog box enclosing the center must block");
    }

    [TestMethod]
    public void Repeat_sample_requires_ten_seconds()
    {
        var timed = new Probe.LocalObservation(4, 3, "geometry", 2, "rail", 10, 10, 0, 0, "height");
        Assert.IsTrue(!timed.CanRepeatAt(13.999), "second sample cannot be early");
        Assert.IsTrue(timed.CanRepeatAt(14) && timed.CanRepeatAt(14.001), "second sample starts at ten seconds");
    }

    [TestMethod]
    public void Content_equality_detects_geometry_and_coverage_drift()
    {
        var first = new Probe.LocalObservation(4, 3, "geometry", 2, "rail", 10, 10, 0, 0, "height");
        Assert.IsTrue(first.SameContent(first with { seconds = 14 }), "observation time must not alter content equality");
        foreach (var changed in new[] { first with { triangleWorldCoordinatesMaterialSha256 = "changed" },
            first with { railSteelSha256 = "changed" }, first with { terrainHeightSha256 = "changed" },
            first with { triangles = 4 }, first with { railSteelTriangles = 3 },
            first with { heightColumns = 11 }, first with { heightFound = 9 },
            first with { heightMissing = 1 }, first with { foggedRegionsIntersecting = 1 },
            first with { objectSetSha256 = "another-id" }, first with { objectCount = 1 },
            first with { nonRailTriangles = 1 }, first with { nonRailTriangleWorldCoordinatesMaterialSha256 = "changed" } })
            Assert.IsTrue(!first.SameContent(changed), "hash or coverage drift must fail stability");
    }

    [TestMethod]
    public void Stability_requires_thirty_seconds_and_restarts_on_changes()
    {
        var observation = new Probe.LocalObservation(10, 3, "geometry", 2, "rail", 10, 10, 0, 0, "height", "ids", 2);
        var window = new Probe.LocalStabilityWindow(0);
        Assert.IsTrue(!window.IsStable(39.999) && window.UnchangedSinceSeconds is null, "an empty window is not stable and has no interval start");
        window.Track(observation);
        window.Track(observation with { seconds = 39.999, railSteelSha256 = "other-rail", terrainHeightSha256 = "other-height" });
        Assert.IsTrue(window.UnchangedSinceSeconds == 10 && !window.IsStable(39.999) && window.IsStable(40) && window.IsStable(40.001),
            "unchanged object set and geometry need a full thirty seconds, excluding rail/height from this wait");
        foreach (var changed in new[] { observation with { triangles = 4 },
            observation with { triangleWorldCoordinatesMaterialSha256 = "other-geometry" },
            observation with { objectSetSha256 = "replacement-id" }, observation with { objectCount = 3 } })
        {
            var changedWindow = new Probe.LocalStabilityWindow(0);
            changedWindow.Track(observation);
            changedWindow.Track(changed with { seconds = 39 });
            Assert.IsTrue(changedWindow.UnchangedSinceSeconds == 39 && !changedWindow.IsStable(40) && !changedWindow.IsStable(68.999) && changedWindow.IsStable(69),
                "object identity, count, geometry hash or triangle count changes restart thirty seconds");
        }
        window.Reset();
        Assert.IsTrue(window.UnchangedSinceSeconds is null && !window.IsStable(100), "lost coverage clears the stability window");
        window.Track(observation with { seconds = 120 });
        Assert.IsTrue(!window.IsStable(149.999) && window.IsStable(150), "coverage recovery starts a fresh window");
        Assert.IsTrue(!window.TimedOut(299.999) && window.TimedOut(300) && window.TimedOut(300.001),
            "single-position wall clock deadline stays at three hundred seconds despite resets");
        var movedWindow = new Probe.LocalStabilityWindow(1000);
        Assert.IsTrue(!movedWindow.TimedOut(1299.999) && movedWindow.TimedOut(1300), "deadline is relative to this camera position");
    }

    [TestMethod]
    public void Updates_shift_roots_then_apply_replacements_and_removals()
    {
        var frame = new TestFrame();
        var root = new SceneObject(new(1), null, new(new(2, 3, 4), Quaternion.Identity), null, [], [], []);
        var child = new SceneObject(new(2), new(1), new(new(5, 6, 7), Quaternion.Identity), null, [], [], []);
        var objects = new Dictionary<SceneObjectId, SceneObject> { [root.Id] = root, [child.Id] = child };
        var stamp = frame.Update.Frame;
        Probe.ApplyUpdate(objects, new(stamp, stamp with { Revision = 2 }, new(-10, -20, -30), [], [], new([], []), []));
        Assert.IsTrue(objects[root.Id].Pose.Position == new Vector3(-8, -17, -26), "incremental origin shift moves retained roots");
        Assert.IsTrue(objects[child.Id].Pose.Position == child.Pose.Position, "children stay relative to their shifted parent");
        var replaced = root with { Pose = new(Vector3.One, Quaternion.Identity) };
        Probe.ApplyUpdate(objects, new(stamp, stamp with { Revision = 3 }, Vector3.One, [replaced], [child.Id], new([], []), []));
        Assert.IsTrue(objects.Count == 1 && objects[root.Id] == replaced, "upserts replace after shift, removed ids disappear");
        Probe.ApplyUpdate(objects, new(null, stamp with { Revision = 4 }, Vector3.One, [child with { Parent = null }], [], new([], []), []));
        Assert.IsTrue(objects.Count == 1 && !objects.ContainsKey(root.Id) && objects[child.Id].Pose == child.Pose,
            "a full snapshot discards old objects and does not shift its upserts");
    }
}
sealed class TestFrame : ISceneFrame
{
    public (int X, int Z)? Missing;
    public ImmutableArray<SceneBounds> Fog = [];
    public ScenePoint Origin => new(10, 5, 20);
    public SceneReadiness Readiness { get; set; } = SceneReadiness.Ready;
    public ScenePanBounds? NodePanBounds => null;
    public SceneNodeOverview? NodeOverview => null;
    public double SimulationSeconds => 0;
    public SceneUpdate Update => new(null, new(new(Guid.Empty), new(1), new(1), 1), Vector3.Zero, [], [], new(Fog, []), []);
    public ImmutableArray<SceneProblem> Diagnostics => [];
    public ImmutableArray<SceneDebugLine> DebugLines => [];
    public Lookup<double> TerrainHeight(double x, double z) =>
        Missing == ((int)Math.Floor(x + Origin.X), (int)Math.Floor(z + Origin.Z))
            ? new Lookup<double>.Missing() : new Lookup<double>.Found(2);
    public Lookup<double> DisplayTerrainHeight(double x, double z) => throw new NotSupportedException();
    public ScenePick Pick(SceneRay ray) => throw new NotSupportedException();
    public Lookup<ScenePose> FindPose(WorldObjectId subject) => throw new NotSupportedException();
    public Lookup<WorldLocation> Locate(Vector3 point) => throw new NotSupportedException();
}
