using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.Creatures;
using Train.Core.Sessions;
using Train.Core.World;
using Train.Core.Tests.Combat;
using Train.Core.Tests.World;

namespace Train.Core.Tests.Creatures;

internal static class CreatureTestSupport
{
    internal const double Tick = 1.0 / 60;
    internal const double Idle = 1000;

    private static readonly object Gate = new();
    private static CoreState? _multi;
    private static CoreState? _combat;

    internal static CoreState Multi
    {
        get
        {
            if (_multi is { } ready) return ready;
            lock (Gate) return _multi ??= Generated(WorldFixture.MultiInput, WorldFixture.MultiContent);
        }
    }

    internal static CoreState CombatWorld
    {
        get
        {
            if (_combat is { } ready) return ready;
            lock (Gate)
            {
                if (_combat is { } existing) return existing;
                using var session = CombatFixture.Session(armedBuilding: false, damage: 1);
                return _combat = session.State;
            }
        }
    }

    internal static CoreSession Open(CoreState baseline, WorldState world, CoreContentProjection? content = null)
        => new(baseline with { World = world, Content = content ?? baseline.Content });

    internal static AdvanceResult Step(ICoreSession session)
    {
        var result = session.Advance(1);
        if (result.Fault is not null)
            Assert.Fail($"{result.Fault.Code}: {result.Diagnostic?.Message}\n{result.Diagnostic?.StackTrace}");
        return result;
    }

    internal static CreatureProjection Animal(CoreContentProjection content, string id, long health, double speed,
        double retreat, double duration, PartCapability.Weapon? weapon) =>
        content.Creatures.Single(creature => creature.Id == new CreatureDefinitionId(id)) with
        {
            HitPoints = health, SpeedMetersPerSecond = speed, RetreatLossRatio = retreat,
            RaidDurationSeconds = duration, Weapon = weapon
        };

    internal static CoreContentProjection Lairs(CoreContentProjection content, CreatureProjection primary,
        CreatureProjection secondary, int maximum, double spawnSeconds, double raidSeconds, int roaming, int radius)
        => content with
        {
            Creatures = [primary, secondary],
            LairTypes = content.LairTypes.Select(type => type with
            {
                Members = [new WeightedCreature(primary.Id, 1)],
                MaximumMembers = maximum,
                SpawnIntervalSeconds = spawnSeconds,
                RaidIntervalSeconds = raidSeconds,
                RoamingRadiusHops = roaming,
                RaidRadiusHops = radius
            }).ToImmutableArray()
        };

    internal static CreatureState Creature(ulong id, string definition, long health)
        => new(new(id), new(definition), health);

    internal static CreatureGroupState Pack(ulong id, LairId home, WorldLocation location, TravelDirection? direction,
        CreatureActivity activity, ulong started, CreatureObjective? objective, params CreatureState[] creatures)
        => new(new(id), home, location, direction, activity, started, objective, creatures.ToImmutableArray());

    internal static WorldState WithPacks(WorldState world, params CreatureGroupState[] groups)
    {
        var nextCreature = Math.Max(world.Next.Creature,
            groups.SelectMany(group => group.Creatures).Select(creature => creature.Id.Value).DefaultIfEmpty(0UL).Max() + 1);
        var nextGroup = Math.Max(world.Next.CreatureGroup,
            groups.Select(group => group.Id.Value).DefaultIfEmpty(0UL).Max() + 1);
        return world with
        {
            CreatureGroups = groups.OrderBy(group => group.Id.Value).ToImmutableArray(),
            Next = world.Next with { Creature = nextCreature, CreatureGroup = nextGroup }
        };
    }

    internal static WorldState StopTrains(WorldState world)
    {
        var fallback = world.Nodes[0].Id;
        return world with
        {
            Trains = world.Trains.Select(train =>
            {
                var node = train.Position is TrainPosition.Node at ? at.Id : fallback;
                var distance = train.Position is TrainPosition.Node atNode ? atNode.DistanceMeters : 0;
                return train with
                {
                    Position = new TrainPosition.Node(node, null, null, null, null, NodeTravelPhase.Waiting, distance, null),
                    Activity = TrainActivity.Stopped, SpeedMetersPerSecond = 0, Plan = null, NextStopIndex = null,
                    RemainingDwellSteps = 0, StopRequested = false
                };
            }).ToImmutableArray()
        };
    }

    internal static WorldState PlaceTrain(WorldState world, TrainPosition.Node position, TrainActivity activity,
        ulong dwell, RunPlan? plan, TrainEngagementRules? engagement = null)
    {
        var train = world.Trains.Single();
        return world with
        {
            Trains =
            [
                train with
                {
                    Position = position, Activity = activity, Plan = plan,
                    NextStopIndex = plan is null ? null : 0, RemainingDwellSteps = dwell,
                    SpeedMetersPerSecond = 0, StopRequested = false,
                    Engagement = engagement ?? train.Engagement
                }
            ]
        };
    }

    internal static WorldState StructureOnly(WorldState world, long damage, double intervalSeconds) => world with
    {
        Designs = world.Designs.Select(design => design.Parts.Any(part => part.Capabilities.OfType<PartCapability.Weapon>().Any())
            ? design with
            {
                StructureHitWeight = 1,
                Parts = design.Parts.Select(part => part with
                {
                    HitWeight = 0,
                    Capabilities = part.Capabilities.Select(capability => capability is PartCapability.Weapon
                        ? (PartCapability)new PartCapability.Weapon(damage, intervalSeconds) : capability).ToImmutableArray()
                }).ToImmutableArray()
            }
            : design).ToImmutableArray()
    };

    internal static WorldState PlayerOwned(WorldState world, SettlementId id)
    {
        var settlement = world.Settlements.Single(item => item.Id == id);
        var plots = settlement.Plots.Select(plot => plot.Id).ToHashSet();
        return world with
        {
            Settlements = world.Settlements.Select(item => item.Id == id
                ? item with { Relation = SettlementRelation.PlayerOwned } : item).ToImmutableArray(),
            Nodes = world.Nodes.Select(node => node.Id == settlement.Node
                ? node with { Occupant = new NodeOccupant.Settlement(id, SettlementRelation.PlayerOwned) } : node).ToImmutableArray(),
            Instances = world.Instances.Select(instance =>
            {
                var owned = instance.Details is InstanceDetails.Building building && plots.Contains(building.Plot)
                    || instance.Id is InstanceId.Vehicle vehicle && settlement.ParkedVehicles.Contains(vehicle.Id);
                return owned ? instance with { Ownership = SettlementRelation.PlayerOwned } : instance;
            }).ToImmutableArray()
        };
    }

    internal static WorldState SingleEdge(WorldState world, NodeId start, NodeId end, double length, int tracks)
    {
        var template = world.Edges[0];
        return world with
        {
            Edges =
            [
                template with
                {
                    Start = start, End = end, LengthMeters = length, Tracks = tracks,
                    TrackLimit = Math.Max(template.TrackLimit, tracks), Breaks = [], Works = []
                }
            ],
            TrackHandoffs = []
        };
    }

    internal static (WorldState World, LairState Lair) AddLair(WorldState world, NodeId node, LairTypeId type)
    {
        var id = new LairId(world.Next.Lair);
        var lair = new LairState(id, node, type);
        return (world with
        {
            Lairs = world.Lairs.Add(lair).OrderBy(item => item.Id.Value).ToImmutableArray(),
            Next = world.Next with { Lair = id.Value + 1 },
            Nodes = world.Nodes.Select(item => item.Id == node
                ? item with { Occupant = new NodeOccupant.Lair(id) } : item).ToImmutableArray()
        }, lair);
    }

    internal static SettlementState Friendly(WorldState world)
    {
        var settlement = world.Settlements.Where(item => item.Relation != SettlementRelation.Hostile)
            .OrderBy(item => item.Id.Value).FirstOrDefault();
        Assert.IsNotNull(settlement, "夹具里没有非敌对聚居地。");
        return settlement!;
    }

    internal static NodeId EmptyNode(WorldState world, params NodeId[] except)
    {
        var skipped = except.ToHashSet();
        var node = world.Nodes.Where(item => item.Occupant is NodeOccupant.Empty && !skipped.Contains(item.Id))
            .OrderBy(item => item.Id.Value).FirstOrDefault();
        Assert.IsNotNull(node, "夹具里没有空节点。");
        return node!.Id;
    }

    internal static bool Npc(WorldState world, NodeId node) =>
        world.Nodes.Single(item => item.Id == node).Occupant is NodeOccupant.Settlement
        {
            Relation: not SettlementRelation.PlayerOwned
        };

    internal static CreatureGroupView Group(ICoreSession session, ulong id)
    {
        var lookup = session.Read().FindCreatureGroup(new(id));
        Assert.IsInstanceOfType<Lookup<CreatureGroupView>.Found>(lookup, $"生物群 {id} 缺失。");
        return ((Lookup<CreatureGroupView>.Found)lookup).Value;
    }

    internal static CreatureView Individual(ICoreSession session, ulong id)
    {
        var lookup = session.Read().FindCreature(new(id));
        Assert.IsInstanceOfType<Lookup<CreatureView>.Found>(lookup, $"生物 {id} 缺失。");
        return ((Lookup<CreatureView>.Found)lookup).Value;
    }

    internal static TrainView ReadTrain(ICoreSession session, TrainId id)
        => ((Lookup<TrainView>.Found)session.Read().FindTrain(id)).Value;

    internal static InstanceView Vehicle(ICoreSession session, VehicleId id)
        => ((Lookup<InstanceView>.Found)session.Read().FindInstance(new InstanceId.Vehicle(id))).Value;

    internal static CreatureGroupState State(ICoreSession session, ulong id)
        => ((CoreSession)session).State.World.CreatureGroups.Single(group => group.Id == new CreatureGroupId(id));

    private static CoreState Generated(WorldGenerationInput input, CoreContentProjection content)
    {
        using var candidate = WorldFixture.Prepare(input, content);
        using var session = (CoreSession)candidate.Activate();
        return session.State;
    }
}
