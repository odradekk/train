using System.Collections.Immutable;
using Train.Content.Contracts;
using Train.Core.Contracts;

namespace Train.App.Measurement;

// 配方期望、样本和窗口长测共用的计数，全部来自公开查询；指定节点时只统计位于该节点的对象。
internal static class WorldCounts
{
    internal static readonly ImmutableArray<string> Keys =
    [
        "armed_buildings", "armed_combats", "armed_line_combats", "armed_node_combats", "armed_trains_in_combat", "buildings", "creatures",
        "creatures_guarding", "creatures_raiding", "creatures_wandering", "dismantles_working", "facilities_running", "jobs_working",
        "large_creatures", "largest_train_meters", "largest_train_vehicles", "nodes", "occupied_platforms", "platforms", "player_settlements", "settlements",
        "titan_builds_working", "titan_trains_in_plan", "trains_in_plan", "trains_queued", "trains_running",
        "trains_servicing", "vehicles_in_plan"
    ];

    internal static ImmutableSortedDictionary<string, double> Read(ICoreReadView view, IContentView content, NodeId? node = null)
    {
        bool Here(WorldLocation location) => node is null || location is WorldLocation.Node at && at.Id == node;
        var trains = view.World.Trains.Where(train => node is null || train.Position is TrainPosition.Node at && at.Id == node)
            .Select(train => Found(view.FindTrain(train.Id))).ToArray();
        var inPlan = trains.Where(train => train.Activity is not (TrainActivity.Stopped or TrainActivity.NoRoute)).ToArray();
        var nodes = view.World.Nodes.Where(item => node is null || item.Id == node).ToArray();
        var settlements = nodes.Select(item => item.Occupant).OfType<NodeOccupant.Settlement>()
            .Select(settlement => Found(view.FindSettlement(settlement.Id))).ToArray();
        var groups = view.World.CreatureGroups.Where(group => Here(group.Location))
            .Select(group => Found(view.FindCreatureGroup(group.Id))).ToArray();
        var armedCombats = view.World.Combats.Where(combat => Here(combat.Location)
            && Found(view.FindCombat(combat.Id)).Participants.Any(participant => ArmedPlayerUnit(participant.Unit))).ToArray();
        var working = settlements.SelectMany(settlement => settlement.OwnedJobs.Concat(settlement.CommissionJobs)).Distinct()
            .Select(job => Found(view.FindJob(job))).OfType<JobView.Construction>().Select(job => job.Value)
            .Where(job => job.State == ConstructionState.Working).ToArray();
        int Creatures(Func<CreatureActivity, bool> activity) =>
            groups.Where(group => activity(group.Activity)).Sum(group => group.Creatures.Length);
        return new Dictionary<string, double>
        {
            ["armed_buildings"] = settlements.Sum(settlement => settlement.Plots.Count(plot => plot.Occupant is PlotOccupant.Building building
                && Armed(new InstanceId.Building(building.Id)))),
            ["armed_combats"] = armedCombats.Length,
            ["armed_line_combats"] = armedCombats.Count(combat => combat.Location is WorldLocation.Edge),
            ["armed_node_combats"] = armedCombats.Count(combat => combat.Location is WorldLocation.Node),
            ["armed_trains_in_combat"] = trains.Count(train => train.Activity == TrainActivity.Combat
                && train.Vehicles.Any(vehicle => Armed(new InstanceId.Vehicle(vehicle)))),
            ["buildings"] = settlements.Sum(settlement => settlement.Plots.Count(plot => plot.Occupant is PlotOccupant.Building)),
            ["creatures"] = Creatures(_ => true),
            ["creatures_guarding"] = Creatures(activity => activity == CreatureActivity.Guarding),
            ["creatures_raiding"] = Creatures(activity => activity is CreatureActivity.RaidingSettlement or CreatureActivity.RaidingRailway),
            ["creatures_wandering"] = Creatures(activity => activity == CreatureActivity.Wandering),
            ["dismantles_working"] = working.Count(job => job.Kind == ConstructionKind.Deconstruction),
            ["facilities_running"] = settlements.Sum(settlement => settlement.Facilities.Count(facility => facility.State == ProductionState.Running)),
            ["jobs_working"] = working.Length,
            // 清单的“大型生物”就是内置定义 core:large-creature；生物投影没有尺寸档位。
            ["large_creatures"] = groups.Sum(group => group.Creatures.Count(creature => creature.Definition.Value == "core:large-creature")),
            ["largest_train_meters"] = trains.Select(train => train.LengthMeters).DefaultIfEmpty().Max(),
            ["largest_train_vehicles"] = trains.Select(train => train.Vehicles.Length).DefaultIfEmpty().Max(),
            ["nodes"] = nodes.Length,
            ["occupied_platforms"] = nodes.Sum(item => item.OccupiedPlatforms),
            ["platforms"] = nodes.Sum(item => item.Platforms),
            ["player_settlements"] = settlements.Count(settlement => settlement.Relation == SettlementRelation.PlayerOwned),
            ["settlements"] = settlements.Length,
            ["titan_builds_working"] = working.Count(job => job.Target is ConstructionTarget.NewVehicle vehicle
                && Size(vehicle.Design) == SizeClass.Titan),
            ["titan_trains_in_plan"] = inPlan.Count(train => train.Vehicles.Any(vehicle =>
                Found(view.FindInstance(new InstanceId.Vehicle(vehicle))).Details is InstanceDetails.Vehicle { Size: SizeClass.Titan })),
            ["trains_in_plan"] = inPlan.Length,
            ["trains_queued"] = trains.Count(train => train.Blockers.Any(blocker => blocker.Code == "platform_wait")),
            ["trains_running"] = trains.Count(train => train.Activity == TrainActivity.Running),
            ["trains_servicing"] = trains.Count(train => train.Activity == TrainActivity.Servicing),
            ["vehicles_in_plan"] = inPlan.Sum(train => train.Vehicles.Length)
        }.ToImmutableSortedDictionary(StringComparer.Ordinal);

        bool ArmedPlayerUnit(CombatUnitId unit) => unit is CombatUnitId.Instance { Id: var id }
            && Found(view.FindInstance(id)).Ownership == SettlementRelation.PlayerOwned && Armed(id);
        bool Armed(InstanceId id)
        {
            var instance = Found(view.FindInstance(id));
            var weapons = Design(instance.Design).Parts.Where(part => part.Capabilities.Any(capability => capability is PartCapability.Weapon))
                .Select(part => part.Id).ToHashSet();
            return instance.Parts.Any(part => weapons.Contains(part.Part) && !part.Destroyed && !part.DefinitionMissing);
        }
        SizeClass? Size(DesignHash design) => Design(design).Kind is DesignKind.Vehicle vehicle ? vehicle.Size : null;
        DesignProjection Design(DesignHash design) => content.FindDesign(design) is ContentLookup<DesignEvaluation>.Found
            { Value: DesignEvaluation.Complete { Core: { } input } } ? input.Design
            : throw new InvalidDataException($"计数找不到设计：{design}。");
    }

    private static T Found<T>(Lookup<T> lookup) where T : notnull => lookup is Lookup<T>.Found found
        ? found.Value : throw new InvalidDataException($"计数对象不存在：{lookup}。");
}
