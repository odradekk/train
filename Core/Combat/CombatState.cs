using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Train.Core.Contracts;

namespace Train.Core.Combat;

internal sealed record CombatState(CombatId Id, WorldLocation Location, ulong StartedAtStep,
    ImmutableArray<CombatControllerState> Controllers, ImmutableArray<CombatSideSummary> Sides)
{
    [JsonRequired] public ImmutableArray<CreatureCombatantState> Creatures { get; init; } = [];
}

internal sealed record CreatureCombatantState(CreatureGroupId Group, ulong JoinedAtStep, int CountAtJoin,
    bool Retreating, ulong RemainingDisengagementSteps);

internal sealed record CombatControllerState(CombatController Controller, ulong JoinedAtStep,
    bool Retreating, ulong RemainingDisengagementSteps, CombatUnitId? FocusTarget);
