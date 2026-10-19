using System.Collections.Immutable;
using Train.Core.Contracts;

namespace Train.Core.Creatures;

internal sealed record CreatureState(CreatureId Id, CreatureDefinitionId Definition, long Health);

internal abstract record CreatureObjective
{
    private CreatureObjective() { }
    internal sealed record Settlement(NodeId Node) : CreatureObjective;
    internal sealed record Railway(EdgeId Edge, double DistanceMeters) : CreatureObjective;
}

internal sealed record CreatureGroupState(CreatureGroupId Id, LairId? Home, WorldLocation Location,
    TravelDirection? Direction, CreatureActivity Activity, ulong ActivityStartedAtStep,
    CreatureObjective? Objective, ImmutableArray<CreatureState> Creatures);
