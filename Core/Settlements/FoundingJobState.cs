using System.Collections.Immutable;
using Train.Core.Contracts;

namespace Train.Core.Settlements;

internal sealed record FoundingJobState(JobId Id, NodeId Node, SettlementId ReservedSettlementId,
    SettlementTypeId Type, SettlementTemplateProjection Template, long InitialResidents,
    RuleDefinitionId Rule, SemanticRevision StartedFromRevision, ulong FrozenDurationTicks,
    ulong ElapsedTicks, ImmutableArray<GoodsQuantity> InvestedGoods)
{
    internal FoundingJobView Read() => new(Id, Node, FrozenDurationTicks, ElapsedTicks,
        ReservedSettlementId, Template.Id, InvestedGoods);
}
