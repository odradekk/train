using System.Collections.Immutable;
using Train.Core.Contracts;
using Train.Core.ContractSupport;

namespace Train.Engine.Tests;

internal static class SettlementFoundingUiFixture
{
    internal static FixedCoreReadView World(ulong revision, JobId? foundingJob, NodeOccupant? firstOccupant = null)
    {
        var terrain = new TerrainView(new("fixture:plain"), 0, []);
        NodeMapItem Node(ulong id, double x, double y, NodeOccupant occupant) =>
            new(new(id), new(x, y), terrain, occupant, [], 0, 1, id == 1 ? foundingJob : null);
        var nodes = new[]
        {
            Node(1, 500, 900, firstOccupant ?? new NodeOccupant.Empty()),
            Node(2, 900, 900, new NodeOccupant.Empty()),
            Node(3, 1300, 900, new NodeOccupant.Empty()),
            Node(4, 500, 1400, new NodeOccupant.Lair(new(1))),
            Node(5, 900, 1400, new NodeOccupant.Settlement(new(1), SettlementRelation.NonHostile)),
            Node(6, 1700, 900, new NodeOccupant.Empty())
        };
        TrainView Train(ulong id, ulong node) => new(new(id),
            new TrainPosition.Node(new(node), null, null, null, null, NodeTravelPhase.Waiting, 80, null),
            TrainActivity.Stopped, [new(id)], new(id), 0, 20, 10, 100, 0, 30, null, null, 0,
            new(TargetPriority.ArmedFirst, TargetScope.All, EncounterAction.Engage, .2), [], null, [],
            TrainOrderFacing.FirstEndLeading, null);
        var trains = new[] { Train(7, 1), Train(8, 2), Train(9, 3) };
        CoreResult<SettlementFoundingPreview> Success(ulong node, ImmutableArray<CoreProblem> blockers) =>
            new CoreResult<SettlementFoundingPreview>.Success(new(new(node), 40, [new(EconomyFixture.Food, 1.5)],
                [new(EconomyFixture.Food, 12)], 180, blockers));
        var economy = EconomyFixture.Create(revision);
        var town = ((Lookup<SettlementView>.Found)economy.FindSettlement(new(1))).Value;
        var settlements = nodes.Where(node => node.Occupant is NodeOccupant.Settlement).Select(node =>
        {
            var occupied = (NodeOccupant.Settlement)node.Occupant;
            return town with { Id = occupied.Id, Node = node.Id, Relation = occupied.Relation,
                Inventory = new(100 + occupied.Id.Value), ParkedVehicles = [], VehiclesForSale = [], Leases = [] };
        }).ToArray();
        return new(new(0, revision), HostCoreScenario.Input, revision: new(revision))
        {
            Economy = UiWorldFixture.Economy,
            Settlements = settlements.ToDictionary(item => item.Id, item => (Lookup<SettlementView>)new Lookup<SettlementView>.Found(item)),
            Inventories = settlements.ToDictionary(item => item.Inventory, item => (Lookup<InventoryView>)new Lookup<InventoryView>.Found(
                new(item.Inventory, new InventoryOwner.Settlement(item.Id), new WorldLocation.Node(item.Node),
                    InventoryAccess.ReadWrite, 100, 0, 0, 100, 0, [], []))),
            Instances = trains.SelectMany(train => train.Vehicles).ToDictionary(id => (InstanceId)new InstanceId.Vehicle(id),
                _ => (Lookup<InstanceView>)new Lookup<InstanceView>.Missing()),
            World = new(1, new("fixture:railway"), 4000, 2400, nodes.ToImmutableArray(), [],
                trains.Select(train => new TrainMapItem(train.Id, train.Position, train.Activity)).ToImmutableArray(), [], []),
            Nodes = nodes.ToDictionary(node => node.Id, node => (Lookup<NodeView>)new Lookup<NodeView>.Found(new(node, [], 80, 0, 80))),
            Locations = nodes.ToDictionary(node => (WorldLocation)new WorldLocation.Node(node.Id), node =>
                (Lookup<LocationView>)new Lookup<LocationView>.Found(new(new WorldLocation.Node(node.Id), [], [], [], [], node.Occupant))),
            Trains = trains.ToDictionary(train => train.Id, train => (Lookup<TrainView>)new Lookup<TrainView>.Found(train)),
            FoundingPreviews = new Dictionary<NodeId, CoreResult<SettlementFoundingPreview>>
            {
                [new(1)] = Success(1, []),
                [new(2)] = new CoreResult<SettlementFoundingPreview>.Failure([new("content_missing", "Population.Rule", null, [])]),
                [new(3)] = Success(3, [new("node_occupied", "Node", new WorldObjectId.Node(new(3)), [])]),
                [new(6)] = Success(6, [])
            },
            Jobs = foundingJob is { } job ? new Dictionary<JobId, Lookup<JobView>>
            {
                [job] = new Lookup<JobView>.Found(new JobView.Founding(new(job, new(1), 180, 0, new(91),
                    new("fixture:template"), [new(EconomyFixture.Food, 12)])))
            } : new Dictionary<JobId, Lookup<JobView>>()
        };
    }
}
