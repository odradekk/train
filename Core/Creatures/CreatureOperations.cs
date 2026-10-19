using System.Collections.Immutable;
using Train.Core.Contracts;
using Train.Core.World;

namespace Train.Core.Creatures;

internal static class CreatureOperations
{
    private readonly record struct Link(NodeId Node, EdgeId Edge);

    internal static WorldState Advance(WorldState world, CoreContentProjection content, ulong step)
    {
        world = Expire(world, content, step);
        world = MergeHome(world, step);
        world = Move(world, content, step);
        world = MergeHome(world, step);
        world = Spawn(world, content, step);
        return Dispatch(world, content, step);
    }

    private static WorldState Expire(WorldState world, CoreContentProjection content, ulong step)
    {
        foreach (var group in world.CreatureGroups.ToArray())
        {
            if (CreatureQueries.InCombat(world, group.Id)) continue;
            var expired = group.Activity switch
            {
                CreatureActivity.Wandering => DurationElapsed(content, group, step),
                CreatureActivity.RaidingSettlement when AtTarget(group) => DurationElapsed(content, group, step),
                _ => false
            };
            if (!expired) continue;
            world = CreatureQueries.Replace(world, group with
            {
                Activity = CreatureActivity.Retreating, Objective = null, ActivityStartedAtStep = step
            });
        }
        return world;
    }

    private static bool DurationElapsed(CoreContentProjection content, CreatureGroupState group, ulong step)
    {
        if (CreatureQueries.MinRaidDuration(content, group) is not { } seconds) return false;
        var interval = WorldResources.IntervalSteps(seconds)
            ?? throw new InvalidOperationException("袭击时长超出可表示范围。");
        return step >= group.ActivityStartedAtStep && step - group.ActivityStartedAtStep >= interval;
    }

    private static bool AtTarget(CreatureGroupState group) =>
        group.Objective is CreatureObjective.Settlement target
        && group.Location is WorldLocation.Node node && node.Id == target.Node;

    private static WorldState MergeHome(WorldState world, ulong step)
    {
        foreach (var group in world.CreatureGroups.ToArray())
        {
            if (group.Activity != CreatureActivity.Retreating || group.Home is not { } home) continue;
            if (CreatureQueries.InCombat(world, group.Id) || !CreatureQueries.AtOwnLair(world, group)) continue;
            var current = CreatureQueries.FindGroup(world, group.Id);
            if (current is null) continue;
            var guard = world.CreatureGroups.FirstOrDefault(item => item.Home == home && item.Id != group.Id
                && item.Activity == CreatureActivity.Guarding);
            if (guard is null)
            {
                world = CreatureQueries.Replace(world, current with
                {
                    Activity = CreatureActivity.Guarding, Objective = null, Direction = null,
                    ActivityStartedAtStep = step
                });
                continue;
            }
            var creatures = guard.Creatures.AddRange(current.Creatures).OrderBy(creature => creature.Id.Value).ToImmutableArray();
            world = CreatureQueries.Remove(world, current.Id);
            world = CreatureQueries.Replace(world, guard with { Creatures = creatures });
        }
        return world;
    }

    private static WorldState Move(WorldState world, CoreContentProjection content, ulong step)
    {
        foreach (var id in world.CreatureGroups.Select(group => group.Id).ToArray())
        {
            var group = CreatureQueries.FindGroup(world, id);
            if (group is null || CreatureQueries.InCombat(world, id) || group.Activity == CreatureActivity.Guarding)
                continue;
            var speed = CreatureQueries.GroupSpeed(content, group);
            if (speed <= 0) continue;
            var delta = speed * SimulationTime.StepSeconds;
            group = group.Location switch
            {
                WorldLocation.Edge edge => ContinueEdge(world, group, edge, delta, step),
                WorldLocation.Node node => LeaveNode(world, content, group, node.Id, delta, step),
                _ => group
            };
            world = CreatureQueries.Replace(world, group);
        }
        return world;
    }

    private static CreatureGroupState ContinueEdge(WorldState world, CreatureGroupState group,
        WorldLocation.Edge location, double delta, ulong step)
    {
        var edge = world.Edges.FirstOrDefault(item => item.Id == location.Id);
        if (edge is null || group.Direction is not { } direction) return group;
        if (group.Activity == CreatureActivity.RaidingRailway
            && group.Objective is CreatureObjective.Railway railway && railway.Edge == edge.Id)
            return ApproachRailway(group, edge, location.DistanceMeters, railway.DistanceMeters, delta, step);
        var forward = direction == TravelDirection.Forward;
        var far = forward ? edge.End : edge.Start;
        return AdvanceOnEdge(world, group, edge, forward, location.DistanceMeters, far, delta, step);
    }

    private static CreatureGroupState LeaveNode(WorldState world, CoreContentProjection content,
        CreatureGroupState group, NodeId node, double delta, ulong step)
    {
        if (group.Activity == CreatureActivity.RaidingSettlement
            && group.Objective is CreatureObjective.Settlement settlement && settlement.Node == node)
            return group;
        if (group.Activity == CreatureActivity.RaidingRailway
            && group.Objective is CreatureObjective.Railway railway)
        {
            var edge = world.Edges.FirstOrDefault(item => item.Id == railway.Edge);
            if (edge is null) return group;
            if (edge.Start == node || edge.End == node)
            {
                var forward = edge.Start == node;
                return ApproachRailway(group, edge, forward ? 0 : edge.LengthMeters, railway.DistanceMeters, delta, step);
            }
            var endpoint = NearerEndpoint(world, node, edge);
            if (endpoint is not { } nearer || NextHop(world, node, nearer) is not { } railwayHop) return group;
            return Enter(world, group, railwayHop, node, delta, step);
        }
        var destination = group.Activity switch
        {
            CreatureActivity.Retreating when group.Home is { } home => world.Lairs.FirstOrDefault(lair => lair.Id == home)?.Node,
            CreatureActivity.RaidingSettlement when group.Objective is CreatureObjective.Settlement target => target.Node,
            _ => null
        };
        if (destination is { } goal)
        {
            if (goal == node) return group;
            if (NextHop(world, node, goal) is not { } hop) return group;
            return Enter(world, group, hop, node, delta, step);
        }
        if (group.Activity != CreatureActivity.Wandering) return group;
        var next = Wander(world, content, group, node, step);
        return next is { } link ? Enter(world, group, link, node, delta, step) : group;
    }

    private static CreatureGroupState ApproachRailway(CreatureGroupState group, EdgeMapItem edge, double distance,
        double target, double delta, ulong step)
    {
        if (distance.Equals(target)) return group;
        var forward = distance < target;
        var next = forward ? Math.Min(distance + delta, target) : Math.Max(distance - delta, target);
        var arrived = next.Equals(target);
        return group with
        {
            Location = new WorldLocation.Edge(edge.Id, next),
            Direction = forward ? TravelDirection.Forward : TravelDirection.Reverse,
            ActivityStartedAtStep = arrived ? step : group.ActivityStartedAtStep
        };
    }

    private static CreatureGroupState Enter(WorldState world, CreatureGroupState group, Link hop, NodeId from,
        double delta, ulong step)
    {
        var edge = world.Edges.First(item => item.Id == hop.Edge);
        var forward = edge.Start == from;
        return AdvanceOnEdge(world, group, edge, forward, forward ? 0 : edge.LengthMeters, hop.Node, delta, step);
    }

    private static CreatureGroupState AdvanceOnEdge(WorldState world, CreatureGroupState group, EdgeMapItem edge,
        bool forward, double distance, NodeId far, double delta, ulong step)
    {
        var remaining = forward ? edge.LengthMeters - distance : distance;
        if (delta >= remaining)
        {
            if (NpcSettlement(world, far)) return group;
            var arrived = group with { Location = new WorldLocation.Node(far), Direction = null };
            if (arrived.Activity == CreatureActivity.RaidingSettlement
                && arrived.Objective is CreatureObjective.Settlement target && target.Node == far)
                arrived = arrived with { ActivityStartedAtStep = step };
            return arrived;
        }
        var next = forward ? distance + delta : distance - delta;
        return group with
        {
            Location = new WorldLocation.Edge(edge.Id, next),
            Direction = forward ? TravelDirection.Forward : TravelDirection.Reverse
        };
    }

    private static Link? Wander(WorldState world, CoreContentProjection content, CreatureGroupState group,
        NodeId node, ulong step)
    {
        if (group.Home is not { } home) return null;
        var lair = world.Lairs.FirstOrDefault(item => item.Id == home);
        var projection = lair is null ? null : content.LairTypes.FirstOrDefault(type => type.Id == lair.Type);
        if (lair is null || projection is null) return null;
        var hops = Hops(world, lair.Node);
        var candidates = Neighbors(world, node)
            .Where(link => !NpcSettlement(world, link.Node)
                && hops.TryGetValue(link.Node, out var hop) && hop <= projection.RoamingRadiusHops)
            .GroupBy(link => link.Node)
            .Select(links => links.OrderBy(link => link.Edge.Value).First())
            .OrderBy(link => link.Node.Value)
            .ToArray();
        if (candidates.Length == 0) return null;
        var sample = KeyedRandom.Sample(new RandomKey(world.Source.WorldSeed, "creature.wander.v1",
            [group.Id.Value, step]));
        var index = (int)WorldRandom.Integer(sample, 0, candidates.Length - 1);
        return candidates[index];
    }

    // 平局取线路编号较小的一跳；不进入 NPC 聚居地。
    private static Link? NextHop(WorldState world, NodeId from, NodeId goal)
    {
        if (from == goal) return null;
        var hops = Hops(world, goal);
        if (!hops.TryGetValue(from, out var here) || here == 0) return null;
        Link? best = null;
        foreach (var link in Neighbors(world, from))
        {
            if (!hops.TryGetValue(link.Node, out var hop) || hop != here - 1) continue;
            if (best is null || link.Edge.Value < best.Value.Edge.Value) best = link;
        }
        return best;
    }

    private static NodeId? NearerEndpoint(WorldState world, NodeId from, EdgeMapItem edge)
    {
        var hops = Hops(world, from);
        int? start = hops.TryGetValue(edge.Start, out var startHop) ? startHop : null;
        int? end = hops.TryGetValue(edge.End, out var endHop) ? endHop : null;
        if (start is null && end is null) return null;
        if (end is null || start is not null && (start < end || start == end && edge.Start.Value <= edge.End.Value))
            return edge.Start;
        return edge.End;
    }

    private static Dictionary<NodeId, int> Hops(WorldState world, NodeId origin)
    {
        var result = new Dictionary<NodeId, int> { [origin] = 0 };
        var pending = new Queue<NodeId>();
        pending.Enqueue(origin);
        while (pending.TryDequeue(out var node))
        {
            foreach (var link in Neighbors(world, node).OrderBy(link => link.Edge.Value))
            {
                if (result.ContainsKey(link.Node) || NpcSettlement(world, link.Node)) continue;
                result[link.Node] = result[node] + 1;
                pending.Enqueue(link.Node);
            }
        }
        return result;
    }

    private static IEnumerable<Link> Neighbors(WorldState world, NodeId node)
    {
        foreach (var edge in world.Edges)
        {
            if (edge.Start == node) yield return new(edge.End, edge.Id);
            else if (edge.End == node) yield return new(edge.Start, edge.Id);
        }
    }

    private static bool NpcSettlement(WorldState world, NodeId node) =>
        world.Nodes.FirstOrDefault(item => item.Id == node)?.Occupant is NodeOccupant.Settlement
        {
            Relation: not SettlementRelation.PlayerOwned
        };

    private static WorldState Spawn(WorldState world, CoreContentProjection content, ulong step)
    {
        foreach (var lair in world.Lairs)
        {
            var projection = content.LairTypes.FirstOrDefault(type => type.Id == lair.Type);
            if (projection is null) continue;
            var interval = WorldResources.IntervalSteps(projection.SpawnIntervalSeconds);
            if (interval is null) throw new InvalidOperationException("补充间隔超出可表示范围。");
            if (step % interval != 0) continue;
            var members = projection.Members.Where(member => member.Weight > 0
                && CreatureQueries.Projection(content, member.Creature)?.HitPoints > 0).ToArray();
            if (members.Length == 0) continue;
            var living = world.CreatureGroups.Where(group => group.Home == lair.Id).Sum(group => group.Creatures.Length);
            if (living >= projection.MaximumMembers) continue;
            var total = members.Sum(member => (long)member.Weight);
            var sample = KeyedRandom.Sample(new RandomKey(world.Source.WorldSeed, "creature.spawn.v1",
                [lair.Id.Value, step]));
            var choice = WorldRandom.Integer(sample, 0, total - 1);
            WeightedCreature? selected = null;
            var cursor = 0L;
            foreach (var member in members)
            {
                cursor += member.Weight;
                if (choice < cursor) { selected = member; break; }
            }
            if (selected is null) continue;
            var creatureProjection = CreatureQueries.Projection(content, selected.Creature)!;
            if (world.Next.Creature == ulong.MaxValue)
                throw new InvalidOperationException("生物身份已耗尽。");
            var creature = new CreatureState(new(world.Next.Creature), selected.Creature, creatureProjection.HitPoints);
            world = world with { Next = world.Next with { Creature = world.Next.Creature + 1 } };
            var guard = world.CreatureGroups.FirstOrDefault(group => group.Home == lair.Id
                && group.Activity == CreatureActivity.Guarding);
            var garrison = guard ?? world.CreatureGroups.FirstOrDefault(group => group.Home == lair.Id
                && group.Activity == CreatureActivity.Combat && CreatureQueries.AtOwnLair(world, group));
            if (garrison is not null)
            {
                world = CreatureQueries.Replace(world, garrison with
                {
                    Creatures = garrison.Creatures.Add(creature).OrderBy(item => item.Id.Value).ToImmutableArray()
                });
                continue;
            }
            if (world.Next.CreatureGroup == ulong.MaxValue)
                throw new InvalidOperationException("生物群身份已耗尽。");
            var created = new CreatureGroupState(new(world.Next.CreatureGroup), lair.Id, new WorldLocation.Node(lair.Node),
                null, CreatureActivity.Guarding, step, null, [creature]);
            world = world with { Next = world.Next with { CreatureGroup = world.Next.CreatureGroup + 1 } };
            world = CreatureQueries.Replace(world, created);
        }
        return world;
    }

    private static WorldState Dispatch(WorldState world, CoreContentProjection content, ulong step)
    {
        foreach (var lair in world.Lairs)
        {
            var projection = content.LairTypes.FirstOrDefault(type => type.Id == lair.Type);
            if (projection is null) continue;
            var interval = WorldResources.IntervalSteps(projection.RaidIntervalSeconds);
            if (interval is null) throw new InvalidOperationException("出动间隔超出可表示范围。");
            if (step % interval != 0) continue;
            var guard = world.CreatureGroups.FirstOrDefault(group => group.Home == lair.Id
                && group.Activity == CreatureActivity.Guarding && !group.Creatures.IsEmpty);
            if (guard is null || world.CreatureGroups.Any(group => group.Home == lair.Id
                    && CreatureQueries.IsOut(world, group)))
                continue;
            var hops = Hops(world, lair.Node);
            var settlements = world.Settlements.Where(settlement => settlement.Relation == SettlementRelation.PlayerOwned
                    && hops.TryGetValue(settlement.Node, out var hop) && hop <= projection.RaidRadiusHops)
                .OrderBy(settlement => settlement.Id.Value).ToArray();
            var edges = world.Edges.Where(edge => edge.Tracks > 0 && (
                    hops.TryGetValue(edge.Start, out var start) && start <= projection.RaidRadiusHops
                    || hops.TryGetValue(edge.End, out var end) && end <= projection.RaidRadiusHops))
                .OrderBy(edge => edge.Id.Value).ToArray();
            var count = settlements.Length + edges.Length;
            if (count == 0)
            {
                if (projection.RoamingRadiusHops <= 0) continue;
                world = CreatureQueries.Replace(world, guard with
                {
                    Activity = CreatureActivity.Wandering, Objective = null, ActivityStartedAtStep = step
                });
                continue;
            }
            var sample = KeyedRandom.Sample(new RandomKey(world.Source.WorldSeed, "creature.raid.target.v1",
                [lair.Id.Value, step]));
            var index = (int)WorldRandom.Integer(sample, 0, count - 1);
            if (index < settlements.Length)
            {
                world = CreatureQueries.Replace(world, guard with
                {
                    Activity = CreatureActivity.RaidingSettlement,
                    Objective = new CreatureObjective.Settlement(settlements[index].Node),
                    ActivityStartedAtStep = step
                });
                continue;
            }
            var edge = edges[index - settlements.Length];
            var position = KeyedRandom.Sample(new RandomKey(world.Source.WorldSeed, "creature.raid.position.v1",
                [lair.Id.Value, step, edge.Id.Value]));
            world = CreatureQueries.Replace(world, guard with
            {
                Activity = CreatureActivity.RaidingRailway,
                Objective = new CreatureObjective.Railway(edge.Id, RailwayDistance(WorldRandom.Unit(position), edge.LengthMeters)),
                ActivityStartedAtStep = step
            });
        }
        return world;
    }

    // Unit 落在 [0, 1)，乘长度后仍要严格落在 (0, 长度) 内。
    private static double RailwayDistance(double unit, double length)
    {
        var distance = unit * length;
        if (!(distance > 0)) distance = Math.BitIncrement(0d);
        if (!(distance < length)) distance = Math.BitDecrement(length);
        if (!(distance > 0) || !(distance < length))
            throw new InvalidOperationException("线路袭击位置无法落在边内。");
        return distance;
    }
}
