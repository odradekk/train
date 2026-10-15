using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.Sessions;
using Train.Core.Tests.World;

namespace Train.Core.Tests.Production;

internal static class ProductionFixture
{
    internal static readonly GoodId Input = new GoodId.Voxel(WorldFixture.Material);
    internal static readonly GoodId Product = new GoodId.Voxel(new("fixture:a-product"));
    internal static readonly GoodId OtherProduct = new GoodId.Voxel(new("fixture:z-product"));
    internal static readonly GoodId Fuel = new GoodId.Voxel(new("fixture:fuel"));
    internal static readonly ProcessRef Process = new ProcessRef.Production(new("fixture:production"));
    internal static readonly ProcessRef Recovery = new ProcessRef.Recovery(new("fixture:recovery"));
    internal static readonly FacilityKey Facility = new(new(2), CapabilityKind.Production);
    internal static readonly SettlementId Settlement = new(1);
    internal static readonly InventoryId LocalInventory = new(1);

    internal static CoreContentProjection Content(ImmutableArray<GoodsQuantity> stock = default,
        long work = 2, double rate = 60, double powerPerWork = 0, double laborPerWork = 0,
        double power = 60, long capacity = 100, bool enabled = true, bool recovery = false,
        ImmutableArray<GoodsQuantity> inputs = default, ImmutableArray<GoodsQuantity> outputs = default)
    {
        var original = WorldFixture.Content;
        var factory = original.Designs[1] with
        {
            Design = WorldFixture.FactoryBuilding,
            Kind = new DesignKind.Building(PlotSize.Small, BuildingKind.Production, [FacilityTag.Agriculture]),
            Parts = original.Designs[1].Parts.Select(p => p with
            { Capabilities = [new PartCapability.Production(rate)] }).ToImmutableArray()
        };
        var batchInputs = inputs.IsDefault ? ImmutableArray.Create(new GoodsQuantity(Input, 2)) : inputs;
        var batchOutputs = outputs.IsDefault ? ImmutableArray.Create(new GoodsQuantity(Product, 3)) : outputs;
        return original with
        {
            Goods = [original.Goods[0], original.Goods[0] with { Good = Product },
                original.Goods[0] with { Good = OtherProduct },
                original.Goods[0] with { Good = Fuel, FuelEnergyPerUnit = 1 }],
            Designs = original.Designs.Select(d => d.Design != WorldFixture.Station ? d : d with
            {
                Parts = d.Parts.Select(p => p with
                { Capabilities = [..p.Capabilities, new PartCapability.Storage(capacity), new PartCapability.Power(power)] }).ToImmutableArray()
            }).Append(factory).ToImmutableArray(),
            Processes = [new(recovery ? new ProcessDetails.Recovery(new("fixture:recovery"), batchInputs, batchOutputs)
                    : new ProcessDetails.Production(new("fixture:production"), batchInputs, batchOutputs),
                WorldFixture.Revision, work, powerPerWork, laborPerWork, FacilityTag.Agriculture, [WorldFixture.Plain])],
            SettlementTemplates = [original.SettlementTemplates[0] with
            {
                Plots = [new(1, PlotSize.SmallStation, WorldFixture.Station), new(2, PlotSize.Small, WorldFixture.FactoryBuilding)],
                SpecialInitialInventory = stock.IsDefault ? [new(Input, 10)] : stock,
                ProductionPlans = [new(2, CapabilityKind.Production, recovery ? Recovery : Process, enabled, 0)]
            }]
        };
    }

    internal static ICoreSessionCandidate Prepare(CoreContentProjection? content = null)
        => WorldFixture.Prepare(content: content ?? Content());

    internal static CoreState State(CoreContentProjection? content = null) => WorldFixture.State(content: content ?? Content());

    internal static FacilityView Read(ICoreReadView view) => ((Lookup<FacilityView>.Found)view.FindFacility(Facility)).Value;
    internal static FacilityView Read(ICoreSession session) => Read(session.Read());
    internal static InventoryView ReadInventory(ICoreSession session) => ((Lookup<InventoryView>.Found)session.Read().FindInventory(LocalInventory)).Value;
    internal static long Stock(ICoreSession session, GoodId good) => ReadInventory(session).Stacks.Where(s => s.Good == good).Sum(s => s.Quantity);

    internal static AdvanceResult Step(ICoreSession session, uint steps = 1)
    {
        var result = session.Advance(steps);
        Assert.IsNull(result.Fault, result.Diagnostic?.Message);
        Assert.AreEqual(steps, result.CompletedSteps);
        return result;
    }

    internal static SubmissionResult.Completed Publish(ICoreSession session, CoreContentProjection content)
    {
        var result = (SubmissionResult.Completed)session.Submit(new CoreCommand.PublishContent(content), CommandSource.AutomationTest);
        Assert.IsInstanceOfType<CommandDecision.Accepted>(result.Receipt.Decision, result.Diagnostic?.Message);
        return result;
    }

    internal static CoreContentProjection Changed(CoreContentProjection content, char identity = 'F')
        => content with { Identity = new(new string(identity, 64)) };
}
