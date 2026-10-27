
using System.Collections.Immutable;
using System.Numerics;
using Train.Core.Contracts;
using Train.Scenes.Contracts;
using Train.SceneScale;
using Microsoft.VisualStudio.TestTools.UnitTesting;
namespace Train.SceneScale.Tests;
[TestClass]
[TestCategory("fast")]
public sealed class RailwayTests
{
    [TestMethod]
    public void Segments_clip_to_fixed_circle_including_tangent()
    {
        var frame = new Frame();
        Probe.LocalRailway Capture() => Probe.CaptureRailway(frame, Vector2.Zero);
        frame.DebugLines = [Line(new(-384,-384,0),new(384,384,0)), Line(new(193,0,0),new(200,0,0)), Line(new(192,0,-5),new(192,0,5))];
        CollectionAssert.AreEqual(new[] { "-192000,-192000,0;192000,192000,0", "192000,0,0;192000,0,0" }, Capture().normalized_segments.ToArray());
    }

    [TestMethod]
    public void Circle_clipping_keeps_exact_boundary_and_vertical_segments()
    {
        var frame = new Frame();
        Probe.LocalRailway Capture() => Probe.CaptureRailway(frame, Vector2.Zero);
        // At z=115.2, x=+-153.6 intersects the 192 circle (the 3-4-5 triangle).
        frame.DebugLines = [Line(new(-300,0,115.2f),new(300,0,115.2f))];
        CollectionAssert.AreEqual(new[] { "-153600,0,115200;153600,0,115200" }, Capture().normalized_segments.ToArray());
        frame.DebugLines = [Line(new(192.0005f,0,-1),new(192.0005f,0,1))];
        Assert.IsTrue(Capture().segment_count == 0,"no mesh epsilon in the centerline circle");
        frame.DebugLines = [Line(new(0,0,0),new(0,5,0))];
        CollectionAssert.AreEqual(new[] { "0,0,0;0,5000,0" }, Capture().normalized_segments.ToArray());
    }

    [TestMethod]
    public void Segments_include_floating_origin()
    {
        var frame = new Frame();
        Probe.LocalRailway Capture() => Probe.CaptureRailway(frame, Vector2.Zero);
        frame.Origin = new(100,10,20);
        frame.DebugLines = [Line(new(-100,-10,-20),new(-99,-8,-17))];
        CollectionAssert.AreEqual(new[] { "0,0,0;1000,2000,3000" }, Capture().normalized_segments.ToArray());
    }

    [TestMethod]
    public void Quantized_unordered_endpoints_deduplicate()
    {
        var frame = new Frame();
        Probe.LocalRailway Capture() => Probe.CaptureRailway(frame, Vector2.Zero);
        frame.DebugLines = [Line(new(.0005f,-.0005f,0),new(1,2,3)),Line(new(1,2,3),new(.0005f,-.0005f,0)),Line(new(.00051f,-.00051f,0),new(1,2,3))];
        CollectionAssert.AreEqual(new[] { "1,-1,0;1000,2000,3000" }, Capture().normalized_segments.ToArray());
        Assert.IsTrue(Capture().segment_count == 1 && Capture().quantization_cells == .001,"deduplicate quantized unordered endpoints");
    }

    [TestMethod]
    public void Segment_digest_ignores_order_but_detects_path_changes()
    {
        var frame = new Frame();
        Probe.LocalRailway Capture() => Probe.CaptureRailway(frame, Vector2.Zero);
        frame.DebugLines = [Line(new(0,0,0),new(1,0,0)),Line(new(1,0,0),new(2,0,0))];
        var first = Capture();
        frame.DebugLines = [Line(new(1,0,0),new(2,0,0)),Line(new(1,0,0),new(0,0,0))];
        Assert.IsTrue(Capture().sha256 == first.sha256,"ordering does not affect digest");
        frame.DebugLines = [Line(new(0,0,0),new(1,1,0)),Line(new(1,1,0),new(2,0,0))];
        Assert.IsTrue(Capture().sha256 != first.sha256,"height/path change affects digest");
    }

    [TestMethod]
    public void Parking_exclusion_requires_valid_station_axis_and_strict_boundary()
    {
        var frame = new Frame();
        Probe.LocalRailway Capture() => Probe.CaptureRailway(frame, Vector2.Zero);
        frame.NodeOverview = new([Plot(9,-300),Plot(2,-232)],[],[]);
        var proof = Capture();
        Assert.IsTrue(proof.parkingOutside && proof.parkingExclusion.stationPlotNumber == 2 && proof.parkingExclusion.minimumLateral == -232 && proof.parkingExclusion.axis == Vector2.UnitX,"first station conservative world proof");
        frame.Origin = new(0,0,100);
        Assert.IsTrue(!Capture().parkingOutside,"overview corners include floating origin");
        frame.Origin = default;
        frame.NodeOverview = new([Plot(2,-192)],[],[]);
        Assert.IsTrue(!Capture().parkingOutside,"boundary alone cannot prove exclusion");
        frame.NodeOverview = new([new(2,PlotSize.LargeStation,[new(232,0),new(232,10),new(222,10),new(222,0)])],[],[]);
        Assert.IsTrue(Capture().parkingOutside && Capture().parkingExclusion.axis == Vector2.UnitY,"rotated station uses its own across direction");
        frame.NodeOverview = new([new(2,PlotSize.SmallStation,[new(0,-300),new(0,-300),new(10,-290),new(0,-290)])],[],[]);
        Assert.IsTrue(!Capture().parkingOutside,"degenerate first station cannot establish axis");
        frame.NodeOverview = new([],[],[]);
        Assert.IsTrue(!Capture().parkingOutside,"missing station cannot prove parking coverage");
    }

    private static SceneDebugLine Line(Vector3 a, Vector3 b) => new(a, b, Vector4.One);
    private static SceneOverviewPlot Plot(int n, float z) => new(n, PlotSize.SmallStation, [new(0,z),new(10,z),new(10,z+10),new(0,z+10)]);
}
sealed class Frame : ISceneFrame {
 public ScenePoint Origin {get;set;}
 public SceneNodeOverview? NodeOverview {get;set;}
 public ImmutableArray<SceneDebugLine> DebugLines {get;set;} = [];
 public ScenePanBounds? NodePanBounds => null;
 public SceneReadiness Readiness => SceneReadiness.Ready;
 public double SimulationSeconds => 0;
 public SceneUpdate Update => throw new NotSupportedException();
 public ImmutableArray<SceneProblem> Diagnostics => [];
 public ScenePick Pick(SceneRay ray) => throw new NotSupportedException();
 public Lookup<ScenePose> FindPose(WorldObjectId subject) => throw new NotSupportedException();
 public Lookup<WorldLocation> Locate(Vector3 point) => throw new NotSupportedException();
 public Lookup<double> TerrainHeight(double x,double z) => throw new NotSupportedException();
 public Lookup<double> DisplayTerrainHeight(double x,double z) => throw new NotSupportedException();
}
