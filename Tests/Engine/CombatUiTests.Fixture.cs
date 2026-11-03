using System.Collections.Immutable;
using Chickensoft.GoDotTest;
using Chickensoft.GodotTestDriver;
using Godot;
using Train.App;
using Train.App.Logging;
using Train.App.Notifications;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using Train.Scenes.Contracts;
using Train.Scenes.ContractSupport;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class CombatUiTests
{
    private static void Queue(ScriptedCoreSession session, ulong step, CoreEvent? item)
    {
        ImmutableArray<EventEnvelope> events = item is null ? [] : [new(new(step, 0), 0, item)];
        session.QueueAdvance(new(1, new(step, 0), events, null, null), new FixedCoreReadView(new(step, 0), HostCoreScenario.Input, null, new(1))
        {
            Economy = UiWorldFixture.Economy,
            World = new(0, new("fixture:railway"), 100, 100, [], [], [], [], [])
        });
    }

    private sealed partial class CombatHost
    {
        private static FixedCoreReadView Battlefield(SimulationPosition position, bool combatPresent, bool settlementPresent)
        {
            var terrain = new TerrainView(new("fixture:plain"), 0, []);
            NodeOccupant occupant = settlementPresent
                ? new NodeOccupant.Settlement(new(4), SettlementRelation.Hostile) : new NodeOccupant.Empty();
            var settlementNode = new NodeMapItem(new(1), new(250, 300), terrain, occupant, [], 0, 2, null);
            var far = settlementNode with { Id = new(2), Position = new(750, 300), Occupant = new NodeOccupant.Empty(), Platforms = 0 };
            var edge = new EdgeMapItem(new(1), new(1), new(2), 600, terrain, 1, 1, [], []);
            var trainPosition = new TrainPosition.Node(new(1), null, null, null, null, NodeTravelPhase.Waiting, 80, null);
            var train = new TrainView(new(7), trainPosition, TrainActivity.Stopped, [new(11)], new(1),
                0, 20, 10, 100, 0, 30, null, null, 0,
                new(TargetPriority.ArmedFirst, TargetScope.All, EncounterAction.Engage, 0.5),
                [new("no_route", "route", new WorldObjectId.Train(new(7)), [])], null, [],
                TrainOrderFacing.FirstEndLeading, null);
            var nodeLocation = new WorldLocation.Node(new(1));
            var vehicle = new InstanceView(new InstanceId.Vehicle(new(11)), new("wagon"), new("fixture"),
                nodeLocation, SettlementRelation.PlayerOwned, new InstanceDetails.Vehicle(SizeClass.Medium, new(7)),
                InstanceLock.None, new(100, 100), [], []);
            var building = new InstanceView(new InstanceId.Building(new(13)), new("station"), new("fixture"),
                nodeLocation, SettlementRelation.Hostile, new InstanceDetails.Building(new(1), PlotSize.SmallStation),
                InstanceLock.None, new(100, 100), [], []);
            var edgeLocation = new WorldLocation.Edge(new(1), 100);
            var combat = new CombatView(new(5), edgeLocation, 3,
            [
                new(new CombatUnitId.Instance(vehicle.Id), false, 0, null),
                new(new CombatUnitId.Instance(building.Id), false, 0, null)
            ]);
            var listed = combatPresent
                ? ImmutableArray.Create(new CombatMapItem(combat.Id, edgeLocation))
                : ImmutableArray.Create(new CombatMapItem(new(9), nodeLocation));
            var settlement = new SettlementView(new(4), new(1), new("preview:town"), new("preview:town"),
                SettlementRelation.Hostile, new(2),
                [new(new(1), 1, PlotSize.SmallStation, [BuildingKind.Station], new PlotOccupant.Building(new(13)))],
                [], 0, 2, 0, [], [], [], new(0, 0, 0, 0),
                new(new(new("preview:population"), null, []), 0, 0, 0, 0, new PopulationExecution.NotStarted([]), null, []),
                new(TargetPriority.LowestDurability, TargetScope.ArmedOnly), 0.4, true, [], [], [],
                new(new(new("preview:market"), null, []), new(0, 0, 0, 0, 0, 0, 0), []), []);
            return new(position, HostCoreScenario.Input, null, new(1))
            {
                Economy = UiWorldFixture.Economy,
                World = new(1, new("fixture:railway"), 1000, 600, [settlementNode, far], [edge],
                    [new(train.Id, trainPosition, train.Activity)], [], listed),
                Nodes = new Dictionary<NodeId, Lookup<NodeView>>
                {
                    [settlementNode.Id] = new Lookup<NodeView>.Found(new(settlementNode, [edge.Id], 80, 0, 80)),
                    [far.Id] = new Lookup<NodeView>.Found(new(far, [edge.Id], 80, 0, 80))
                },
                Edges = new Dictionary<EdgeId, Lookup<EdgeView>>
                {
                    [edge.Id] = new Lookup<EdgeView>.Found(new(edge, [], null))
                },
                Locations = new Dictionary<WorldLocation, Lookup<LocationView>>
                {
                    [nodeLocation] = new Lookup<LocationView>.Found(new(nodeLocation, [train.Id],
                        [vehicle.Id, building.Id], [], [], occupant)),
                    [new WorldLocation.Node(far.Id)] = new Lookup<LocationView>.Found(
                        new(new WorldLocation.Node(far.Id), [], [], [], [], far.Occupant))
                },
                Trains = new Dictionary<TrainId, Lookup<TrainView>> { [train.Id] = new Lookup<TrainView>.Found(train) },
                Instances = new Dictionary<InstanceId, Lookup<InstanceView>>
                {
                    [vehicle.Id] = new Lookup<InstanceView>.Found(vehicle),
                    [SecondVehicle] = new Lookup<InstanceView>.Found(vehicle with { Id = SecondVehicle }),
                    [building.Id] = new Lookup<InstanceView>.Found(building)
                },
                Inventories = new Dictionary<InventoryId, Lookup<InventoryView>>
                { [settlement.Inventory] = new Lookup<InventoryView>.Found(new(settlement.Inventory,
                    new InventoryOwner.Settlement(settlement.Id), nodeLocation, InventoryAccess.ReadWrite, 100, 0, 0, 100, 0, [], [])) },
                Settlements = new Dictionary<SettlementId, Lookup<SettlementView>>
                {
                    [settlement.Id] = settlementPresent
                        ? new Lookup<SettlementView>.Found(settlement) : new Lookup<SettlementView>.Missing()
                },
                Combats = new Dictionary<CombatId, Lookup<CombatView>>
                {
                    [combat.Id] = combatPresent
                        ? new Lookup<CombatView>.Found(combat) : new Lookup<CombatView>.Missing(),
                    [new(9)] = new Lookup<CombatView>.Found(new(new(9), nodeLocation, 4, []))
                }
            };
        }
    }
}
