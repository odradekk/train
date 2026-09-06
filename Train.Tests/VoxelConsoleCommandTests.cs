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

        dispatcher.Execute("voxel add 4 4 4 core:iron-ore pz_py");
        var query = voxelSession.Query(new WorldVoxelCoord(4, 4, 4));
        Assert.AreEqual(VoxelOrientation.Yaw90, query.Voxel!.Orientation);
        Assert.AreEqual(100, query.Voxel.CurrentDurability);

        dispatcher.Execute("voxel query 4 4 4");
        var queried = session.Memory.Snapshot().Last(item => item.EventId == ConsoleCommandDispatcher.VoxelEventId);
        StringAssert.Contains(queried.Message, "core:iron-ore");
        StringAssert.Contains(queried.Message, "pz_py");
        StringAssert.Contains(queried.Message, "durability 100");
    }
}
