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

    internal sealed record Population : ParsedRule
    {
        internal Population(K.PopulationRuleProjection projection, string path, ImmutableArray<RuleGoodReference> goods) : base(path, goods) => Projection = projection;
        internal K.PopulationRuleProjection Projection { get; }
        internal override K.RuleDefinitionId Id => Projection.Id;
        internal override K.SemanticRevision Revision => Projection.Revision;
    }

    internal sealed record Market : ParsedRule
    {
        internal Market(K.MarketRuleProjection projection, string path, ImmutableArray<RuleGoodReference> goods) : base(path, goods) => Projection = projection;
        internal K.MarketRuleProjection Projection { get; }
        internal override K.RuleDefinitionId Id => Projection.Id;
        internal override K.SemanticRevision Revision => Projection.Revision;
    }

    internal sealed record Construction : ParsedRule
    {
        internal Construction(K.ConstructionRuleProjection projection, string path) : base(path, []) => Projection = projection;
        internal K.ConstructionRuleProjection Projection { get; }
        internal override K.RuleDefinitionId Id => Projection.Id;
        internal override K.SemanticRevision Revision => Projection.Revision;
    }

    internal sealed record Railway : ParsedRule
    {
        internal Railway(K.RailwayRuleProjection projection, C.RailwayAppearanceProjection appearance, string path,
            ImmutableArray<RuleGoodReference> goods) : base(path, goods) => (Projection, Appearance) = (projection, appearance);
        internal K.RailwayRuleProjection Projection { get; }
        internal C.RailwayAppearanceProjection Appearance { get; }
        internal override K.RuleDefinitionId Id => Projection.Id;
        internal override K.SemanticRevision Revision => Projection.Revision;
    }

    internal sealed record Combat : ParsedRule
    {
        internal Combat(K.CombatRuleProjection projection, string path) : base(path, []) => Projection = projection;
        internal K.CombatRuleProjection Projection { get; }
        internal override K.RuleDefinitionId Id => Projection.Id;
        internal override K.SemanticRevision Revision => Projection.Revision;
    }
}
