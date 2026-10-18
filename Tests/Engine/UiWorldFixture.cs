using System.Collections.Immutable;
using Train.Core.Contracts;
using Train.Core.ContractSupport;

namespace Train.Engine.Tests;

internal static class UiWorldFixture
{
    private enum Scenario { Map, Save, Scene, Formation }

    internal static ScriptedCoreSession Session(ICoreReadView view)
    {
        var capture = new FixedCoreCapture(new(view.Position, "UI recording start", "ui-start"),
            HostCoreScenario.References, "ui-start", view.InputIdentity, view.Revision);
        return new(view)
        {
            ReadRecordingCall = () => new(view.Position, [new(new(1), capture)], [],
                [capture.Checkpoint], [], [], capture.References),
            ConfirmRecordingStartCall = (point, _) => point.Value == 1 ? new([], [])
                : throw new InvalidOperationException("UNCONFIGURED: UI recording point")
        };
    }

    internal static FixedCoreReadView Map(SimulationPosition tick = default)
        => Create(Scenario.Map, tick, HostCoreScenario.Input);

    internal static FixedCoreReadView InformationLayers(bool changed = false)
    {
        var original = Map();
        var first = original.World.Nodes[0] with
        {
            Resources = [new(new GoodId.Voxel(new("core:ferrous-ore")), changed ? 60 : 100,
                new ResourceRenewal.NonRenewable()),
                new(new GoodId.Voxel(new("core:timber")), changed ? 12 : 0, new ResourceRenewal.Renewable(200, .5))],
            Platforms = 2, OccupiedPlatforms = changed ? 2 : 1
        };
        var second = original.World.Nodes[1];
        var empty = second with { Id = new(3), Position = new(250, 500) };
        var edge = new EdgeMapItem(new(1), first.Id, empty.Id, 200, first.Terrain, 1, 1, [], []);
        var train = ((Lookup<TrainView>.Found)original.FindTrain(new(7))).Value;
        var queued = train with { Activity = TrainActivity.Waiting, PlatformQueueEnteredStep = changed ? null : 2 };
        var stopped = train with { Id = new(8) };
        var otherQueue = train with { Id = new(11), Position = new TrainPosition.Node(second.Id, null, null, null, null,
            NodeTravelPhase.Approaching, 20, null), PlatformQueueEnteredStep = 5 };
        ImmutableArray<TrainMapItem> trains = [new(queued.Id, queued.Position, queued.Activity),
            new(stopped.Id, stopped.Position, stopped.Activity),
            new(new(9), new TrainPosition.Node(first.Id, null, null, null, null, NodeTravelPhase.Docked, 80, 0), TrainActivity.Servicing),
            // 线路列车穿过首行资源标注，供真实窗口截图检查绘制顺序。
            new(new(10), new TrainPosition.Edge(edge.Id, 0, TravelDirection.Forward, 30), TrainActivity.Running),
            new(otherQueue.Id, otherQueue.Position, otherQueue.Activity)];
        if (changed) trains = trains.Add(new(new(12), new TrainPosition.Node(first.Id, null, null, null, null,
            NodeTravelPhase.Docked, 80, 1), TrainActivity.Servicing));
        var nodes = ImmutableArray.Create(first, second, empty);
        return new(default, HostCoreScenario.Input)
        {
            World = original.World with { Nodes = nodes, Edges = [edge], Trains = trains },
            Nodes = nodes.ToDictionary(node => node.Id, node => (Lookup<NodeView>)new Lookup<NodeView>.Found(new(node, [], 80, 0, 80))),
            // 已持有站台及线路上的列车不配置 FindTrain；意外查询会直接使测试失败。
            Trains = new[] { queued, stopped, otherQueue }.ToDictionary(item => item.Id,
                item => (Lookup<TrainView>)new Lookup<TrainView>.Found(item))
        };
    }

    internal static FixedCoreReadView Save(CoreInputId input)
        => Create(Scenario.Save, default, input);

    internal static FixedCoreReadView Scene()
        => Create(Scenario.Scene, default, HostCoreScenario.Input);

    internal static FixedCoreReadView Formation(ulong sequence, bool formed, RunPlan? plan = null)
        => Create(Scenario.Formation, new(0, sequence), HostCoreScenario.Input, formed, plan);

    private static FixedCoreReadView Create(Scenario scenario, SimulationPosition tick, CoreInputId input,
        bool formed = true, RunPlan? plan = null)
    {
        var terrain = new TerrainView(new(scenario == Scenario.Map ? "core:plains" : "fixture:plain"), 0, []);
        var first = new NodeMapItem(new(1), new(250, 300), terrain, new NodeOccupant.Empty(), [],
            0, scenario == Scenario.Map ? 0 : 1, null);
        var second = first with { Id = new(2), Position = new(750, 300) };
        var edge = new EdgeMapItem(new(1), first.Id, second.Id, 600, terrain, 1, 1, [], []);
        var position = new TrainPosition.Node(first.Id, null, null, null, null, NodeTravelPhase.Waiting, 80, null);
        // Save 情景保留一辆未编组车辆；Scene 情景需一辆可跟随的车；Formation 检查两辆车的顺序。
        ImmutableArray<VehicleId> ordered = scenario switch
        {
            Scenario.Scene => [new(9)],
            Scenario.Formation when formed => [new(2), new(1)],
            _ => []
        };
        var train = new TrainView(new(7), position, TrainActivity.Stopped, ordered, new(1),
            0, 20, 10, 100, 0, 30, plan, null, 0,
            new(TargetPriority.ArmedFirst, TargetScope.All, EncounterAction.Engage, .2), [], null, [], TrainOrderFacing.FirstEndLeading, null);
        InstanceId.Vehicle[] vehicles = scenario switch
        {
            Scenario.Save => [new(new(1))],
            Scenario.Scene => [new(new(9))],
            Scenario.Formation => [new(new(1)), new(new(2))],
            _ => []
        };
        var location = new WorldLocation.Node(first.Id);
        var design = scenario switch
        {
            Scenario.Save => "engine-vehicle",
            Scenario.Scene => "fixture-design",
            _ => "vehicle-design"
        };
        var instances = vehicles.ToDictionary(id => (InstanceId)id, id => (Lookup<InstanceView>)new Lookup<InstanceView>.Found(
            new(id, new(design), new("fixture-composition"), location, SettlementRelation.PlayerOwned,
                new InstanceDetails.Vehicle(SizeClass.Medium, ordered.Contains(id.Id) ? train.Id : null),
                InstanceLock.None, new(100, 100), [], [])));
        ImmutableArray<EdgeId> connected = scenario == Scenario.Scene ? [edge.Id] : [];
        return new(tick, input)
        {
            World = new(1, new("fixture:railway"), 1000, 600, [first, second],
                scenario == Scenario.Scene ? [edge] : [], formed ? [new(train.Id, position, train.Activity)] : [], [], []),
            Nodes = new Dictionary<NodeId, Lookup<NodeView>>
            {
                [first.Id] = new Lookup<NodeView>.Found(new(first, connected, 80, 0, 80)),
                [second.Id] = new Lookup<NodeView>.Found(new(second, connected, 80, 0, 80))
            },
            Edges = scenario == Scenario.Scene
                ? new Dictionary<EdgeId, Lookup<EdgeView>> { [edge.Id] = new Lookup<EdgeView>.Found(new(edge, [], null)) }
                : new Dictionary<EdgeId, Lookup<EdgeView>>(),
            Locations = new Dictionary<WorldLocation, Lookup<LocationView>>
            {
                [location] = new Lookup<LocationView>.Found(new(location, formed ? [train.Id] : [],
                    vehicles.Cast<InstanceId>().ToImmutableArray(), [], [], new NodeOccupant.Empty())),
                [new WorldLocation.Node(second.Id)] = new Lookup<LocationView>.Found(new(
                    new WorldLocation.Node(second.Id), [], [], [], [], new NodeOccupant.Empty()))
            },
            Instances = instances,
            Trains = formed
                ? new Dictionary<TrainId, Lookup<TrainView>> { [train.Id] = new Lookup<TrainView>.Found(train) }
                : new Dictionary<TrainId, Lookup<TrainView>>()
        };
    }
}
