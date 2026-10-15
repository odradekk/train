using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.Sessions;
using Train.Core.Tests.World;
using static Train.Core.Tests.Settlements.SettlementFixture;

namespace Train.Core.Tests.Settlements;

[TestClass]
public sealed class SettlementRecoveryTests
{
    [TestMethod]
    public void Nonzero_remainder_round_trips_and_continued_resolution_matches_uninterrupted()
    {
        var state = State([new(Fuel, 1)]);
        state = state with { World = Resolve(state, [Work(rate: 30)]).World };
        var saved = new CoreCapture(state);
        var decoded = WorldFixture.Success(CoreFactory.Create().DecodeState(saved.Encode()));
        Assert.AreEqual(saved.Encode(), decoded.Encode());
        Assert.AreEqual(saved.Checkpoint, decoded.Checkpoint);
        using var candidate = WorldFixture.Success(CoreFactory.Create().PrepareRestore(decoded, state.Content, WorldFixture.Options));
        using var restored = (CoreSession)candidate.Activate();
        var expected = Resolve(state, [Work()]);
        var actual = Resolve(restored.State, [Work()]);
        Assert.AreEqual(60, actual.Work.Single().WorkPerSecond);
        Assert.AreEqual(.5, Remainder(actual.World), 1e-12);
        Assert.AreEqual(new CoreCapture(state with { World = expected.World }).Checkpoint,
            new CoreCapture(restored.State with { World = actual.World }).Checkpoint);
        var changed = new CoreCapture(state with { World = actual.World });
        var comparison = (CheckpointComparison.Different)WorldFixture.Success(CoreFactory.Create().CompareCheckpoints(saved.Checkpoint, changed.Checkpoint));
        Assert.AreEqual("/world/settlements/0/fuel_energy_remaining", comparison.Difference.Field);
    }

    [TestMethod]
    [DataRow(4d, 60d, 3.5, 0L)]
    [DataRow(0d, 30d, 0d, 1L)]
    public void Publication_changes_only_new_fuel_units(double newEnergy, double secondRate, double remainder, long stock)
    {
        var state = State([new(Fuel, 2)]);
        state = state with { World = Resolve(state, [Work(rate: 30)]).World };
        using var session = new CoreSession(state);
        var content = state.Content with
        {
            Identity = new(new string('A', 64)),
            Goods = state.Content.Goods.Select(g => g.Good == Fuel ? g with
                { FuelEnergyPerUnit = newEnergy == 0 ? null : newEnergy } : g).ToImmutableArray()
        };
        var published = (SubmissionResult.Completed)session.Submit(new CoreCommand.PublishContent(content), CommandSource.AutomationTest);
        Assert.IsInstanceOfType<CommandDecision.Accepted>(published.Receipt.Decision);
        Assert.AreEqual(1.5, Remainder(session.State.World));
        var first = Resolve(session.State, [Work()]);
        Assert.AreEqual(60, first.Work.Single().WorkPerSecond);
        Assert.AreEqual(.5, Remainder(first.World), 1e-12);
        var second = Resolve(session.State with { World = first.World }, [Work()]);
        Assert.AreEqual(secondRate, second.Work.Single().WorkPerSecond, 1e-12);
        Assert.AreEqual(remainder, Remainder(second.World), 1e-12);
        Assert.AreEqual(stock, Stock(second.World, Fuel));
    }

    [TestMethod]
    public void Removing_generation_preserves_the_pool_but_prevents_delivery()
    {
        var state = State([new(Fuel, 2)]);
        state = state with { World = Resolve(state, [Work(rate: 30)]).World };
        using var session = new CoreSession(state);
        var content = state.Content with
        {
            Identity = new(new string('A', 64)),
            Designs = state.Content.Designs.Select(d => d with { Parts = d.Parts.Select(p => p with
                { Capabilities = p.Capabilities.Where(c => c is not PartCapability.Power).ToImmutableArray() }).ToImmutableArray() }).ToImmutableArray()
        };
        var published = (SubmissionResult.Completed)session.Submit(new CoreCommand.PublishContent(content), CommandSource.AutomationTest);
        Assert.IsInstanceOfType<CommandDecision.Accepted>(published.Receipt.Decision);
        var result = Resolve(session.State, [Work()]);
        Assert.AreEqual(0, result.Work.Single().WorkPerSecond);
        Assert.AreEqual(1.5, Remainder(result.World));
        Assert.AreEqual(1L, Stock(result.World, Fuel));
    }

    [TestMethod]
    public void Publication_and_commandless_tail_preserve_remainder_in_strict_replay()
    {
        var state = State([new(Fuel, 1)]);
        state = state with { World = Resolve(state, [Work(rate: 30)]).World };
        using var session = new CoreSession(state);
        var content = state.Content with { Identity = new(new string('A', 64)) };
        session.Submit(new CoreCommand.PublishContent(content), CommandSource.AutomationTest);
        var published = WorldFixture.Success(session.Capture(CapturePurpose.Save));
        Assert.IsNull(session.Advance(2).Fault);
        var last = WorldFixture.Success(session.Capture(CapturePurpose.Save));
        var recording = session.ReadRecording();
        var input = new CoreReplayInput(2, WorldFixture.Options.Runtime, recording.Starts[0].State.Encode(), recording.Commands,
            [published.Checkpoint, last.Checkpoint], recording.EndPosition, null);
        using var replay = WorldFixture.Success(CoreFactory.Create().PrepareReplay(input, WorldFixture.Options.Runtime, [state.Content, content]));
        foreach (var sample in new[] { published, last })
        {
            var result = replay.Advance(10, sample.Position);
            Assert.IsNull(result.Difference);
            Assert.IsNull(result.Fault);
            Assert.AreEqual(sample.Encode(), replay.CaptureBoundary().Encode());
        }
        Assert.AreEqual(1.5, Remainder(session.State.World));
    }

    [TestMethod]
    [DataRow("-1", "/world/settlements/fuel_energy_remaining")]
    [DataRow("1e999", "/world/settlements/fuel_energy_remaining")]
    [DataRow("\"NaN\"", "/world")]
    [DataRow("null", "/world")]
    public void Invalid_fuel_remainders_are_rejected(string value, string field)
    {
        var payload = new CoreCapture(State()).Encode().Replace("\"fuel_energy_remaining\":0", "\"fuel_energy_remaining\":" + value, StringComparison.Ordinal);
        var failure = (CoreResult<ICoreCapture>.Failure)CoreFactory.Create().DecodeState(payload);
        Assert.AreEqual("invalid_state", failure.Problems.Single().Code);
        Assert.AreEqual(field, failure.Problems.Single().Field);
    }

    [TestMethod]
    public void Missing_fuel_remainder_is_not_silently_reset_on_decode()
    {
        var root = JsonNode.Parse(new CoreCapture(State()).Encode())!;
        root["world"]!["settlements"]![0]!.AsObject().Remove("fuel_energy_remaining");
        var failure = (CoreResult<ICoreCapture>.Failure)CoreFactory.Create().DecodeState(root.ToJsonString());
        Assert.AreEqual("invalid_state", failure.Problems.Single().Code);
        Assert.AreEqual("/world", failure.Problems.Single().Field);
    }
}
