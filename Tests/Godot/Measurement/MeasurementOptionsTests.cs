using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.App;
using Train.App.Measurement;
using Train.App.Notifications;
using Train.Core.Contracts;

namespace Train.Godot.Tests.Measurement;

[TestClass]
public sealed class MeasurementOptionsTests
{
    [TestMethod]
    [DataRow("scene-route-8x")]
    [DataRow("scene-follow-8x")]
    [DataRow("scene-soak")]
    public void EightTimesMeasurementsDisableAndReportEncounterSlowdown(string scenario)
    {
        var policy = new NotificationPolicy();
        var action = MeasurementRun.ConfigureEncounterAction(scenario, policy);

        Assert.AreEqual(NotificationAction.None, policy.Action(NotificationCategory.Encounter));
        using var report = JsonDocument.Parse(MeasurementReport.Serialize(scenario,
            new Dictionary<string, object?> { ["encounter_action"] = action }, null, "configuration only"));
        var recorded = report.RootElement.GetProperty("identity").GetProperty("encounter_action");
        Assert.AreEqual(policy.Action(NotificationCategory.Encounter).ToString(), recorded.GetProperty("value").GetString());
        Assert.AreEqual("SlowToOne", recorded.GetProperty("default_value").GetString());
        Assert.IsFalse(string.IsNullOrWhiteSpace(recorded.GetProperty("reason").GetString()));
    }

    [TestMethod]
    [DataRow("scene-node", "SlowToOne")]
    [DataRow("scene-node-first", "SlowToOne")]
    [DataRow("scene-edge", "SlowToOne")]
    [DataRow("scene-follow", "SlowToOne")]
    [DataRow("scene-memory", "SlowToOne")]
    [DataRow("scene-capture", "SlowToOne")]
    [DataRow("scene-map", "SlowToOne")]
    [DataRow("scene-reference", "SlowToOne")]
    [DataRow("scene-editor", "SlowToOne")]
    [DataRow("scene-route-1x", "SlowToOne")]
    [DataRow("scene-route-2x", "SlowToOne")]
    [DataRow("scene-route-4x", "SlowToOne")]
    [DataRow("scene-route-capture-1x", "SlowToOne")]
    [DataRow("scene-route-capture-8x", "SlowToOne")]
    [DataRow("scene-map", "Pause")]
    public void OtherMeasurementsKeepAndReportCurrentEncounterAction(string scenario, string originalAction)
    {
        var policy = new NotificationPolicy();
        var original = Enum.Parse<NotificationAction>(originalAction);
        policy.SetAction(NotificationCategory.Encounter, original);
        var action = MeasurementRun.ConfigureEncounterAction(scenario, policy);

        Assert.AreEqual(original, policy.Action(NotificationCategory.Encounter));
        using var report = JsonDocument.Parse(MeasurementReport.Serialize(scenario,
            new Dictionary<string, object?> { ["encounter_action"] = action }, null, "configuration only"));
        var recorded = report.RootElement.GetProperty("identity").GetProperty("encounter_action");
        Assert.AreEqual(policy.Action(NotificationCategory.Encounter).ToString(), recorded.GetProperty("value").GetString());
        Assert.AreEqual("SlowToOne", recorded.GetProperty("default_value").GetString());
        Assert.IsFalse(recorded.TryGetProperty("reason", out _));
    }

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
