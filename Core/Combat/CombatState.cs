using System.Collections.Immutable;
using Train.Core.Contracts;

namespace Train.Core.Combat;

internal sealed record CombatState(CombatId Id, WorldLocation Location, ulong StartedAtStep,
    ImmutableArray<CombatControllerState> Controllers, ImmutableArray<CombatSideSummary> Sides);

internal sealed record CombatControllerState(CombatController Controller, ulong JoinedAtStep,
    bool Retreating, ulong RemainingDisengagementSteps, CombatUnitId? FocusTarget);
