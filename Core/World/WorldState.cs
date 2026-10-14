using System.Collections.Immutable;
using Train.Core.Contracts;

namespace Train.Core.World;

internal abstract record GeneratedOccupant
{
    private GeneratedOccupant() { }
    internal sealed record Empty : GeneratedOccupant;
    internal sealed record Settlement(SettlementTypeProjection Type, SettlementTemplateProjection Template,
        SettlementRelation Relation, long Population, int Platforms) : GeneratedOccupant;
    internal sealed record Lair(LairTypeId Type) : GeneratedOccupant;
}

internal sealed record SettlementState(SettlementId Id, NodeId Node, SettlementTypeId Type,
    SettlementRelation Relation, long Population, SettlementTemplateProjection Template,
    ImmutableArray<PlotState> Plots, InventoryId Inventory, ImmutableArray<VehicleId> ParkedVehicles);
internal sealed record PlotState(PlotId Id, int TemplateNumber, PlotSize Size, BuildingId? Building);
internal sealed record LairState(LairId Id, NodeId Node, LairTypeId Type);
internal sealed record PartDurability(PartId Id, long Current);
internal sealed record InstanceState(InstanceId Id, DesignHash Design,
    SettlementRelation Ownership, InstanceDetails Details, long StructureDurability,
    ImmutableArray<PartDurability> Parts)
{
    internal InstanceView Read(WorldLocation location, DesignProjection design) => new(Id, design.Design, design.Composition, location, Ownership, Details,
        InstanceLock.None, new(Math.Min(StructureDurability, design.MaxStructureDurability), design.MaxStructureDurability),
        Parts.Select(p =>
        {
            var specification = design.Parts.Single(d => d.Id == p.Id);
            return new PartDamageView(p.Id, new(Math.Min(p.Current, specification.MaxDurability), specification.MaxDurability),
                specification.Missing, p.Current == 0);
        }).ToImmutableArray(), []);
}
internal sealed record TrainState(TrainId Id, TrainPosition Position, ImmutableArray<VehicleId> Vehicles,
    InventoryId Inventory, NodeTransitView? Transit, TrainOrderFacing OrderFacing,
    TrainActivity Activity, double SpeedMetersPerSecond, RunPlan? Plan, int? NextStopIndex,
    ulong RemainingDwellSteps, bool StopRequested, ulong? QueueEnteredStep, BreakId? ReturningFromBreak);
internal sealed record TrackHandoff(EdgeId Edge, int Track, TravelDirection Direction);
internal sealed record InventoryState(InventoryId Id, InventoryOwner Owner,
    ImmutableArray<GoodsQuantity> Goods, ImmutableArray<StorageReservationView> Reservations);
internal sealed record LeaseState(LeaseId Id, SettlementId Settlement, InventoryId Inventory,
    long Capacity, ulong ExpiresAtStep, Money Paid);
internal sealed record WorldIdentities(ulong Node, ulong Edge, ulong Settlement, ulong Lair, ulong Plot,
    ulong Building, ulong Train, ulong Vehicle, ulong Inventory, ulong Break, ulong Lease);

internal sealed record WorldState(WorldGenerationInput Source, ImmutableArray<NodeMapItem> Nodes, ImmutableArray<EdgeMapItem> Edges,
    ImmutableArray<SettlementState> Settlements, ImmutableArray<LairState> Lairs,
    ImmutableArray<InstanceState> Instances, ImmutableArray<TrainState> Trains,
    ImmutableArray<InventoryState> Inventories, ImmutableArray<DesignProjection> Designs,
    ImmutableArray<GoodDefinitionProjection> DesignGoods, Money Money, WorldIdentities Next,
    ImmutableArray<RequiredDefinition> DefinitionDependencies, ImmutableArray<TrackHandoff> TrackHandoffs,
    ImmutableArray<LeaseState> Leases)
{
    internal WorldSummary Map => new(Source.WorldSeed, Source.Configuration.Rules.Railway,
        Source.Configuration.WidthMeters, Source.Configuration.HeightMeters, Nodes.Select(ReadNode).ToImmutableArray(), Edges,
        Trains.Select(t => new TrainMapItem(t.Id, t.Position, t.Activity)).ToImmutableArray(), [], []);

    internal static int CountPlatforms(SettlementState settlement, IReadOnlyDictionary<InstanceId, InstanceState> instances,
        IReadOnlyDictionary<DesignHash, DesignProjection> designs)
    {
        var platforms = 0;
        foreach (var plot in settlement.Plots)
        {
            if (plot.Building is not { } building) continue;
            var instance = instances[new InstanceId.Building(building)];
            var design = designs[instance.Design];
            if (design.Kind is not DesignKind.Building { Type: BuildingKind.Station }
                || Math.Min(instance.StructureDurability, design.MaxStructureDurability) == 0) continue;
            platforms += design.Parts.Count(part => part.Capabilities.Any(c => c is PartCapability.Platform)
                && Math.Min(instance.Parts.Single(p => p.Id == part.Id).Current, part.MaxDurability) > 0);
        }
        return platforms;
    }

    internal static WorldState Create(WorldGenerationInput input, CoreContentProjection content,
        ImmutableArray<CandidateNode> nodes, ImmutableArray<CandidateEdge> edges,
        ImmutableArray<GeneratedOccupant> occupants, ImmutableArray<ImmutableArray<NodeResourceView>> resources,
        ImmutableArray<int> tracks, int start)
    {
        ulong settlementNumber = 1, lairNumber = 1, plotNumber = 1, buildingNumber = 1,
            trainNumber = 1, vehicleNumber = 1, inventoryNumber = 1;
        var settlements = ImmutableArray.CreateBuilder<SettlementState>();
        var lairs = ImmutableArray.CreateBuilder<LairState>();
        var instances = ImmutableArray.CreateBuilder<InstanceState>();
        var inventories = ImmutableArray.CreateBuilder<InventoryState>();
        var mapNodes = ImmutableArray.CreateBuilder<NodeMapItem>();
        var designHashes = new HashSet<DesignHash> { input.Configuration.StartConstraints.RequiredAffordableCommissionDesign };
        designHashes.UnionWith(input.Configuration.NpcInitialization.ForSaleVehicleCandidates);
        for (var index = 0; index < nodes.Length; index++)
        {
            var id = new NodeId((ulong)index + 1);
            NodeOccupant occupant = new NodeOccupant.Empty();
            var platforms = 0;
            if (occupants[index] is GeneratedOccupant.Settlement settlement)
            {
                var settlementId = new SettlementId(settlementNumber++);
                occupant = new NodeOccupant.Settlement(settlementId, settlement.Relation);
                platforms = settlement.Platforms;
                var plots = ImmutableArray.CreateBuilder<PlotState>();
                foreach (var plot in settlement.Template.Plots)
                {
                    var plotId = new PlotId(plotNumber++);
                    BuildingId? building = null;
                    if (plot.PrebuiltDesign is { } hash)
                    {
                        building = new(buildingNumber++);
                        instances.Add(CreateInstance(new InstanceId.Building(building.Value), hash,
                            settlement.Relation, new InstanceDetails.Building(plotId, plot.Size)));
                    }
                    plots.Add(new(plotId, plot.Number, plot.Size, building));
                }
                foreach (var founding in settlement.Template.FoundingBuildings) designHashes.Add(founding.Design);
                var inventory = new InventoryId(inventoryNumber++);
                inventories.Add(new(inventory, new InventoryOwner.Settlement(settlementId), settlement.Template.SpecialInitialInventory, []));
                settlements.Add(new(settlementId, id, settlement.Type.Id, settlement.Relation, settlement.Population,
                    settlement.Template, plots.ToImmutable(), inventory, []));
            }
            else if (occupants[index] is GeneratedOccupant.Lair lair)
            {
                var lairId = new LairId(lairNumber++);
                occupant = new NodeOccupant.Lair(lairId);
                lairs.Add(new(lairId, id, lair.Type));
            }
            mapNodes.Add(new(id, nodes[index].Position, nodes[index].Terrain, occupant, resources[index], 0, platforms, null));
        }
        var mapEdges = edges.Select((edge, index) => new EdgeMapItem(new((ulong)index + 1),
            new((ulong)edge.A + 1), new((ulong)edge.B + 1), edge.LengthMeters, edge.Terrain, tracks[index], edge.TrackLimit, [], [])).ToImmutableArray();
        var trains = ImmutableArray.CreateBuilder<TrainState>();
        var startNode = new NodeId((ulong)start + 1);
        foreach (var initial in input.Start.Trains)
        {
            var trainId = new TrainId(trainNumber++);
            var vehicles = ImmutableArray.CreateBuilder<VehicleId>();
            foreach (var hash in initial.Vehicles)
            {
                var vehicleId = new VehicleId(vehicleNumber++);
                var design = content.Designs.Single(d => d.Design == hash);
                vehicles.Add(vehicleId);
                instances.Add(CreateInstance(new InstanceId.Vehicle(vehicleId), hash, SettlementRelation.PlayerOwned,
                    new InstanceDetails.Vehicle(((DesignKind.Vehicle)design.Kind).Size, trainId)));
            }
            var inventory = new InventoryId(inventoryNumber++);
            inventories.Add(new(inventory, new InventoryOwner.Train(trainId), initial.Inventory, []));
            trains.Add(new(trainId, new TrainPosition.Node(startNode), vehicles.ToImmutable(), inventory,
                new(null, null), TrainOrderFacing.FirstEndLeading, TrainActivity.Stopped, 0, null, null, 0, false, null, null));
        }
        var designs = content.Designs.Where(d => designHashes.Contains(d.Design)).ToImmutableArray();
        var goods = new HashSet<GoodId>(inventories.SelectMany(i => i.Goods).Select(g => g.Good));
        goods.UnionWith(designs.SelectMany(WorldReferences.DesignGoods).Select(g => g.Good));
        var state = new WorldState(input, mapNodes.ToImmutable(), mapEdges, settlements.ToImmutable(), lairs.ToImmutable(), instances.ToImmutable(), trains.ToImmutable(),
            inventories.ToImmutable(), designs, content.DesignGoods.Where(g => goods.Contains(g.Good)).ToImmutableArray(),
            input.Start.InitialMoney, new((ulong)nodes.Length + 1, (ulong)edges.Length + 1, settlementNumber, lairNumber,
                plotNumber, buildingNumber, trainNumber, vehicleNumber, inventoryNumber, 1, 1), [], [], []);
        return state with { DefinitionDependencies = WorldReferences.Dependencies(state, content) };

        InstanceState CreateInstance(InstanceId id, DesignHash hash,
            SettlementRelation ownership, InstanceDetails details)
        {
            designHashes.Add(hash);
            var design = content.Designs.Single(d => d.Design == hash);
            return new(id, hash, ownership, details, design.MaxStructureDurability,
                design.Parts.Select(p => new PartDurability(p.Id, p.MaxDurability)).ToImmutableArray());
        }
    }

    internal NodeView? FindNode(NodeId id)
    {
        var node = Nodes.FirstOrDefault(n => n.Id == id);
        return node is null ? null : new(ReadNode(node), Edges.Where(e => e.Start == id || e.End == id).Select(e => e.Id).ToImmutableArray());
    }

    internal int OccupiedPlatforms(NodeId node) => Trains.Count(t => t.Activity == TrainActivity.Servicing
        && t.Position is TrainPosition.Node position && position.Id == node
        && Nodes.Single(n => n.Id == node).Occupant is NodeOccupant.Settlement { Relation: not SettlementRelation.Hostile });

    private NodeMapItem ReadNode(NodeMapItem node) => node with { OccupiedPlatforms = OccupiedPlatforms(node.Id) };

    internal EdgeView? FindEdge(EdgeId id)
    {
        var edge = Edges.FirstOrDefault(e => e.Id == id);
        return edge is null ? null : new(edge,
            Enumerable.Range(1, edge.Tracks).Select(n => new TrackView(n,
                Trains.FirstOrDefault(t => t.Position is TrainPosition.Edge p && p.Id == id && p.Track == n)?.Position is TrainPosition.Edge locked ? locked.Direction : null,
                Trains.Where(t => t.Position is TrainPosition.Edge p && p.Id == id && p.Track == n)
                    .Select(t => t.Id).ToImmutableArray())).ToImmutableArray(), null);
    }

    internal LocationView? FindLocation(WorldLocation location)
    {
        var occupant = location is WorldLocation.Node node ? FindNode(node.Id)?.Map.Occupant : null;
        if (location is WorldLocation.Node && occupant is null) return null;
        if (location is WorldLocation.Edge edge)
        {
            if (!double.IsFinite(edge.DistanceMeters) || edge.DistanceMeters < 0)
                throw new ArgumentException("地点距离必须是有限非负数。", nameof(location));
            var found = FindEdge(edge.Id);
            if (found is null) return null;
        }
        return new(location,
            Trains.Where(t => SameLocation(location, Location(t.Position))).Select(t => t.Id).ToImmutableArray(),
            Instances.Where(i => SameLocation(Location(i), location)).Select(i => i.Id).ToImmutableArray(), [], [], occupant);
    }

    private static bool SameLocation(WorldLocation left, WorldLocation right) => (left, right) switch
    {
        (WorldLocation.Node a, WorldLocation.Node b) => a.Id == b.Id,
        (WorldLocation.Edge a, WorldLocation.Edge b) => a.Id == b.Id,
        _ => false
    };

    internal InstanceView? FindInstance(InstanceId id)
    {
        var instance = Instances.FirstOrDefault(i => i.Id == id);
        return instance?.Read(Location(instance), Designs.Single(d => d.Design == instance.Design));
    }

    private WorldLocation Location(InstanceState instance) => instance.Details switch
    {
        InstanceDetails.Vehicle { Train: { } train } => Location(Trains.Single(t => t.Id == train).Position),
        InstanceDetails.Vehicle { Train: null } => new WorldLocation.Node(Settlements.Single(s => s.ParkedVehicles.Contains(((InstanceId.Vehicle)instance.Id).Id)).Node),
        InstanceDetails.Building building => new WorldLocation.Node(Settlements.Single(s => s.Plots.Any(p => p.Id == building.Plot)).Node),
        _ => throw new InvalidOperationException("实例缺少所属列车或聚居地。")
    };

    internal static WorldLocation Location(TrainPosition position) => position switch
    {
        TrainPosition.Node node => new WorldLocation.Node(node.Id),
        TrainPosition.Edge edge => new WorldLocation.Edge(edge.Id, edge.DistanceMeters),
        _ => throw new InvalidOperationException("列车位置无效。")
    };
}
