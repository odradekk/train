
using System.Collections.Immutable;
using System.Numerics;
using Train.Core.Contracts;
using Train.Scenes.Contracts;
using Train.SceneScale;
using Microsoft.VisualStudio.TestTools.UnitTesting;
namespace Train.SceneScale.Tests;
[TestClass]
public sealed class RailwayTests
{
    [TestMethod]
    [TestCategory("fast")]
    [DataRow("clip") ]
    [DataRow("circle") ]
    [DataRow("origin") ]
    [DataRow("quantize") ]
    [DataRow("path") ]
    [DataRow("parking") ]
    public void Observe(string name)
    {
var frame = new Frame();
void Check(bool ok, string why) { if (!ok) throw new Exception(why); }
SceneDebugLine Line(Vector3 a, Vector3 b) => new(a,b,Vector4.One);
Probe.LocalRailway Capture() => Probe.CaptureRailway(frame, Vector2.Zero);
void Segments(params string[] expected) => Check(Capture().normalized_segments.SequenceEqual(expected), string.Join("|",Capture().normalized_segments));
switch(name) {
case "clip":
    frame.DebugLines = [Line(new(-384,-384,0),new(384,384,0)), Line(new(193,0,0),new(200,0,0)), Line(new(192,0,-5),new(192,0,5))];
    Segments("-192000,-192000,0;192000,192000,0", "192000,0,0;192000,0,0");
    break;
case "circle":
    // At z=115.2, x=+-153.6 intersects the 192 circle (the 3-4-5 triangle).
    frame.DebugLines = [Line(new(-300,0,115.2f),new(300,0,115.2f))];
    Segments("-153600,0,115200;153600,0,115200");
    frame.DebugLines = [Line(new(192.0005f,0,-1),new(192.0005f,0,1))];
    Check(Capture().segment_count == 0,"no mesh epsilon in the centerline circle");
    frame.DebugLines = [Line(new(0,0,0),new(0,5,0))];
    Segments("0,0,0;0,5000,0");
    break;
case "origin":
    frame.Origin = new(100,10,20);
    frame.DebugLines = [Line(new(-100,-10,-20),new(-99,-8,-17))];
    Segments("0,0,0;1000,2000,3000");
    break;
case "quantize":
    frame.DebugLines = [Line(new(.0005f,-.0005f,0),new(1,2,3)),Line(new(1,2,3),new(.0005f,-.0005f,0)),Line(new(.00051f,-.00051f,0),new(1,2,3))];
    Segments("1,-1,0;1000,2000,3000");
    Check(Capture().segment_count == 1 && Capture().quantization_cells == .001,"deduplicate quantized unordered endpoints");
    break;
case "path":
    frame.DebugLines = [Line(new(0,0,0),new(1,0,0)),Line(new(1,0,0),new(2,0,0))];
    var first = Capture();
    frame.DebugLines = [Line(new(1,0,0),new(2,0,0)),Line(new(1,0,0),new(0,0,0))];
    Check(Capture().sha256 == first.sha256,"ordering does not affect digest");
    frame.DebugLines = [Line(new(0,0,0),new(1,1,0)),Line(new(1,1,0),new(2,0,0))];
    Check(Capture().sha256 != first.sha256,"height/path change affects digest");
    break;
case "parking":
    frame.NodeOverview = new([Plot(9,-300),Plot(2,-232)],[],[]);
    var proof = Capture();
    Check(proof.parkingOutside && proof.parkingExclusion.stationPlotNumber == 2 && proof.parkingExclusion.minimumLateral == -232 && proof.parkingExclusion.axis == Vector2.UnitX,"first station conservative world proof");
    frame.Origin = new(0,0,100);
    Check(!Capture().parkingOutside,"overview corners include floating origin");
    frame.Origin = default;
    frame.NodeOverview = new([Plot(2,-192)],[],[]);
    Check(!Capture().parkingOutside,"boundary alone cannot prove exclusion");
    frame.NodeOverview = new([new(2,PlotSize.LargeStation,[new(232,0),new(232,10),new(222,10),new(222,0)])],[],[]);
    Check(Capture().parkingOutside && Capture().parkingExclusion.axis == Vector2.UnitY,"rotated station uses its own across direction");
    frame.NodeOverview = new([new(2,PlotSize.SmallStation,[new(0,-300),new(0,-300),new(10,-290),new(0,-290)])],[],[]);
    Check(!Capture().parkingOutside,"degenerate first station cannot establish axis");
    frame.NodeOverview = new([],[],[]);
    Check(!Capture().parkingOutside,"missing station cannot prove parking coverage");
    break;
}
SceneOverviewPlot Plot(int n,float z) => new(n,PlotSize.SmallStation,[new(0,z),new(10,z),new(10,z+10),new(0,z+10)]);

    }
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
