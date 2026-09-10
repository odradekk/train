using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content;
using Train.Domain.Composites;
using Train.Domain.Voxels;
using Train.Logging;

namespace Train.Tests;

[TestClass]
public sealed class CompositeConsoleCommandTests
{
    [TestMethod]
    public void Console_manufacture_query_and_reset_use_the_session_entry_points()
    {
        using var session = LoggerSession.Create();
        var source = CompositeFixtures.FormalFixturePackage();
        var reloader = ContentFixtures.Create(source, session.Logger);
        Assert.AreEqual(ContentReloadStatus.Committed, reloader.Reload().Status);
        var voxelSession = new VoxelDevelopmentSession(reloader.Catalog, VoxelWorldIdentity.Slice3, composites: reloader.Composites);
        var dispatcher = new ConsoleCommandDispatcher(
            session.Logger,
            static () => { },
            static () => { },
            static () => { },
            allowReload: true,
            new VoxelConsoleCommands(voxelSession),
            new CompositeConsoleCommands(voxelSession));

        dispatcher.Execute("help");
        var help = session.Memory.Snapshot().Single(item => item.EventId == ConsoleCommandDispatcher.HelpEventId);
        StringAssert.Contains(help.Message, "composite");

        dispatcher.Execute("composite manufacture");
        var manufactured = voxelSession.QueryComposites();
        Assert.AreEqual(1, manufactured.Count);
        Assert.AreEqual(168, manufactured[0].Parts.Count);
        var vehicleId = manufactured[0].Id;
        var manufactureLog = Recent(session, ConsoleCommandDispatcher.CompositeEventId);
        StringAssert.Contains(manufactureLog, "Valid");
        StringAssert.Contains(manufactureLog, $"vehicle {vehicleId}");
        StringAssert.Contains(manufactureLog, "parts 168");

        dispatcher.Execute($"composite query {vehicleId}");
        var queryLog = Recent(session, ConsoleCommandDispatcher.CompositeEventId, 80);
        StringAssert.Contains(queryLog, HollowShellFixture.DefinitionId.Value);
        StringAssert.Contains(queryLog, "identity Retained");
        StringAssert.Contains(queryLog, "operational Unevaluated");
        StringAssert.Contains(queryLog, "8,8,8");
        StringAssert.Contains(queryLog, "contact 1,-0.75,1.5");
        StringAssert.Contains(queryLog, "cell (1,0,1)");
        StringAssert.Contains(queryLog, "parts page 1/");

        var before = voxelSession.CaptureAuthority();
        dispatcher.Execute("voxel save Should Fail");
        Assert.AreEqual(WorldSaveStatus.UnsupportedVehicles, voxelSession.Save("again").Status);
        Assert.AreEqual(before, voxelSession.CaptureAuthority());

        dispatcher.Execute("composite reset");
        Assert.AreEqual(0, voxelSession.QueryComposites().Count);
        Assert.AreEqual(1UL, voxelSession.NextEntityId);
        Assert.AreEqual(0UL, voxelSession.TransactionCount);
        StringAssert.Contains(Recent(session, ConsoleCommandDispatcher.CompositeEventId), "vehicles 0");
    }

    [TestMethod]
    public void Console_query_pages_every_part_from_the_manufacture_result()
    {
        using var session = LoggerSession.Create();
        var source = CompositeFixtures.FormalFixturePackage();
        var reloader = ContentFixtures.Create(source, session.Logger);
        Assert.AreEqual(ContentReloadStatus.Committed, reloader.Reload().Status);
        var voxelSession = new VoxelDevelopmentSession(reloader.Catalog, VoxelWorldIdentity.Slice3, composites: reloader.Composites);
        var commands = new CompositeConsoleCommands(voxelSession);
        var manufactured = voxelSession.Manufacture(HollowShellFixture.DefinitionId);
        Assert.IsTrue(manufactured.Succeeded);
        var before = voxelSession.CaptureAuthority();
        var vehicleId = manufactured.VehicleId!.Value;
        var expected = manufactured.Parts!.OrderBy(part => part.Cell).ToArray();

        var listed = new List<string>();
        var page = 1;
        int totalPages;
        do
        {
            var text = commands.Execute(new[] { "query", vehicleId.ToString(), "parts", page.ToString() });
            var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var header = lines.Single(line => line.StartsWith("parts page ", StringComparison.Ordinal));
            var parts = header.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            totalPages = int.Parse(parts[2].Split('/')[1]);
            listed.AddRange(lines.Where(line => line.StartsWith("part ", StringComparison.Ordinal)));
            page++;
        }
        while (page <= totalPages);

        Assert.AreEqual(expected.Length, listed.Count);
        for (var i = 0; i < expected.Length; i++)
        {
            var part = expected[i];
            StringAssert.StartsWith(
                listed[i],
                $"part {part.Id} cell ({part.Cell.X},{part.Cell.Y},{part.Cell.Z}) {part.DefinitionId.Value} {part.Orientation} durability {part.CurrentDurability}");
        }

        var unknownPage = commands.Execute(new[] { "query", vehicleId.ToString(), "parts", (totalPages + 1).ToString() });
        StringAssert.Contains(unknownPage, "parts page");
        Assert.AreEqual(before, voxelSession.CaptureAuthority());
    }

    [TestMethod]
    public void Illegal_manufacture_command_does_not_change_authority()
    {
        using var session = LoggerSession.Create();
        var reloader = ContentFixtures.Create(CompositeFixtures.FormalFixturePackage(), session.Logger);
        Assert.AreEqual(ContentReloadStatus.Committed, reloader.Reload().Status);
        var voxelSession = new VoxelDevelopmentSession(reloader.Catalog, VoxelWorldIdentity.Slice3, composites: reloader.Composites);
        var dispatcher = new ConsoleCommandDispatcher(
            session.Logger,
            static () => { },
            static () => { },
            static () => { },
            allowReload: true,
            new VoxelConsoleCommands(voxelSession),
            new CompositeConsoleCommands(voxelSession));
        var before = voxelSession.CaptureAuthority();
        dispatcher.Execute("composite manufacture core:missing-car");
        Assert.AreEqual(before, voxelSession.CaptureAuthority());
        StringAssert.Contains(Recent(session, ConsoleCommandDispatcher.CompositeEventId), "UnknownDefinition");
    }

    [TestMethod]
    public void Composite_commands_require_the_development_scene()
    {
        using var harness = DispatchHarness.Create(allowReload: true);
        harness.Dispatcher.Execute("composite query");
        var warning = harness.ConsoleEvents.Single(item => item.EventId == ConsoleCommandDispatcher.CompositeUnavailableEventId);
        StringAssert.Contains(warning.Message, "voxel development scene");
    }

    private static string Recent(LoggerSession session, string eventId, int span = 12)
    {
        var snapshot = session.Memory.Snapshot();
        var last = snapshot.Last(item => item.EventId == eventId).Sequence;
        return string.Join("\n", snapshot.Where(item => item.EventId == eventId && item.Sequence >= last - span).Select(item => item.Message));
    }
}
