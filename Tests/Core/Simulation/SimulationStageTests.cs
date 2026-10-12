using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Train.Core.Tests.Simulation.SimulationProcessTests;

namespace Train.Core.Tests.Simulation;

[TestClass]
[TestCategory("stage")]
public sealed class SimulationStageTests
{
    [TestMethod]
    public async Task Recorded_commands_replay_from_disk_in_a_new_process_at_the_same_checkpoints()
    {
        var directory = CreateEvidenceDirectory();
        var recording = Path.Combine(directory, "commands.json");
        var original = await RunProcess(directory, "original", "record", "steady", payload: recording);
        var replay = await RunProcess(directory, "replay", "replay", "steady", input: recording);

        AssertIdentity(original.Identity);
        Assert.AreEqual(original.Identity, replay.Identity);
        AssertTraceComparison(original, replay);
        CollectionAssert.AreEqual(original.Executions.ToArray(), replay.Executions.ToArray());
        CollectionAssert.AreEqual(original.CommandRecords.ToArray(), replay.CommandRecords.ToArray());
        Directory.Delete(directory, recursive: true);
    }

    [TestMethod]
    public async Task Every_step_saved_to_disk_and_restored_matches_continuous_execution()
    {
        var directory = CreateEvidenceDirectory();
        var saves = Path.Combine(directory, "saves");
        var original = await RunProcess(directory, "original", "record", "steady",
            payload: Path.Combine(directory, "commands.json"));
        var restored = await RunProcess(directory, "restored", "heavy", "steady", payload: saves);

        AssertIdentity(original.Identity);
        Assert.AreEqual(original.Identity, restored.Identity);
        AssertTraceComparison(original, restored);
        CollectionAssert.AreEqual(original.Executions.ToArray(), restored.Executions.ToArray());
        CollectionAssert.AreEqual(new ulong[] { 1, 2, 3, 4 },
            restored.Samples.Where(point => point.Sequence == 0).Select(point => point.CompletedSteps).ToArray());
        Assert.AreEqual(4, Directory.GetFiles(saves, "*.core-state.json").Length);
        Directory.Delete(directory, recursive: true);
    }
}
