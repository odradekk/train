using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.App.Measurement;
using Train.Core.Contracts;

namespace Train.Godot.Tests.Measurement;

[TestClass]
public sealed class MeasurementOptionsTests
{
    [TestMethod]
    public void OmittingNewOptionsKeepsDefaultCameraAndObservationWindows()
    {
        MeasurementRun.ValidateCameraOptions("scene-route-1x", "default", false, false);
        var node = new NodeId(13);
        foreach (var scenario in new[] { "scene-node", "scene-node-first", "scene-memory", "scene-reference" })
            Assert.AreEqual(scenario == "scene-reference" ? 10 : 5,
                MeasurementRun.ReadyObservationSeconds(scenario, new WorldObjectId.Node(node), node, false, false));
    }

    [TestMethod]
    public void ExplicitRouteCameraRequiresDiagnostics()
    {
        foreach (var camera in new[] { "default", "farthest", "default-low-orbit", "farthest-low-orbit" })
            Assert.Throws<ArgumentException>(() => MeasurementRun.ValidateCameraOptions("scene-route-1x", camera, true, false));
    }
}
