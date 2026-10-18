using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.Tests.World;
using static Train.Core.Tests.Construction.ConstructionFixture;

namespace Train.Core.Tests.Construction;

[TestClass]
public sealed class VehicleCommissionRefitTests
{
    [TestMethod]
    public void Missing_refit_materials_money_and_payment_limit_leave_all_business_state_unchanged()
    {
        var missingContent = CommissionFixture.Content(stock: 0);
        using var missing = CommissionFixture.Session(missingContent);
        var missingRequest = AddedPart(missingContent);
        var quote = WorldFixture.Success(missing.Read().GetJobQuote(missingRequest, JobFunding.NpcCommission));
        Assert.IsNull(quote.TotalPayment);
        CollectionAssert.AreEqual(new[] { "voxel:fixture:material" }, quote.Blockers.Single().RelatedIdentities.ToArray());
        Reject(missing, new CoreCommand.CommissionVehicleWork(missingRequest, new(long.MaxValue)), "Sources", "insufficient_goods");
        var content = CommissionFixture.Content();
        using var poor = CommissionFixture.Session(content, money: 102);
        var request = AddedPart(content);
        var poorQuote = WorldFixture.Success(poor.Read().GetJobQuote(request, JobFunding.NpcCommission));
        Assert.AreEqual(new Money(103), poorQuote.TotalPayment);
        Reject(poor, new CoreCommand.CommissionVehicleWork(request, new(103)), "Wallet", "insufficient_money");
        using var limited = CommissionFixture.Session(content);
        Reject(limited, new CoreCommand.CommissionVehicleWork(request, new(102)), "MaximumPayment", "price_condition_failed");
        Start(limited, new CoreCommand.CommissionVehicleWork(request, new(103)));
    }

    [TestMethod]
    public void Queued_refit_holds_its_lock_at_acceptance_and_waits_until_the_step_after_the_head_completes()
    {
        var content = CommissionFixture.Content();
        using var session = CommissionFixture.Session(content);
        Start(session, CommissionFixture.Commission(content));
        var request = AddedPart(content);
        var job = Start(session, new CoreCommand.CommissionVehicleWork(request, new(103)));
        Assert.AreEqual(ConstructionState.Queued, job.State);
        Assert.AreEqual(InstanceLock.UnderRefit, ((Lookup<InstanceView>.Found)session.Read().FindInstance(Vehicle)).Value.Lock);
        Reject(session, new CoreCommand.StartTrain(new(1)), "Train", "instance_locked");
        Reject(session, new CoreCommand.CommissionVehicleWork(request, new(103)), "Instance", "instance_locked");
        Reject(session, new CoreCommand.ApplyInstanceDesign(Vehicle, request.Change), "Instance", "instance_locked");
        Reject(session, new CoreCommand.SetDeconstructionPaused(job.Id, true), "Job.Kind", "invalid_state");
        Step(session, 2);
        Assert.AreEqual(ConstructionState.Queued, Job(session.Read(), job.Id).State);
        Assert.AreEqual(0d, Job(session.Read(), job.Id).ProgressWork);
        Step(session);
        Assert.IsInstanceOfType<Lookup<JobView>.Missing>(session.Read().FindJob(job.Id));
    }

    [TestMethod]
    public void Commission_refit_quotes_and_atomically_pays_materials_and_wages()
    {
        var content = CommissionFixture.Content();
        using var session = CommissionFixture.Session(content);
        var request = AddedPart(content);
        var quote = WorldFixture.Success(session.Read().GetJobQuote(request, JobFunding.NpcCommission));
        Assert.IsTrue(quote.Blockers.IsEmpty);
        Assert.AreEqual(new Money(100), quote.MaterialPayment);
        Assert.AreEqual(new Money(3), quote.ServicePayment);
        Assert.AreEqual(new Money(103), quote.TotalPayment);
        Assert.AreEqual(10000L, session.Read().Economy.Wallet.Value);
        var job = Start(session, new CoreCommand.CommissionVehicleWork(request, new(103)));
        Assert.AreEqual(9897L, session.Read().Economy.Wallet.Value);
        Assert.AreEqual(3L, session.Read().Economy.UnassignedServicePayments.Value);
        Assert.AreEqual(9L, Stock(session, Local));
        Assert.AreEqual(InstanceLock.UnderRefit, ((Lookup<InstanceView>.Found)session.Read().FindInstance(Vehicle)).Value.Lock);
        var completed = Step(session).Events.Select(e => e.Event).OfType<CoreEvent.ConstructionCompleted>().Single();
        Assert.AreEqual(job.Id, completed.Job);
        Assert.IsTrue(completed.ReturnedGoods.IsEmpty);
        Assert.AreEqual(request.Change.Target.Design.Design, ((Lookup<InstanceView>.Found)session.Read().FindInstance(Vehicle)).Value.Design);
    }

    [TestMethod]
    public void Paid_refit_survives_restore_and_strict_replay_without_repeating_material_or_wage_charges()
    {
        var content = CommissionFixture.Content(rate: 30);
        using var session = CommissionFixture.Session(content);
        var request = AddedPart(content);
        var command = new CoreCommand.CommissionVehicleWork(request, new(103));
        var job = Start(session, command);
        Reject(session, command, "Instance", "instance_locked");
        Step(session);
        Assert.AreEqual(1L, Job(session.Read(), job.Id).FrozenTotalWork);
        Assert.AreEqual(.5, Job(session.Read(), job.Id).ProgressWork, 1e-12);
        AssertCharges(session.Read());
        var saved = WorldFixture.Success(session.Capture(CapturePurpose.Save));
        var factory = CoreFactory.Create();
        var decoded = WorldFixture.Success(factory.DecodeState(saved.Encode()));
        using var candidate = WorldFixture.Success(factory.PrepareRestore(decoded, content, WorldFixture.Options));
        using var restored = candidate.Activate();
        Assert.AreEqual(saved.Encode(), WorldFixture.Success(restored.Capture(CapturePurpose.Save)).Encode());
        AssertCharges(restored.Read());

        Step(session);
        Step(restored);
        var completed = WorldFixture.Success(session.Capture(CapturePurpose.Save));
        var recovered = WorldFixture.Success(restored.Capture(CapturePurpose.Save));
        Assert.AreEqual(completed.Encode(), recovered.Encode());
        Assert.AreEqual(completed.Checkpoint, recovered.Checkpoint);
        AssertCharges(session.Read());
        AssertCharges(restored.Read());
        Assert.IsInstanceOfType<Lookup<JobView>.Missing>(restored.Read().FindJob(job.Id));
        Assert.AreEqual(request.Change.Target.Design.Design,
            ((Lookup<InstanceView>.Found)restored.Read().FindInstance(Vehicle)).Value.Design);

        var recording = session.ReadRecording();
        Assert.AreEqual(2, recording.Commands.Length);
        Assert.IsInstanceOfType<CommandDecision.Accepted>(recording.Commands[0].Result.Decision);
        Assert.IsInstanceOfType<CommandDecision.Rejected>(recording.Commands[1].Result.Decision);
        using var replay = WorldFixture.Success(factory.PrepareReplay(new(2, WorldFixture.Options.Runtime,
            recording.Starts[0].State.Encode(), recording.Commands, recording.Checkpoints, recording.EndPosition, null),
            WorldFixture.Options.Runtime, [content]));
        var replayed = replay.Advance(2);
        Assert.AreEqual(ReplayStop.EndOfRecording, replayed.Stop);
        Assert.IsNull(replayed.Difference);
        Assert.IsNull(replayed.Fault);
        Assert.AreEqual(completed.Checkpoint, replay.CaptureBoundary().Checkpoint);
        AssertCharges(replay.Read());

        static void AssertCharges(ICoreReadView view)
        {
            // One material costs 100; one work unit costs 3. Both are paid at acceptance.
            Assert.AreEqual(9897L, view.Economy.Wallet.Value);
            Assert.AreEqual(3L, view.Economy.UnassignedServicePayments.Value);
            var inventory = ((Lookup<InventoryView>.Found)view.FindInventory(Local)).Value;
            Assert.AreEqual(9L, inventory.Stacks.Where(stack => stack.Good == Material).Sum(stack => stack.Quantity));
        }
    }

    private static JobRequest.Refit AddedPart(CoreContentProjection content)
    {
        var input = Design(content);
        var target = input with { Design = input.Design with
        {
            Design = new(new string('9', 64)), Composition = new(new string('8', 64)),
            BillOfMaterials = [new(Material, 3)], BillOfMaterialsItemCount = 3
        } };
        return new(Facility, Vehicle, new(input.Design.Design, input.Design.Composition, target,
            [new(new(0), new(0))], [new(Material, 1)], [], 1), [], 0);
    }
}
