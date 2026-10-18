using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.Sessions;
using Train.Core.Tests.Construction;
using Train.Core.Tests.World;

namespace Train.Core.Tests.Combat;

[TestClass]
public sealed class CombatCommandTests
{
    [TestMethod]
    public void Engagement_and_combat_commands_have_fixed_vectors_and_round_trip()
    {
        const string trainPayload = "{\"input_format_version\":2,\"kind\":\"engagement.set_train\",\"train\":7,\"rules\":{\"priority\":\"armed_first\",\"scope\":\"armed_only\",\"encounter\":\"engage\",\"retreat_structure_ratio\":0.25}}";
        var trainRules = new TrainEngagementRules(TargetPriority.ArmedFirst, TargetScope.ArmedOnly,
            EncounterAction.Engage, 0.25);
        Assert.AreEqual(trainPayload, CoreCommandEncoding.Encode(new CoreCommand.SetTrainEngagement(new(7), trainRules)));
        Assert.AreEqual(new CoreCommand.SetTrainEngagement(new(7), trainRules), Decode(trainPayload));

        const string settlementPayload = "{\"input_format_version\":2,\"kind\":\"engagement.set_settlement\",\"settlement\":3,\"rules\":{\"priority\":\"highest_durability\",\"scope\":\"all\"}}";
        var settlementRules = new SettlementEngagementRules(TargetPriority.HighestDurability, TargetScope.All);
        Assert.AreEqual(settlementPayload, CoreCommandEncoding.Encode(new CoreCommand.SetSettlementEngagement(new(3), settlementRules)));
        Assert.AreEqual(new CoreCommand.SetSettlementEngagement(new(3), settlementRules), Decode(settlementPayload));

        const string focusPayload = "{\"input_format_version\":2,\"kind\":\"combat.focus_fire\",\"combat\":5,\"controller\":{\"kind\":\"train\",\"train\":7},\"target\":{\"kind\":\"instance\",\"instance\":{\"kind\":\"building\",\"building\":2}}}";
        var focus = new CoreCommand.FocusFire(new(5), new CombatController.Train(new(7)),
            new WorldObjectId.Instance(new InstanceId.Building(new(2))));
        Assert.AreEqual(focusPayload, CoreCommandEncoding.Encode(focus));
        Assert.AreEqual(focus, Decode(focusPayload));

        const string retreatPayload = "{\"input_format_version\":2,\"kind\":\"combat.retreat\",\"train\":7}";
        Assert.AreEqual(retreatPayload, CoreCommandEncoding.Encode(new CoreCommand.Retreat(new(7))));
        Assert.AreEqual(new CoreCommand.Retreat(new(7)), Decode(retreatPayload));
    }

    [TestMethod]
    public void Combat_payload_fields_are_strict()
    {
        AssertInvalid("{\"input_format_version\":2,\"kind\":\"engagement.set_train\",\"train\":1,\"rules\":{\"priority\":\"armed_first\",\"scope\":\"armed_only\",\"encounter\":\"engage\",\"retreat_structure_ratio\":1.5}}", "/rules/retreat_structure_ratio");
        AssertInvalid("{\"input_format_version\":2,\"kind\":\"engagement.set_train\",\"train\":1,\"rules\":{\"priority\":\"unknown\",\"scope\":\"armed_only\",\"encounter\":\"engage\",\"retreat_structure_ratio\":0}}", "/rules/priority");
        AssertInvalid("{\"input_format_version\":2,\"kind\":\"engagement.set_settlement\",\"settlement\":1,\"rules\":{\"priority\":\"armed_first\",\"scope\":\"unknown\"}}", "/rules/scope");
        AssertInvalid("{\"input_format_version\":2,\"kind\":\"combat.focus_fire\",\"combat\":1,\"controller\":{\"kind\":\"train\",\"train\":0},\"target\":{\"kind\":\"train\",\"train\":1}}", "/controller/train");
        AssertInvalid("{\"input_format_version\":2,\"kind\":\"combat.focus_fire\",\"combat\":1,\"controller\":{\"kind\":\"train\",\"train\":1},\"target\":{\"kind\":\"unknown\",\"id\":1}}", "/target/kind");
    }

    [TestMethod]
    public void Invalid_combat_values_cannot_be_encoded()
    {
        Assert.ThrowsExactly<ArgumentException>(() => CoreCommandEncoding.Encode(
            new CoreCommand.SetTrainEngagement(new(0), new(TargetPriority.ArmedFirst, TargetScope.All, EncounterAction.Engage, 0))));
        Assert.ThrowsExactly<ArgumentException>(() => CoreCommandEncoding.Encode(
            new CoreCommand.SetTrainEngagement(new(1), new(TargetPriority.ArmedFirst, TargetScope.All, EncounterAction.Engage, 1.1))));
        Assert.ThrowsExactly<ArgumentException>(() => CoreCommandEncoding.Encode(
            new CoreCommand.Retreat(new(0))));
        Assert.ThrowsExactly<ArgumentException>(() => CoreCommandEncoding.Encode(
            new CoreCommand.FocusFire(new(1), new CombatController.Train(new(1)),
                new WorldObjectId.Instance(new InstanceId.Vehicle(new(0))))));
    }

    [TestMethod]
    public void Engagement_commands_update_the_read_view()
    {
        using var session = new CoreSession(WorldFixture.State());
        var rules = new TrainEngagementRules(TargetPriority.LowestDurability, TargetScope.All, EncounterAction.Retreat, 0.4);
        Accepted(session.Submit(new CoreCommand.SetTrainEngagement(new(1), rules), CommandSource.AutomationTest));
        var train = ((Lookup<TrainView>.Found)session.Read().FindTrain(new(1))).Value;
        Assert.AreEqual(rules, train.Engagement);

        using var owned = ConstructionFixture.Session();
        var settlement = owned.Read().World.Nodes.Single(n => n.Occupant is NodeOccupant.Settlement).Occupant!;
        var id = ((NodeOccupant.Settlement)settlement).Id;
        var settlementRules = new SettlementEngagementRules(TargetPriority.HighestDurability, TargetScope.All);
        Accepted(owned.Submit(new CoreCommand.SetSettlementEngagement(id, settlementRules), CommandSource.AutomationTest));
        var view = ((Lookup<SettlementView>.Found)owned.Read().FindSettlement(id)).Value;
        Assert.AreEqual(settlementRules, view.Engagement);
    }

    [TestMethod]
    public void Settlement_engagement_rejects_a_settlement_the_player_does_not_own()
    {
        using var session = new CoreSession(WorldFixture.State());
        var settlement = session.Read().World.Nodes.Single(n => n.Occupant is NodeOccupant.Settlement).Occupant!;
        var id = ((NodeOccupant.Settlement)settlement).Id;
        var result = (SubmissionResult.Completed)session.Submit(new CoreCommand.SetSettlementEngagement(id,
            new SettlementEngagementRules(TargetPriority.ArmedFirst, TargetScope.All)), CommandSource.AutomationTest);
        var rejected = (CommandDecision.Rejected)result.Receipt.Decision;
        Assert.AreEqual("not_owned", rejected.Problems.Single().Code);
        Assert.AreEqual("Settlement", rejected.Problems.Single().Field);
        Assert.IsNull(session.Read().Fault);
    }

    [TestMethod]
    public void Retreat_outside_combat_is_a_normal_rejection_and_does_not_freeze()
    {
        using var session = new CoreSession(WorldFixture.State());
        var result = (SubmissionResult.Completed)session.Submit(new CoreCommand.Retreat(new(1)), CommandSource.AutomationTest);
        var rejected = (CommandDecision.Rejected)result.Receipt.Decision;
        Assert.AreEqual("invalid_state", rejected.Problems.Single().Code);
        Assert.AreEqual("Train.Activity", rejected.Problems.Single().Field);
        Assert.IsNull(session.Read().Fault);
        Assert.AreEqual(1u, session.Advance(1).CompletedSteps);
    }

    [TestMethod]
    public void Focus_fire_on_a_missing_combat_is_a_normal_rejection()
    {
        using var session = new CoreSession(WorldFixture.State());
        var result = (SubmissionResult.Completed)session.Submit(new CoreCommand.FocusFire(new(9),
            new CombatController.Train(new(1)), new WorldObjectId.Instance(new InstanceId.Vehicle(new(1)))),
            CommandSource.AutomationTest);
        var rejected = (CommandDecision.Rejected)result.Receipt.Decision;
        Assert.AreEqual("object_missing", rejected.Problems.Single().Code);
        Assert.AreEqual("Combat", rejected.Problems.Single().Field);
        Assert.IsNull(session.Read().Fault);
    }

    private static CoreCommand Decode(string payload)
        => ((CoreResult<DecodedCommand>.Success)CoreCommandEncoding.Decode(payload)).Value.Command!;

    private static void AssertInvalid(string payload, string field)
    {
        var failure = (CoreResult<DecodedCommand>.Failure)CoreCommandEncoding.Decode(payload);
        Assert.AreEqual("invalid_state", failure.Problems.Single().Code);
        Assert.AreEqual(field, failure.Problems.Single().Field);
    }

    private static void Accepted(SubmissionResult result)
        => Assert.IsInstanceOfType<CommandDecision.Accepted>(((SubmissionResult.Completed)result).Receipt.Decision);
}
