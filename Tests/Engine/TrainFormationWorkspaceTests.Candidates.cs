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
    public async Task Newly_arriving_candidates_follow_vehicle_id_order()
    {
        await WithWorkspace([], (panel, binding, current, content) =>
        {
            Open(panel, binding, current, content);
            var available = Find<VBoxContainer>(panel, "AvailableVehicles");
            Require(available.GetChildCount() == 1 && available.GetChild(0).Name == "Vehicle2",
                "初始候选列表应只有当地车辆2。");

            panel.Refresh(binding, CandidateWorld(2), content);

            Require(available.GetChildren().Select(child => child.Name.ToString()).SequenceEqual(["Vehicle1", "Vehicle2"]),
                "车辆1到达后，候选行应立即按车辆ID排列为Vehicle1、Vehicle2。");
            return Task.CompletedTask;
        }, CandidateWorld(1, firstAway: true));
    }

    [Test]
    public async Task Departed_selected_candidate_keeps_its_name_and_disappears_when_removed()
    {
        await WithWorkspace([], async (panel, binding, current, content) =>
        {
            Open(panel, binding, current, content);
            var input = new UiTestInput(TestScene);
            var candidate = Find<CheckBox>(panel, "Vehicle1");
            var originalName = candidate.Text;
            await input.Activate(candidate);

            panel.Refresh(binding, CandidateWorld(2, firstAway: true), content);
            var retainedName = candidate.Text.Contains(originalName);
            var markedDeparted = candidate.Text.Contains("已离开");
            var retainedSelection = candidate.Disabled && candidate.ButtonPressed;
            var ordered = Find<VBoxContainer>(panel, "OrderedVehicles");
            await input.Activate(Find<Button>(ordered.GetChild(0), "Remove"));
            var removedCandidate = !Find<VBoxContainer>(panel, "AvailableVehicles").GetChildren()
                .Any(child => child.Name == "Vehicle1");

            Require(retainedName && markedDeparted && retainedSelection && removedCandidate && ordered.GetChildCount() == 0,
                $"离开车辆应保留原名称并标记已离开，移出后立即清除候选行：保留名称={retainedName}，" +
                $"已离开标记={markedDeparted}，保留禁用勾选={retainedSelection}，候选已清除={removedCandidate}。");
        }, CandidateWorld(1));
    }

    private static FixedCoreReadView CandidateWorld(ulong revision, bool firstAway = false)
    {
        var basis = World();
        var instances = new[] { new InstanceId.Vehicle(new(1)), new InstanceId.Vehicle(new(2)) }
            .Select(id => ((Lookup<InstanceView>.Found)basis.FindInstance(id)).Value)
            .Select(instance => instance with
            {
                Location = new WorldLocation.Node(new(firstAway && instance.Id == new InstanceId.Vehicle(new(1)) ? 2UL : 1UL))
            }).ToArray();
        return new(basis.Position, basis.InputIdentity, revision: new(revision))
        {
            World = basis.World, Economy = basis.Economy,
            Nodes = basis.World.Nodes.ToDictionary(node => node.Id, node => basis.FindNode(node.Id)),
            Instances = instances.ToDictionary(instance => instance.Id,
                instance => (Lookup<InstanceView>)new Lookup<InstanceView>.Found(instance)),
            Locations = basis.World.Nodes.ToDictionary(node => (WorldLocation)new WorldLocation.Node(node.Id),
                node => (Lookup<LocationView>)new Lookup<LocationView>.Found(new(new WorldLocation.Node(node.Id), [],
                    instances.Where(instance => instance.Location == new WorldLocation.Node(node.Id))
                        .Select(instance => instance.Id).ToImmutableArray(), [], [], node.Occupant))),
            FormationPreviews = new[] { Array.Empty<ulong>(), new ulong[] { 1 } }
                .Select(ids => ((TrainId?)null, ids.Select(id => new VehicleId(id)).ToImmutableArray(),
                    basis.PreviewTrainFormation(ids.Select(id => new VehicleId(id)).ToImmutableArray(), null)))
        };
    }
}
