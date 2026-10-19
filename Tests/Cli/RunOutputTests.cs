using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;

namespace Train.Cli.Tests;

[TestClass]
[TestCategory("fast")]
public sealed class RunOutputTests
{
    [TestMethod]
    public void Node_position_json_preserves_distinct_entry_and_departure_tracks()
    {
        var position = new TrainPosition.Node(new(3), new(7), 2, new(11), 1,
            NodeTravelPhase.Departing, 150, null);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(TrainPositionSample.From(position), Recipe.Json));
        var sample = json.RootElement;
        Assert.AreEqual(7UL, sample.GetProperty("entered_from").GetUInt64());
        Assert.AreEqual(2, sample.GetProperty("entered_track").GetInt32());
        Assert.AreEqual(11UL, sample.GetProperty("next_edge").GetUInt64());
        Assert.AreEqual(1, sample.GetProperty("next_track").GetInt32());

        using var settlement = JsonDocument.Parse(JsonSerializer.Serialize(new SettlementSample(6, 14, "NonHostile", false, 4, [], [], [], [], 0, []), Recipe.Json));
        Assert.AreEqual("NonHostile", settlement.RootElement.GetProperty("relation").GetString());
        Assert.IsFalse(settlement.RootElement.GetProperty("surrendered").GetBoolean());
        using var node = JsonDocument.Parse(JsonSerializer.Serialize(new NodeOccupantSample(1, "lair", 2), Recipe.Json));
        Assert.AreEqual("lair", node.RootElement.GetProperty("occupant").GetString());
        Assert.AreEqual(2UL, node.RootElement.GetProperty("subject").GetUInt64());
        using var vehicle = JsonDocument.Parse(JsonSerializer.Serialize(new VehicleConditionSample(8, 40, 100, 1, 2), Recipe.Json));
        Assert.AreEqual(40L, vehicle.RootElement.GetProperty("structure_current").GetInt64());
        Assert.AreEqual(100L, vehicle.RootElement.GetProperty("structure_maximum").GetInt64());
        Assert.AreEqual(1, vehicle.RootElement.GetProperty("damaged_parts").GetInt32());
        Assert.AreEqual(2, vehicle.RootElement.GetProperty("destroyed_parts").GetInt32());
        using var job = JsonDocument.Parse(JsonSerializer.Serialize(
            new CommissionJobSample(3, "Repair", "Working", 0, 4, 1, 0.25, 0, [], [], []), Recipe.Json));
        Assert.AreEqual("Repair", job.RootElement.GetProperty("kind").GetString());
    }
}
