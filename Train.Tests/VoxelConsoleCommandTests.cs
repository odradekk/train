using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content;
using Train.Domain.Commands;
using Train.Domain.WorldEditor;
using Train.Domain.Voxels;
using Train.Logging;

namespace Train.Tests;

[TestClass]
[TestCategory("family:headless")]
[TestCategory("scope:voxels")]
public sealed class VoxelConsoleCommandTests
{
    [TestMethod]
    public void Console_save_reports_that_the_editor_draft_is_retained_and_excluded()
    {
        using var harness = WorldEditorHarness.Content();
        Assert.IsTrue(BlankWorldSource.TryIdentity(16, 8, 16, 12, out var identity));
        harness.Attach(BlankWorldSource.Establish(identity, harness.Catalog, harness.Composites, harness.Store));
        var session = harness.Session;
        using var tool = new WorldEditorVoxelTool(session);
        var cell = new WorldVoxelCoord(1, 2, 1);
        tool.SetExact(cell, WorldEditorSequences.EditVoxelId, VoxelOrientation.Identity, VoxelCellOperation.Add);

        var result = new VoxelConsoleCommands(session).Execute(["save", "Draft excluded"]);

        StringAssert.Contains(result, "Voxel Saved.");
        StringAssert.Contains(result, "draft retained; not included in save");
        Assert.IsTrue(session.HasEditorDraft);
        Assert.IsFalse(session.Query(cell).IsOccupied);
        var restored = new VoxelDevelopmentSession(harness.Catalog, session.Identity, harness.Store, harness.Composites);
        try
        {
            Assert.IsTrue(restored.Load(session.SaveId!.Value).Succeeded);
            Assert.IsFalse(restored.Query(cell).IsOccupied);
            Assert.IsFalse(restored.HasEditorDraft);
        }
        finally
        {
            restored.Detach();
        }
    }

    [TestMethod]
    public void Console_cell_commands_submit_formal_add_replace_remove_edits()
    {
        using var logger = LoggerSession.Create();
        var source = ContentFixtures.ValidFerrousOre();
        var reloader = ContentFixtures.Create(source, logger.Logger);
        Assert.AreEqual(ContentReloadStatus.Committed, reloader.Reload().Status);
        var voxelSession = new VoxelDevelopmentSession(reloader.Catalog);
        var dispatcher = new ConsoleCommandDispatcher(
            logger.Logger,
            static () => { },
            static () => { },
            static () => { },
            allowReload: true,
            new VoxelConsoleCommands(voxelSession));

        dispatcher.Execute("voxel cell add 0 0 0 core:ferrous-ore px_py");
        dispatcher.Execute("voxel cell replace 0 0 0 core:ferrous-ore pz_py");
        dispatcher.Execute("voxel cell remove 0 0 0 at 2");

        var records = voxelSession.QueryEditLog();
        Assert.AreEqual(2, records.Count);
        Assert.IsTrue(records.All(record => record.Edit.Kind == WorldEditKind.VoxelCellSet));
        Assert.AreEqual("Applied", records[0].Status);
        Assert.AreEqual(VoxelOrientation.Yaw90, voxelSession.Query(new WorldVoxelCoord(0, 0, 0)).Voxel!.Orientation);
        var pending = voxelSession.QueryPendingEdits().Single();
        Assert.AreEqual(WorldEditKind.VoxelCellSet, pending.Edit.Kind);
        Assert.AreEqual(2UL, pending.TargetStep);

        var messages = string.Join('\n', logger.Memory.Snapshot()
            .Where(item => item.EventId == ConsoleCommandDispatcher.VoxelEventId)
            .Select(item => item.Message));
        StringAssert.Contains(messages, "Edit 1 voxel.cell-set executed Applied");
        StringAssert.Contains(messages, "Edit 2 voxel.cell-set executed Applied");
        StringAssert.Contains(messages, "Edit 3 voxel.cell-set pending");
    }

    [TestMethod]
    public void Console_cell_rejects_invalid_syntax_without_consuming_an_edit_id()
    {
        using var logger = LoggerSession.Create();
        var source = ContentFixtures.ValidFerrousOre();
        var reloader = ContentFixtures.Create(source, logger.Logger);
        Assert.AreEqual(ContentReloadStatus.Committed, reloader.Reload().Status);
        var voxelSession = new VoxelDevelopmentSession(reloader.Catalog);
        var commands = new VoxelConsoleCommands(voxelSession);

        StringAssert.Contains(commands.Execute(["cell", "replace", "4", "4", "4", "core:ferrous-ore"]), "Usage:");
        StringAssert.Contains(commands.Execute(["cell", "remove", "4", "4", "4", "at", "soon"]), "Usage:");
        Assert.AreEqual(1UL, voxelSession.NextEditId);
        Assert.AreEqual(0UL, voxelSession.EditLogCount);
    }

    [TestMethod]
    public void Console_uses_the_development_sessions_formal_world_commands_and_query()
    {
        using var session = LoggerSession.Create();
        var source = ContentFixtures.ValidFerrousOre();
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
        var removed = string.Join("\n", session.Memory.Snapshot()
            .Where(item => item.EventId == ConsoleCommandDispatcher.VoxelEventId)
            .Select(item => item.Message));
        StringAssert.Contains(removed, "Removed");
        StringAssert.Contains(removed, "empty");
        StringAssert.Contains(removed, "Voxel count: 511");

        dispatcher.Execute("voxel add 4 4 4 core:ferrous-ore pz_py");
        var query = voxelSession.Query(new WorldVoxelCoord(4, 4, 4));
        Assert.AreEqual(VoxelOrientation.Yaw90, query.Voxel!.Orientation);
        Assert.AreEqual(100, query.Voxel.CurrentDurability);
        Assert.AreEqual(100, query.MaxDurability);

        var beforeQuery = session.Memory.Snapshot().Count;
        dispatcher.Execute("voxel query 4 4 4");
        var queried = string.Join("\n", session.Memory.Snapshot()
            .Skip(beforeQuery)
            .Where(item => item.EventId == ConsoleCommandDispatcher.VoxelEventId)
            .Select(item => item.Message));
        StringAssert.Contains(queried, "core:ferrous-ore");
        StringAssert.Contains(queried, "pz_py");
        StringAssert.Contains(queried, "durability 100");
        StringAssert.Contains(queried, "value 100");
        StringAssert.Contains(queried, "max 100");
        StringAssert.Contains(queried, "Voxel count: 512");
    }

    [TestMethod]
    public void Console_damage_keeps_multi_target_display_inside_format_bounds()
    {
        using var session = LoggerSession.Create();
        var source = ContentFixtures.ValidFerrousOre();
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

        var beforePartial = session.Memory.Snapshot().Count;
        dispatcher.Execute("voxel damage 4 4 4 25");
        var partial = voxelSession.Query(new WorldVoxelCoord(4, 4, 4));
        Assert.AreEqual(75, partial.Voxel!.CurrentDurability);
        Assert.AreEqual(512, voxelSession.World.VoxelCount);
        AssertDamageDisplay(session, beforePartial, "Damaged", "25", "durability 75", "Voxel count: 512");

        dispatcher.Execute("voxel reset discard");
        var beforeMulti = session.Memory.Snapshot().Count;
        dispatcher.Execute("voxel damage 4 4 4 100 5 4 4 100 6 4 4 25");
        Assert.AreEqual(510, voxelSession.World.VoxelCount);
        Assert.AreEqual(75, voxelSession.Query(new WorldVoxelCoord(6, 4, 4)).Voxel!.CurrentDurability);
        AssertDamageDisplay(session, beforeMulti, "Removed", "Damaged", "durability 75", "Voxel count: 510");
    }

    [TestMethod]
    public void Console_damage_keeps_legal_long_mod_id_and_counts_inside_format_bounds()
    {
        using var session = LoggerSession.Create();
        var source = ContentFixtures.ValidFerrousOre();
        var localId = new string('a', VoxelDefinitionId.PartMaxLength);
        var definitionId = $"{ModFixtures.SampleUuid}:{localId}";
        source.AddMod("review-long-id", ModFixtures.SampleUuid);
        source.AddVoxelFile(
            ModFixtures.SampleUuid,
            "long.voxels.json",
            ContentFixtures.GoodsFile(ContentFixtures.Good(localId, "Long ID", "Review")));
        source.AddImage(ModFixtures.SampleUuid, "iron.png", ContentFixtures.Square(16));
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

        dispatcher.Execute($"voxel add 0 0 0 {definitionId} px_py");
        Assert.AreEqual(513, voxelSession.World.VoxelCount);
        var beforeDamage = session.Memory.Snapshot().Count;
        dispatcher.Execute("voxel damage 0 0 0 1");
        Assert.AreEqual(513, voxelSession.World.VoxelCount);
        Assert.AreEqual(99, voxelSession.Query(new WorldVoxelCoord(0, 0, 0)).Voxel!.CurrentDurability);
        AssertDamageDisplay(session, beforeDamage, definitionId, "Damaged", "by 1", "durability 99", "Voxel count: 513");
    }

    [TestMethod]
    public void Console_damage_failures_keep_the_world_and_report_the_first_reason()
    {
        using var session = LoggerSession.Create();
        var source = ContentFixtures.ValidFerrousOre();
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

    private static void AssertDamageDisplay(LoggerSession session, int beforeCount, params string[] required)
    {
        var events = session.Memory.Snapshot()
            .Skip(beforeCount)
            .Where(item => item.EventId == ConsoleCommandDispatcher.VoxelEventId)
            .ToArray();
        Assert.IsTrue(events.Length > 0);
        foreach (var item in events)
            Assert.IsTrue(item.Message.Length <= 200, item.Message);
        var displayed = events.Select(LogLevelText.FormatConsole).ToArray();
        var display = string.Join("\n", displayed);
        foreach (var fragment in required)
            StringAssert.Contains(display, fragment);
        foreach (var fragment in required.Where(item => item.StartsWith("Voxel count:", StringComparison.Ordinal) ||
                                                       item.StartsWith("durability ", StringComparison.Ordinal)))
        {
            var prefix = fragment[..fragment.LastIndexOf(' ')];
            Assert.IsFalse(
                displayed.Any(line => line.Contains(prefix, StringComparison.Ordinal) &&
                                      !line.Contains(fragment, StringComparison.Ordinal)),
                display);
        }
    }
}
