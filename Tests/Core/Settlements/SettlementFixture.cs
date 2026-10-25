using System.Collections.Immutable;
using Train.Core.Contracts;
using Train.Core.Sessions;
using Train.Core.Settlements;
using Train.Core.Tests.World;
using Train.Core.World;

namespace Train.Core.Tests.Settlements;

internal static class SettlementFixture
{
    internal static readonly GoodId Material = new GoodId.Voxel(WorldFixture.Material);
    internal static readonly GoodId Fuel = new GoodId.Voxel(new("fixture:a-fuel"));
    internal static readonly GoodId OtherFuel = new GoodId.Voxel(new("fixture:z-fuel"));

    internal static CoreState State(ImmutableArray<GoodsQuantity> stock = default, double power = 30)
    {
        var original = WorldFixture.Content;
        var content = original with
        {
            Goods = original.Goods.Add(original.Goods[0] with { Good = Fuel, FuelEnergyPerUnit = 2 })
                .Add(original.Goods[0] with { Good = OtherFuel, FuelEnergyPerUnit = 3 }),
            Designs = original.Designs.Select(d => d.Design != WorldFixture.Station ? d : d with
            {
                Parts = d.Parts.Select(p => p with { Capabilities = [..p.Capabilities,
                    new PartCapability.Storage(100), new PartCapability.Production(300), new PartCapability.Extraction(400),
                    new PartCapability.Construction(500, PlotSize.Medium, SizeClass.Small), new PartCapability.Power(power)] }).ToImmutableArray()
            }).ToImmutableArray(),
            SettlementTemplates = [original.SettlementTemplates[0] with
            {
                Plots = [new(1, PlotSize.SmallStation, WorldFixture.Station), new(2, PlotSize.SmallStation, WorldFixture.Station)],
                SpecialInitialInventory = stock.IsDefault ? [] : stock
            }]
        };
        return WorldFixture.State(content: content);
    }

    internal static FacilityRequest.Work Work(ulong building = 1, CapabilityKind kind = CapabilityKind.Production,
        int priority = 0, double rate = 60, double power = 1, double labor = 1)
        => new(new(new(building), kind), priority, rate, power, labor);

    internal static FacilityRequest.Batch Batch(ulong building, int priority, params GoodsQuantity[] goods)
        => new(new(new(building), CapabilityKind.Production), priority, goods.ToImmutableArray());

    internal static SettlementResolution Resolve(CoreState state, ImmutableArray<FacilityRequest> facilities,
        double labor = 100, ImmutableArray<GoodsQuantity> basic = default, ImmutableArray<GoodsQuantity> improvement = default)
        => SettlementResources.Resolve(state.World, state.Content, state.World.Settlements.Single().Id, labor,
            basic.IsDefault ? [] : basic, facilities, _ => improvement.IsDefault ? [] : improvement,
            SettlementResources.Fuels(state.World, state.Content).ToImmutableArray());

    internal static long Stock(WorldState world, GoodId good)
        => world.Inventories.Single(i => i.Id == world.Settlements.Single().Inventory).Goods
            .Where(g => g.Good == good).Sum(g => g.Quantity);

    internal static double Remainder(WorldState world) => world.Settlements.Single().FuelEnergyRemaining;
}
