using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Train.Core.Contracts;

namespace Train.Core.World;

internal static class WorldStateValidation
{
    internal static void Validate(WorldState world)
    {
        Ordered(world.Nodes, n => n.Id.Value, "/world/nodes");
        Ordered(world.Edges, e => e.Id.Value, "/world/edges");
        Ordered(world.Settlements, s => s.Id.Value, "/world/settlements");
        Ordered(world.Lairs, l => l.Id.Value, "/world/lairs");
        Ordered(world.Trains, t => t.Id.Value, "/world/trains");
        Ordered(world.Inventories, i => i.Id.Value, "/world/inventories");
        Strings(world.Designs, d => d.Design.Value, "/world/designs");
        Strings(world.DesignGoods, g => WorldReferences.GoodKey(g.Good), "/world/design_goods");
        Items(world.Instances, "/world/instances");
        Items(world.DefinitionDependencies, "/world/definition_dependencies");
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
            Require(node.Platforms >= 0 && node.OccupiedPlatforms == 0 && node.FoundingJob is null, "/world/nodes/platforms");
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
                    && parked.Ownership == SettlementRelation.PlayerOwned, "/world/settlements/parked_vehicles");
            Require(nodes.TryGetValue(settlement.Node, out var node)
                && node.Occupant == new NodeOccupant.Settlement(settlement.Id, settlement.Relation), "/world/settlements/node");
            Identity(settlement.Type.Value, "/world/settlements/type");
            Require(Enum.IsDefined(settlement.Relation) && settlement.Population > 0
                && settlement.Template.Type == settlement.Type, "/world/settlements");
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
            ValidateTrain(train, nodes, edges);
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
        Items(world.TrackHandoffs, "/world/track_handoffs");
        (ulong Edge, int Track) previousHandoff = (0, 0);
        foreach (var handoff in world.TrackHandoffs)
        {
            var key = (handoff.Edge.Value, handoff.Track);
            Require(key.CompareTo(previousHandoff) > 0, "/world/track_handoffs");
            previousHandoff = key;
            Require(edges.TryGetValue(handoff.Edge, out var edge), "/world/track_handoffs/edge");
            Require(handoff.Track >= 1 && handoff.Track <= edge.Tracks, "/world/track_handoffs/track");
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
        foreach (var inventory in world.Inventories)
        {
            Require(inventory.Owner switch
            {
                InventoryOwner.Settlement s => settlements.TryGetValue(s.Id, out var owner) && owner.Inventory == inventory.Id,
                InventoryOwner.Train t => trains.TryGetValue(t.Id, out var owner) && owner.Inventory == inventory.Id,
                _ => false
            }, "/world/inventories/owner");
            Strings(inventory.Goods, g => WorldReferences.GoodKey(g.Good), "/world/inventories/goods");
            foreach (var good in inventory.Goods) { Good(good.Good, "/world/inventories/goods"); Require(good.Quantity > 0, "/world/inventories/goods"); }
        }
        Require(world.Money.Value >= 0 && world.Next is not null, "/world/money");
        var next = world.Next;
        Next(next.Node, world.Nodes.Select(n => n.Id.Value), "/world/next/node");
        Next(next.Edge, world.Edges.Select(e => e.Id.Value), "/world/next/edge");
        Next(next.Settlement, world.Settlements.Select(s => s.Id.Value), "/world/next/settlement");
        Next(next.Lair, world.Lairs.Select(l => l.Id.Value), "/world/next/lair");
        Next(next.Plot, plots.Keys.Select(p => p.Value), "/world/next/plot");
        Next(next.Building, world.Instances.Select(i => i.Id).OfType<InstanceId.Building>().Select(i => i.Id.Value), "/world/next/building");
        Next(next.Vehicle, world.Instances.Select(i => i.Id).OfType<InstanceId.Vehicle>().Select(i => i.Id.Value), "/world/next/vehicle");
        Next(next.Train, world.Trains.Select(t => t.Id.Value), "/world/next/train");
        Next(next.Inventory, world.Inventories.Select(i => i.Id.Value), "/world/next/inventory");
        Next(next.Break, breakIds, "/world/next/break");
    }

    private static void ValidateTrain(TrainState train, Dictionary<NodeId, NodeMapItem> nodes,
        Dictionary<EdgeId, EdgeMapItem> edges)
    {
        Require(Enum.IsDefined(train.OrderFacing) && double.IsFinite(train.SpeedMetersPerSecond)
            && train.SpeedMetersPerSecond >= 0, "/world/trains");
        Require(train.QueueEnteredStep is null || train.Position is TrainPosition.Node
            && train.Activity == TrainActivity.Waiting, "/world/trains/queue_entered_step");
        Require(train.ReturningFromBreak is null || train.Position is TrainPosition.Edge returning
            && train.Activity == TrainActivity.Running && edges.TryGetValue(returning.Id, out var returningEdge)
            && returningEdge.Breaks.Any(b => b.Id == train.ReturningFromBreak && b.Track == returning.Track),
            "/world/trains/returning_from_break");
        switch (train.Position)
        {
            case TrainPosition.Node node:
                Require(nodes.ContainsKey(node.Id), "/world/trains/position");
                Require(train.Transit is not null, "/world/trains/transit");
                foreach (var edgeId in new[] { train.Transit.EnteredFrom, train.Transit.NextEdge })
                    if (edgeId is { } id) Require(edges.TryGetValue(id, out var adjacentEdge)
                        && (adjacentEdge.Start == node.Id || adjacentEdge.End == node.Id), "/world/trains/transit");
                Require(train.Transit.NextEdge is null || train.Activity == TrainActivity.Running, "/world/trains/transit");
                break;
            case TrainPosition.Edge position:
                Require(edges.TryGetValue(position.Id, out var edge)
                    && position.Track >= 1 && position.Track <= edge.Tracks
                    && Enum.IsDefined(position.Direction) && double.IsFinite(position.DistanceMeters)
                    && position.DistanceMeters >= 0 && position.DistanceMeters <= edge.LengthMeters, "/world/trains/position");
                Require(train.Transit is null, "/world/trains/transit");
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
                    Require(station.GoodsRules.IsEmpty, $"/world/trains/plan/stops/{index}/goods_rules");
                }
            }
            Require(!plan.Loop || targets.Count >= 2, "/world/trains/plan");
            Require(train.NextStopIndex is null || train.NextStopIndex >= 0 && train.NextStopIndex < plan.Stops.Length,
                "/world/trains/next_stop_index");
            Require(!plan.Loop || train.NextStopIndex is not null, "/world/trains/next_stop_index");
        }
        else Require(train.NextStopIndex is null, "/world/trains/next_stop_index");
        var unfinished = train.Plan is not null && train.NextStopIndex is not null;
        Require(unfinished || train.Activity == TrainActivity.Stopped && train.RemainingDwellSteps == 0,
            "/world/trains/activity");
        Require(!train.StopRequested || train.Position is TrainPosition.Edge && train.Activity == TrainActivity.Running,
            "/world/trains/stop_requested");
        if (train.RemainingDwellSteps > 0)
            Require(unfinished && train.Position is TrainPosition.Node dwellNode
                && train.Plan!.Stops[train.NextStopIndex!.Value] is PlanStop.Station dwellStation
                && dwellStation.Node == dwellNode.Id && train.RemainingDwellSteps <= dwellStation.DwellSteps,
                "/world/trains/remaining_dwell_steps");
        switch (train.Activity)
        {
            case TrainActivity.Stopped:
                Require(train.Position is TrainPosition.Node && train.SpeedMetersPerSecond == 0
                    && train.Transit!.NextEdge is null, "/world/trains/activity");
                break;
            case TrainActivity.Running:
                Require(unfinished && train.RemainingDwellSteps == 0, "/world/trains/activity");
                break;
            case TrainActivity.Servicing:
                Require(unfinished && train.Position is TrainPosition.Node && train.SpeedMetersPerSecond == 0
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
            default: throw new CoreStateFormatException("/world/trains/activity");
        }
    }

    internal static void ValidateQueueSteps(WorldState world, ulong completedSteps)
    {
        foreach (var train in world.Trains)
            Require(train.QueueEnteredStep is null || train.QueueEnteredStep <= completedSteps,
                "/world/trains/queue_entered_step");
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
