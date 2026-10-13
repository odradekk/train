using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.World;
using static Train.Core.Tests.World.WorldFixture;

namespace Train.Core.Tests.World;

[TestClass]
public sealed class WorldStartTests
{
    [TestMethod]
    public void Start_selects_the_settlement_without_an_adjacent_lair_even_on_an_unbuilt_edge()
    {
        var input = Input with { Configuration = Input.Configuration with
            { StartConstraints = Input.Configuration.StartConstraints with { MinimumReachableNonHostileSettlements = 1 } } };
        var start = WorldGenerator.Start(Nodes(), [Edge(0, 1), Edge(0, 2)], Occupants(), [1, 0],
            input, new(input.Configuration.Budget));
        Assert.AreEqual(1, start);
    }

    [TestMethod]
    public void Start_reports_the_lair_constraint_when_every_settlement_is_adjacent()
    {
        var failure = Assert.ThrowsExactly<GenerationFailure>(() => WorldGenerator.Start(Nodes(),
            [Edge(0, 1), Edge(0, 2), Edge(1, 2)], Occupants(), [1, 0, 0], Input, new(Input.Configuration.Budget)));
        Assert.AreEqual("constraint_unsatisfied", failure.Category);
        Assert.AreEqual("Configuration.StartConstraints.ExcludeAdjacentLairs", failure.Field);
    }

    private static ImmutableArray<CandidateNode> Nodes() =>
        [new(new(0, 0), new(0, 0), new(Plain, 0, [])),
         new(new(1, 0), new(1, 0), new(Plain, 0, [])),
         new(new(2, 0), new(2, 0), new(Plain, 0, []))];

    private static ImmutableArray<GeneratedOccupant> Occupants() =>
        [new GeneratedOccupant.Settlement(Content.SettlementTypes[0], Content.SettlementTemplates[0], SettlementRelation.NonHostile, 10, 1),
         new GeneratedOccupant.Settlement(Content.SettlementTypes[0], Content.SettlementTemplates[0], SettlementRelation.NonHostile, 10, 1),
         new GeneratedOccupant.Lair(new("fixture:lair"))];

    private static CandidateEdge Edge(int a, int b) => new(a, b, 1, new(Plain, 0, []), 2);
}
