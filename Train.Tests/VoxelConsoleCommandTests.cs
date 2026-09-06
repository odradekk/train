using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content;
using Train.Domain.Voxels;
using Train.Logging;

namespace Train.Tests;

[TestClass]
public sealed class VoxelConsoleCommandTests
{
    [TestMethod]
    public void Console_uses_the_development_sessions_formal_world_commands_and_query()
    {
        using var session = LoggerSession.Create();
        var source = ContentFixtures.ValidIron();
        var reloader = ContentFixtures.Create(source, session.Logger);
        Assert.AreEqual(ContentReloadStatus.Committed, reloader.Reload().Status);
        var voxelSession = new VoxelDevelopmentSession(reloader.Catalog);
        var dispatcher = new ConsoleCommandDispatcher(
            session.Logger,
            static () => { },
            static () => { },
            static () => { },
            allowReload: true,
            new VoxelConsoleCommands(voxelSession));

        dispatcher.Execute("voxel remove 4 4 4");
        Assert.IsFalse(voxelSession.Query(new WorldVoxelCoord(4, 4, 4)).IsOccupied);
        var remove = session.Memory.Snapshot().Single(item => item.EventId == ConsoleCommandDispatcher.VoxelEventId);
        StringAssert.Contains(remove.Message, "Removed");
        StringAssert.Contains(remove.Message, "empty");
        StringAssert.Contains(remove.Message, "Voxel count: 511");

        dispatcher.Execute("voxel add 4 4 4 core:iron-ore pz_py");
        var query = voxelSession.Query(new WorldVoxelCoord(4, 4, 4));
        Assert.AreEqual(VoxelOrientation.Yaw90, query.Voxel!.Orientation);
        Assert.AreEqual(100, query.Voxel.CurrentDurability);

        dispatcher.Execute("voxel query 4 4 4");
        var queried = session.Memory.Snapshot().Last(item => item.EventId == ConsoleCommandDispatcher.VoxelEventId);
        StringAssert.Contains(queried.Message, "core:iron-ore");
        StringAssert.Contains(queried.Message, "pz_py");
        StringAssert.Contains(queried.Message, "durability 100");
        StringAssert.Contains(queried.Message, "Voxel count: 512");
    }

    [TestMethod]
    public void Console_damage_reports_remaining_durability_and_uses_one_event_for_many_targets()
    {
        using var session = LoggerSession.Create();
        var source = ContentFixtures.ValidIron();
        var reloader = ContentFixtures.Create(source, session.Logger);
        Assert.AreEqual(ContentReloadStatus.Committed, reloader.Reload().Status);
        var voxelSession = new VoxelDevelopmentSession(reloader.Catalog);
        var dispatcher = new ConsoleCommandDispatcher(
            session.Logger,
            static () => { },
            static () => { },
            static () => { },
            allowReload: true,
            new VoxelConsoleCommands(voxelSession));

        dispatcher.Execute("voxel damage 4 4 4 25");
        var partial = voxelSession.Query(new WorldVoxelCoord(4, 4, 4));
        Assert.AreEqual(75, partial.Voxel!.CurrentDurability);
        Assert.AreEqual(512, voxelSession.World.VoxelCount);
        var damaged = session.Memory.Snapshot().Last(item => item.EventId == ConsoleCommandDispatcher.VoxelEventId);
        StringAssert.Contains(damaged.Message, "Damaged");
        StringAssert.Contains(damaged.Message, "25");
        StringAssert.Contains(damaged.Message, "durability 75");
        StringAssert.Contains(damaged.Message, "Voxel count: 512");

        dispatcher.Execute("voxel reset");
        dispatcher.Execute("voxel damage 4 4 4 100 5 4 4 100 6 4 4 25");
        Assert.AreEqual(510, voxelSession.World.VoxelCount);
        Assert.AreEqual(75, voxelSession.Query(new WorldVoxelCoord(6, 4, 4)).Voxel!.CurrentDurability);
        var multi = session.Memory.Snapshot().Last(item => item.EventId == ConsoleCommandDispatcher.VoxelEventId);
        StringAssert.Contains(multi.Message, "Removed");
        StringAssert.Contains(multi.Message, "Damaged");
        StringAssert.Contains(multi.Message, "durability 75");
        StringAssert.Contains(multi.Message, "Voxel count: 510");
    }

    [TestMethod]
    public void Console_damage_failures_keep_the_world_and_report_the_first_reason()
    {
        using var session = LoggerSession.Create();
        var source = ContentFixtures.ValidIron();
        var reloader = ContentFixtures.Create(source, session.Logger);
        Assert.AreEqual(ContentReloadStatus.Committed, reloader.Reload().Status);
        var voxelSession = new VoxelDevelopmentSession(reloader.Catalog);
        var dispatcher = new ConsoleCommandDispatcher(
            session.Logger,
            static () => { },
            static () => { },
            static () => { },
            allowReload: true,
            new VoxelConsoleCommands(voxelSession));

        dispatcher.Execute("voxel damage 4 4 4 0");
        dispatcher.Execute("voxel damage 16 4 4 25");
        dispatcher.Execute("voxel damage 0 0 0 25");
        dispatcher.Execute("voxel damage 4 4 4 25 0 0 0 25");
        Assert.AreEqual(512, voxelSession.World.VoxelCount);
        Assert.AreEqual(100, voxelSession.Query(new WorldVoxelCoord(4, 4, 4)).Voxel!.CurrentDurability);
        Assert.IsFalse(voxelSession.Query(new WorldVoxelCoord(0, 0, 0)).IsOccupied);

        var messages = session.Memory.Snapshot()
            .Where(item => item.EventId == ConsoleCommandDispatcher.VoxelEventId)
            .Select(item => item.Message)
            .ToArray();
        Assert.IsTrue(messages.Any(message => message.Contains("InvalidDamage", StringComparison.Ordinal)));
        Assert.IsTrue(messages.Any(message => message.Contains("OutOfBounds", StringComparison.Ordinal)));
        Assert.IsTrue(messages.Count(message => message.Contains("Empty", StringComparison.Ordinal)) >= 2);

        dispatcher.Execute("voxel damage 4 4");
        var usage = session.Memory.Snapshot().Last(item => item.EventId == ConsoleCommandDispatcher.VoxelEventId);
        StringAssert.Contains(usage.Message, "voxel damage");
        Assert.AreEqual(512, voxelSession.World.VoxelCount);
    }
}
