using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content;
using Train.Domain.Composites;
using Train.Domain.Voxels;
using Train.Logging;

namespace Train.Tests;

[TestClass]
[TestCategory("family:headless")]
[TestCategory("scope:composites")]
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
        Assert.AreEqual(WorldSaveStatus.Unavailable, voxelSession.Save("again").Status);
        Assert.AreEqual(before, voxelSession.CaptureAuthority());

        dispatcher.Execute("composite reset discard");
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
    public void Console_query_pages_every_vehicle_through_the_ring_buffer()
    {
        using var session = LoggerSession.Create();
        var source = CompositeFixtures.FormalFixturePackage();
        var reloader = ContentFixtures.Create(source, session.Logger);
        Assert.AreEqual(ContentReloadStatus.Committed, reloader.Reload().Status);
        var voxelSession = new VoxelDevelopmentSession(reloader.Catalog, VoxelWorldIdentity.Slice3, composites: reloader.Composites);
        var directory = Directory.CreateTempSubdirectory("train-fleet-query-").FullName;
        var buffer = new RingBufferLogSink();
        try
        {
            using var logger = new TrainLogger(directory, LogLevel.Info, session.Godot, buffer, readEnvironmentVariable: false);
            var dispatcher = new ConsoleCommandDispatcher(
                logger,
                buffer.Clear,
                static () => { },
                static () => { },
                allowReload: true,
                new VoxelConsoleCommands(voxelSession),
                new CompositeConsoleCommands(voxelSession));

            var manufactured = new List<EntityId>();
            for (var i = 0; i < 12; i++)
            {
                dispatcher.Execute("composite manufacture");
                var line = buffer.Snapshot().Last(item =>
                    item.EventId == ConsoleCommandDispatcher.CompositeEventId &&
                    item.Message.StartsWith("vehicle ", StringComparison.Ordinal));
                manufactured.Add(new EntityId(ulong.Parse(line.Message.Split(' ')[1])));
            }

            Assert.AreEqual(12, manufactured.Count);
            var before = voxelSession.CaptureAuthority();
            var discovered = new List<EntityId>();
            var page = 1;
            int totalPages;
            do
            {
                dispatcher.Execute(page == 1 ? "composite query" : $"composite query page {page}");
                var visible = buffer.Snapshot();
                Assert.IsTrue(visible.Count <= RingBufferLogSink.Capacity, visible.Count.ToString());
                var lines = LinesAfterLastEcho(visible, page == 1 ? "composite query" : $"composite query page {page}");
                var header = lines.Single(line => line.StartsWith("vehicles page ", StringComparison.Ordinal));
                totalPages = int.Parse(header.Split(' ')[2].Split('/')[1]);
                Assert.IsTrue(totalPages > 1);
                foreach (var line in lines.Where(item => item.StartsWith("vehicle ", StringComparison.Ordinal)))
                    discovered.Add(new EntityId(ulong.Parse(line.Split(' ')[1])));
                page++;
            }
            while (page <= totalPages);

            CollectionAssert.AreEqual(manufactured, discovered);
            Assert.IsTrue(buffer.Snapshot().Any(item =>
                item.EventId == ConsoleCommandDispatcher.CompositeEventId &&
                item.Message.StartsWith($"vehicle {manufactured[0]} ", StringComparison.Ordinal)));

            foreach (var vehicleId in manufactured)
            {
                var expected = voxelSession.QueryComposite(vehicleId)!.Parts;
                var listed = new List<string>();
                var partPage = 1;
                int partPages;
                do
                {
                    dispatcher.Execute($"composite query {vehicleId} parts {partPage}");
                    var lines = LinesAfterLastEcho(buffer.Snapshot(), $"composite query {vehicleId} parts {partPage}");
                    var header = lines.Single(line => line.StartsWith("parts page ", StringComparison.Ordinal));
                    partPages = int.Parse(header.Split(' ')[2].Split('/')[1]);
                    listed.AddRange(lines.Where(line => line.StartsWith("part ", StringComparison.Ordinal)));
                    partPage++;
                }
                while (partPage <= partPages);

                Assert.AreEqual(expected.Count, listed.Count, vehicleId.ToString());
                for (var i = 0; i < expected.Count; i++)
                {
                    var part = expected[i];
                    StringAssert.StartsWith(
                        listed[i],
                        $"part {part.Id} cell ({part.Cell.X},{part.Cell.Y},{part.Cell.Z}) {part.DefinitionId.Value} {part.Orientation} durability {part.CurrentDurability}");
                }
            }

            Assert.AreEqual(before, voxelSession.CaptureAuthority());
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    [TestMethod]
    public void Console_reset_damage_and_damaged_query_cover_the_bridge_fixture()
    {
        using var session = LoggerSession.Create();
        var reloader = ContentFixtures.Create(CompositeFixtures.FormalFixturePackage(), session.Logger);
        Assert.AreEqual(ContentReloadStatus.Committed, reloader.Reload().Status);
        var voxelSession = new VoxelDevelopmentSession(reloader.Catalog, VoxelWorldIdentity.Slice3, composites: reloader.Composites);
        var commands = new CompositeConsoleCommands(voxelSession);

        var reset = commands.Execute(new[] { "reset", "bridge" });
        StringAssert.Contains(reset, "Composite Valid.");
        StringAssert.Contains(reset, "parts 55");
        var beforeUnknown = voxelSession.CaptureAuthority();
        StringAssert.Contains(commands.Execute(new[] { "reset", "core:dev-bridge" }), "Usage:");
        Assert.AreEqual(beforeUnknown, voxelSession.CaptureAuthority());
        var vehicleId = voxelSession.QueryComposites()[0].Id;
        var before = voxelSession.CaptureAuthority();
        Assert.AreEqual(before, voxelSession.CaptureAuthority());

        var damaged = commands.Execute(new[]
        {
            "damage",
            vehicleId.ToString(),
            "3", "1", "1", "100"
        });
        StringAssert.Contains(damaged, "Composite Damaged.");
        StringAssert.Contains(damaged, "vehicles 2");
        StringAssert.Contains(damaged, "operational Disabled");
        StringAssert.Contains(damaged, "part ");
        Assert.AreEqual(2, voxelSession.QueryComposites().Count);

        voxelSession.Reset();
        voxelSession.Manufacture(HollowShellFixture.DefinitionId);
        var hollowId = voxelSession.QueryComposites()[0].Id;
        commands.Execute(new[] { "damage", hollowId.ToString(), "0", "1", "0", "25" });
        var listed = commands.Execute(new[] { "query", "damaged" });
        StringAssert.Contains(listed, "damaged 1");
        StringAssert.Contains(listed, "durability 75/100");
        var unchanged = voxelSession.CaptureAuthority();
        commands.Execute(new[] { "query", "damaged" });
        Assert.AreEqual(unchanged, voxelSession.CaptureAuthority());
    }

    [TestMethod]
    public void Console_refit_fracture_damage_line_keeps_one_vehicle_id()
    {
        using var session = LoggerSession.Create();
        var reloader = ContentFixtures.Create(CompositeFixtures.FormalFixturePackage(), session.Logger);
        Assert.AreEqual(ContentReloadStatus.Committed, reloader.Reload().Status);
        var voxelSession = new VoxelDevelopmentSession(reloader.Catalog, VoxelWorldIdentity.Slice3, composites: reloader.Composites);
        var commands = new CompositeConsoleCommands(voxelSession);

        StringAssert.Contains(commands.Execute(new[] { "reset", "bridge" }), "parts 55");
        StringAssert.Contains(
            commands.Execute(new[] { "add", "1", "3", "0", "1", HollowShellRefit.MarkerId.Value, "px_py" }),
            "Composite Valid.");
        Assert.AreEqual(56, voxelSession.QueryComposites()[0].Parts.Count);

        var extraIdUsage = commands.Execute(new[]
        {
            "damage", "1", "3", "1", "1", "100", "1", "3", "0", "1", "100"
        });
        StringAssert.Contains(extraIdUsage, "Usage:");
        Assert.AreEqual(1, voxelSession.QueryComposites().Count);
        Assert.AreEqual(56, voxelSession.QueryComposites()[0].Parts.Count);

        var cut = commands.Execute(new[] { "damage", "1", "3", "1", "1", "100", "3", "0", "1", "100" });
        StringAssert.Contains(cut, "Composite Damaged.");
        Assert.AreEqual(2, voxelSession.Vehicles.Count);
        Assert.AreEqual(0, voxelSession.World.VoxelCount);
        foreach (var successor in voxelSession.Vehicles)
            Assert.AreEqual(27, successor.Parts.Count);
    }

    [TestMethod]
    public void Console_refit_covers_five_operations_and_atomic_swap()
    {
        using var session = LoggerSession.Create();
        var reloader = ContentFixtures.Create(CompositeFixtures.FormalFixturePackage(), session.Logger);
        Assert.AreEqual(ContentReloadStatus.Committed, reloader.Reload().Status);
        var voxelSession = new VoxelDevelopmentSession(reloader.Catalog, VoxelWorldIdentity.Slice3, composites: reloader.Composites);
        var commands = new CompositeConsoleCommands(voxelSession);
        var manufactured = voxelSession.Manufacture(HollowShellFixture.DefinitionId);
        Assert.IsTrue(manufactured.Succeeded);
        var vehicleId = manufactured.VehicleId!.Value.ToString();

        var added = commands.Execute(new[] { "add", vehicleId, "2", "1", "0", HollowShellRefit.MarkerId.Value, "px_py" });
        StringAssert.Contains(added, "Composite Valid.");
        var p = AllocatedId(added);
        commands.Execute(new[] { "damage", vehicleId, "2", "1", "0", "25" });
        StringAssert.Contains(commands.Execute(new[] { "move", vehicleId, p.ToString(), "3", "1", "0" }), "allocated none");
        StringAssert.Contains(commands.Execute(new[] { "rotate", vehicleId, p.ToString(), "nz_py" }), "Valid");
        var addedQ = commands.Execute(new[] { "add", vehicleId, "4", "1", "0", HollowShellRefit.MarkerId.Value, "px_py" });
        var q = AllocatedId(addedQ);
        Assert.AreNotEqual(p, q);

        var sequential = commands.Execute(new[] { "move", vehicleId, p.ToString(), "4", "1", "0" });
        StringAssert.Contains(sequential, "DuplicateOccupancy");
        Assert.AreEqual(p, voxelSession.QueryComposite(manufactured.VehicleId!.Value)!.Parts.Single(part => part.Cell == HollowShellRefit.Moved).Id);

        var swapped = commands.Execute(new[]
        {
            "batch", vehicleId,
            "move", p.ToString(), "4", "1", "0",
            "move", q.ToString(), "3", "1", "0"
        });
        StringAssert.Contains(swapped, "Composite Valid.");
        Assert.AreEqual(p, voxelSession.QueryComposite(manufactured.VehicleId.Value)!.Parts.Single(part => part.Cell == HollowShellRefit.SecondAdd).Id);
        Assert.AreEqual(q, voxelSession.QueryComposite(manufactured.VehicleId.Value)!.Parts.Single(part => part.Cell == HollowShellRefit.Moved).Id);

        var replaced = commands.Execute(new[] { "replace", vehicleId, "3", "1", "0", HollowShellRefit.ReplacementId.Value, "px_py" });
        var r = AllocatedId(replaced);
        Assert.AreNotEqual(q, r);
        Assert.AreEqual(150, voxelSession.QueryComposite(manufactured.VehicleId.Value)!.Parts.Single(part => part.Id == r).CurrentDurability);
        StringAssert.Contains(commands.Execute(new[] { "remove", vehicleId, r.ToString() }), "allocated none");

        var final = voxelSession.QueryComposite(manufactured.VehicleId.Value)!;
        Assert.AreEqual(p, final.Parts.Single(part => part.Cell == HollowShellRefit.SecondAdd).Id);
        Assert.AreEqual(75, final.Parts.Single(part => part.Id == p).CurrentDurability);
        Assert.AreEqual(HollowShellRefit.MarkerOrientation, final.Parts.Single(part => part.Id == p).Orientation);
        Assert.AreEqual(169, final.Parts.Count);

        var before = voxelSession.CaptureAuthority();
        var rejected = commands.Execute(new[]
        {
            "batch", vehicleId,
            "rotate", p.ToString(), "px_py",
            "add", "8", "1", "0", HollowShellRefit.MarkerId.Value, "px_py"
        });
        StringAssert.Contains(rejected, "EnvelopeExceeded");
        Assert.AreEqual(before, voxelSession.CaptureAuthority());
        Assert.AreEqual(HollowShellRefit.MarkerOrientation, voxelSession.QueryComposite(manufactured.VehicleId.Value)!.Parts.Single(part => part.Id == p).Orientation);
        StringAssert.Contains(commands.Execute(new[] { "move", vehicleId, p.ToString(), "3", "1", "0" }), "Valid");
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

    private static EntityId AllocatedId(string text)
    {
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Single(item => item.StartsWith("allocated ", StringComparison.Ordinal));
        var token = line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1];
        Assert.AreNotEqual("none", token);
        return new EntityId(ulong.Parse(token));
    }

    private static string Recent(LoggerSession session, string eventId, int span = 12)
    {
        var snapshot = session.Memory.Snapshot();
        var last = snapshot.Last(item => item.EventId == eventId).Sequence;
        return string.Join("\n", snapshot.Where(item => item.EventId == eventId && item.Sequence >= last - span).Select(item => item.Message));
    }

    private static IReadOnlyList<string> LinesAfterLastEcho(IReadOnlyList<LogEvent> snapshot, string command)
    {
        var echo = snapshot.Last(item =>
            item.EventId == ConsoleCommandDispatcher.EchoEventId &&
            item.Message == command);
        return snapshot
            .Where(item => item.Sequence > echo.Sequence && item.EventId == ConsoleCommandDispatcher.CompositeEventId)
            .Select(item => item.Message)
            .ToArray();
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, true);
        }
        catch
        {
            // Best-effort cleanup for temp directories that Windows may still have open.
        }
    }
}
