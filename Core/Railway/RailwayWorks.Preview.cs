using System.Collections.Immutable;
using Train.Core.Contracts;
using Train.Core.World;

namespace Train.Core.Railway;

internal static partial class RailwayWorks
{
    internal static RailwayWorkPreview Preview(WorldState world, CoreContentProjection content,
        RailwayWorkRequest request, SettlementId? source)
    {
        var id = request is RailwayWorkRequest.AddTrack add ? add.Edge : ((RailwayWorkRequest.RepairBreak)request).Edge;
        var edge = world.Edges.FirstOrDefault(item => item.Id == id);
        if (edge is null) return new([], 0, [new("object_missing", "Edge", new WorldObjectId.Edge(id), [])]);
        var blockers = ImmutableArray.CreateBuilder<CoreProblem>();
        var kind = request is RailwayWorkRequest.AddTrack ? RailwayWorkKind.AddTrack : RailwayWorkKind.RepairBreak;
        if (request is RailwayWorkRequest.RepairBreak repair)
        {
            if (!edge.Breaks.Any(item => item.Id == repair.Break))
                blockers.Add(new("object_missing", "Break", new WorldObjectId.Edge(id), []));
            else if (world.RailwayJobs.Any(job => job.Break == repair.Break))
                blockers.Add(new("busy", "Break", new WorldObjectId.Edge(id), []));
        }
        else if (edge.Tracks + AddCount(world, id) >= edge.TrackLimit)
            blockers.Add(new("invalid_state", "Edge.Tracks", new WorldObjectId.Edge(id), []));
        var rule = RuleProblem(world, content, edge);
        var price = rule is null ? Estimate(world, content, edge, kind) : new WorkPrice(null, 0, 0, rule);
        var problem = rule ?? (source is { } selected
            ? SettlementProblem(world, selected) ?? MaterialProblem(world, selected, price)
            : new CoreProblem("invalid_state", "MaterialSource", null, []));
        if (problem is not null) blockers.Add(problem);
        if (world.Next.Job == ulong.MaxValue) blockers.Add(new("identity_exhausted", "World.Next.Job", null, []));
        return new(price.Good is { } good ? [new(good, price.Units)] : [], price.Ticks, blockers.ToImmutable());
    }

    private static WorkPrice Estimate(WorldState world, CoreContentProjection content, EdgeMapItem edge, RailwayWorkKind kind)
    {
        var projection = content.RailwayRules.Single(item => item.Id == world.Source.Configuration.Rules.Railway);
        var good = Rail(world, content);
        var units = kind == RailwayWorkKind.RepairBreak
            ? projection.RepairRailUnitsPerBreak
            : checked((long)Math.Ceiling(edge.LengthMeters * projection.RailUnitsPerMeter));
        var seconds = kind == RailwayWorkKind.AddTrack
            ? edge.LengthMeters * projection.BuildSecondsPerMeter : projection.RepairSecondsPerBreak;
        var ticks = checked((ulong)Math.Ceiling(seconds * SimulationTime.StepsPerSecond));
        return new(good, units, ticks, null);
    }

    private static CoreProblem? MaterialProblem(WorldState world, SettlementId source, WorkPrice price)
    {
        var have = world.Inventories.Single(item => item.Id == Inventory(world, source)).Goods
            .SingleOrDefault(item => item.Good == price.Good)?.Quantity ?? 0;
        return have < price.Units
            ? new("insufficient_goods", "MaterialSource.Inventory", new WorldObjectId.Settlement(source),
                [WorldReferences.GoodKey(price.Good!)]) : null;
    }
}
