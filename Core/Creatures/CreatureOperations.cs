using System.Collections.Immutable;
using Train.Core.Combat;
using Train.Core.Contracts;
using Train.Core.Railway;
using Train.Core.Sessions;
using Train.Core.World;

namespace Train.Core.Creatures;

internal static class CreatureOperations
{
    private readonly record struct Link(NodeId Node, EdgeId Edge);

    internal static (WorldState World, ImmutableArray<CoreEvent> Events) Advance(WorldState world,
        CoreContentProjection content, ulong step)
    {
        var events = ImmutableArray.CreateBuilder<CoreEvent>();
        world = Expire(world, content, step);
        world = Move(world, content, step);
        world = MergeHome(world, step);
        world = Spawn(world, content, step);
        world = Dispatch(world, content, step);
        world = FoundLairs(world, content, step, events);
        return (world, events.ToImmutable());
    }

    internal static void ValidateDebugSpawn(CoreCommand.DebugSpawnCreatureGroup command)
    {
        if (command.Node.Value == 0 || !WorldFormat.IsIdentity(command.Creature.Value) || command.Count < 1)
            throw new ArgumentException("生物群须提供非零节点编号、合法生物定义和正数量。", nameof(command));
    }

    // 开发命令：在节点放一群无家的游荡生物，供规模样例和测试使用。
    internal static CommandEffect DebugSpawn(WorldState world, CoreContentProjection content,
        CoreCommand.DebugSpawnCreatureGroup command, ulong step)
    {
        var node = new WorldObjectId.Node(command.Node);
        if (world.Nodes.All(item => item.Id != command.Node))
            return new CommandEffect.Rejected([new("object_missing", "Node", node, [])]);
        if (CreatureQueries.Projection(content, command.Creature) is not { HitPoints: > 0 } projection)
            return new CommandEffect.Rejected([new("content_missing", "Creature", node, [command.Creature.Value])]);
        if (world.Next.CreatureGroup == ulong.MaxValue || ulong.MaxValue - world.Next.Creature < (ulong)command.Count)
            return new CommandEffect.Rejected([new("identity_exhausted", "World.Next.Creature", node, [])]);
        var first = world.Next.Creature;
        var group = new CreatureGroupState(new(world.Next.CreatureGroup), null, new WorldLocation.Node(command.Node), null,
            CreatureActivity.Wandering, step, null, Enumerable.Range(0, command.Count)
                .Select(index => new CreatureState(new(first + (ulong)index), command.Creature, projection.HitPoints)).ToImmutableArray());
        world = world with { Next = world.Next with { Creature = first + (ulong)command.Count, CreatureGroup = group.Id.Value + 1 } };
        return new CommandEffect.Accepted(content, [], CreatureQueries.Replace(world, group), [new WorldObjectId.CreatureGroup(group.Id)]);
    }

    private static WorldState Expire(WorldState world, CoreContentProjection content, ulong step)
    {
        foreach (var group in world.CreatureGroups.ToArray())
        {
            if (CreatureQueries.InCombat(world, group.Id)) continue;
            var expired = group.Activity switch
            {
                CreatureActivity.Wandering when group.Home is not null => DurationElapsed(content, group, step),
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
            if (group.Activity != CreatureActivity.Retreating || group.Home is null) continue;
            if (CreatureQueries.InCombat(world, group.Id) || !CreatureQueries.AtOwnLair(world, group)) continue;
            var current = CreatureQueries.FindGroup(world, group.Id);
            if (current is null) continue;
            world = BecomeGarrison(world, current, step);
        }
        return world;
    }

    internal static WorldState BecomeGarrison(WorldState world, CreatureGroupState group, ulong step)
    {
        if (group.Home is not { } home) return world;
        var others = world.CreatureGroups.Where(other => other.Id != group.Id && other.Home == home).ToArray();
        var keeper = others.Where(other => other.Activity == CreatureActivity.Guarding)
            .OrderBy(other => other.Id.Value).FirstOrDefault()
            ?? others.Where(other => other.Activity == CreatureActivity.Combat
                && CreatureQueries.AtOwnLair(world, other) && other.Id.Value < group.Id.Value)
                .OrderBy(other => other.Id.Value).FirstOrDefault();
        if (keeper is null)
            return CreatureQueries.Replace(world, group with
            {
                Activity = CreatureActivity.Guarding, Objective = null, Direction = null,
                ActivityStartedAtStep = step
            });
        var creatures = keeper.Creatures.AddRange(group.Creatures).OrderBy(creature => creature.Id.Value).ToImmutableArray();
        world = CreatureQueries.Remove(world, group.Id);
        return CreatureQueries.Replace(world, keeper with { Creatures = creatures });
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
        var behind = forward ? edge.Start : edge.End;
        var turn = false;
        if (Destination(world, group) is { } goal)
        {
            var hops = Hops(world, goal);
            int? farHops = hops.TryGetValue(far, out var farHop) ? farHop : null;
            int? behindHops = hops.TryGetValue(behind, out var behindHop) ? behindHop : null;
            turn = behindHops is { } better && (farHops is null || better < farHops);
        }
        else if (group.Activity == CreatureActivity.Wandering)
            turn = NpcSettlement(world, far) && !NpcSettlement(world, behind);
        if (turn)
        {
            forward = !forward;
            far = forward ? edge.End : edge.Start;
        }
        return AdvanceOnEdge(world, group, edge, forward, location.DistanceMeters, far, delta, step);
    }

    private static NodeId? Destination(WorldState world, CreatureGroupState group) => group.Activity switch
    {
        CreatureActivity.Retreating when group.Home is { } home => world.Lairs.FirstOrDefault(lair => lair.Id == home)?.Node,
        CreatureActivity.RaidingSettlement when group.Objective is CreatureObjective.Settlement target => target.Node,
        _ => null
    };

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
        var destination = Destination(world, group);
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
        if (group.Home is not { } home)
            return Pick(world, group, step, Neighbors(world, node)
                .Where(link => !NpcSettlement(world, link.Node)).ToArray());
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
        return Pick(world, group, step, candidates);
    }

    private static Link? Pick(WorldState world, CreatureGroupState group, ulong step, Link[] links)
    {
        var candidates = links.GroupBy(link => link.Node)
            .Select(choices => choices.OrderBy(link => link.Edge.Value).First())
            .OrderBy(link => link.Node.Value).ToArray();
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

    internal static (WorldState World, ImmutableArray<CoreEvent> Events) DamageRails(WorldState world,
        CoreContentProjection content, ulong step)
    {
        if (world.CreatureGroups.IsEmpty) return (world, []);
        var events = ImmutableArray.CreateBuilder<CoreEvent>();
        foreach (var id in world.CreatureGroups.Select(group => group.Id).OrderBy(group => group.Value).ToArray())
        {
            var group = CreatureQueries.FindGroup(world, id);
            if (group is null || CreatureQueries.InCombat(world, id) || group.Activity != CreatureActivity.RaidingRailway
                || group.Objective is not CreatureObjective.Railway railway
                || group.Location is not WorldLocation.Edge location
                || location.Id != railway.Edge || !location.DistanceMeters.Equals(railway.DistanceMeters)
                || group.Home is not { } home) continue;
            var lair = world.Lairs.FirstOrDefault(item => item.Id == home);
            var projection = lair is null ? null : content.LairTypes.FirstOrDefault(type => type.Id == lair.Type);
            if (projection is null) continue;
            var interval = WorldResources.IntervalSteps(projection.RailDamageWorkSeconds)
                ?? throw new InvalidOperationException("线路破坏工期超出可表示范围。");
            if (step < group.ActivityStartedAtStep || step - group.ActivityStartedAtStep < interval) continue;
            var written = RailwayWorks.WriteBreaks(world, content, railway.Edge, railway.DistanceMeters, events);
            if (written is null) continue;
            world = CreatureQueries.Replace(written, group with
            {
                Activity = CreatureActivity.Retreating, Objective = null, ActivityStartedAtStep = step
            });
        }
        return (world, events.ToImmutable());
    }

    private static WorldState FoundLairs(WorldState world, CoreContentProjection content, ulong step,
        ImmutableArray<CoreEvent>.Builder events)
    {
        var rule = CombatRules.FindRule(world, content);
        if (rule is null) return world;
        var interval = WorldResources.IntervalSteps(rule.NewLairIntervalSeconds)
            ?? throw new InvalidOperationException("新聚落间隔超出可表示范围。");
        if (step % interval != 0) return world;
        var players = world.Settlements.Where(settlement => settlement.Relation == SettlementRelation.PlayerOwned)
            .Select(settlement => settlement.Node).ToHashSet();
        var candidates = world.Nodes.Where(node => node.Occupant is NodeOccupant.Empty && node.FoundingJob is null
                && !world.Edges.Any(edge => (edge.Start == node.Id && players.Contains(edge.End))
                    || (edge.End == node.Id && players.Contains(edge.Start))))
            .OrderBy(node => node.Id.Value).ToArray();
        var types = content.LairTypes.Where(type => type.GenerationWeight > 0)
            .OrderBy(type => type.Id.Value, StringComparer.Ordinal).ToArray();
        if (candidates.Length == 0 || types.Length == 0) return world;
        if (world.Next.Lair == ulong.MaxValue)
            throw new InvalidOperationException("生物聚落身份已耗尽。");
        var nodeSample = KeyedRandom.Sample(new RandomKey(world.Source.WorldSeed, "creature.lair.node.v1", [step]));
        var node = candidates[(int)WorldRandom.Integer(nodeSample, 0, candidates.Length - 1)];
        var weights = types.Sum(type => (long)type.GenerationWeight);
        var typeSample = KeyedRandom.Sample(new RandomKey(world.Source.WorldSeed, "creature.lair.type.v1",
            [step, node.Id.Value]));
        var choice = WorldRandom.Integer(typeSample, 0, weights - 1);
        LairTypeProjection? selected = null;
        var cursor = 0L;
        foreach (var type in types)
        {
            cursor += type.GenerationWeight;
            if (choice < cursor) { selected = type; break; }
        }
        if (selected is null) return world;
        var lair = new LairState(new(world.Next.Lair), node.Id, selected.Id);
        events.Add(new CoreEvent.LairCreated(lair.Id, lair.Node, lair.Type));
        return world with
        {
            Lairs = world.Lairs.Add(lair).OrderBy(item => item.Id.Value).ToImmutableArray(),
            Nodes = world.Nodes.Select(item => item.Id == node.Id
                ? item with { Occupant = new NodeOccupant.Lair(lair.Id) } : item).ToImmutableArray(),
            Next = world.Next with { Lair = world.Next.Lair + 1 }
        };
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
