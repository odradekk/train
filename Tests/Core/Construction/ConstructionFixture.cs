using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.Sessions;
using Train.Core.Tests.World;

namespace Train.Core.Tests.Construction;

internal static class ConstructionFixture
{
    internal static readonly GoodId Material = new GoodId.Voxel(WorldFixture.Material);
    internal static readonly FacilityKey Facility = new(new(1), CapabilityKind.Construction);
    internal static readonly InventoryId Local = new(1);
    internal static readonly InventoryId Cargo = new(2);
    internal static readonly InstanceId Vehicle = new InstanceId.Vehicle(new(1));

    internal static CoreContentProjection Content(double rate = 60, SizeClass? maximum = SizeClass.Small,
        long stock = 10, double power = 0, double labor = 0) => WorldFixture.Content with
    {
        Designs = WorldFixture.Content.Designs.Select(d => d.Design == WorldFixture.Station ? d with
        {
            Parts = d.Parts.Select(p => p with { Capabilities = [..p.Capabilities,
                new PartCapability.Storage(100), new PartCapability.Construction(rate, PlotSize.Small, maximum)] }).ToImmutableArray()
        } : d with { BillOfMaterials = [new(Material, 2)], BillOfMaterialsItemCount = 2,
            StructuralGoods = [new(Material, 2)] }).ToImmutableArray(),
        ConstructionRules = [WorldFixture.Content.ConstructionRules[0] with { PowerPerWork = power, LaborPerWork = labor }],
        SettlementTemplates = [WorldFixture.Content.SettlementTemplates[0] with
            { SpecialInitialInventory = stock == 0 ? [] : [new(Material, stock)] }]
    };

    internal static CoreSession Session(CoreContentProjection? content = null, bool damaged = false)
    {
        var state = WorldFixture.State(content: content ?? Content());
        var settlement = state.World.Settlements.Single();
        return new(state with { World = state.World with
        {
            Settlements = [settlement with { Relation = SettlementRelation.PlayerOwned }],
            Nodes = state.World.Nodes.Select(n => n.Id != settlement.Node ? n : n with
                { Occupant = new NodeOccupant.Settlement(settlement.Id, SettlementRelation.PlayerOwned) }).ToImmutableArray(),
            Instances = state.World.Instances.Select(i => i with
            {
                Ownership = SettlementRelation.PlayerOwned,
                StructureDurability = damaged && i.Id == Vehicle ? 50 : i.StructureDurability,
                Parts = damaged && i.Id == Vehicle ? i.Parts.Select(p => p with { Current = 0 }).ToImmutableArray() : i.Parts
            }).ToImmutableArray(),
            Trains = state.World.Trains.Select(t => t with { Position = WorldFixture.Node(settlement.Node,
                NodeTravelPhase.Docked, WorldFixture.ApproachLength + WorldFixture.PlatformLength / 2, platform: 1) }).ToImmutableArray(),
            Inventories = state.World.Inventories.Select(i => i.Id != Cargo ? i : i with
                { Goods = [new(Material, 10)] }).ToImmutableArray()
        } });
    }

    internal static DesignInput Design(CoreContentProjection content) => new(content.Identity,
        content.Designs.Single(d => d.Design == WorldFixture.Vehicle), []);
    internal static CoreCommand Build(CoreContentProjection content, params InventoryId[] sources)
        => new CoreCommand.StartOwnedJob(new JobRequest.BuildVehicle(Facility, Design(content), [..sources], 0));
    internal static CoreCommand Repair(params InventoryId[] sources)
        => new CoreCommand.StartOwnedJob(new JobRequest.Repair(Facility, Vehicle, [..sources], 0));
    internal static SubmissionResult.Completed Submit(ICoreSession session, CoreCommand command)
        => (SubmissionResult.Completed)session.Submit(command, CommandSource.AutomationTest);
    internal static SubmissionResult.Completed Accept(ICoreSession session, CoreCommand command)
    {
        var result = Submit(session, command);
        Assert.IsInstanceOfType<CommandDecision.Accepted>(result.Receipt.Decision, result.Diagnostic?.Message);
        return result;
    }
    internal static ConstructionJobView Start(ICoreSession session, CoreCommand command)
        => Accept(session, command).Events.Select(e => e.Event).OfType<CoreEvent.ConstructionStarted>().Single().Job;
    internal static ConstructionJobView Job(ICoreReadView view, JobId id)
        => ((JobView.Construction)((Lookup<JobView>.Found)view.FindJob(id)).Value).Value;
    internal static long Stock(ICoreSession session, InventoryId id)
        => ((Lookup<InventoryView>.Found)session.Read().FindInventory(id)).Value.Stacks.Where(s => s.Good == Material).Sum(s => s.Quantity);
    internal static AdvanceResult Step(ICoreSession session, uint count = 1)
    {
        var result = session.Advance(count);
        Assert.IsNull(result.Fault, result.Diagnostic?.Message);
        Assert.AreEqual(count, result.CompletedSteps);
        return result;
    }
    internal static void Reject(CoreSession session, CoreCommand command, string? field = null, string? code = null)
    {
        var before = session.State.World;
        var result = Submit(session, command);
        var rejected = (CommandDecision.Rejected)result.Receipt.Decision;
        if (field is not null) Assert.AreEqual(field, rejected.Problems[0].Field);
        if (code is not null) Assert.AreEqual(code, rejected.Problems[0].Code);
        Assert.IsTrue(result.Events.IsEmpty);
        Assert.AreEqual(before, session.State.World);
    }
    internal static CoreContentProjection Changed(CoreContentProjection content, char id = 'F')
        => content with { Identity = new(new string(id, 64)) };
}
