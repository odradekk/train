using System.Collections.Immutable;
using Train.Core.Construction;
using Train.Core.Contracts;
using Train.Core.Sessions;
using Train.Core.World;

namespace Train.Core.Railway;

internal sealed record RailwayWorkState(JobId Id, EdgeId Edge, RailwayWorkKind Kind, BreakId? Break,
    SettlementId MaterialSource, ulong FrozenDurationTicks, ulong ElapsedTicks,
    ImmutableArray<GoodsQuantity> InvestedGoods, ImmutableArray<CoreProblem> Blockers);

internal sealed record AutomaticRailRepair(EdgeId Edge, SettlementId MaterialSource);

internal static class RailwayWorks
{
    internal static RailwayWorkView Read(WorldState world, RailwayWorkState job)
    {
        var edge = world.Edges.Single(item => item.Id == job.Edge);
        var track = job.Kind == RailwayWorkKind.RepairBreak
            ? edge.Breaks.Single(item => item.Id == job.Break).Track
            : edge.Tracks + 1 + world.RailwayJobs.Count(other => other.Edge == job.Edge
                && other.Kind == RailwayWorkKind.AddTrack && other.Id.Value < job.Id.Value);
        var progress = job.Blockers.IsEmpty ? job.ElapsedTicks / (double)job.FrozenDurationTicks : 0;
        return new(job.Id, job.Kind, track, job.Break, job.MaterialSource, job.InvestedGoods, progress, job.Blockers);
    }

    internal static ImmutableArray<RailwayWorkView> Views(WorldState world, EdgeMapItem edge)
        => world.RailwayJobs.Where(job => job.Edge == edge.Id).Select(job => Read(world, job)).ToImmutableArray();

    internal static CommandEffect Expand(WorldState world, CoreContentProjection content, CoreCommand.ExpandRail command)
    {
        var edge = world.Edges.FirstOrDefault(item => item.Id == command.Edge);
        if (edge is null) return MissingEdge(command.Edge);
        if (RuleProblem(world, content, edge) is { } rule) return new CommandEffect.Rejected([rule]);
        if (edge.Tracks + AddCount(world, edge.Id) >= edge.TrackLimit)
            return Reject("invalid_state", "Edge.Tracks", new WorldObjectId.Edge(edge.Id));
        if (SettlementProblem(world, command.MaterialSource) is { } settlement) return new CommandEffect.Rejected([settlement]);
        var price = Price(world, content, edge, RailwayWorkKind.AddTrack, command.MaterialSource);
        if (price.Problem is { } problem) return new CommandEffect.Rejected([problem]);
        if (world.Next.Job == ulong.MaxValue) return Reject("identity_exhausted", "World.Next.Job");
        return AcceptStarted(world, content, edge, RailwayWorkKind.AddTrack, null, command.MaterialSource, price);
    }

    internal static CommandEffect Repair(WorldState world, CoreContentProjection content, CoreCommand.RepairRail command)
    {
        var edge = world.Edges.FirstOrDefault(item => item.Id == command.Edge);
        if (edge is null) return MissingEdge(command.Edge);
        if (!edge.Breaks.Any(item => item.Id == command.Break))
            return Reject("object_missing", "Break", new WorldObjectId.Edge(edge.Id));
        if (world.RailwayJobs.Any(job => job.Break == command.Break))
            return Reject("busy", "Break", new WorldObjectId.Edge(edge.Id));
        if (RuleProblem(world, content, edge) is { } rule) return new CommandEffect.Rejected([rule]);
        if (SettlementProblem(world, command.MaterialSource) is { } settlement) return new CommandEffect.Rejected([settlement]);
        var price = Price(world, content, edge, RailwayWorkKind.RepairBreak, command.MaterialSource);
        if (price.Problem is { } problem) return new CommandEffect.Rejected([problem]);
        if (world.Next.Job == ulong.MaxValue) return Reject("identity_exhausted", "World.Next.Job");
        return AcceptStarted(world, content, edge, RailwayWorkKind.RepairBreak, command.Break, command.MaterialSource, price);
    }

    internal static CommandEffect SetAutomatic(WorldState world, CoreContentProjection content, CoreCommand.SetAutomaticRailRepair command)
    {
        var edge = world.Edges.FirstOrDefault(item => item.Id == command.Edge);
        if (edge is null) return MissingEdge(command.Edge);
        var current = world.AutomaticRailRepairs.FirstOrDefault(item => item.Edge == edge.Id);
        if (current?.MaterialSource == command.MaterialSource)
            return new CommandEffect.Accepted(content, [], world);
        if (command.MaterialSource is not { } source)
        {
            return new CommandEffect.Accepted(content, [], world with
            {
                AutomaticRailRepairs = world.AutomaticRailRepairs.Where(item => item.Edge != edge.Id).ToImmutableArray(),
                RailwayJobs = world.RailwayJobs.Where(job => job.Edge != edge.Id || job.Blockers.IsEmpty).ToImmutableArray()
            });
        }
        if (SettlementProblem(world, source) is { } settlement) return new CommandEffect.Rejected([settlement]);
        var covered = world.RailwayJobs.Where(job => job.Break is not null).Select(job => job.Break!.Value).ToHashSet();
        var needed = edge.Breaks.Count(item => !covered.Contains(item.Id));
        if (!CanAllocate(world.Next.Job, needed)) return Reject("identity_exhausted", "World.Next.Job");
        world = world with
        {
            AutomaticRailRepairs = world.AutomaticRailRepairs.Where(item => item.Edge != edge.Id)
                .Append(new AutomaticRailRepair(edge.Id, source)).OrderBy(item => item.Edge.Value).ToImmutableArray()
        };
        var events = ImmutableArray.CreateBuilder<CoreEvent>();
        var created = ImmutableArray.CreateBuilder<WorldObjectId>();
        foreach (var job in world.RailwayJobs.Where(job => job.Edge == edge.Id && !job.Blockers.IsEmpty).ToArray())
            world = Reevaluate(world, content, job with { MaterialSource = source }, events);
        foreach (var broken in edge.Breaks)
        {
            if (world.RailwayJobs.Any(job => job.Break == broken.Id)) continue;
            world = OpenRepair(world, content, edge, source, broken.Id, events, created);
        }
        return new CommandEffect.Accepted(content, events.ToImmutable(), WithDependencies(world, content), created.ToImmutable());
    }

    internal static CommandEffect AddBreak(WorldState world, CoreContentProjection content, CoreCommand.DebugAddRailBreak command)
    {
        var edge = world.Edges.FirstOrDefault(item => item.Id == command.Edge);
        if (edge is null) return MissingEdge(command.Edge);
        if (command.Track > edge.Tracks || command.DistanceMeters > edge.LengthMeters)
            return Reject("invalid_state", "Break", new WorldObjectId.Edge(command.Edge));
        if (world.Next.Break == ulong.MaxValue) return Reject("identity_exhausted", "World.Next.Break");
        var auto = world.AutomaticRailRepairs.FirstOrDefault(item => item.Edge == edge.Id);
        if (auto is not null && !CanAllocate(world.Next.Job, 1)) return Reject("identity_exhausted", "World.Next.Job");
        var broken = new TrackBreakView(new(world.Next.Break), command.Track, command.DistanceMeters);
        world = world with
        {
            Edges = world.Edges.Select(item => item.Id == edge.Id ? item with { Breaks = item.Breaks.Add(broken) } : item).ToImmutableArray(),
            TrackHandoffs = world.TrackHandoffs.Where(item => item.Edge != edge.Id || item.Track != broken.Track).ToImmutableArray(),
            Next = world.Next with { Break = world.Next.Break + 1 }
        };
        var events = ImmutableArray.CreateBuilder<CoreEvent>();
        events.Add(new CoreEvent.RailBroken(edge.Id, broken));
        if (auto is null) return new CommandEffect.Accepted(content, events.ToImmutable(), world);
        var created = ImmutableArray.CreateBuilder<WorldObjectId>();
        edge = world.Edges.Single(item => item.Id == edge.Id);
        world = OpenRepair(world, content, edge, auto.MaterialSource, broken.Id, events, created);
        return new CommandEffect.Accepted(content, events.ToImmutable(), WithDependencies(world, content), created.ToImmutable());
    }

    internal static (WorldState World, ImmutableArray<CoreEvent> Events) Advance(WorldState world, CoreContentProjection content)
    {
        if (world.RailwayJobs.IsEmpty) return (world, []);
        var events = ImmutableArray.CreateBuilder<CoreEvent>();
        foreach (var id in world.RailwayJobs.Select(job => job.Id).ToArray())
        {
            var original = world.RailwayJobs.SingleOrDefault(job => job.Id == id);
            if (original is null || !original.Blockers.IsEmpty) continue;
            var job = original with { ElapsedTicks = checked(original.ElapsedTicks + 1) };
            if (job.ElapsedTicks < job.FrozenDurationTicks)
            {
                world = Replace(world, job);
                continue;
            }
            world = Complete(world, job, events);
        }
        foreach (var id in world.RailwayJobs.Select(job => job.Id).ToArray())
        {
            var original = world.RailwayJobs.SingleOrDefault(job => job.Id == id);
            if (original is null || original.Blockers.IsEmpty) continue;
            world = Reevaluate(world, content, original, events);
        }
        return (world, events.ToImmutable());
    }

    private static WorldState Reevaluate(WorldState world, CoreContentProjection content, RailwayWorkState job,
        ImmutableArray<CoreEvent>.Builder events)
    {
        var edge = world.Edges.Single(item => item.Id == job.Edge);
        var price = Price(world, content, edge, job.Kind, job.MaterialSource);
        if (price.Problem is { } problem)
        {
            var changed = job with { Blockers = [problem], FrozenDurationTicks = 0, ElapsedTicks = 0, InvestedGoods = [] };
            world = Replace(world, changed);
            if (!SameBlockers(job.Blockers, changed.Blockers))
                events.Add(new CoreEvent.RailwayWorkChanged(edge.Id, job.Id, changed.Blockers));
            return world;
        }
        var started = job with
        {
            FrozenDurationTicks = price.Ticks, ElapsedTicks = 0, Blockers = [],
            InvestedGoods = [new GoodsQuantity(price.Good!, price.Units)]
        };
        world = ConstructionCommands.Consume(Replace(world, started),
            [new(Inventory(world, job.MaterialSource), started.InvestedGoods)]);
        events.Add(new CoreEvent.RailwayWorkStarted(edge.Id, Read(world, started)));
        return world;
    }

    private static WorldState OpenRepair(WorldState world, CoreContentProjection content, EdgeMapItem edge,
        SettlementId source, BreakId broken, ImmutableArray<CoreEvent>.Builder events, ImmutableArray<WorldObjectId>.Builder created)
    {
        var id = new JobId(world.Next.Job);
        world = world with { Next = world.Next with { Job = checked(world.Next.Job + 1) } };
        var job = new RailwayWorkState(id, edge.Id, RailwayWorkKind.RepairBreak, broken, source, 0, 0, [], []);
        world = world with { RailwayJobs = world.RailwayJobs.Add(job) };
        created.Add(new WorldObjectId.Job(id));
        return Reevaluate(world, content, job, events);
    }

    private static CommandEffect AcceptStarted(WorldState world, CoreContentProjection content, EdgeMapItem edge,
        RailwayWorkKind kind, BreakId? broken, SettlementId source, WorkPrice price)
    {
        var id = new JobId(world.Next.Job);
        var job = new RailwayWorkState(id, edge.Id, kind, broken, source, price.Ticks, 0,
            [new GoodsQuantity(price.Good!, price.Units)], []);
        world = ConstructionCommands.Consume(world with
        {
            RailwayJobs = world.RailwayJobs.Add(job),
            Next = world.Next with { Job = checked(world.Next.Job + 1) }
        }, [new(Inventory(world, source), job.InvestedGoods)]);
        world = WithDependencies(world, content);
        return new CommandEffect.Accepted(content, [new CoreEvent.RailwayWorkStarted(edge.Id, Read(world, job))],
            world, [new WorldObjectId.Job(id)]);
    }

    private static WorldState Complete(WorldState world, RailwayWorkState job, ImmutableArray<CoreEvent>.Builder events)
    {
        var edge = world.Edges.Single(item => item.Id == job.Edge);
        world = world with { RailwayJobs = world.RailwayJobs.Where(item => item.Id != job.Id).ToImmutableArray() };
        if (job.Kind == RailwayWorkKind.AddTrack)
        {
            var tracks = checked(edge.Tracks + 1);
            events.Add(new CoreEvent.RailExpanded(edge.Id, tracks));
            return world with { Edges = ReplaceEdge(world, edge with { Tracks = tracks }) };
        }
        var broken = edge.Breaks.Single(item => item.Id == job.Break);
        events.Add(new CoreEvent.RailRepaired(edge.Id, broken.Id, broken.Track));
        return world with
        {
            Edges = ReplaceEdge(world, edge with { Breaks = edge.Breaks.Where(item => item.Id != broken.Id).ToImmutableArray() }),
            Trains = world.Trains.Select(train => train.ReturningFromBreak == broken.Id
                ? train with { ReturningFromBreak = null } : train).ToImmutableArray()
        };
    }

    private readonly record struct WorkPrice(GoodId? Good, long Units, ulong Ticks, CoreProblem? Problem);

    private static CoreProblem? RuleProblem(WorldState world, CoreContentProjection content, EdgeMapItem edge)
    {
        var ruleId = world.Source.Configuration.Rules.Railway;
        if (!content.RailwayRules.Any(rule => rule.Id == ruleId))
            return new("content_missing", "RailwayRule", new WorldObjectId.Edge(edge.Id), [ruleId.Value]);
        var good = Rail(world, content);
        return WorldGoods.DefinitionMissing(world, content, good)
            ? new("content_missing", "RailwayRule.RailGood", new WorldObjectId.Edge(edge.Id), [WorldReferences.GoodKey(good)])
            : null;
    }

    private static CoreProblem? SettlementProblem(WorldState world, SettlementId source)
    {
        var settlement = world.Settlements.FirstOrDefault(item => item.Id == source);
        if (settlement is null) return new("object_missing", "MaterialSource", new WorldObjectId.Settlement(source), []);
        return settlement.Relation == SettlementRelation.PlayerOwned
            ? null : new("not_owned", "MaterialSource", new WorldObjectId.Settlement(source), []);
    }

    private static WorkPrice Price(WorldState world, CoreContentProjection content, EdgeMapItem edge,
        RailwayWorkKind kind, SettlementId source)
    {
        if (RuleProblem(world, content, edge) is { } rule) return new(null, 0, 0, rule);
        if (SettlementProblem(world, source) is { } settlement) return new(Rail(world, content), 0, 0, settlement);
        var projection = content.RailwayRules.Single(item => item.Id == world.Source.Configuration.Rules.Railway);
        var good = Rail(world, content);
        var units = kind == RailwayWorkKind.RepairBreak
            ? projection.RepairRailUnitsPerBreak
            : checked((long)Math.Ceiling(edge.LengthMeters * projection.RailUnitsPerMeter));
        var seconds = kind == RailwayWorkKind.AddTrack
            ? edge.LengthMeters * projection.BuildSecondsPerMeter : projection.RepairSecondsPerBreak;
        var ticks = checked((ulong)Math.Ceiling(seconds * SimulationTime.StepsPerSecond));
        var have = world.Inventories.Single(item => item.Id == Inventory(world, source)).Goods
            .SingleOrDefault(item => item.Good == good)?.Quantity ?? 0;
        return have < units
            ? new(good, units, ticks, new("insufficient_goods", "MaterialSource.Inventory",
                new WorldObjectId.Settlement(source), [WorldReferences.GoodKey(good)]))
            : new(good, units, ticks, null);
    }

    private static GoodId.Voxel Rail(WorldState world, CoreContentProjection content)
        => new(content.RailwayRules.Single(rule => rule.Id == world.Source.Configuration.Rules.Railway).RailGood);

    private static InventoryId Inventory(WorldState world, SettlementId source)
        => world.Settlements.Single(item => item.Id == source).Inventory;

    // 记录相等不会按元素比较 RelatedIdentities，相同阻塞会被当成变化。
    private static bool SameBlockers(ImmutableArray<CoreProblem> left, ImmutableArray<CoreProblem> right)
    {
        if (left.IsDefault || right.IsDefault || left.Length != right.Length) return false;
        for (var index = 0; index < left.Length; index++)
        {
            var first = left[index];
            var second = right[index];
            if (first.Code != second.Code || first.Field != second.Field || first.Subject != second.Subject
                || !first.RelatedIdentities.SequenceEqual(second.RelatedIdentities))
                return false;
        }
        return true;
    }

    private static int AddCount(WorldState world, EdgeId edge)
        => world.RailwayJobs.Count(job => job.Edge == edge && job.Kind == RailwayWorkKind.AddTrack);

    private static bool CanAllocate(ulong next, int count)
        => (ulong)count <= ulong.MaxValue - next;

    private static WorldState WithDependencies(WorldState world, CoreContentProjection content)
    {
        var dependencies = WorldReferences.Dependencies(world, content);
        return dependencies.SequenceEqual(world.DefinitionDependencies) ? world : world with { DefinitionDependencies = dependencies };
    }

    private static WorldState Replace(WorldState world, RailwayWorkState job)
        => world with { RailwayJobs = world.RailwayJobs.Select(item => item.Id == job.Id ? job : item).ToImmutableArray() };

    private static ImmutableArray<EdgeMapItem> ReplaceEdge(WorldState world, EdgeMapItem edge)
        => world.Edges.Select(item => item.Id == edge.Id ? edge : item).ToImmutableArray();

    private static CommandEffect MissingEdge(EdgeId edge) => Reject("object_missing", "Edge", new WorldObjectId.Edge(edge));

    private static CommandEffect.Rejected Reject(string code, string field, WorldObjectId? subject = null)
        => new([new(code, field, subject, [])]);
}
