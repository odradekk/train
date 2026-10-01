using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Domain.WorldEditor;
using Train.Domain.Voxels;
using Train.Logging;

namespace Train.Tests;

[TestClass]
[TestCategory("family:headless")]
[TestCategory("scope:world-editor")]
public sealed class ObserveConsoleCommandTests
{
    [TestMethod]
    public void Observe_is_unavailable_outside_the_world_editor_and_listed_in_help()
    {
        using var logs = LoggerSession.Create();
        var dispatcher = new ConsoleCommandDispatcher(logs.Logger, static () => { }, static () => { }, static () => { }, allowReload: false);
        dispatcher.Execute("help");
        StringAssert.Contains(logs.Memory.Snapshot().Single(item => item.EventId == ConsoleCommandDispatcher.HelpEventId).Message, "observe");

        dispatcher.Execute("observe");
        var warning = logs.Memory.Snapshot().Single(item => item.EventId == ConsoleCommandDispatcher.ObserveUnavailableEventId);
        Assert.AreEqual(LogLevel.Warning, warning.Level);
        StringAssert.Contains(warning.Message, "世界编辑器");
    }

    [TestMethod]
    public void Observe_commands_select_focus_follow_and_free_without_touching_the_session()
    {
        using var worldEditor = WorldEditorHarness.Establish(WorldEditorConfigurations.StraightRailTrainId);
        using var logs = LoggerSession.Create();
        var session = worldEditor.Session;
        var observer = new WorldEditorObserver(session, new ObservationVector(76, 16, -8), new ObservationVector(76, 2, 16));
        var dispatcher = Dispatcher(logs, session, observer);
        var assembly = session.QueryTrainAssemblies().Single();
        var loco = assembly.VehicleIds[0];
        var segment = session.Rails.Segments.First().Id;
        var authority = WorldSaveAuthority.Fingerprint(session);
        var step = session.SimulationStep;
        var paused = session.SimulationPaused;

        dispatcher.Execute("observe");
        StringAssert.Contains(Recent(logs), "mode free selection none");

        dispatcher.Execute("observe list");
        var list = Recent(logs);
        StringAssert.Contains(list, $"vehicle {loco}");
        StringAssert.Contains(list, $"segment {segment}");

        dispatcher.Execute("observe follow");
        StringAssert.Contains(Recent(logs), "NoSelection");

        dispatcher.Execute($"observe select segment {segment}");
        StringAssert.Contains(Recent(logs), $"Selected segment {segment}");
        dispatcher.Execute("observe follow");
        StringAssert.Contains(Recent(logs), "NotVehicle");
        Assert.AreEqual(WorldEditorCameraMode.Free, observer.Mode);

        dispatcher.Execute($"observe select vehicle {loco}");
        var selected = Recent(logs);
        StringAssert.Contains(selected, $"Selected vehicle {loco}");
        StringAssert.Contains(selected, $"owner assembly {assembly.Id} position 1/2 active-locomotive");
        StringAssert.Contains(selected, "durability parts");
        StringAssert.Contains(selected, "eligibility");
        Assert.AreEqual(WorldEditorCameraMode.Free, observer.Mode);

        dispatcher.Execute("observe focus");
        StringAssert.Contains(Recent(logs), $"Focused mode focus target vehicle {loco}");
        dispatcher.Execute("observe follow");
        StringAssert.Contains(Recent(logs), $"Following mode follow target vehicle {loco}");
        Assert.AreEqual(WorldEditorCameraMode.Follow, observer.Mode);
        dispatcher.Execute("observe free");
        StringAssert.Contains(Recent(logs), "Free mode free selection vehicle");
        Assert.IsNull(observer.Target);

        dispatcher.Execute("observe select cell 100 10 15");
        StringAssert.Contains(Recent(logs), "UnknownObject cell 100,10,15");
        Assert.AreEqual(ObservedObjectKind.Vehicle, observer.Selection!.Value.Kind);

        dispatcher.Execute("observe select none");
        StringAssert.Contains(Recent(logs), "Cleared");
        Assert.IsNull(observer.Selection);

        dispatcher.Execute("observe select vehicle banana");
        StringAssert.Contains(Recent(logs), "Usage: observe");

        Assert.AreEqual(authority, WorldSaveAuthority.Fingerprint(session));
        Assert.AreEqual(step, session.SimulationStep);
        Assert.AreEqual(paused, session.SimulationPaused);
    }

    [TestMethod]
    public void Observe_reports_a_destroyed_follow_target_and_the_camera_stays_free_in_place()
    {
        using var worldEditor = WorldEditorHarness.Establish(WorldEditorConfigurations.StraightRailTrainId);
        using var logs = LoggerSession.Create();
        var session = worldEditor.Session;
        var observer = new WorldEditorObserver(session, new ObservationVector(76, 16, -8), new ObservationVector(76, 2, 16));
        var dispatcher = Dispatcher(logs, session, observer);
        var carriage = session.QueryTrainAssemblies().Single().VehicleIds[1];
        dispatcher.Execute($"observe select vehicle {carriage}");
        dispatcher.Execute("observe follow");
        for (var frame = 0; frame < 60; frame++)
            observer.Present(1.0 / 60, session.QueryDisplayPoses());
        var pose = observer.Pose;

        dispatcher.Execute($"composite damage {carriage} 1 0 1 100");
        dispatcher.Execute("observe");
        var report = Recent(logs);
        StringAssert.Contains(report, "mode free selection none");
        StringAssert.Contains(report, $"notice target-lost vehicle {carriage} mode follow camera free-in-place");
        Assert.AreEqual(pose, observer.Pose);
    }

    private static ConsoleCommandDispatcher Dispatcher(LoggerSession logs, VoxelDevelopmentSession session, WorldEditorObserver observer) =>
        new(
            logs.Logger,
            static () => { },
            static () => { },
            static () => { },
            allowReload: true,
            new VoxelConsoleCommands(session),
            new CompositeConsoleCommands(session),
            new RailConsoleCommands(session),
            new ObserveConsoleCommands(session, observer));

    private static string Recent(LoggerSession logs)
    {
        var snapshot = logs.Memory.Snapshot();
        var echo = snapshot.Last(item => item.EventId == ConsoleCommandDispatcher.EchoEventId).Sequence;
        return string.Join("\n", snapshot
            .Where(item => item.EventId == ConsoleCommandDispatcher.ObserveEventId && item.Sequence > echo)
            .Select(item => item.Message));
    }
}
