using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.Tests.World;
using static Train.Core.Tests.Construction.ConstructionFixture;

namespace Train.Core.Tests.Construction;

[TestClass]
public sealed class ConstructionRestoreTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Restored_job_uses_saved_facility_until_content_is_published(bool removeQualification)
    {
        var content = Content() with
        { ConstructionRules = [Content().ConstructionRules[0] with { ConstructionWorkPerItem = 10 }] };
        using var original = Session(content);
        var job = Start(original, Build(content, Local));
        var saved = WorldFixture.Success(original.Capture(CapturePurpose.Save));
        var changed = Changed(Content(rate: 120, maximum: removeQualification ? null : SizeClass.Small));
        using var candidate = WorldFixture.Success(CoreFactory.Create().PrepareRestore(
            WorldFixture.Success(CoreFactory.Create().DecodeState(saved.Encode())), changed, WorldFixture.Options));
        using var session = candidate.Activate();

        Step(session);
        Assert.AreEqual(1d, Job(session.Read(), job.Id).ProgressWork, 1e-12);
        Assert.AreEqual(60d, Job(session.Read(), job.Id).ActualWorkPerSecond, 1e-12);

        var publication = Accept(session, new CoreCommand.PublishContent(Changed(changed, 'E')));
        var advance = Step(session);
        var events = publication.Events.Concat(advance.Events).Select(e => e.Event).ToArray();
        if (removeQualification)
        {
            Assert.IsInstanceOfType<Lookup<JobView>.Missing>(session.Read().FindJob(job.Id));
            Assert.IsTrue(events.OfType<CoreEvent.ConstructionStateChanged>().Any(e => e.After == ConstructionState.Failed));
            Assert.IsFalse(events.OfType<CoreEvent.ConstructionCompleted>().Any());
        }
        else
        {
            Assert.AreEqual(3d, Job(session.Read(), job.Id).ProgressWork, 1e-12);
            Assert.AreEqual(120d, Job(session.Read(), job.Id).ActualWorkPerSecond, 1e-12);
        }
    }

    [TestMethod]
    public void Restored_repair_uses_saved_material_cost_and_durability_limits()
    {
        var content = Content();
        using var original = Session(content, damaged: true);
        var saved = WorldFixture.Success(original.Capture(CapturePurpose.Save));
        var changed = Changed(content) with
        {
            Designs = content.Designs.Select(d => d.Design != WorldFixture.Vehicle ? d : d with
            {
                MaxStructureDurability = 200,
                Parts = d.Parts.Select(p => p with { MaxDurability = 200 }).ToImmutableArray(),
                StructuralGoods = [new(Material, 8)]
            }).ToImmutableArray()
        };
        using var candidate = WorldFixture.Success(CoreFactory.Create().PrepareRestore(
            WorldFixture.Success(CoreFactory.Create().DecodeState(saved.Encode())), changed, WorldFixture.Options));
        using var session = candidate.Activate();

        var job = Start(session, Repair(Local));
        Assert.AreEqual(2L, job.FrozenTotalWork);
        CollectionAssert.AreEqual(new[] { new GoodsQuantity(Material, 2) }, job.InvestedGoods.ToArray());
        Step(session, 2);
        var repaired = ((Lookup<InstanceView>.Found)session.Read().FindInstance(Vehicle)).Value;
        Assert.AreEqual(100L, repaired.Structure.EffectiveCurrent);
        Assert.AreEqual(100L, repaired.Structure.Maximum);
        Assert.IsTrue(repaired.Parts.All(p => p.Durability.EffectiveCurrent == 100 && p.Durability.Maximum == 100));
    }

    [TestMethod]
    public void Completing_restored_build_preserves_the_design_shared_with_existing_vehicles()
    {
        var content = Content();
        using var original = Session(content);
        var job = Start(original, Build(content, Local));
        var saved = WorldFixture.Success(original.Capture(CapturePurpose.Save));
        var changed = Changed(content) with
        {
            Designs = content.Designs.Select(d => d.Design != WorldFixture.Vehicle ? d : d with
            { MaxStructureDurability = 200 }).ToImmutableArray()
        };
        using var candidate = WorldFixture.Success(CoreFactory.Create().PrepareRestore(
            WorldFixture.Success(CoreFactory.Create().DecodeState(saved.Encode())), changed, WorldFixture.Options));
        using var session = candidate.Activate();
        var before = ((Lookup<InstanceView>.Found)session.Read().FindInstance(Vehicle)).Value;
        Assert.AreEqual(100L, before.Structure.Maximum);

        var completed = Step(session, 2).Events.Select(e => e.Event).OfType<CoreEvent.ConstructionCompleted>().Single();
        Assert.AreEqual(job.Id, completed.Job);
        var existing = ((Lookup<InstanceView>.Found)session.Read().FindInstance(Vehicle)).Value;
        var created = ((Lookup<InstanceView>.Found)session.Read().FindInstance(completed.CreatedInstance!)).Value;
        Assert.AreEqual(before.Structure, existing.Structure);
        Assert.AreEqual(100L, created.Structure.Maximum);
        Assert.AreEqual(100L, created.Structure.EffectiveCurrent);

        Accept(session, new CoreCommand.PublishContent(Changed(changed, 'E')));
        Assert.AreEqual(200L, ((Lookup<InstanceView>.Found)session.Read().FindInstance(Vehicle)).Value.Structure.Maximum);
    }
}
