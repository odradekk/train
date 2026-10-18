using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Train.Core.Combat;
using Train.Core.Contracts;
using Train.Core.Settlements;
using Train.Core.Trading;

namespace Train.Core.World;

internal static class WorldStateValidation
{
    internal static void Validate(WorldState world)
    {
        Ordered(world.Nodes, n => n.Id.Value, "/world/nodes");
        Ordered(world.Edges, e => e.Id.Value, "/world/edges");
        Ordered(world.Settlements, s => s.Id.Value, "/world/settlements");
        Ordered(world.FoundingJobs, j => j.Id.Value, "/world/founding_jobs");
        Ordered(world.Lairs, l => l.Id.Value, "/world/lairs");
        Ordered(world.Trains, t => t.Id.Value, "/world/trains");
        Ordered(world.Inventories, i => i.Id.Value, "/world/inventories");
        Ordered(world.Leases, l => l.Id.Value, "/world/leases");
        Ordered(world.Combats, c => c.Id.Value, "/world/combats");
        Strings(world.Designs, d => d.Design.Value, "/world/designs");
        Strings(world.DesignGoods, g => WorldReferences.GoodKey(g.Good), "/world/design_goods");
        Items(world.Instances, "/world/instances");
        Items(world.DefinitionDependencies, "/world/definition_dependencies");
        Items(world.RecentRunReports, "/world/recent_run_reports");
        foreach (var report in world.RecentRunReports)
        {
            Require(report.Train.Value > 0 && report.Train.Value < world.Next.Train && Enum.IsDefined(report.Reason), "/world/recent_run_reports");
            RunTotals(report.Totals, "/world/recent_run_reports/totals");
            Require(NotAfter(report.Totals.StartedAt, report.EndedAt), "/world/recent_run_reports/ended_at");
        }
        var dependencies = world.DefinitionDependencies;
        Strings(dependencies, d => WorldReferences.DefinitionKey(d.Definition) + "\0" + d.Field,
            "/world/definition_dependencies");
        Require(dependencies.All(d => d.FrozenRevision is null && !d.WasMissing && !string.IsNullOrEmpty(d.Field)),
            "/world/definition_dependencies");
        foreach (var dependency in dependencies)
        {
            try { WorldInputs.ValidateReference(dependency.Definition); }
            catch (ArgumentException) { throw new CoreStateFormatException("/world/definition_dependencies/definition"); }
        }
        foreach (var good in world.DesignGoods)
        {
            try { WorldInputs.ValidateDesignGood(good); }
            catch (ArgumentException) { throw new CoreStateFormatException("/world/design_goods"); }
        }
        try { WorldInputs.ValidateInput(world.Source); }
        catch (ArgumentException) { throw new CoreStateFormatException("/world/source"); }
        foreach (var design in world.Designs)
        {
            try { WorldInputs.ValidateDesign(design); }
            catch (ArgumentException) { throw new CoreStateFormatException("/world/designs"); }
            foreach (var unit in design.ReturnUnits)
                Require(unit.Parts.All(id => design.Parts.Any(p => p.Id == id)), "/world/designs/return_units/parts");
        }
        var designs = world.Designs.ToDictionary(d => d.Design);
        Require(designs.TryGetValue(world.Source.Configuration.StartConstraints.RequiredAffordableCommissionDesign, out var commission)
            && commission.Kind is DesignKind.Vehicle, "/world/source/configuration/start_constraints/required_affordable_commission_design");
        Require(world.Source.Configuration.NpcInitialization.ForSaleVehicleCandidates.All(hash =>
            designs.TryGetValue(hash, out var design) && design.Kind is DesignKind.Vehicle), "/world/source/configuration/npc_initialization/for_sale_vehicle_candidates");
        Require(world.Source.Start.Trains.All(t => t.Vehicles.All(hash =>
            designs.TryGetValue(hash, out var design) && design.Kind is DesignKind.Vehicle)), "/world/source/start/trains/vehicles");
        foreach (var settlement in world.Settlements)
        {
            Require(double.IsFinite(settlement.LastConstructionPowerDemand) && settlement.LastConstructionPowerDemand >= 0
                && double.IsFinite(settlement.LastConstructionActualSupply) && settlement.LastConstructionActualSupply >= 0
                && settlement.LastConstructionActualSupply <= settlement.LastConstructionPowerDemand
                && double.IsFinite(settlement.LastConstructionAllocatedLabor) && settlement.LastConstructionAllocatedLabor >= 0,
                "/world/settlements/last_construction_allocation");
            ValidatePopulation(settlement, world.Source.Configuration.Rules.Population);
            Require(double.IsFinite(settlement.FuelEnergyRemaining) && settlement.FuelEnergyRemaining >= 0,
                "/world/settlements/fuel_energy_remaining");
            try { WorldInputs.ValidateTemplate(settlement.Template); }
            catch (ArgumentException) { throw new CoreStateFormatException("/world/settlements/template"); }
            Require(settlement.Template.Plots.All(p => p.PrebuiltDesign is not { } hash ||
                designs.TryGetValue(hash, out var design) && design.Kind is DesignKind.Building), "/world/settlements/template/plots/prebuilt_design");
            Require(settlement.Template.FoundingBuildings.All(b => designs.TryGetValue(b.Design, out var design)
                && design.Kind is DesignKind.Building), "/world/settlements/template/founding_buildings/design");
        }
        var nodes = world.Nodes.ToDictionary(n => n.Id);
        var edges = world.Edges.ToDictionary(e => e.Id);
        var settlements = world.Settlements.ToDictionary(s => s.Id);
        var lairs = world.Lairs.ToDictionary(l => l.Id);
        var trains = world.Trains.ToDictionary(t => t.Id);
        var inventories = world.Inventories.ToDictionary(i => i.Id);
        var leases = world.Leases.ToDictionary(l => l.Id);
        var plots = new Dictionary<PlotId, (SettlementState Settlement, PlotState Plot)>();
        var instances = new Dictionary<InstanceId, InstanceState>();
        (int Kind, ulong Id) previousInstance = (-1, 0);
        foreach (var instance in world.Instances)
        {
            var key = instance.Id switch
            {
                InstanceId.Building b => (0, b.Id.Value),
                InstanceId.Vehicle v => (1, v.Id.Value),
                _ => throw new CoreStateFormatException("/world/instances/id")
            };
            Require(key.Item2 != 0 && key.CompareTo(previousInstance) > 0, "/world/instances/id");
            previousInstance = key;
            instances.Add(instance.Id, instance);
            Require(Enum.IsDefined(instance.Ownership) && instance.StructureDurability >= 0, "/world/instances");
            Require(designs.TryGetValue(instance.Design, out var design), "/world/instances/design");
            Items(instance.Parts, "/world/instances/parts");
            Require(instance.Parts.Select(p => p.Id).SequenceEqual(design.Parts.Select(p => p.Id))
                && instance.Parts.All(p => p.Current >= 0), "/world/instances/parts");
        }
        foreach (var node in world.Nodes)
        {
            Require(double.IsFinite(node.Position.X) && double.IsFinite(node.Position.Y)
                && node.Position.X >= 0 && node.Position.X <= world.Source.Configuration.WidthMeters
                && node.Position.Y >= 0 && node.Position.Y <= world.Source.Configuration.HeightMeters, "/world/nodes/position");
            Terrain(node.Terrain, "/world/nodes/terrain");
            Require(node.Platforms >= 0 && node.OccupiedPlatforms == 0, "/world/nodes/platforms");
            Require(node.FoundingJob is not { } founding || node.Occupant is NodeOccupant.Empty
                && world.FoundingJobs.Any(j => j.Id == founding && j.Node == node.Id), "/world/nodes/founding_job");
            switch (node.Occupant)
            {
                case NodeOccupant.Empty: break;
                case NodeOccupant.Settlement s:
                    Require(settlements.TryGetValue(s.Id, out var settlement) && settlement.Node == node.Id
                        && settlement.Relation == s.Relation, "/world/nodes/occupant"); break;
                case NodeOccupant.Lair l:
                    Require(lairs.TryGetValue(l.Id, out var lair) && lair.Node == node.Id, "/world/nodes/occupant"); break;
                default: throw new CoreStateFormatException("/world/nodes/occupant");
            }
            if (node.Occupant is not NodeOccupant.Settlement) Require(node.Platforms == 0, "/world/nodes/platforms");
            Strings(node.Resources, r => WorldReferences.GoodKey(r.Good), "/world/nodes/resources");
            foreach (var resource in node.Resources)
            {
                Good(resource.Good, "/world/nodes/resources/good");
                Require(resource.Good is GoodId.Voxel && resource.Quantity >= 0, "/world/nodes/resources");
                Require(resource.Renewal is ResourceRenewal.NonRenewable || resource.Renewal is ResourceRenewal.Renewable r
                    && r.Maximum >= resource.Quantity && double.IsFinite(r.UnitsPerSecond) && r.UnitsPerSecond >= 0,
                    "/world/nodes/resources/renewal");
            }
        }
        ValidateResources(world, nodes);
        var pairs = new HashSet<(NodeId, NodeId)>();
        var breakIds = new HashSet<ulong>();
        foreach (var edge in world.Edges)
        {
            Require(nodes.ContainsKey(edge.Start) && nodes.ContainsKey(edge.End) && edge.Start.Value < edge.End.Value
                && pairs.Add((edge.Start, edge.End)), "/world/edges/endpoints");
            Require(double.IsFinite(edge.LengthMeters) && edge.LengthMeters > 0 && edge.TrackLimit > 0
                && edge.TrackLimit <= world.Source.Configuration.InitialRailway.MaximumTracks
                && edge.Tracks >= 0 && edge.Tracks <= edge.TrackLimit, "/world/edges");
            var start = nodes[edge.Start].Position;
            var end = nodes[edge.End].Position;
            var distance = Math.Sqrt((start.X - end.X) * (start.X - end.X) + (start.Y - end.Y) * (start.Y - end.Y));
            Require(double.IsFinite(distance) && distance > 0 && edge.LengthMeters >= distance, "/world/edges/length_meters");
            Terrain(edge.Terrain, "/world/edges/terrain");
            Ordered(edge.Breaks, b => b.Id.Value, "/world/edges/breaks");
            foreach (var broken in edge.Breaks)
            {
                Require(breakIds.Add(broken.Id.Value), "/world/edges/breaks/id");
                Require(broken.Track >= 1 && broken.Track <= edge.Tracks, "/world/edges/breaks/track");
                Require(double.IsFinite(broken.DistanceMeters) && broken.DistanceMeters >= 0
                    && broken.DistanceMeters <= edge.LengthMeters, "/world/edges/breaks/distance_meters");
            }
            Items(edge.Works, "/world/edges/works");
            Require(edge.Works.IsEmpty, "/world/edges/works");
        }
        var parkedVehicles = new HashSet<VehicleId>();
        foreach (var settlement in world.Settlements)
        {
            Ordered(settlement.ParkedVehicles, id => id.Value, "/world/settlements/parked_vehicles");
            foreach (var vehicle in settlement.ParkedVehicles)
                Require(parkedVehicles.Add(vehicle)
                    && instances.TryGetValue(new InstanceId.Vehicle(vehicle), out var parked)
                    && parked.Details is InstanceDetails.Vehicle { Train: null }
                    && (parked.Ownership == SettlementRelation.PlayerOwned || parked.Ownership == settlement.Relation), "/world/settlements/parked_vehicles");
            Require(nodes.TryGetValue(settlement.Node, out var node)
                && node.Occupant == new NodeOccupant.Settlement(settlement.Id, settlement.Relation), "/world/settlements/node");
            Identity(settlement.Type.Value, "/world/settlements/type");
            Require(Enum.IsDefined(settlement.Relation) && settlement.Population > 0
                && settlement.Template.Type == settlement.Type, "/world/settlements");
            Require(CombatRules.Valid(settlement.Engagement), "/world/settlements/engagement");
            Ordered(settlement.Plots, p => p.Id.Value, "/world/settlements/plots");
            Require(settlement.Plots.Select(p => (p.TemplateNumber, p.Size)).SequenceEqual(
                settlement.Template.Plots.Select(p => (p.Number, p.Size))), "/world/settlements/plots");
            foreach (var plot in settlement.Plots)
            {
                Require(plots.TryAdd(plot.Id, (settlement, plot)), "/world/settlements/plots/id");
                if (plot.Building is { } building)
                    Require(instances.TryGetValue(new InstanceId.Building(building), out var instance)
                        && instance.Details == new InstanceDetails.Building(plot.Id, plot.Size)
                        && instance.Ownership == settlement.Relation, "/world/settlements/plots/building");
            }
            ValidateProduction(settlement);
            Require(inventories.TryGetValue(settlement.Inventory, out var inventory)
                && inventory.Owner == new InventoryOwner.Settlement(settlement.Id), "/world/settlements/inventory");
            Require(node.Platforms == WorldState.CountPlatforms(settlement, instances, designs), "/world/nodes/platforms");
        }
        foreach (var lair in world.Lairs)
        {
            Identity(lair.Type.Value, "/world/lairs/type");
            Require(nodes.TryGetValue(lair.Node, out var node) && node.Occupant == new NodeOccupant.Lair(lair.Id), "/world/lairs/node");
        }
        foreach (var train in world.Trains)
        {
            ValidateTrain(world, train, nodes, edges);
            Items(train.Vehicles, "/world/trains/vehicles");
            Require(!train.Vehicles.IsEmpty && train.Vehicles.Distinct().Count() == train.Vehicles.Length, "/world/trains/vehicles");
            foreach (var vehicle in train.Vehicles)
                Require(instances.TryGetValue(new InstanceId.Vehicle(vehicle), out var instance)
                    && instance.Details is InstanceDetails.Vehicle { Train: { } owner } && owner == train.Id
                    && instance.Ownership == SettlementRelation.PlayerOwned,
                    "/world/trains/vehicles");
            Require(inventories.TryGetValue(train.Inventory, out var inventory)
                && inventory.Owner == new InventoryOwner.Train(train.Id), "/world/trains/inventory");

        }
        foreach (var track in world.Trains.Select(t => t.Position).OfType<TrainPosition.Edge>()
            .GroupBy(p => (p.Id, p.Track)))
            Require(track.Select(p => p.Direction).Distinct().Count() == 1, "/world/trains/position/direction");
        foreach (var node in world.Trains.Select(t => t.Position).OfType<TrainPosition.Node>()
            .Where(p => p.PlatformNumber is not null).GroupBy(p => p.Id))
            Require(node.Select(p => p.PlatformNumber).Distinct().Count() == node.Count(), "/world/trains/position/platform_number");
        Items(world.TrackHandoffs, "/world/track_handoffs");
        (ulong Edge, int Track) previousHandoff = (0, 0);
        foreach (var handoff in world.TrackHandoffs)
        {
            var key = (handoff.Edge.Value, handoff.Track);
            Require(key.CompareTo(previousHandoff) > 0, "/world/track_handoffs");
            previousHandoff = key;
            Require(edges.TryGetValue(handoff.Edge, out var edge), "/world/track_handoffs/edge");
            Require(handoff.Track >= 1 && handoff.Track <= edge.Tracks, "/world/track_handoffs/track");
            Require(!edge.Breaks.Any(b => b.Track == handoff.Track), "/world/track_handoffs/track");
            Require(Enum.IsDefined(handoff.Direction), "/world/track_handoffs/direction");
            Require(!world.Trains.Any(t => t.Position is TrainPosition.Edge p
                && p.Id == handoff.Edge && p.Track == handoff.Track), "/world/track_handoffs/track");
        }
        foreach (var instance in world.Instances)
        {
            switch (instance.Id, instance.Details, designs[instance.Design].Kind)
            {
                case (InstanceId.Building id, InstanceDetails.Building b, DesignKind.Building design):
                    Require(plots.TryGetValue(b.Plot, out var plot) && plot.Plot.Building == id.Id
                        && b.Size == plot.Plot.Size && WorldInputs.FitsPlot(design.PlotSize, b.Size), "/world/instances/details"); break;
                case (InstanceId.Vehicle id, InstanceDetails.Vehicle v, DesignKind.Vehicle design):
                    Require(design.Size == v.Size && (v.Train is { } train
                        ? trains.TryGetValue(train, out var owner) && owner.Vehicles.Contains(id.Id)
                        : parkedVehicles.Contains(id.Id)), "/world/instances/details"); break;
                default: throw new CoreStateFormatException("/world/instances/details");
            }
        }
        foreach (var lease in world.Leases)
        {
            Require(settlements.ContainsKey(lease.Settlement), "/world/leases/settlement");
            Require(inventories.TryGetValue(lease.Inventory, out var inventory)
                && inventory.Owner == new InventoryOwner.Lease(lease.Id), "/world/leases/inventory");
            Require(lease.Capacity > 0, "/world/leases/capacity");
            Require(lease.ExpiresAtStep > 0, "/world/leases/expires_at_step");
            Require(lease.Paid.Value >= 0, "/world/leases/paid");
        }
        var reservationJobs = new HashSet<JobId>();
        foreach (var inventory in world.Inventories)
        {
            Require(inventory.Owner switch
            {
                InventoryOwner.Settlement s => settlements.TryGetValue(s.Id, out var owner) && owner.Inventory == inventory.Id,
                InventoryOwner.Train t => trains.TryGetValue(t.Id, out var owner) && owner.Inventory == inventory.Id,
                InventoryOwner.Lease l => leases.TryGetValue(l.Id, out var owner) && owner.Inventory == inventory.Id,
                _ => false
            }, "/world/inventories/owner");
            Strings(inventory.Goods, g => WorldReferences.GoodKey(g.Good), "/world/inventories/goods");
            foreach (var good in inventory.Goods) { Good(good.Good, "/world/inventories/goods"); Require(good.Quantity > 0, "/world/inventories/goods"); }
            Items(inventory.Reservations, "/world/inventories/reservations");
            foreach (var reservation in inventory.Reservations)
            {
                Require(reservation.Job.Value != 0 && reservationJobs.Add(reservation.Job), "/world/inventories/reservations/job");
                Require(reservation.Capacity > 0, "/world/inventories/reservations/capacity");
                Strings(reservation.ExpectedReturns, g => WorldReferences.GoodKey(g.Good),
                    "/world/inventories/reservations/expected_returns");
                Int128 total = 0;
                foreach (var good in reservation.ExpectedReturns)
                {
                    Good(good.Good, "/world/inventories/reservations/expected_returns");
                    Require(good.Quantity > 0, "/world/inventories/reservations/expected_returns");
                    total += good.Quantity;
                }
                Require(total > 0 && total <= reservation.Capacity, "/world/inventories/reservations/expected_returns");
            }
        }
        Require(world.Money.Value >= 0 && world.Next is not null, "/world/money");
        Require(world.UnassignedServicePayments.Value >= 0, "/world/unassigned_service_payments");
        ValidateConstruction(world);
        ValidateFounding(world);
        ValidateCombat(world, nodes, edges, settlements, trains, instances, designs);
        var next = world.Next;
        Next(next.Node, world.Nodes.Select(n => n.Id.Value), "/world/next/node");
        Next(next.Edge, world.Edges.Select(e => e.Id.Value), "/world/next/edge");
        Next(next.Settlement, world.Settlements.Select(s => s.Id.Value).Concat(world.FoundingJobs.Select(j => j.ReservedSettlementId.Value)), "/world/next/settlement");
        Next(next.Lair, world.Lairs.Select(l => l.Id.Value), "/world/next/lair");
        Next(next.Plot, plots.Keys.Select(p => p.Value), "/world/next/plot");
        Next(next.Building, world.Instances.Select(i => i.Id).OfType<InstanceId.Building>().Select(i => i.Id.Value), "/world/next/building");
        Next(next.Vehicle, world.Instances.Select(i => i.Id).OfType<InstanceId.Vehicle>().Select(i => i.Id.Value), "/world/next/vehicle");
        Next(next.Train, world.Trains.Select(t => t.Id.Value), "/world/next/train");
        Next(next.Inventory, world.Inventories.Select(i => i.Id.Value), "/world/next/inventory");
        Next(next.Break, breakIds, "/world/next/break");
        Next(next.Lease, world.Leases.Select(l => l.Id.Value), "/world/next/lease");
        Next(next.Job, world.ConstructionJobs.Select(j => j.Id.Value).Concat(world.FoundingJobs.Select(j => j.Id.Value)), "/world/next/job");
        Next(next.Combat, world.Combats.Select(c => c.Id.Value), "/world/next/combat");
    }

    private static void ValidateFounding(WorldState world)
    {
        const string field = "/world/founding_jobs";
        var ids = world.ConstructionJobs.Select(j => j.Id).ToHashSet();
        var settlements = world.Settlements.Select(s => s.Id).ToHashSet();
        foreach (var job in world.FoundingJobs)
        {
            var path = field + $"/{job.Id.Value}";
            Require(ids.Add(job.Id), path + "/id");
            Require(job.ReservedSettlementId.Value > 0 && settlements.Add(job.ReservedSettlementId), path + "/reserved_settlement_id");
            Require(world.Nodes.Any(n => n.Id == job.Node && n.Occupant is NodeOccupant.Empty
                && n.FoundingJob == job.Id), path + "/node");
            Require(WorldFormat.IsIdentity(job.Rule.Value) && WorldFormat.IsHash(job.StartedFromRevision.Value), path + "/rule");
            Require(job.FrozenDurationTicks > 0 && job.ElapsedTicks < job.FrozenDurationTicks, path + "/elapsed_ticks");
            Require(job.InitialResidents > 0, path + "/initial_residents");
            try { WorldInputs.ValidateTemplate(job.Template); }
            catch (ArgumentException) { throw new CoreStateFormatException(path + "/template"); }
            Require(job.Template.Type == job.Type, path + "/type");
            Require(job.Template.SpecialInitialInventory.IsEmpty && job.Template.ProductionPlans.IsEmpty, path + "/template");
            foreach (var plot in job.Template.Plots)
            {
                var building = job.Template.FoundingBuildings.SingleOrDefault(b => b.PlotNumber == plot.Number);
                Require(plot.PrebuiltDesign == building?.Design, path + "/template/plots/prebuilt_design");
            }
            foreach (var building in job.Template.FoundingBuildings)
                Require(job.Template.Plots.Any(p => p.Number == building.PlotNumber
                    && world.Designs.Any(d => d.Design == building.Design && d.Kind is DesignKind.Building kind
                        && WorldInputs.FitsPlot(kind.PlotSize, p.Size))), path + "/template/founding_buildings");
            Strings(job.InvestedGoods, g => WorldReferences.GoodKey(g.Good), path + "/invested_goods");
            foreach (var good in job.InvestedGoods)
            {
                Good(good.Good, path + "/invested_goods/good");
                Require(good.Quantity > 0, path + "/invested_goods/quantity");
            }
        }
    }

    private static void ValidateConstruction(WorldState world)
    {
        const string field = "/world/construction_jobs";
        Ordered(world.ConstructionJobs, j => j.Id.Value, field);
        foreach (var queue in world.ConstructionJobs.GroupBy(j => j.Facility))
        {
            Require(queue.Count() == 1 || queue.All(j => j.Funding == JobFunding.NpcCommission), field + "/facility");
            Require(queue.Skip(1).All(j => j.State == ConstructionState.Queued), field + "/state");
        }
        var reservations = world.Inventories.SelectMany(i => i.Reservations.Select(r => (Inventory: i, Reservation: r)))
            .ToDictionary(r => r.Reservation.Job);
        Require(reservations.Keys.All(id => world.ConstructionJobs.Any(j => j.Id == id)), "/world/inventories/reservations/job");
        var targets = new HashSet<InstanceId>();
        var sites = new HashSet<PlotId>();
        foreach (var job in world.ConstructionJobs)
        {
            var path = field + $"/{job.Id.Value}";
            Require(job.Facility is not null && job.Facility.Capability == CapabilityKind.Construction, path + "/facility");
            Require(world.Settlements.Any(s => s.Id == job.Settlement && s.Plots.Any(p => p.Building == job.Facility.Building)), path + "/settlement");
            var design = world.Designs.SingleOrDefault(d => d.Design == job.TargetDesign);
            Require(design is not null, path + "/target_design");
            Require(Enum.IsDefined(job.Kind) && (job.Kind == ConstructionKind.Construction) == (job.Instance is null), path + "/kind");
            InstanceState? instance = null;
            if (job.Plot is { } plot)
            {
                var owner = world.Settlements.Single(s => s.Id == job.Settlement);
                var plotState = owner.Plots.SingleOrDefault(p => p.Id == plot);
                Require(sites.Add(plot) && job.Instance is null && design.Kind is DesignKind.Building
                    && plotState is not null && plotState.Building is null, path + "/plot");
            }
            else if (job.Instance is { } target)
            {
                instance = world.Instances.SingleOrDefault(i => i.Id == target);
                Require(targets.Add(target) && instance is not null
                    && instance.Ownership == SettlementRelation.PlayerOwned, path + "/instance");
                Require(job.Kind == ConstructionKind.Refit || instance.Design == job.TargetDesign, path + "/target_design");
                if (target is InstanceId.Building building)
                    Require(design.Kind is DesignKind.Building
                        && job.Kind is ConstructionKind.Repair or ConstructionKind.Refit or ConstructionKind.Deconstruction
                        && world.Settlements.Single(s => s.Id == job.Settlement).Plots.Any(p => p.Building == building.Id), path + "/instance");
                else Require(design.Kind is DesignKind.Vehicle, path + "/target_design");
            }
            else if (design.Kind is DesignKind.LargeEquipment)
                Require(job.ReturnInventory == world.Settlements.Single(s => s.Id == job.Settlement).Inventory, path + "/return_inventory");
            else Require(design.Kind is DesignKind.Vehicle, path + "/target_design");
            Items(job.RetainedParts, path + "/retained_parts");
            Items(job.RemovedItems, path + "/removed_items");
            if (job.Kind != ConstructionKind.Refit)
                Require(job.RetainedParts.IsEmpty && job.RemovedItems.IsEmpty, path + "/retained_parts");
            else
            {
                var before = world.Designs.Single(d => d.Design == instance!.Design);
                var after = world.Designs.Single(d => d.Design == job.TargetDesign);
                Require(job.RetainedParts.Select(p => p.Before).Distinct().Count() == job.RetainedParts.Length
                    && job.RetainedParts.Select(p => p.After).Distinct().Count() == job.RetainedParts.Length
                    && job.RetainedParts.All(p => before.Parts.Any(b => b.Id == p.Before)
                        && after.Parts.Any(a => a.Id == p.After
                            && a.Definition == before.Parts.Single(b => b.Id == p.Before).Definition)), path + "/retained_parts");
                var removed = new HashSet<PartId>();
                foreach (var unit in job.RemovedItems)
                {
                    Good(unit.Good, path + "/removed_items/good");
                    Require(unit.Quantity > 0, path + "/removed_items/quantity");
                    Items(unit.Parts, path + "/removed_items/parts");
                    Require(unit.Parts.All(id => removed.Add(id) && before.Parts.Any(p => p.Id == id)
                        && !job.RetainedParts.Any(p => p.Before == id)), path + "/removed_items/parts");
                    Strings(unit.StructuralGoods, g => WorldReferences.GoodKey(g.Good), path + "/removed_items/structural_goods");
                    foreach (var good in unit.StructuralGoods)
                    {
                        Good(good.Good, path + "/removed_items/structural_goods");
                        Require(good.Quantity > 0, path + "/removed_items/structural_goods");
                    }
                    Items(unit.MissingReferences, path + "/removed_items/missing_references");
                    foreach (var missing in unit.MissingReferences)
                    {
                        try { WorldInputs.ValidateReference(missing.Reference); }
                        catch (ArgumentException) { throw new CoreStateFormatException(path + "/removed_items/missing_references"); }
                        Require(missing.FieldPath is not null, path + "/removed_items/missing_references");
                    }
                }
            }
            if (job.Kind == ConstructionKind.Refit)
            {
                var old = world.Designs.Single(d => d.Design == instance!.Design);
                Require(old.Parts.All(p => job.RetainedParts.Any(pair => pair.Before == p.Id)
                    || job.RemovedItems.Any(u => u.Parts.Contains(p.Id))), path + "/retained_parts");
            }
            Require((job.ReturnInventory is not null) == reservations.ContainsKey(job.Id), path + "/return_inventory");
            if (job.ReturnInventory is { } returnInventory)
            {
                Require(job.Kind is ConstructionKind.Refit or ConstructionKind.Deconstruction
                    || design.Kind is DesignKind.LargeEquipment, path + "/return_inventory");
                var destination = reservations[job.Id].Inventory;
                Require(destination.Id == returnInventory, path + "/return_inventory");
                var settlement = world.Settlements.Single(s => s.Id == job.Settlement);
                Require(job.Funding == JobFunding.OwnInventory
                    ? destination.Id == settlement.Inventory && settlement.Relation == SettlementRelation.PlayerOwned
                    : destination.Owner is InventoryOwner.Lease lease
                        && world.Leases.Any(l => l.Id == lease.Id && l.Settlement == job.Settlement), path + "/return_inventory");
            }
            Require(WorldFormat.IsIdentity(job.Rule.Value) && WorldFormat.IsHash(job.StartedFromRevision.Value), path + "/rule");
            Require(job.FrozenTotalWork >= 0 && double.IsFinite(job.ProgressWork) && job.ProgressWork >= 0
                && (job.FrozenTotalWork == 0 ? (job.Kind is ConstructionKind.Refit or ConstructionKind.Deconstruction) && job.ProgressWork == 0
                    : job.ProgressWork < job.FrozenTotalWork), path + "/progress_work");
            Require(double.IsFinite(job.FrozenPowerPerWork) && job.FrozenPowerPerWork >= 0
                && double.IsFinite(job.FrozenLaborPerWork) && job.FrozenLaborPerWork >= 0, path + "/frozen_factors");
            Require(Enum.IsDefined(job.Funding), path + "/funding");
            Require(job.State is ConstructionState.Working or ConstructionState.ResourceBlocked
                || job.Funding == JobFunding.NpcCommission && job.State == ConstructionState.Queued
                || job.Kind == ConstructionKind.Deconstruction && job.State == ConstructionState.Paused, path + "/state");
            Require(job.State != ConstructionState.Queued || job.ProgressWork == 0 && job.LastActualWorkPerSecond == 0
                && job.LastLimits.IsEmpty && job.Blockers.IsEmpty, path + "/progress_work");
            Require(double.IsFinite(job.LastActualWorkPerSecond) && job.LastActualWorkPerSecond >= 0,
                path + "/last_actual_work_per_second");
            Items(job.LastLimits, path + "/last_limits");
            Require(job.LastLimits.All(l => Enum.IsDefined(l))
                && job.LastLimits.SequenceEqual(job.LastLimits.Distinct().Order()), path + "/last_limits");
            ProductionGoods(job.InvestedGoods, path + "/invested_goods");
            Items(job.SourceConsumption, path + "/source_consumption");
            Require(job.SourceConsumption.Select(s => s.Inventory).Distinct().Count() == job.SourceConsumption.Length, path + "/source_consumption");
            var totals = new Dictionary<GoodId, Int128>();
            foreach (var source in job.SourceConsumption)
            {
                Require(source.Inventory.Value > 0 && source.Inventory.Value < world.Next.Inventory, path + "/source_consumption/inventory");
                ProductionGoods(source.Goods, path + "/source_consumption/goods");
                Require(!source.Goods.IsEmpty, path + "/source_consumption/goods");
                foreach (var good in source.Goods) totals[good.Good] = totals.GetValueOrDefault(good.Good) + good.Quantity;
            }
            Require(totals.Count == job.InvestedGoods.Length && job.InvestedGoods.All(g => totals.GetValueOrDefault(g.Good) == g.Quantity), path + "/invested_goods");
            Items(job.Blockers, path + "/blockers");
            foreach (var blocker in job.Blockers)
            {
                Require(!string.IsNullOrEmpty(blocker.Code) && !string.IsNullOrEmpty(blocker.Field), path + "/blockers");
                Items(blocker.RelatedIdentities, path + "/blockers/related_identities");
            }
        }
    }

    private static void ValidateProduction(SettlementState settlement)
    {
        const string field = "/world/settlements/production_facilities";
        Items(settlement.ProductionFacilities, field);
        Require(settlement.ProductionFacilities.All(f => f.Facility is not null
            && f.Facility.Building.Value != 0 && f.Facility.Capability is CapabilityKind.Production or CapabilityKind.Extraction), field + "/facility");
        var expected = settlement.Template.ProductionPlans.Where(p => p.Capability is CapabilityKind.Production or CapabilityKind.Extraction)
            .Select(p => (Plan: p, Building: settlement.Plots.SingleOrDefault(plot => plot.TemplateNumber == p.PlotNumber)?.Building))
            .Where(p => p.Building is not null).Select(p => new FacilityKey(p.Building!.Value, p.Plan.Capability))
            .OrderBy(f => f.Building.Value).ThenBy(f => f.Capability).ToArray();
        Require(settlement.ProductionFacilities.Select(f => f.Facility).SequenceEqual(expected), field + "/facility");
        foreach (var facility in settlement.ProductionFacilities)
        {
            Require(double.IsFinite(facility.LastActualWorkPerSecond) && facility.LastActualWorkPerSecond >= 0,
                field + "/last_actual_work_per_second");
            Require(double.IsFinite(facility.LastPowerDemand) && facility.LastPowerDemand >= 0,
                field + "/last_power_demand");
            Require(double.IsFinite(facility.LastActualSupply) && facility.LastActualSupply >= 0
                && facility.LastActualSupply <= facility.LastPowerDemand, field + "/last_actual_supply");
            Require(double.IsFinite(facility.LastAllocatedLabor) && facility.LastAllocatedLabor >= 0,
                field + "/last_allocated_labor");
            Items(facility.LastLimits, field + "/last_limits");
            Require(facility.LastLimits.All(l => Enum.IsDefined(l))
                && facility.LastLimits.SequenceEqual(facility.LastLimits.Distinct().Order()), field + "/last_limits");
            ProductionGoods(facility.OutputBuffer, field + "/output_buffer");
            if (facility.Batch is not { } batch) continue;
            Require(facility.OutputBuffer.IsEmpty, field + "/output_buffer");
            Require(WorldFormat.IsHash(batch.StartedFromRevision.Value), field + "/batch/started_from_revision");
            Require(batch.FrozenWorkRequired > 0 && double.IsFinite(batch.ProgressWork)
                && batch.ProgressWork >= 0 && batch.ProgressWork < batch.FrozenWorkRequired, field + "/batch/progress_work");
            Require(double.IsFinite(batch.FrozenPowerPerWork) && batch.FrozenPowerPerWork >= 0,
                field + "/batch/frozen_power_per_work");
            Require(double.IsFinite(batch.FrozenLaborPerWork) && batch.FrozenLaborPerWork >= 0,
                field + "/batch/frozen_labor_per_work");
            ProductionGoods(batch.FrozenOutputs, field + "/batch/frozen_outputs");
            Require(!batch.FrozenOutputs.IsEmpty, field + "/batch/frozen_outputs");
        }
    }

    private static void ProductionGoods(ImmutableArray<GoodsQuantity> goods, string field)
    {
        Strings(goods, g => WorldReferences.GoodKey(g.Good), field);
        foreach (var good in goods)
        {
            Good(good.Good, field);
            Require(good.Good is GoodId.Voxel && good.Quantity > 0, field);
        }
    }

    private static void ValidateTrain(WorldState world, TrainState train, Dictionary<NodeId, NodeMapItem> nodes,
        Dictionary<EdgeId, EdgeMapItem> edges)
    {
        Require(Enum.IsDefined(train.OrderFacing) && double.IsFinite(train.SpeedMetersPerSecond)
            && train.SpeedMetersPerSecond >= 0, "/world/trains");
        Require(CombatRules.Valid(train.Engagement), "/world/trains/engagement");
        Require(train.RetreatDestination is not { } destination || nodes.ContainsKey(destination),
            "/world/trains/retreat_destination");
        Require(train.RetreatDestination is null || train.Activity == TrainActivity.Retreating,
            "/world/trains/retreat_destination");
        Require(train.QueueEnteredStep is null || train.Position is TrainPosition.Node
            && train.Activity is TrainActivity.Waiting or TrainActivity.Running or TrainActivity.Combat
                or TrainActivity.Retreating, "/world/trains/queue_entered_step");
        Require(train.ReturningFromBreak is null || train.Position is TrainPosition.Edge returning
            && train.Activity == TrainActivity.Running && edges.TryGetValue(returning.Id, out var returningEdge)
            && returningEdge.Breaks.Any(b => b.Id == train.ReturningFromBreak && b.Track == returning.Track),
            "/world/trains/returning_from_break");
        switch (train.Position)
        {
            case TrainPosition.Node node:
                Require(Enum.IsDefined(node.Phase), "/world/trains/position/phase");
                Require(nodes.ContainsKey(node.Id)
                    && double.IsFinite(node.DistanceMeters) && node.DistanceMeters >= 0
                    && node.PlatformNumber is null or > 0, "/world/trains/position");
                foreach (var edgeId in new[] { node.EnteredFrom, node.NextEdge })
                    if (edgeId is { } id) Require(edges.TryGetValue(id, out var adjacentEdge)
                        && (adjacentEdge.Start == node.Id || adjacentEdge.End == node.Id), "/world/trains/position");
                Require(node.Phase != NodeTravelPhase.Departing || node.NextEdge is not null, "/world/trains/position");
                Require((node.EnteredFrom is null) == (node.EnteredTrack is null) && node.EnteredTrack is null or > 0,
                    "/world/trains/position/entered_track");
                Require(node.NextTrack is null || node.Phase == NodeTravelPhase.Departing && node.NextEdge is { } next
                    && node.NextTrack >= 1 && node.NextTrack <= edges[next].Tracks, "/world/trains/position/next_track");
                Require(node.Phase is not (NodeTravelPhase.Waiting or NodeTravelPhase.Docked)
                    || train.SpeedMetersPerSecond == 0, "/world/trains/position");
                break;
            case TrainPosition.Edge position:
                Require(edges.TryGetValue(position.Id, out var edge)
                    && position.Track >= 1 && position.Track <= edge.Tracks
                    && Enum.IsDefined(position.Direction) && double.IsFinite(position.DistanceMeters)
                    && position.DistanceMeters >= 0 && position.DistanceMeters <= edge.LengthMeters, "/world/trains/position");
                break;
            default: throw new CoreStateFormatException("/world/trains/position");
        }
        if (train.Plan is { } plan)
        {
            Items(plan.Stops, "/world/trains/plan/stops");
            Require(!plan.Stops.IsEmpty, "/world/trains/plan/stops");
            var targets = new HashSet<NodeId>();
            for (var index = 0; index < plan.Stops.Length; index++)
            {
                var stop = plan.Stops[index];
                var target = stop switch
                {
                    PlanStop.Waypoint waypoint => waypoint.Node,
                    PlanStop.Station stationStop => stationStop.Node,
                    _ => throw new CoreStateFormatException("/world/trains/plan/stops")
                };
                Require(nodes.ContainsKey(target), $"/world/trains/plan/stops/{index}/node");
                targets.Add(target);
                if (stop is PlanStop.Station station)
                {
                    Items(station.GoodsRules, $"/world/trains/plan/stops/{index}/goods_rules");
                    var goods = new HashSet<GoodId>();
                    foreach (var rule in station.GoodsRules)
                    {
                        var field = $"/world/trains/plan/stops/{index}/goods_rules";
                        try { AutomaticGoodsOperations.Validate(rule); }
                        catch (ArgumentException) { throw new CoreStateFormatException(field); }
                        Require(goods.Add(rule.Good), field);
                    }
                }
            }
            Require(!plan.Loop || targets.Count >= 2, "/world/trains/plan");
            Require(train.NextStopIndex is null || train.NextStopIndex >= 0 && train.NextStopIndex < plan.Stops.Length,
                "/world/trains/next_stop_index");
            Require(!plan.Loop || train.NextStopIndex is not null, "/world/trains/next_stop_index");
        }
        else Require(train.NextStopIndex is null, "/world/trains/next_stop_index");
        var unfinished = train.Plan is not null && train.NextStopIndex is not null;
        Require((train.CurrentRun is not null) == unfinished, "/world/trains/current_run");
        if (train.CurrentRun is { } run)
        {
            Require(run.SalesReceipts.Value >= 0 && run.PurchasePayments.Value >= 0 && run.TransportedUnits >= 0
                && double.IsFinite(run.LoadedUnitsDistance) && run.LoadedUnitsDistance >= 0
                && double.IsFinite(run.CapacityDistance) && run.CapacityDistance >= 0, "/world/trains/current_run");
            RunTotals(RunReports.Read(run), "/world/trains/current_run");
            foreach (var result in run.IncompleteRules)
                Require(result.StopIndex < train.Plan!.Stops.Length && train.Plan.Stops[result.StopIndex] is PlanStop.Station station
                    && result.RuleIndex < station.GoodsRules.Length && station.GoodsRules[result.RuleIndex].Good == result.Good,
                    "/world/trains/current_run/incomplete_rules");
        }
        var finishingStop = train.Plan is not null && train.NextStopIndex is null
            && train.Activity == TrainActivity.Running && train.StopRequested;
        var combatActivity = train.Activity is TrainActivity.Combat or TrainActivity.Retreating;
        Require(unfinished || finishingStop || combatActivity
            || train.Activity == TrainActivity.Stopped && train.RemainingDwellSteps == 0,
            "/world/trains/activity");
        Require(!train.StopRequested || train.Activity is TrainActivity.Running or TrainActivity.Waiting
            or TrainActivity.Combat or TrainActivity.Retreating, "/world/trains/stop_requested");
        if (train.RemainingDwellSteps > 0)
            Require(unfinished && train.Position is TrainPosition.Node dwellNode
                && train.Plan!.Stops[train.NextStopIndex!.Value] is PlanStop.Station dwellStation
                && dwellStation.Node == dwellNode.Id && train.RemainingDwellSteps <= dwellStation.DwellSteps,
                "/world/trains/remaining_dwell_steps");
        switch (train.Activity)
        {
            case TrainActivity.Stopped:
                Require(train.Position is TrainPosition.Node && train.SpeedMetersPerSecond == 0, "/world/trains/activity");
                break;
            case TrainActivity.Running:
                Require((unfinished || finishingStop) && train.RemainingDwellSteps == 0, "/world/trains/activity");
                break;
            case TrainActivity.Servicing:
                Require(unfinished && train.Position is TrainPosition.Node { Phase: NodeTravelPhase.Docked } && train.SpeedMetersPerSecond == 0
                    && train.RemainingDwellSteps > 0, "/world/trains/activity");
                break;
            case TrainActivity.Waiting:
                Require(unfinished && train.Position is TrainPosition.Node && train.SpeedMetersPerSecond == 0,
                    "/world/trains/activity");
                break;
            case TrainActivity.NoRoute:
                Require(unfinished && train.Position is TrainPosition.Node && train.SpeedMetersPerSecond == 0
                    && train.RemainingDwellSteps == 0, "/world/trains/activity");
                break;
            case TrainActivity.Combat:
                Require(train.Position is TrainPosition.Node && train.SpeedMetersPerSecond == 0
                    && CombatQueries.TrainInCombat(world, train.Id), "/world/trains/activity");
                break;
            case TrainActivity.Retreating:
                Require(train.Position is TrainPosition.Node or TrainPosition.Edge, "/world/trains/activity");
                break;
            default: throw new CoreStateFormatException("/world/trains/activity");
        }
        Require(train.AutomaticEvaluationRemainingSteps is null || unfinished
            && train.Position is TrainPosition.Node atStation
            && train.Plan!.Stops[train.NextStopIndex!.Value] is PlanStop.Station current && current.Node == atStation.Id
            && !current.GoodsRules.IsEmpty
            && train.RemainingDwellSteps > 0 && train.Activity is TrainActivity.Servicing or TrainActivity.Stopped
                or TrainActivity.Waiting or TrainActivity.Combat,
            "/world/trains/automatic_evaluation_remaining_steps");
        Require(train.AutomaticEvaluationRemainingSteps is not null
            || train.Activity != TrainActivity.Servicing && train.RemainingDwellSteps == 0
            || unfinished && train.Plan!.Stops[train.NextStopIndex!.Value] is PlanStop.Station { GoodsRules.IsEmpty: true },
            "/world/trains/automatic_evaluation_remaining_steps");
    }

    private static void ValidateCombat(WorldState world, Dictionary<NodeId, NodeMapItem> nodes,
        Dictionary<EdgeId, EdgeMapItem> edges, Dictionary<SettlementId, SettlementState> settlements,
        Dictionary<TrainId, TrainState> trains, Dictionary<InstanceId, InstanceState> instances,
        Dictionary<DesignHash, DesignProjection> designs)
    {
        var controllers = new HashSet<(int Kind, ulong Id)>();
        var unitsLost = new HashSet<CombatUnitId>();
        var partsLost = new HashSet<(int Kind, ulong Id, int Part)>();
        for (var index = 0; index < world.Combats.Length; index++)
        {
            var combat = world.Combats[index];
            var path = $"/world/combats/{index}";
            Require(combat.Id.Value != 0 && combat.Controllers.IsDefault == false
                && combat.Sides.IsDefault == false && !combat.Controllers.IsEmpty, path);
            switch (combat.Location)
            {
                case WorldLocation.Node node:
                    Require(nodes.ContainsKey(node.Id), path + "/location");
                    break;
                case WorldLocation.Edge edge:
                    Require(edges.TryGetValue(edge.Id, out var edgeItem)
                        && double.IsFinite(edge.DistanceMeters) && edge.DistanceMeters >= 0
                        && edge.DistanceMeters <= edgeItem.LengthMeters, path + "/location");
                    break;
                default: throw new CoreStateFormatException(path + "/location");
            }
            for (var other = 0; other < index; other++)
                Require(!CombatQueries.SameLocation(combat.Location, world.Combats[other].Location),
                    path + "/location");
            (int Kind, ulong Id) previousController = (-1, 0);
            foreach (var controller in combat.Controllers)
            {
                var controllerPath = path + "/controllers";
                var key = CombatQueries.ControllerKey(controller.Controller);
                Require(key.CompareTo(previousController) > 0 && controllers.Add(key), controllerPath);
                previousController = key;
                Require(controller.JoinedAtStep >= combat.StartedAtStep, controllerPath + "/joined_at_step");
                Require(controller.Retreating || controller.RemainingDisengagementSteps == 0,
                    controllerPath + "/remaining_disengagement_steps");
                switch (controller.Controller)
                {
                    case CombatController.Train train:
                        Require(trains.TryGetValue(train.Id, out var controllerTrain)
                            && CombatQueries.SameLocation(combat.Location, WorldState.Location(controllerTrain.Position)),
                            controllerPath + "/train");
                        break;
                    case CombatController.Settlement settlement:
                        Require(settlements.TryGetValue(settlement.Id, out var controllerSettlement)
                            && combat.Location is WorldLocation.Node node && node.Id == controllerSettlement.Node,
                            controllerPath + "/settlement");
                        break;
                    default: throw new CoreStateFormatException(controllerPath);
                }
                var units = CombatQueries.Units(world, controller.Controller);
                Require(!units.IsEmpty, controllerPath + "/units");
                foreach (var unit in units)
                {
                    Require(unit is CombatUnitId.Instance { Id: var id } && instances.ContainsKey(id)
                        && CombatQueries.SameLocation(combat.Location, InstanceLocation(world, id, trains, settlements)),
                        controllerPath + "/units");
                }
                if (controller.FocusTarget is { } focus)
                {
                    var hostileParticipant = combat.Controllers
                        .Where(other => other.Controller != controller.Controller)
                        .SelectMany(other => CombatQueries.Units(world, other.Controller))
                        .Contains(focus);
                    Require(hostileParticipant
                        && units.Any(unit => CombatQueries.Hostile(world, unit, focus)),
                        controllerPath + "/focus_target");
                }
            }
            (int Kind, ulong Id) previousOwner = (-1, 0);
            var owners = new HashSet<(int Kind, ulong Id)>();
            foreach (var side in combat.Sides)
            {
                var sidePath = path + "/sides";
                var owner = OwnerKey(side.Owner);
                Require(owner.CompareTo(previousOwner) > 0 && owners.Add(owner), sidePath + "/owner");
                previousOwner = owner;
                Require(side.Reward.Value >= 0, sidePath + "/reward");
                Items(side.UnitsLost, sidePath + "/units_lost");
                (int Kind, ulong Id) previousUnit = (-1, 0);
                foreach (var lost in side.UnitsLost)
                {
                    Require(lost is DestroyedUnit.Instance, sidePath + "/units_lost");
                    var unit = (DestroyedUnit.Instance)lost;
                    var unitKey = CombatQueries.UnitKey(new CombatUnitId.Instance(unit.Value.Instance));
                    Require(unitKey.CompareTo(previousUnit) > 0 && unitsLost.Add(new CombatUnitId.Instance(unit.Value.Instance)),
                        sidePath + "/units_lost");
                    previousUnit = unitKey;
                    Require(designs.ContainsKey(unit.Value.Design)
                        && CombatQueries.SameLocation(unit.Value.Location, combat.Location)
                        && !instances.ContainsKey(unit.Value.Instance), sidePath + "/units_lost");
                }
                Items(side.PartsLost, sidePath + "/parts_lost");
                (int Kind, ulong Id, int Part) previousPart = (-1, 0, -1);
                foreach (var lost in side.PartsLost)
                {
                    var instance = lost.Instance.Instance;
                    var partKey = (CombatQueries.UnitKey(new CombatUnitId.Instance(instance)).Kind,
                        CombatQueries.UnitKey(new CombatUnitId.Instance(instance)).Id, lost.Part.Value);
                    Require(instance is InstanceId.Building or InstanceId.Vehicle
                        && partKey.CompareTo(previousPart) > 0 && partsLost.Add(partKey), sidePath + "/parts_lost");
                    previousPart = partKey;
                    Require(designs.TryGetValue(lost.Instance.Design, out var design)
                        && design.Parts.Any(part => part.Id == lost.Part)
                        && CombatQueries.SameLocation(lost.Instance.Location, combat.Location), sidePath + "/parts_lost");
                    if (instances.TryGetValue(instance, out var survivor))
                        Require(survivor.Parts.Single(p => p.Id == lost.Part).Current == 0, sidePath + "/parts_lost");
                }
                Strings(side.CargoLost, good => WorldReferences.GoodKey(good.Good), sidePath + "/cargo_lost");
                foreach (var cargo in side.CargoLost)
                {
                    Good(cargo.Good, sidePath + "/cargo_lost");
                    Require(cargo.Quantity > 0, sidePath + "/cargo_lost");
                }
            }
            foreach (var controller in combat.Controllers)
                Require(owners.Contains(OwnerKey(CombatQueries.Owner(world, controller.Controller))),
                    path + "/sides/owner");
        }
    }

    private static WorldLocation InstanceLocation(WorldState world, InstanceId id,
        Dictionary<TrainId, TrainState> trains, Dictionary<SettlementId, SettlementState> settlements)
    {
        var instance = world.Instances.Single(i => i.Id == id);
        return instance.Details switch
        {
            InstanceDetails.Vehicle { Train: { } train } => WorldState.Location(trains[train].Position),
            InstanceDetails.Vehicle { Train: null } => new WorldLocation.Node(settlements.Values
                .Single(s => s.ParkedVehicles.Contains(((InstanceId.Vehicle)id).Id)).Node),
            InstanceDetails.Building building => new WorldLocation.Node(settlements.Values
                .Single(s => s.Plots.Any(plot => plot.Id == building.Plot)).Node),
            _ => throw new CoreStateFormatException("/world/combats/instances")
        };
    }

    private static (int Kind, ulong Id) OwnerKey(CombatOwner owner) => owner switch
    {
        CombatOwner.Player => (0, 0),
        CombatOwner.Settlement settlement => (1, settlement.Id.Value),
        CombatOwner.Creatures => (2, 0),
        _ => throw new CoreStateFormatException("/world/combats/sides/owner")
    };

    private static bool NotAfter(SimulationPosition a, SimulationPosition b)
        => a.CompletedSteps < b.CompletedSteps || a.CompletedSteps == b.CompletedSteps && a.Sequence <= b.Sequence;

    private static void RunTotals(RunAccumulatorView totals, string field)
    {
        Require(totals is not null && totals.SalesReceipts.Value >= 0 && totals.PurchasePayments.Value >= 0
            && totals.ServicePayments.Value == 0 && totals.NetCashFlow == totals.SalesReceipts.Value - totals.PurchasePayments.Value
            && totals.TransportedUnits >= 0
            && (totals.LoadedCapacityRatio is not { } ratio || double.IsFinite(ratio) && ratio >= 0), field);
        Items(totals.IncompleteRules, field + "/incomplete_rules");
        (int Stop, int Rule) previous = (-1, -1);
        foreach (var result in totals.IncompleteRules)
        {
            Require(result.StopIndex >= 0 && result.RuleIndex >= 0 && (result.StopIndex, result.RuleIndex).CompareTo(previous) > 0
                && Enum.IsDefined(result.Action) && result.RequestedQuantity >= 0 && result.CompletedQuantity >= 0
                && result.CompletedQuantity <= result.RequestedQuantity && result.ActualAmount.Value >= 0, field + "/incomplete_rules");
            Require(result.Action is TradeAction.Buy or TradeAction.Sell || result.ActualAmount.Value == 0,
                field + "/incomplete_rules/actual_amount");
            Good(result.Good, field + "/incomplete_rules/good");
            Items(result.IncompleteReasons, field + "/incomplete_rules/incomplete_reasons");
            Require(!result.IncompleteReasons.IsEmpty, field + "/incomplete_rules/incomplete_reasons");
            foreach (var problem in result.IncompleteReasons)
            {
                Require(!string.IsNullOrEmpty(problem.Code) && problem.Field is not null, field + "/incomplete_rules/incomplete_reasons");
                Items(problem.RelatedIdentities, field + "/incomplete_rules/incomplete_reasons/related_identities");
            }
            previous = (result.StopIndex, result.RuleIndex);
        }
    }

    internal static void ValidateBoundary(WorldState world, SimulationPosition boundary)
    {
        var completedSteps = boundary.CompletedSteps;
        foreach (var job in world.FoundingJobs)
            Require(job.ElapsedTicks <= completedSteps, "/world/founding_jobs/elapsed_ticks");
        foreach (var report in world.RecentRunReports)
            Require(NotAfter(report.EndedAt, boundary), "/world/recent_run_reports/ended_at");
        foreach (var train in world.Trains)
            Require(train.CurrentRun is not { } run || NotAfter(run.StartedAt, boundary), "/world/trains/current_run/started_at");
        foreach (var state in world.ResourceRegenerations)
        {
            var interval = WorldResources.IntervalSteps(world.Source.Configuration.Resources.Single(e => e.Good == state.Good).RegenerationIntervalSeconds);
            Require(interval is not { } period
                ? state.Sequence == 0
                : state.Sequence <= completedSteps / period,
                "/world/resource_regenerations");
        }
        foreach (var train in world.Trains)
            Require(train.QueueEnteredStep is null || train.QueueEnteredStep <= completedSteps,
                "/world/trains/queue_entered_step");
        foreach (var combat in world.Combats)
        {
            Require(combat.StartedAtStep > 0 && combat.StartedAtStep <= completedSteps,
                "/world/combats/started_at_step");
            foreach (var controller in combat.Controllers)
                Require(controller.JoinedAtStep >= combat.StartedAtStep && controller.JoinedAtStep <= completedSteps,
                    "/world/combats/controllers/joined_at_step");
        }
        foreach (var settlement in world.Settlements)
        {
            Require(settlement.PopulationState.LastSettlement is not { } report || report.Position.CompletedSteps <= completedSteps,
                "/world/settlements/population_state/last_settlement/position");
            var previousSettlement = settlement.PopulationState.LastSettlement?.Position.CompletedSteps ?? 0;
            if (settlement.PopulationState.Period is not { } period) continue;
            const string field = "/world/settlements/population_state/period";
            Require(period.LastAdvancedStep >= previousSettlement && period.LastAdvancedStep <= completedSteps,
                field + "/last_advanced_step");
            var sinceAnchor = settlement.PopulationState.Available ? completedSteps - period.LastAdvancedStep : 0;
            Require(sinceAnchor < (ulong)(period.Rule.StatisticsPeriodTicks - period.ElapsedSteps), field + "/elapsed_steps");
            var elapsed = (ulong)period.ElapsedSteps + sinceAnchor;
            Require(elapsed > 0 && elapsed <= completedSteps - previousSettlement, field + "/elapsed_steps");
            if (elapsed < (ulong)period.Rule.ConsumptionIntervalTicks)
                Require(period.Required.All(r => r == 0) && period.Satisfied.All(s => s == 0), field);
        }
    }

    private static void ValidateResources(WorldState world, IReadOnlyDictionary<NodeId, NodeMapItem> nodes)
    {
        const string regeneration = "/world/resource_regenerations";
        Strings(world.ResourceRegenerations, r => r.Good.Value, regeneration);
        var entries = world.Source.Configuration.Resources.Where(e => !e.Renewable).ToArray();
        Require(world.ResourceRegenerations.Select(r => r.Good).SequenceEqual(entries.Select(e => e.Good)), regeneration);
        foreach (var state in world.ResourceRegenerations)
        {
            Require(state.InitialQuantity >= 0, regeneration + "/initial_quantity");
        }
        const string recovery = "/world/resource_recovery";
        Items(world.ResourceRecovery, recovery);
        var keys = world.ResourceRecovery.Select(r => (r.Node.Value, Good: WorldReferences.GoodKey(r.Good))).ToArray();
        Require(keys.SequenceEqual(keys.Distinct().OrderBy(k => k.Value).ThenBy(k => k.Good, StringComparer.Ordinal)), recovery);
        foreach (var state in world.ResourceRecovery)
        {
            Good(state.Good, recovery + "/good");
            Require(nodes.TryGetValue(state.Node, out var node) && node.Resources.Any(r => r.Good == state.Good
                && r.Renewal is ResourceRenewal.Renewable renewable && r.Quantity < renewable.Maximum), recovery);
            Require(double.IsFinite(state.Remaining) && state.Remaining is > 0 and < 1, recovery + "/remaining");
        }
    }

    private static void ValidatePopulation(SettlementState settlement, RuleDefinitionId selectedRule)
    {
        const string field = "/world/settlements/population_state";
        var state = settlement.PopulationState;
        Require(state is not null, field);
        Require(double.IsFinite(state.ChangeRemaining) && state.ChangeRemaining is > -1 and < 1
            && (settlement.Population > 1 || state.ChangeRemaining >= 0), field + "/change_remaining");
        Items(state.Shortages, field + "/shortages");
        Require(state.Shortages.Length == 6, field + "/shortages");
        Strings(state.ConsumptionRemaining, r => WorldReferences.GoodKey(r.Good), field + "/consumption_remaining");
        foreach (var remainder in state.ConsumptionRemaining)
        {
            Good(remainder.Good, field + "/consumption_remaining/good");
            Require(double.IsFinite(remainder.Units) && remainder.Units is > 0 and <= 1, field + "/consumption_remaining/units");
        }
        if (state.Period is { } period)
        {
            try { WorldInputs.ValidatePopulationRule(period.Rule); }
            catch (ArgumentException) { throw new CoreStateFormatException(field + "/period/rule"); }
            Require(period.Rule.Id == selectedRule, field + "/period/rule/id");
            Require(period.ElapsedSteps >= 0 && period.ElapsedSteps < period.Rule.StatisticsPeriodTicks,
                field + "/period/elapsed_steps");
            Items(period.Required, field + "/period/required"); Items(period.Satisfied, field + "/period/satisfied");
            Require(period.Required.Length == 6 && period.Satisfied.Length == 6, field + "/period");
            for (var i = 0; i < 6; i++)
            {
                Require(double.IsFinite(period.Required[i]) && period.Required[i] >= 0,
                    field + "/period/required");
                Require(double.IsFinite(period.Satisfied[i]) && period.Satisfied[i] >= 0 && period.Satisfied[i] <= period.Required[i],
                    field + "/period/satisfied");
            }
        }
        if (state.LastSettlement is { } report)
        {
            Require(report.Before >= 1 && report.After == settlement.Population && report.Change == report.After - report.Before,
                field + "/last_settlement");
            Require(report.Position.CompletedSteps > 0 && report.Position.Sequence == 0 && WorldFormat.IsHash(report.ExecutionRevision.Value),
                field + "/last_settlement/position");
            Require(double.IsFinite(report.BasicSatisfaction) && report.BasicSatisfaction is >= 0 and <= 1
                && double.IsFinite(report.ImprovementSatisfaction) && report.ImprovementSatisfaction is >= 0 and <= 1
                && Enum.IsDefined(report.Reason), field + "/last_settlement");
        }
    }

    private static void Terrain(TerrainView terrain, string field)
    {
        Require(terrain is not null && double.IsFinite(terrain.Relief) && terrain.Relief >= 0, field);
        Identity(terrain.TypeId.Value, field);
        Strings(terrain.FeatureIds, s => s, field);
        Require(terrain.FeatureIds.All(s => s is "lake" or "river"), field);
    }

    private static void Good(GoodId good, string field)
    {
        try { WorldInputs.ValidateGood(good); }
        catch (ArgumentException) { throw new CoreStateFormatException(field); }
    }

    private static void Identity(string value, string field)
    {
        Require(WorldFormat.IsIdentity(value), field);
    }

    private static void Items<T>(ImmutableArray<T> values, string field)
        => Require(!values.IsDefault && values.All(v => v is not null), field);

    private static void Ordered<T>(ImmutableArray<T> values, Func<T, ulong> key, string field)
    {
        Items(values, field);
        ulong previous = 0;
        foreach (var value in values) { var id = key(value); Require(id > previous, field); previous = id; }
    }

    private static void Strings<T>(ImmutableArray<T> values, Func<T, string> key, string field)
    {
        Items(values, field);
        string? previous = null;
        foreach (var value in values)
        {
            string id;
            try { id = key(value); }
            catch (ArgumentException) { throw new CoreStateFormatException(field); }
            Require(id is not null && (previous is null || StringComparer.Ordinal.Compare(previous, id) < 0), field);
            previous = id;
        }
    }

    private static void Next(ulong next, IEnumerable<ulong> ids, string field)
        => Require(next != 0 && next > ids.DefaultIfEmpty().Max(), field);

    private static void Require([DoesNotReturnIf(false)] bool condition, string field)
    {
        if (!condition) throw new CoreStateFormatException(field);
    }
}
