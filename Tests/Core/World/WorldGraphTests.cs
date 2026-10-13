using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.World;

namespace Train.Core.Tests.World;

[TestClass]
public sealed class WorldGraphTests
{
    private static ImmutableArray<CandidateNode> Nodes(params MapPoint[] points) =>
        [.. points.Select((point, index) => new CandidateNode(new((ulong)index, 0), point,
            new(WorldFixture.Plain, 0, [])))];

    private static ImmutableArray<CandidateEdge> Graph(ImmutableArray<CandidateNode> nodes)
    {
        var input = WorldFixture.Input;
        var budget = new GenerationBudgetCounter(input.Configuration.Budget);
        return WorldGraph.Build(nodes, new(input, budget, 0), budget);
    }

    [TestMethod]
    [DataRow(6, "fixture:a-low", 0.12634067247966257)]
    [DataRow(8, "fixture:z-high", 0.12634067247966257)]
    public void EdgeSamplesIncludeEndpointsAndAggregateTerrainByContract(int endX, string mainTerrain, double maximumRelief)
    {
        var low = new TerrainGenerationBand(new("fixture:a-low"), 0, .08, 0, 1, 1, 2, 2);
        var high = new TerrainGenerationBand(new("fixture:z-high"), .08, 1, 0, 1, 1, 3, 1);
        var input = WorldFixture.Input;
        input = input with { Configuration = input.Configuration with
        {
            WidthMeters = 8, HeightMeters = 8, TerrainSampleSpacingMeters = 2,
            ElevationNoise = [new(8, 1)], MoistureNoise = [new(8, 1)], RiverNoise = [new(2, 1)],
            LakeElevationThreshold = .06, RiverBandHalfWidth = .02,
            TerrainBands = [high, low], InitialRailway = new(2, 1, 2, 0)
        } };
        var budget = new GenerationBudgetCounter(input.Configuration.Budget);
        var terrain = new WorldTerrain(input, budget, 0);

        // 独立 Python struct/SplitMix64 参考实现的固定值：海拔/湿度 scale=8，河流 scale=2，seed=42、retry=0。
        // 起伏取中心及半径 2 的四邻点海拔极差；边界夹到 [0,8]，不使用生产采样计算预期。
        // 河流噪声在 x=0/2/4/6/8 为 .968606144949475/.5159516314957724/
        // .7730254359761948/.6040288828916289/.6572288441132559，仅 x=2 落在 [.48,.52]。
        (int X, TerrainGenerationBand Band, double Relief, string[] Features)[] samples =
        [
            (0, low, 0.03719254803849838, ["lake"]),
            (2, low, 0.061946146129409685, ["river"]),
            (4, high, 0.10249115403886555, []),
            (6, high, 0.12634067247966257, []),
            (8, high, 0.12048587321136611, [])
        ];
        foreach (var expected in samples.Where(sample => sample.X <= endX))
        {
            var actual = terrain.Sample(new(expected.X, 0));
            Assert.AreEqual(expected.Band, actual.Band);
            Assert.AreEqual(expected.Band.Terrain, actual.View.TypeId);
            Assert.AreEqual(expected.Relief, actual.View.Relief, 1e-15);
            CollectionAssert.AreEqual(expected.Features, actual.View.FeatureIds.ToArray());
        }

        // 从高地端开始：6 米样例低/高各两个，Ordinal 必须覆盖首次出现的高地；
        // 8 米样例高地三个，不能先按身份选低地。0 米端独有湖泊，河流在内部，6 米端给出最大起伏。
        var edges = WorldGraph.Build(Nodes(new(endX, 0), new(0, 0)), terrain, budget);
        Assert.AreEqual(1, edges.Length);
        var edge = edges[0];
        Assert.AreEqual(new TerrainTypeId(mainTerrain), edge.Terrain.TypeId);
        Assert.AreEqual(endX * 3d, edge.LengthMeters);
        Assert.AreEqual(1, edge.TrackLimit);
        Assert.AreEqual(maximumRelief, edge.Terrain.Relief, 1e-15);
        CollectionAssert.AreEqual(new[] { "lake", "river" }, edge.Terrain.FeatureIds.ToArray());
    }

    [TestMethod]
    public void CollinearNodesKeepOnlyAdjacentSegmentsAndAllowSharedEndpoints()
    {
        var nodes = Nodes(new(1, 1), new(5, 1), new(9, 1));
        var edges = Graph(nodes);
        CollectionAssert.AreEquivalent(new[] { (0, 1), (1, 2) }, edges.Select(e => (e.A, e.B)).ToArray());
        AssertGraph(nodes, edges);
    }

    [TestMethod]
    public void SquareIsConnectedPlanarAndDropsLongerDiagonal()
    {
        var nodes = Nodes(new(1, 1), new(9, 1), new(1, 9), new(9, 9));
        var edges = Graph(nodes);
        CollectionAssert.AreEquivalent(new[] { (0, 1), (0, 2), (1, 3), (2, 3) },
            edges.Select(e => (e.A, e.B)).ToArray());
        AssertGraph(nodes, edges);
    }

    [TestMethod]
    public void TriangleDropsLongerEdgeWhenTwoShorterEdgesStillConnectItsEndpoints()
    {
        var nodes = Nodes(new(1, 1), new(4, 1), new(1, 5));
        var edges = Graph(nodes);
        CollectionAssert.AreEquivalent(new[] { (0, 1), (0, 2) }, edges.Select(e => (e.A, e.B)).ToArray());
        AssertGraph(nodes, edges);
    }

    [TestMethod]
    public void PublicGeneratedWorldHasSpacedBoundedNodesAndConnectedNoncrossingEdges()
    {
        var input = WorldFixture.Input with { Configuration = WorldFixture.Input.Configuration with { WidthMeters = 100 } };
        using var candidate = ((CoreResult<ICoreSessionCandidate>.Success)CoreFactory.Create()
            .PrepareNew(input, WorldFixture.Content, WorldGenerationTests.Options)).Value;
        var map = candidate.View.World;
        var nodes = map.Nodes.Select(n => new CandidateNode(new(n.Id.Value, 0), n.Position, n.Terrain)).ToImmutableArray();
        var edges = map.Edges.Select(e => new CandidateEdge((int)e.Start.Value - 1, (int)e.End.Value - 1,
            e.LengthMeters, e.Terrain, e.TrackLimit)).ToImmutableArray();
        Assert.IsTrue(nodes.Length > 1);
        foreach (var node in nodes)
        {
            Assert.IsTrue(node.Position.X >= 0 && node.Position.X <= map.WidthMeters);
            Assert.IsTrue(node.Position.Y >= 0 && node.Position.Y <= map.HeightMeters);
        }
        for (var a = 0; a < nodes.Length; a++)
        for (var b = a + 1; b < nodes.Length; b++)
        {
            var dx = nodes[a].Position.X - nodes[b].Position.X;
            var dy = nodes[a].Position.Y - nodes[b].Position.Y;
            Assert.IsTrue(dx * dx + dy * dy >= input.Configuration.MinimumNodeSpacingMeters * input.Configuration.MinimumNodeSpacingMeters);
        }
        AssertGraph(nodes, edges);
    }

    [TestMethod]
    public void GreedyGraphRejectsCrossingShortDiagonalsBeforeLongBoundaryEdges()
    {
        // 独立 Python struct/SplitMix64 + 高 53 位 smoothstep 参考实现，seed=42、scale=8、retry=0：
        // 四端 (0,0)/(4,4)/(0,4)/(4,0) 及中心 (2,2) 的海拔。
        double[] lowElevations = [0.05290786737049469, 0.3274539939951563,
            0.17192402109368948, 0.09742917262025147, 0.11485401349990437];
        var bands = lowElevations.Select(elevation => new TerrainGenerationBand(new("fixture:low"),
            elevation - 1e-14, elevation + 1e-14, 0, 1, 1, 1, 2)).ToImmutableArray();
        var input = WorldFixture.Input;
        input = input with { Configuration = input.Configuration with
        {
            WidthMeters = 4, HeightMeters = 4, TerrainSampleSpacingMeters = Math.Sqrt(8),
            ElevationNoise = [new(8, 1)],
            TerrainBands = [.. bands, new(new("fixture:detour"), 0, 1, 0, 1, 2, 3, 2)]
        } };
        var budget = new GenerationBudgetCounter(input.Configuration.Budget);
        var nodes = Nodes(new(0, 0), new(4, 4), new(0, 4), new(4, 0));
        var edges = WorldGraph.Build(nodes, new(input, budget, 0), budget);

        // 间距 √8 让对角线只采两端与中心。边界内部采样的海拔分别为
        // .1211307003207879/.07842851709546747/.1241388552008819/.18998362084967235，均只匹配绕行带。
        // 两条对角线因此都比全部边界边短；删边不可能通过更短的两段路径删除任一对角线。
        AssertConnectedAndNoncrossing(nodes, edges);
        var diagonals = edges.Where(edge => (edge.A, edge.B) is (0, 1) or (2, 3)).ToArray();
        Assert.AreEqual(1, diagonals.Length);
        Assert.AreEqual(Math.Sqrt(32), diagonals[0].LengthMeters, 1e-12);
        var boundary = edges.Where(edge => (edge.A, edge.B) is not ((0, 1) or (2, 3))).ToArray();
        Assert.IsTrue(boundary.Length >= 2);
        foreach (var edge in boundary) Assert.AreEqual(12d, edge.LengthMeters, 1e-12);
    }

    private static void AssertGraph(ImmutableArray<CandidateNode> nodes, ImmutableArray<CandidateEdge> edges)
    {
        AssertConnectedAndNoncrossing(nodes, edges);
        foreach (var edge in edges)
        {
            var a = nodes[edge.A].Position;
            var b = nodes[edge.B].Position;
            Assert.AreEqual(Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y)),
                edge.LengthMeters, 1e-12);
            Assert.AreEqual(2, edge.TrackLimit);
            Assert.AreEqual(WorldFixture.Plain, edge.Terrain.TypeId);
        }
    }

    private static void AssertConnectedAndNoncrossing(ImmutableArray<CandidateNode> nodes, ImmutableArray<CandidateEdge> edges)
    {
        var reached = new HashSet<int> { 0 };
        var pending = new Queue<int>();
        pending.Enqueue(0);
        while (pending.TryDequeue(out var node))
            foreach (var edge in edges.Where(e => e.A == node || e.B == node))
            {
                var next = edge.A == node ? edge.B : edge.A;
                if (reached.Add(next)) pending.Enqueue(next);
            }
        Assert.AreEqual(nodes.Length, reached.Count);
        for (var i = 0; i < edges.Length; i++)
            for (var j = i + 1; j < edges.Length; j++)
            {
                var first = edges[i];
                var second = edges[j];
                if (first.A == second.A || first.A == second.B || first.B == second.A || first.B == second.B)
                    continue;
                Assert.IsFalse(Intersect(nodes[first.A].Position, nodes[first.B].Position,
                    nodes[second.A].Position, nodes[second.B].Position));
            }
    }

    // 独立参数方程求交，不复用生成器的方向符号判定。
    private static bool Intersect(MapPoint a, MapPoint b, MapPoint c, MapPoint d)
    {
        var rx = b.X - a.X; var ry = b.Y - a.Y;
        var sx = d.X - c.X; var sy = d.Y - c.Y;
        var determinant = rx * sy - ry * sx;
        if (determinant == 0)
        {
            if ((c.X - a.X) * ry - (c.Y - a.Y) * rx != 0) return false;
            return Math.Max(Math.Min(a.X, b.X), Math.Min(c.X, d.X)) <= Math.Min(Math.Max(a.X, b.X), Math.Max(c.X, d.X))
                && Math.Max(Math.Min(a.Y, b.Y), Math.Min(c.Y, d.Y)) <= Math.Min(Math.Max(a.Y, b.Y), Math.Max(c.Y, d.Y));
        }
        var t = ((c.X - a.X) * sy - (c.Y - a.Y) * sx) / determinant;
        var u = ((c.X - a.X) * ry - (c.Y - a.Y) * rx) / determinant;
        return t >= 0 && t <= 1 && u >= 0 && u <= 1;
    }

    private static GeneratedOccupant Town() => new GeneratedOccupant.Settlement(
        WorldFixture.Content.SettlementTypes[0], WorldFixture.Content.SettlementTemplates[0],
        SettlementRelation.NonHostile, 10, 1);
    private static CandidateEdge Edge(int a, int b, double length = 1) => new(a, b, length,
        new(WorldFixture.Plain, 0, []), 2);

    [TestMethod]
    public void RailwayUsesMinimumEmptyRelayToConnectMandatorySettlements()
    {
        var nodes = Nodes(new(1, 1), new(5, 1), new(9, 1));
        var input = WorldFixture.Input;
        var tracks = WorldRailway.Build(nodes, [Edge(0, 1), Edge(1, 2)],
            [Town(), new GeneratedOccupant.Empty(), Town()], input, new(input.Configuration.Budget));
        CollectionAssert.AreEqual(new[] { 1, 1 }, tracks.ToArray());
    }

    [TestMethod]
    public void RailwayAddsConfiguredCycleAndRespectsTrackLimit()
    {
        var nodes = Nodes(new(1, 1), new(9, 1), new(9, 9), new(1, 9));
        var input = WorldFixture.Input;
        input = input with { Configuration = input.Configuration with { InitialRailway = new(2, 2, 2, 1) } };
        ImmutableArray<CandidateEdge> edges = [Edge(0, 1), Edge(1, 2), Edge(2, 3), Edge(0, 3) with { TrackLimit = 1 }];
        // 下限 2 与最后一条线的上限 1 无交集，不能假装成功。
        var failure = Assert.ThrowsExactly<GenerationFailure>(() => WorldRailway.Build(nodes, edges,
            [Town(), Town(), Town(), Town()], input, new(input.Configuration.Budget)));
        Assert.AreEqual("constraint_unsatisfied", failure.Category);
        Assert.AreEqual("Configuration.InitialRailway.AdditionalCycleEdges", failure.Field);
        input = input with { Configuration = input.Configuration with { InitialRailway = new(2, 1, 2, 1) } };
        var tracks = WorldRailway.Build(nodes, edges, [Town(), Town(), Town(), Town()], input,
            new(input.Configuration.Budget));
        Assert.AreEqual(4, tracks.Count(t => t > 0));
        for (var i = 0; i < tracks.Length; i++) Assert.IsTrue(tracks[i] >= 1 && tracks[i] <= edges[i].TrackLimit);
    }

    [TestMethod]
    public void RailwayCannotUseLairAsRelayAndReportsExhaustedBudget()
    {
        var nodes = Nodes(new(1, 1), new(5, 1), new(9, 1));
        var input = WorldFixture.Input;
        var lair = new LairTypeProjection(new("fixture:lair"), WorldFixture.Revision, 1, [], 1, 1, 1, 1, 1, 1, new(0));
        var blocked = Assert.ThrowsExactly<GenerationFailure>(() => WorldRailway.Build(nodes,
            [Edge(0, 1), Edge(1, 2)], [Town(), new GeneratedOccupant.Lair(lair.Id), Town()],
            input, new(input.Configuration.Budget)));
        Assert.AreEqual("constraint_unsatisfied", blocked.Category);
        Assert.AreEqual("Configuration.InitialRailway", blocked.Field);
        var exhausted = Assert.ThrowsExactly<GenerationFailure>(() => WorldRailway.Build(nodes,
            [Edge(0, 1), Edge(1, 2)], [Town(), new GeneratedOccupant.Empty(), Town()],
            input, new(input.Configuration.Budget with { MaximumSearchWork = 0 })));
        Assert.AreEqual("budget_exhausted", exhausted.Category);
    }

    [TestMethod]
    public void RailwayRejectsSettlementsInSeparateBuildableComponents()
    {
        var nodes = Nodes(new(0, 0), new(1, 0), new(2, 0));
        var input = WorldFixture.Input;
        var failure = Assert.ThrowsExactly<GenerationFailure>(() => WorldRailway.Build(nodes,
            [Edge(0, 1), Edge(1, 2) with { TrackLimit = 0 }],
            [Town(), new GeneratedOccupant.Empty(), Town()], input, new(input.Configuration.Budget)));
        Assert.AreEqual("constraint_unsatisfied", failure.Category);
        Assert.AreEqual("Configuration.InitialRailway", failure.Field);
    }

    [TestMethod]
    public void RailwayCountsReachableEmptyNodesWithinSettlementComponent()
    {
        var nodes = Nodes(new(0, 0), new(1, 0), new(2, 0));
        var input = WorldFixture.Input;
        input = input with { Configuration = input.Configuration with
            { StartConstraints = input.Configuration.StartConstraints with { MinimumReachableEmptyNodes = 1 } } };
        var failure = Assert.ThrowsExactly<GenerationFailure>(() => WorldRailway.Build(nodes, [Edge(0, 1)],
            [Town(), Town(), new GeneratedOccupant.Empty()], input, new(input.Configuration.Budget)));
        Assert.AreEqual("constraint_unsatisfied", failure.Category);
        Assert.AreEqual("Configuration.StartConstraints.MinimumReachableEmptyNodes", failure.Field);
    }

    [TestMethod]
    public void RailwayRevisitsEmptyRelayAfterItsLeafIsRemoved()
    {
        var nodes = Nodes(new(0, 0), new(1, 0), new(2, 0));
        var input = WorldFixture.Input;
        ImmutableArray<CandidateEdge> edges = [Edge(0, 1), Edge(1, 2)];
        ImmutableArray<GeneratedOccupant> occupants = [Town(), new GeneratedOccupant.Empty(), new GeneratedOccupant.Empty()];
        var tracks = WorldRailway.Build(nodes, edges, occupants, input, new(input.Configuration.Budget));
        CollectionAssert.AreEqual(new[] { 0, 0 }, tracks.ToArray());

        // 第一轮先保留中继再删叶子；第二轮中继才可删除，此次重试也消耗候选预算。
        var failure = Assert.ThrowsExactly<GenerationFailure>(() => WorldRailway.Build(nodes, edges, occupants,
            input, new(input.Configuration.Budget with { MaximumCandidates = 2 })));
        Assert.AreEqual("budget_exhausted", failure.Category);
        Assert.AreEqual("Configuration.Budget.MaximumCandidates", failure.Field);
    }

    [TestMethod]
    [DataRow(0, 1)]
    [DataRow(1, 0)]
    [DataRow(0, 0)]
    public void RailwayRemovesEmptyNodesByCellYThenX(int firstY, int secondY)
    {
        var nodes = Nodes(new(0, 0), new(1, 0), new(0, 1), new(1, 1));
        nodes = nodes.SetItem(1, nodes[1] with { Cell = new(10, (ulong)firstY) })
            .SetItem(2, nodes[2] with { Cell = new(11, (ulong)secondY) });
        var input = WorldFixture.Input;
        var tracks = WorldRailway.Build(nodes, [Edge(0, 1), Edge(1, 3), Edge(0, 2), Edge(2, 3)],
            [Town(), new GeneratedOccupant.Empty(), new GeneratedOccupant.Empty(), Town()], input, new(input.Configuration.Budget));
        var expected = firstY <= secondY ? new[] { 0, 0, 1, 1 } : new[] { 1, 1, 0, 0 };
        CollectionAssert.AreEqual(expected, tracks.ToArray());
    }

    [TestMethod]
    public void RailwayKeepsAnInclusionMinimalRelaySetWithoutClaimingGlobalMinimum()
    {
        var nodes = Nodes(new(0, 0), new(1, 0), new(0, 1), new(1, 1), new(2, 0));
        var input = WorldFixture.Input;
        ImmutableArray<CandidateEdge> edges = [Edge(0, 1), Edge(1, 4), Edge(0, 2), Edge(2, 3), Edge(3, 4)];
        var tracks = WorldRailway.Build(nodes, edges,
            [Town(), new GeneratedOccupant.Empty(), new GeneratedOccupant.Empty(), new GeneratedOccupant.Empty(), Town()],
            input, new(input.Configuration.Budget));
        CollectionAssert.AreEqual(new[] { 0, 0, 1, 1, 1 }, tracks.ToArray());

        // 删点顺序选择两个中继的路径；另一条路径只有一个中继，因此这里只承诺包含意义最少。
        foreach (var removed in new[] { 2, 3 })
        {
            var selected = new HashSet<int> { 0, 2, 3, 4 };
            selected.Remove(removed);
            var reached = new HashSet<int> { 0 };
            var pending = new Queue<int>(reached);
            while (pending.TryDequeue(out var node))
                foreach (var edge in edges.Where(e => selected.Contains(e.A) && selected.Contains(e.B) && (e.A == node || e.B == node)))
                {
                    var next = edge.A == node ? edge.B : edge.A;
                    if (reached.Add(next)) pending.Enqueue(next);
                }
            Assert.IsTrue(reached.Count < selected.Count);
        }
    }

    [TestMethod]
    public void RailwayRetainsEmptyNodesRequiredByCycleAndReachabilityConstraints()
    {
        var nodes = Nodes(new(0, 0), new(1, 0), new(2, 0));
        var input = WorldFixture.Input;
        input = input with { Configuration = input.Configuration with { InitialRailway = new(2, 1, 1, 1) } };
        var cycleTracks = WorldRailway.Build(nodes, [Edge(0, 1), Edge(1, 2), Edge(0, 2)],
            [Town(), new GeneratedOccupant.Empty(), new GeneratedOccupant.Empty()], input, new(input.Configuration.Budget));
        CollectionAssert.AreEqual(new[] { 1, 1, 1 }, cycleTracks.ToArray());

        input = input with { Configuration = input.Configuration with
        {
            InitialRailway = new(2, 1, 1, 0),
            StartConstraints = input.Configuration.StartConstraints with { MinimumReachableEmptyNodes = 1 }
        } };
        var reachableTracks = WorldRailway.Build(nodes, [Edge(0, 1), Edge(1, 2)],
            [Town(), new GeneratedOccupant.Empty(), new GeneratedOccupant.Empty()], input, new(input.Configuration.Budget));
        CollectionAssert.AreEqual(new[] { 1, 0 }, reachableTracks.ToArray());
    }
}
