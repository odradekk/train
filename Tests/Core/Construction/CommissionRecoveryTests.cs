using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.Sessions;
using Train.Core.Tests.World;
using static Train.Core.Tests.Construction.ConstructionFixture;

namespace Train.Core.Tests.Construction;

[TestClass]
public sealed class CommissionRecoveryTests
{
    [TestMethod]
    public void Publishing_content_keeps_paid_amount_and_frozen_work_but_new_commissions_use_new_terms()
    {
        var original = CommissionProcessDriver.Original;
        var updated = CommissionProcessDriver.Updated;
        using var session = CommissionFixture.Session(original);
        var first = Start(session, CommissionFixture.Commission(original));
        var paid = session.Read().Economy;
        Assert.AreEqual(6L, paid.UnassignedServicePayments.Value);
        Accept(session, new CoreCommand.PublishContent(updated));
        Assert.AreEqual(paid.Wallet, session.Read().Economy.Wallet);
        Assert.AreEqual(paid.UnassignedServicePayments, session.Read().Economy.UnassignedServicePayments);
        Assert.AreEqual(2L, Job(session.Read(), first.Id).FrozenTotalWork);
        var quote = WorldFixture.Success(session.Read().GetJobQuote(CommissionFixture.BuildRequest(updated), JobFunding.NpcCommission));
        Assert.AreEqual(200L, quote.TotalWork);
        Assert.AreEqual(1800L, quote.ServicePayment!.Value.Value);
        var next = Start(session, CommissionFixture.Commission(updated));
        Assert.AreEqual(200L, next.FrozenTotalWork);
        Assert.AreEqual(1806L, session.Read().Economy.UnassignedServicePayments.Value);
        Assert.AreEqual(paid.Wallet.Value - quote.TotalPayment!.Value.Value, session.Read().Economy.Wallet.Value);
    }

    [TestMethod]
    public void Completed_accepted_and_rejected_commissions_retain_their_design_input_dependencies()
    {
        using var session = CommissionFixture.Session();
        foreach (var identity in new[] { 'D', 'E' })
        {
            var request = CommissionFixture.BuildRequest(session.State.Content);
            var input = request.Design with { InputIdentity = new(new string(identity, 64)),
                Design = request.Design.Design with { Design = new(new string(identity, 64)) } };
            var result = Submit(session, new CoreCommand.CommissionVehicleWork(request with { Design = input }, new(identity == 'D' ? 10000 : 0)));
            if (identity == 'D') Assert.IsInstanceOfType<CommandDecision.Accepted>(result.Receipt.Decision);
            else Assert.IsInstanceOfType<CommandDecision.Rejected>(result.Receipt.Decision);
        }
        var recording = session.ReadRecording();
        Assert.AreEqual(2, recording.Commands.Length);
        foreach (var identity in new[] { 'D', 'E' })
        {
            Assert.IsTrue(recording.RequiredReferences.Inputs.Contains(new(new string(identity, 64))));
            Assert.IsTrue(recording.RequiredReferences.Designs.Contains(new(new string(identity, 64))));
        }
        var saved = WorldFixture.Success(session.Capture(CapturePurpose.Save));
        Assert.IsFalse(saved.References.Designs.Contains(new(new string('E', 64))));
    }

    [TestMethod]
    public void Negative_maximum_payment_is_rejected_by_command_decode()
    {
        var payload = JsonNode.Parse(CoreCommandEncoding.Encode(CommissionFixture.Commission(CommissionFixture.Content())))!;
        payload["maximum_payment"] = -1;
        var failure = (CoreResult<DecodedCommand>.Failure)CoreCommandEncoding.Decode(payload.ToJsonString());
        Assert.AreEqual("invalid_state", failure.Problems.Single().Code);
        Assert.AreEqual("/maximum_payment", failure.Problems.Single().Field);
    }

    [TestMethod]
    [DataRow("negative_payments")]
    [DataRow("unknown_funding")]
    [DataRow("queued_progress")]
    [DataRow("mixed_funding")]
    public void Invalid_commission_accounting_funding_and_queue_state_are_rejected(string corruption)
    {
        using var session = CommissionFixture.Session();
        Start(session, CommissionFixture.Commission(session.State.Content));
        Start(session, CommissionFixture.Commission(session.State.Content));
        var payload = JsonNode.Parse(WorldFixture.Success(session.Capture(CapturePurpose.Save)).Encode())!;
        var world = payload["world"]!;
        var jobs = world["construction_jobs"]!;
        switch (corruption)
        {
            case "negative_payments": world["unassigned_service_payments"] = -1; break;
            case "unknown_funding": jobs[0]!["funding"] = 99; break;
            case "queued_progress": jobs[1]!["progress_work"] = .5; break;
            case "mixed_funding": jobs[0]!["funding"] = (int)JobFunding.OwnInventory; break;
        }
        Assert.IsInstanceOfType<CoreResult<ICoreCapture>.Failure>(CoreFactory.Create().DecodeState(payload.ToJsonString()));
    }
}
