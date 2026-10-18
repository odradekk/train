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
    }
}
