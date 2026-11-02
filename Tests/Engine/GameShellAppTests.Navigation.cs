using System.Text.RegularExpressions;
using Chickensoft.GoDotTest;
using Godot;
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class GameShellAppTests
{
    [Test]
    public async Task Navigation_counts_each_train_needing_attention_once_and_all_railway_breaks()
    {
        var basis = UiWorldFixture.Map();
        var first = basis.World.Nodes[0];
        var second = basis.World.Nodes[1];
        var third = second with { Id = new(3), Position = new(750, 500) };
        var nodes = new[] { first, second, third };
        var edges = new[]
        {
            new EdgeMapItem(new(1), first.Id, second.Id, 500, first.Terrain, 1, 1,
                [new(new(1), 1, 100), new(new(2), 1, 300)], []),
            new EdgeMapItem(new(2), second.Id, third.Id, 200, first.Terrain, 1, 1,
                [new(new(3), 1, 40), new(new(4), 1, 80), new(new(5), 1, 120)], [])
        };
        var train = ((Lookup<TrainView>.Found)basis.FindTrain(new(7))).Value;
        var plan = new RunPlan(true, [new PlanStop.Waypoint(second.Id), new PlanStop.Waypoint(first.Id)]);
        // A 只有受阻，B 只有无计划，C 两者都有但只算一次，D 无需关注。
        var trains = new[]
        {
            train with { Id = new(1), Activity = TrainActivity.NoRoute, Plan = plan },
            train with { Id = new(2), Activity = TrainActivity.Stopped, Plan = null },
            train with { Id = new(3), Activity = TrainActivity.NoRoute, Plan = null },
            train with { Id = new(4), Activity = TrainActivity.Stopped, Plan = plan }
        };
        var view = new FixedCoreReadView(basis.Position, basis.InputIdentity)
        {
            Economy = basis.Economy,
            World = basis.World with
            {
                Nodes = [.. nodes], Edges = [.. edges],
                Trains = [.. trains.Select(item => new TrainMapItem(item.Id, item.Position, item.Activity))]
            },
            Nodes = nodes.ToDictionary(item => item.Id, item => (Lookup<NodeView>)new Lookup<NodeView>.Found(
                new(item, [.. edges.Where(edge => edge.Start == item.Id || edge.End == item.Id).Select(edge => edge.Id)], 80, 0, 80))),
            Edges = edges.ToDictionary(item => item.Id, item => (Lookup<EdgeView>)new Lookup<EdgeView>.Found(
                new(item, [new(1, null, [])], null))),
            Trains = trains.ToDictionary(item => item.Id, item => (Lookup<TrainView>)new Lookup<TrainView>.Found(item)),
            Locations = nodes.ToDictionary(item => (WorldLocation)new WorldLocation.Node(item.Id),
                item => (Lookup<LocationView>)new Lookup<LocationView>.Found(new(new WorldLocation.Node(item.Id),
                    item.Id == first.Id ? [.. trains.Select(train => train.Id)] : [], [], [], [], item.Occupant)))
        };
        var setup = new HostSimulationTests.HostSetup(view, EconomyFixture.Content(mapPresentation: true));
        await Run((ui, shell) =>
        {
            var fleetNumbers = Regex.Matches(Find<Button>(shell.Navigation, "Fleet").Text, @"\d+");
            var edgeNumbers = Regex.Matches(Find<Button>(shell.Navigation, "Edges").Text, @"\d+");
            Require(fleetNumbers.Count == 1 && int.Parse(fleetNumbers[0].Value) == 3,
                "导航应显示三列需关注列车：受阻、有计划缺失，以及两者兼有各一列。");
            Require(edgeNumbers.Count == 1 && int.Parse(edgeNumbers[0].Value) == 5,
                "线路导航应显示两条线路上合计五处断点，不能只计算线路数。");
            return Task.CompletedTask;
        }, setup);
    }
}
