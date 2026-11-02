using System.Collections.Immutable;
using Chickensoft.GoDotTest;
using Godot;
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class TrainFormationWorkspaceTests
{
    [Test]
    public async Task New_formation_uses_the_settlements_display_name()
    {
        await WithWorkspace([], (panel, binding, view, content) =>
        {
            Open(panel, binding, view, content);
            Require(panel.FindChildren("*", "Label", true, false).OfType<Label>()
                .Any(label => label.Text == "编组列车 · 青川"), "新编组标题没有显示聚居地名称。");
            Require(!Find<Label>(panel, "Destination").IsVisibleInTree(), "新编组不应显示移出车辆的去向。");
            return Task.CompletedTask;
        }, SettlementWorld(false));
    }

    [Test]
    public async Task Unchanged_refresh_does_not_notify_the_footer_theme_again()
    {
        await WithWorkspace([], async (panel, binding, view, content) =>
        {
            Open(panel, binding, view, content);
            var input = new UiTestInput(TestScene);
            await input.Activate(Find<CheckBox>(panel, "Vehicle1"));
            await input.Frames(3);
            var reason = Find<Label>(panel, "Reason");
            var text = reason.Text;
            var changes = 0;
            reason.ThemeChanged += () => changes++;
            for (var frame = 0; frame < 3; frame++)
            {
                panel.Refresh(binding, view, content);
                await input.Frames();
            }
            Require(changes == 0 && reason.Text == text, "未变化的刷新仍重复通知页脚主题变化。");
        });
    }

    private static FixedCoreReadView SettlementWorld(bool formed)
    {
        var basis = formed ? UiWorldFixture.Formation(0, true) : World();
        var nodes = basis.World.Nodes.SetItem(0, basis.World.Nodes[0] with
            { Occupant = new NodeOccupant.Settlement(new(1), SettlementRelation.PlayerOwned) });
        return new(basis.Position, basis.InputIdentity, revision: basis.Revision)
        {
            World = basis.World with { Nodes = nodes }, Economy = basis.Economy,
            Nodes = nodes.ToDictionary(node => node.Id, node => (Lookup<NodeView>)new Lookup<NodeView>.Found(
                ((Lookup<NodeView>.Found)basis.FindNode(node.Id)).Value with { Map = node })),
            Locations = nodes.ToDictionary(node => (WorldLocation)new WorldLocation.Node(node.Id), node =>
                (Lookup<LocationView>)new Lookup<LocationView>.Found(
                    ((Lookup<LocationView>.Found)basis.FindLocation(new WorldLocation.Node(node.Id))).Value with { Occupant = node.Occupant })),
            Instances = new[] { new InstanceId.Vehicle(new(1)), new InstanceId.Vehicle(new(2)) }
                .ToDictionary(id => (InstanceId)id, id => basis.FindInstance(id)),
            Trains = formed ? new Dictionary<TrainId, Lookup<TrainView>> { [new(7)] = basis.FindTrain(new(7)) } : [],
            FormationPreviews = formed ? [] : new[] { Array.Empty<ulong>(), new ulong[] { 1 }, [2], [1, 2], [2, 1] }
                .Select(ids => ((TrainId?)null, ids.Select(id => new VehicleId(id)).ToImmutableArray(),
                    basis.PreviewTrainFormation(ids.Select(id => new VehicleId(id)).ToImmutableArray(), null)))
        };
    }
}
