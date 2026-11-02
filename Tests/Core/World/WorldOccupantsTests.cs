using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.World;

namespace Train.Core.Tests.World;

[TestClass]
public sealed class WorldOccupantsTests
{
    private static readonly CoreContentProjection Content = WorldFixture.Content with
    {
        LairTypes = [new(new("fixture:lair"), WorldFixture.Revision, 1, [], 1, 1, 1, 0, 0, 1, new(0))]
    };

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Lairs_skip_the_relay_between_settlements_even_with_an_unusable_bypass(bool bypass)
    {
        var (nodes, input) = Input(.25, bypass ? 2 : 1);
        var edges = ImmutableArray.Create(Edge(0, 2), Edge(1, 2), Edge(2, 3));
        if (bypass) edges = edges.Add(Edge(0, 1) with { TrackLimit = 1 });

        var occupants = WorldOccupants.Build(nodes, edges, input, Content,
            new(input.Configuration.Budget), 0, true);

        Assert.IsInstanceOfType<GeneratedOccupant.Settlement>(occupants[0]);
        Assert.IsInstanceOfType<GeneratedOccupant.Settlement>(occupants[1]);
        Assert.IsInstanceOfType<GeneratedOccupant.Empty>(occupants[2]);
        Assert.IsInstanceOfType<GeneratedOccupant.Lair>(occupants[3]);
    }

    [TestMethod]
    public void Insufficient_connected_lair_sites_fail_instead_of_reducing_the_count()
    {
        var (nodes, input) = Input(.5, 1);
        var failure = Assert.ThrowsExactly<GenerationFailure>(() => WorldOccupants.Build(nodes,
            [Edge(0, 2), Edge(1, 2), Edge(2, 3)], input, Content,
            new(input.Configuration.Budget), 0, true));

        Assert.AreEqual("constraint_unsatisfied", failure.Category);
        Assert.AreEqual("Configuration.LairRatio", failure.Field);
    }

    private static (ImmutableArray<CandidateNode> Nodes, WorldGenerationInput Input) Input(double lairRatio, int minimumTracks)
    {
        var input = WorldFixture.Input;
        input = input with { Configuration = input.Configuration with
        {
            SettlementRatio = .5, LairRatio = lairRatio,
            LairWeights = [new(new("fixture:lair"), 1)],
            InitialRailway = input.Configuration.InitialRailway with
                { MinimumInitialTracks = minimumTracks, MaximumInitialTracks = 2 }
        } };
        // 把拓扑角色按既有随机顺序排列，使第一个生物聚落候选恰好是两座聚居地间的中继点。
        var nodes = Enumerable.Range(0, 4)
            .Select(i => new CandidateNode(new((ulong)i, 0), new(i, 0), new(WorldFixture.Plain, 0, [])))
            .OrderBy(node => WorldRandom.Sample(input.WorldSeed, "occupant.rank", 4, 0, 0, node.Cell.X, node.Cell.Y))
            .ToImmutableArray();
        return (nodes, input);
    }

    private static CandidateEdge Edge(int a, int b) => new(a, b, 1, new(WorldFixture.Plain, 0, []), 2);
}
