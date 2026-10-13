using System.Collections.Immutable;
using C = Train.Content.Contracts;
using K = Train.Core.Contracts;

namespace Train.Content.Loading;

internal sealed record RuleGoodReference(K.VoxelDefinitionId Id, string Field);

internal abstract record ParsedRule
{
    private ParsedRule(string path, ImmutableArray<RuleGoodReference> goods) => (Path, Goods) = (path, goods);
    internal string Path { get; }
    internal ImmutableArray<RuleGoodReference> Goods { get; }
    internal abstract K.RuleDefinitionId Id { get; }
    internal abstract K.SemanticRevision Revision { get; }

    internal sealed record Population(K.PopulationRuleProjection Projection, string SourcePath,
        ImmutableArray<RuleGoodReference> References) : ParsedRule(SourcePath, References)
    {
        internal override K.RuleDefinitionId Id => Projection.Id;
        internal override K.SemanticRevision Revision => Projection.Revision;
    }

    internal sealed record Market(K.MarketRuleProjection Projection, string SourcePath,
        ImmutableArray<RuleGoodReference> References) : ParsedRule(SourcePath, References)
    {
        internal override K.RuleDefinitionId Id => Projection.Id;
        internal override K.SemanticRevision Revision => Projection.Revision;
    }

    internal sealed record Construction(K.ConstructionRuleProjection Projection, string SourcePath)
        : ParsedRule(SourcePath, [])
    {
        internal override K.RuleDefinitionId Id => Projection.Id;
        internal override K.SemanticRevision Revision => Projection.Revision;
    }

    internal sealed record Railway(K.RailwayRuleProjection Projection, C.RailwayAppearanceProjection Appearance,
        string SourcePath, ImmutableArray<RuleGoodReference> References) : ParsedRule(SourcePath, References)
    {
        internal override K.RuleDefinitionId Id => Projection.Id;
        internal override K.SemanticRevision Revision => Projection.Revision;
    }

    internal sealed record Combat(K.CombatRuleProjection Projection, string SourcePath) : ParsedRule(SourcePath, [])
    {
        internal override K.RuleDefinitionId Id => Projection.Id;
        internal override K.SemanticRevision Revision => Projection.Revision;
    }
}
