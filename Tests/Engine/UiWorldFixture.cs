using System.Collections.Immutable;
using Train.Core.Contracts;
using Train.Core.ContractSupport;

namespace Train.Engine.Tests;

internal static class UiWorldFixture
{
    private enum Scenario { Map, Save, Scene, Formation }

    internal static FixedCoreReadView Map(SimulationPosition tick = default)
        => Create(Scenario.Map, tick, HostCoreScenario.Input);

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
        var position = new TrainPosition.Node(first.Id);
        // Save 情景保留一辆未编组车辆；Scene 情景需一辆可跟随的车；Formation 检查两辆车的顺序。
        ImmutableArray<VehicleId> ordered = scenario switch
        {
            Scenario.Scene => [new(9)],
            Scenario.Formation when formed => [new(2), new(1)],
            _ => []
        };
        var train = new TrainView(new(7), position, TrainActivity.Stopped, ordered, new(1),
            0, 20, 10, 100, 0, 30, plan, null, 0,
            new(TargetPriority.ArmedFirst, TargetScope.All, EncounterAction.Engage, .2), [], null, [], null,
            TrainOrderFacing.FirstEndLeading);
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
                [first.Id] = new Lookup<NodeView>.Found(new(first, connected)),
                [second.Id] = new Lookup<NodeView>.Found(new(second, connected))
            },
            Edges = scenario == Scenario.Scene
                ? new Dictionary<EdgeId, Lookup<EdgeView>> { [edge.Id] = new Lookup<EdgeView>.Found(new(edge, [], null)) }
                : new Dictionary<EdgeId, Lookup<EdgeView>>(),
            Locations = new Dictionary<WorldLocation, Lookup<LocationView>>
            {
                [location] = new Lookup<LocationView>.Found(new(location, formed ? [train.Id] : [],
                    vehicles.Cast<InstanceId>().ToImmutableArray(), [], [], new NodeOccupant.Empty()))
            },
            Instances = instances,
            Trains = formed
                ? new Dictionary<TrainId, Lookup<TrainView>> { [train.Id] = new Lookup<TrainView>.Found(train) }
                : new Dictionary<TrainId, Lookup<TrainView>>()
        };
    }
}
