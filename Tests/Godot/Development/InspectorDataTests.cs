using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.App.Development;
using Train.Core.Contracts;
using Train.Core.ContractSupport;

namespace Train.Godot.Tests.Development;

[TestClass]
public sealed class InspectorDataTests
{
    [TestMethod]
    public void ObjectInspectionPreservesNestedRuntimeFieldsAndMissingIdentity()
    {
        var id = new WorldObjectId.Node(new(7));
        var node = new NodeView(new(new(7), new(1, 2), new(new("test:terrain"), 0, []),
            new NodeOccupant.Settlement(new(9), SettlementRelation.PlayerOwned), [], 0, 1, null), [], 80, 0, 80);
        var view = new FixedCoreReadView
        {
            Nodes = new Dictionary<NodeId, Lookup<NodeView>> { [id.Id] = new Lookup<NodeView>.Found(node) }
        };
        Assert.IsTrue(InspectorData.Exists(view, id));
        Assert.IsFalse(InspectorData.Exists(view, new WorldObjectId.Node(new(8))));
        var found = InspectorData.Inspect(view, id);
        Assert.AreEqual(InspectionStatus.Found, found.Status);
        using var json = JsonDocument.Parse(found.Json!);
        Assert.AreEqual(9UL, json.RootElement.GetProperty("Map").GetProperty("Occupant")
            .GetProperty("Id").GetProperty("Value").GetUInt64());
        var missing = InspectorData.Inspect(new FixedCoreReadView
        {
            Nodes = new Dictionary<NodeId, Lookup<NodeView>> { [id.Id] = new Lookup<NodeView>.Missing() }
        }, id);
        Assert.AreEqual(id, missing.Id);
        Assert.AreEqual(InspectionStatus.Missing, missing.Status);
        var unavailable = InspectorData.Inspect(new FixedCoreReadView(), new WorldObjectId.Settlement(new(9)));
        Assert.IsFalse(InspectorData.Exists(new FixedCoreReadView(), new WorldObjectId.Settlement(new(9))));
        Assert.AreEqual(InspectionStatus.Unavailable, unavailable.Status);
        Assert.IsNotNull(unavailable.Explanation);
    }

    [TestMethod]
    public void CommandFilterUsesPayloadTargetsAndReceiptSubjectsAndKeepsFutureKind()
    {
        var edge = new WorldObjectId.Edge(new(8));
        var created = new WorldObjectId.Train(new(12));
        const string payload = "{\"input_format_version\":2,\"kind\":\"rail.debug_add_break\",\"edge\":8,\"track\":1,\"distance_meters\":0}";
        var commands = ImmutableArray.Create(
            Record(payload, new CommandDecision.Accepted([created], [])),
            Record("{\"kind\":\"future.command\",\"extra\":42}", new CommandDecision.Rejected(
                [new("invalid_state", "Target", edge, [])])),
            Record("{\"kind\":\"content.publish\"}", new CommandDecision.Faulted(new("fault", new(0, 0), "Submit", "Target", edge))));
        var filtered = InspectorData.ReadCommands(commands, "rail.debug_add_break", "accepted", edge);
        Assert.AreEqual(1, filtered.Length);
        Assert.AreEqual(payload, filtered[0].Record.CanonicalPayload);
        Assert.IsTrue(filtered[0].Subjects.Contains(created));
        Assert.AreEqual(3, InspectorData.ReadCommands(commands, subject: edge).Length);
        var future = InspectorData.ReadCommands(commands, "future.command")[0];
        Assert.AreEqual("future.command", future.Kind);
        Assert.IsNotNull(future.Explanation);
    }

    private static RecordedCommand Record(string payload, CommandDecision decision)
        => new(CommandSource.Console, payload, new(1, new(0, 0), new(0, 1), decision), new("before"), new("after"));
}
