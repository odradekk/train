using System.Collections.Immutable;
using Train.Core.Contracts;
using Train.Core.Inventory;
using Train.Core.Production;
using Train.Core.World;
using Train.Core.Trading;

namespace Train.Core.Settlements;

internal static class SettlementQueries
{
    internal static SettlementView Read(WorldState world, CoreContentProjection content, SettlementState settlement, ulong step)
    {
        var targets = new MarketStockTargets(world, content, settlement, step);
        var potential = targets.Facilities.ToDictionary(f => f.Key);
        var plots = settlement.Plots.Select(p => new PlotView(p.Id, p.TemplateNumber, p.Size,
            p.Size is PlotSize.SmallStation or PlotSize.LargeStation ? [BuildingKind.Station]
                : Enum.GetValues<BuildingKind>().Where(k => k != BuildingKind.Station).ToImmutableArray(),
            p.Building is { } building ? new PlotOccupant.Building(building)
                : world.ConstructionJobs.SingleOrDefault(j => j.Plot == p.Id) is { } site
                    ? new PlotOccupant.Construction(site.Id) : new PlotOccupant.Empty())).ToImmutableArray();
        var buildings = settlement.Plots.Where(p => p.Building is not null).Select(p => p.Building!.Value).OrderBy(b => b.Value).ToArray();
        var facilities = buildings.SelectMany(b => new[] { CapabilityKind.Production, CapabilityKind.Extraction }
                .Select(c => ProductionQueries.Find(world, content, new(b, c))))
            .OfType<FacilityView>().Select(view => potential.GetValueOrDefault(view.Key) ?? view).ToImmutableArray();
        var power = new PowerPoolView(buildings.Sum(b => BuildingCapabilities.Read(world, content, b).Power),
            settlement.ProductionFacilities.Sum(f => f.LastPowerDemand) + settlement.LastConstructionPowerDemand,
            settlement.ProductionFacilities.Sum(f => f.LastActualSupply) + settlement.LastConstructionActualSupply,
            settlement.FuelEnergyRemaining);
        var market = new MarketView(MarketRule(world, content), InventoryStorage.Settlement(world, content, settlement, step).View, targets.Goods(world, content));
        return new(settlement.Id, settlement.Node, settlement.Type, settlement.Template.Id, settlement.Relation, settlement.Inventory,
            plots, settlement.ParkedVehicles, ParkingCapacity: int.MaxValue, world.Nodes.Single(n => n.Id == settlement.Node).Platforms,
            world.OccupiedPlatforms(settlement.Node), VehicleTrades.ForSale(world, settlement), VehicleTrades.PurchaseCapacities(world, content, settlement),
            facilities, power, PopulationQueries.Read(world, content, settlement, step),
            Engagement: settlement.Engagement, MoraleRatio: 1, Surrendered: false,
            CommissionJobs: world.ConstructionJobs.Where(j => j.Settlement == settlement.Id && j.Funding == JobFunding.NpcCommission)
                .OrderBy(j => j.Id.Value).Select(j => j.Id).ToImmutableArray(),
            OwnedJobs: settlement.Relation != SettlementRelation.PlayerOwned ? [] : world.ConstructionJobs
                .Where(j => j.Settlement == settlement.Id && j.Funding == JobFunding.OwnInventory)
                .OrderBy(j => j.Id.Value).Select(j => j.Id).ToImmutableArray(),
            world.Leases.Where(l => l.Settlement == settlement.Id).Select(l => l.Id).ToImmutableArray(), market);
    }

    internal static EconomyView Economy(WorldState world, CoreContentProjection content)
    {
        var rules = world.Source.Configuration.Rules;
        return new(world.Money, MarketRule(world, content),
            Rule(content, rules.Population, content.PopulationRules.SingleOrDefault(r => r.Id == rules.Population)?.Revision, "Population"),
            Rule(content, rules.Construction, content.ConstructionRules.SingleOrDefault(r => r.Id == rules.Construction)?.Revision, "Construction"),
            UnassignedServicePayments: world.UnassignedServicePayments, RecentRunReports: world.RecentRunReports);
    }

    private static RuleStatusView MarketRule(WorldState world, CoreContentProjection content)
    {
        var id = world.Source.Configuration.Rules.Market;
        return Rule(content, id, content.MarketRules.SingleOrDefault(r => r.Id == id)?.Revision, "Market");
    }

    internal static RuleStatusView Rule(CoreContentProjection content, RuleDefinitionId id, SemanticRevision? revision, string field)
        => revision is null || content.MissingReferences.Any(m => m.Reference == new CoreDefinitionReference.Rule(id))
            ? new(id, null, [new("content_missing", $"Configuration.Rules.{field}", null, [id.Value])])
            : new(id, revision, []);
}
