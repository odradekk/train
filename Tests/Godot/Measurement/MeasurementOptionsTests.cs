using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.App.Measurement;
using Train.Core.Contracts;

namespace Train.Godot.Tests.Measurement;

[TestClass]
public sealed class MeasurementOptionsTests
{
    [TestMethod]
    public void CameraVariantsKeepDiagnosticsOnRoutesAndAllowFreeOrbitOnlyOnExistingFreeScenes()
    {
        foreach (var mode in new[] { "default-low-orbit", "farthest-low-orbit" })
        {
            foreach (var scenario in new[] { "scene-route-1x", "scene-route-8x" })
            {
                MeasurementRun.ValidateCameraOptions(scenario, mode, true, true);
                Assert.Throws<ArgumentException>(() => MeasurementRun.ValidateCameraOptions(scenario, mode, true, false));
            }
            foreach (var scenario in new[] { "scene-node", "scene-edge" })
            {
                MeasurementRun.ValidateCameraOptions(scenario, mode, true, false);
                Assert.Throws<ArgumentException>(() => MeasurementRun.ValidateCameraOptions(scenario, mode, true, true));
            }
            Assert.Throws<ArgumentException>(() => MeasurementRun.ValidateCameraOptions("scene-follow", mode, true, false));
        }
        MeasurementRun.ValidateCameraOptions("scene-route-1x", "default", false, false);
        MeasurementRun.ValidateCameraOptions("scene-route-8x", "farthest", true, true);
        Assert.Throws<ArgumentException>(() => MeasurementRun.ValidateCameraOptions("scene-node", "farthest", true, false));
        Assert.Throws<ArgumentException>(() => MeasurementRun.ValidateCameraOptions("scene-route-1x", "unknown", true, true));
    }

    [TestMethod]
    public void ReadyHoldRejectsUnsupportedDurationsAndScenesBeforeOpeningAnything()
    {
        foreach (var scenario in new[] { "scene-node", "scene-node-first", "scene-memory", "scene-reference" })
        {
            Assert.AreEqual(0, MeasurementRun.ParseNodeReadyHold(scenario, null));
            Assert.AreEqual(60, MeasurementRun.ParseNodeReadyHold(scenario, "60"));
            foreach (var invalid in new[] { "-1", "0", "59", "61", "60.5", "invalid" })
                Assert.Throws<ArgumentException>(() => MeasurementRun.ParseNodeReadyHold(scenario, invalid));
        }
        Assert.Throws<ArgumentException>(() => MeasurementRun.ParseNodeReadyHold("scene-edge", "60"));
        Assert.Throws<ArgumentException>(() => MeasurementRun.ParseNodeReadyHold("scene-route-1x", "60"));
    }

    [TestMethod]
    public void ReadyHoldChangesOnlySelectedNodeAndLeavesReferenceAndLegacyOperationsAlone()
    {
        var selected = new NodeId(13);
        WorldObjectId node = new WorldObjectId.Node(selected);
        WorldObjectId other = new WorldObjectId.Node(new(3));
        WorldObjectId edge = new WorldObjectId.Edge(new(11));
        Assert.AreEqual(60, MeasurementRun.ReadyObservationSeconds("scene-node-first", node, selected, 60, false));
        Assert.AreEqual(60, MeasurementRun.ReadyObservationSeconds("scene-reference", node, selected, 60, false));
        Assert.AreEqual(10, MeasurementRun.ReadyObservationSeconds("scene-reference", other, selected, 60, false));
        Assert.AreEqual(10, MeasurementRun.ReadyObservationSeconds("scene-reference", edge, selected, 60, false));
        Assert.AreEqual(5, MeasurementRun.ReadyObservationSeconds("scene-node", edge, selected, 60, false));
        Assert.AreEqual(5, MeasurementRun.ReadyObservationSeconds("scene-node", node, selected, 0, false));
        Assert.AreEqual(60, MeasurementRun.ReadyObservationSeconds("scene-edge", edge, selected, 0, true));
    }
}
