using System.Collections.Immutable;
using Chickensoft.GoDotTest;
using Godot;
using Train.App;
using Train.App.ArtCapture;
using Train.App.Logging;
using Train.App.Saves;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using Train.Scenes.Contracts;
using Train.Scenes.ContractSupport;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class ArtCaptureTests
{
    private static FixedCoreReadView CreatureView(bool empty = false, SimulationPosition position = default, bool buildingPresent = true, ulong buildingNode = 1, bool settlementBuilding = false)
    {
        var terrain = new TerrainView(new("fixture:terrain"), 0, []);
        return new(position, HostCoreScenario.Input)
        {
            Economy = UiWorldFixture.Economy,
            Nodes = new Dictionary<NodeId, Lookup<NodeView>>
            {
                [new(1)] = new Lookup<NodeView>.Found(new(new(new(1), new(0, 0), terrain,
                    settlementBuilding ? new NodeOccupant.Settlement(new(1), SettlementRelation.NonHostile) : new NodeOccupant.Lair(new(1)), [], 0, 0, null), [], 0, 0, 0))
            },
            Edges = new Dictionary<EdgeId, Lookup<EdgeView>>
            {
                [new(1)] = new Lookup<EdgeView>.Found(new(new(new(1), new(1), new(2), 100,
                    terrain, 1, 1, [], []), [], null))
            },
            Instances = new Dictionary<InstanceId, Lookup<InstanceView>>
            {
                [new InstanceId.Vehicle(new(1))] = new Lookup<InstanceView>.Found(new(new InstanceId.Vehicle(new(1)),
                    new("fixture:vehicle"), new("fixture"), new WorldLocation.Edge(new(1), 10), SettlementRelation.PlayerOwned,
                    new InstanceDetails.Vehicle(SizeClass.Small, null), InstanceLock.None, new(100, 100), [], [])),
                [new InstanceId.Building(new(1))] = buildingPresent
                    ? new Lookup<InstanceView>.Found(new(new InstanceId.Building(new(1)), new("fixture:building"), new("fixture"),
                        new WorldLocation.Node(new(buildingNode)), SettlementRelation.NonHostile,
                        new InstanceDetails.Building(new(1), PlotSize.SmallStation), InstanceLock.None, new(100, 100), [], []))
                    : new Lookup<InstanceView>.Missing()
            },
            Settlements = settlementBuilding ? new Dictionary<SettlementId, Lookup<SettlementView>>
            {
                [new(1)] = new Lookup<SettlementView>.Found(new(new(1), new(1), new("fixture:town"), null,
                    SettlementRelation.NonHostile, new(1),
                    [new(new(1), 7, PlotSize.SmallStation, [BuildingKind.Station], new PlotOccupant.Building(new(1)))],
                    [], 0, 0, 0, [], [], [], new(0, 0, 0, 0),
                    new(new(new("fixture:population"), null, []), 0, 0, 0, 0, new PopulationExecution.NotStarted([]), null, []),
                    new(TargetPriority.ArmedFirst, TargetScope.All), 1, false, [], [], [],
                    new(new(new("fixture:market"), null, []), new(0, 0, 0, 0, 0, 0, 0), []), []))
            } : new Dictionary<SettlementId, Lookup<SettlementView>>(),
            Lairs = new Dictionary<LairId, Lookup<LairView>>
            {
                [new(1)] = new Lookup<LairView>.Found(new(new(1), new(1), new("fixture:lair"), [new(1)]))
            },
            CreatureGroups = new Dictionary<CreatureGroupId, Lookup<CreatureGroupView>>
            {
                [new(1)] = new Lookup<CreatureGroupView>.Found(new(new(1), new(1),
                    new WorldLocation.Node(new(1)), null, CreatureActivity.Guarding, 0,
                    empty ? [] : [new(new(1), new("fixture:creature"), 1, 1)]))
            }
        };
    }

}
