using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.Sessions;
using Train.Core.Tests.World;
using static Train.Core.Tests.Construction.ConstructionFixture;

namespace Train.Core.Tests.Construction;

[TestClass]
public sealed class OwnedConstructionJobsTests
{
    [TestMethod]
    public void Owned_jobs_follow_job_order_survive_restore_and_disappear_on_completion_without_changing_old_views()
    {
        var content = TwoFacilities();
        using var original = Session(content);
        var settlement = original.Read().World.Nodes.Select(n => n.Occupant).OfType<NodeOccupant.Settlement>().Single().Id;
        var facilities = Town(original.Read(), settlement).Plots.Select(p => ((PlotOccupant.Building)p.Occupant).Id)
            .OrderByDescending(id => id.Value).Select(id => new FacilityKey(id, CapabilityKind.Construction)).ToArray();
        var first = Start(original, new CoreCommand.StartOwnedJob(new JobRequest.BuildVehicle(facilities[0], Design(content), [Local], 0)));
        var second = Start(original, new CoreCommand.StartOwnedJob(new JobRequest.BuildVehicle(facilities[1], Design(content), [Local], 0)));
        var expected = new[] { first.Id, second.Id };
        var old = original.Read();
        CollectionAssert.AreEqual(expected, Town(old, settlement).OwnedJobs.ToArray());
        var factory = CoreFactory.Create();
        var saved = WorldFixture.Success(original.Capture(CapturePurpose.Save));
        using var candidate = WorldFixture.Success(factory.PrepareRestore(
            WorldFixture.Success(factory.DecodeState(saved.Encode())), content, WorldFixture.Options));
        using var restored = candidate.Activate();
        CollectionAssert.AreEqual(expected, Town(restored.Read(), settlement).OwnedJobs.ToArray());

        Step(restored, 2);

        Assert.IsTrue(Town(restored.Read(), settlement).OwnedJobs.IsEmpty);
        foreach (var id in expected) Assert.IsInstanceOfType<Lookup<JobView>.Missing>(restored.Read().FindJob(id));
        CollectionAssert.AreEqual(expected, Town(old, settlement).OwnedJobs.ToArray());
        foreach (var id in expected) Assert.IsInstanceOfType<Lookup<JobView>.Found>(old.FindJob(id));
    }

    [TestMethod]
    public void Npc_commissions_are_not_owned_jobs()
    {
        var content = CommissionFixture.Content();
        using var session = CommissionFixture.Session(content);
        var first = Start(session, CommissionFixture.Commission(content));
        var second = Start(session, CommissionFixture.Commission(content));
        var town = Town(session.Read(), first.Settlement);
        CollectionAssert.AreEqual(new[] { first.Id, second.Id }, town.CommissionJobs.ToArray());
        Assert.IsTrue(town.OwnedJobs.IsEmpty);
    }

    [TestMethod]
    [DataRow(ConstructionKind.Construction)]
    [DataRow(ConstructionKind.Repair)]
    [DataRow(ConstructionKind.Refit)]
    [DataRow(ConstructionKind.Deconstruction)]
    public void Failed_owned_jobs_disappear_with_job_lookup_including_paused_deconstruction(ConstructionKind kind)
    {
        var content = Content();
        using var initial = Session(content, damaged: true);
        using var session = new CoreSession(initial.State with { World = initial.State.World with
        {
            Inventories = initial.State.World.Inventories.Select(i => i.Id != Cargo ? i : i with { Goods = [] }).ToImmutableArray()
        } });
        CoreCommand command;
        if (kind == ConstructionKind.Deconstruction)
        {
            Accept(session, new CoreCommand.DisbandTrain(new(1)));
            command = new CoreCommand.StartOwnedJob(new JobRequest.Dismantle(Facility, Vehicle, 0));
        }
        else if (kind == ConstructionKind.Refit)
        {
            var input = Design(content);
            var change = new DesignChangeProjection(input.Design.Design, input.Design.Composition,
                input with { Design = input.Design with { Design = new(new string('9', 64)), Composition = new(new string('8', 64)) } },
                [new(new(0), new(0))], [], [], 0);
            command = new CoreCommand.StartOwnedJob(new JobRequest.Refit(Facility, Vehicle, change, [Local], 0));
        }
        else command = kind == ConstructionKind.Repair ? Repair(Local) : Build(content, Local);
        var job = Start(session, command);
        if (kind == ConstructionKind.Deconstruction) Accept(session, new CoreCommand.SetDeconstructionPaused(job.Id, true));
        var old = session.Read();
        CollectionAssert.AreEqual(new[] { job.Id }, Town(old, job.Settlement).OwnedJobs.ToArray());
        var updated = Changed(content) with { Designs = session.State.World.Designs.Select(d => d.Design != WorldFixture.Station
            ? d : d with { Parts = d.Parts.Select(p => p with
                { Capabilities = p.Capabilities.Where(c => c is not PartCapability.Construction).ToImmutableArray() }).ToImmutableArray() }).ToImmutableArray() };

        Accept(session, new CoreCommand.PublishContent(updated));
        Step(session);

        Assert.IsTrue(Town(session.Read(), job.Settlement).OwnedJobs.IsEmpty);
        Assert.IsInstanceOfType<Lookup<JobView>.Missing>(session.Read().FindJob(job.Id));
        CollectionAssert.AreEqual(new[] { job.Id }, Town(old, job.Settlement).OwnedJobs.ToArray());
    }

    private static SettlementView Town(ICoreReadView view, SettlementId id) => ((Lookup<SettlementView>.Found)view.FindSettlement(id)).Value;

    private static CoreContentProjection TwoFacilities()
    {
        var content = Content();
        var workshop = content.Designs.Single(d => d.Design == WorldFixture.Station) with
        {
            Design = new(new string('7', 64)), Composition = new(new string('6', 64)),
            Kind = new DesignKind.Building(PlotSize.Small, BuildingKind.Construction, []),
            Parts = content.Designs.Single(d => d.Design == WorldFixture.Station).Parts.Select(p => p with
                { Capabilities = p.Capabilities.Where(c => c is not PartCapability.Platform).ToImmutableArray() }).ToImmutableArray()
        };
        return content with
        {
            Designs = content.Designs.Add(workshop),
            SettlementTemplates = [content.SettlementTemplates[0] with
                { Plots = content.SettlementTemplates[0].Plots.Add(new(2, PlotSize.Small, workshop.Design)) }]
        };
    }
}
